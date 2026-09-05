using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;

namespace GwentCompanion.Core.GameState;

public enum PendingValueKind { HandBoost, DeckBoost, Resilience, Phoenix, LocationOrder, GraveyardReturn }
public sealed record PendingValue(string Id, PlayerSide Side, string CardId, string Name, PendingValueKind Kind,
    int Minimum, int? Maximum, string Reason, DateTimeOffset At, int? Round = null, string? TargetCardId = null,
    string? InstanceId = null, bool Settled = false, int? TargetRound = null);
public sealed record GrowingCardValue(PlayerSide Side, string CardId, string Name, int Minimum, int? Maximum, string Unit, string Reason);
public sealed record StoredCardEffect(PlayerSide Side, string SourceId, CardDefinition Target, string Reason);
public sealed record BountyHistory(PlayerSide Side, int TotalBasePower, int TotalPlacements, int MaximumBasePower);
public sealed record ProvisionUsage(int SpentFloor, int? Total, int? RemainingCeiling, string Detail,
    int CommittedCards = 0, int? UnaccountedCards = null, double? ProvisionsPerCard = null, bool AssumedSize = false)
{
    public string RemainingReadout => Total is { } total && SpentFloor > total
        ? "Provision evidence conflicts — review origins / copies"
        : RemainingCeiling is { } p && UnaccountedCards is > 0
        ? $"Left {p}p / {UnaccountedCards} · {ProvisionsPerCard:F1}p/card"
        : UnaccountedCards == 0 && !AssumedSize ? "All starting slots accounted for" : "Remaining not read";
}

/// <summary>Evidence-backed pending value, separate from current board score and original-deck bookkeeping.</summary>
public sealed class LiveValueLedger
{
    private readonly Dictionary<string, PendingValue> _entries = [];
    private readonly Dictionary<(PlayerSide, string), GrowingCardValue> _growth = [];
    private readonly Dictionary<(PlayerSide, string), StoredCardEffect> _stored = [];
    private readonly Dictionary<(PlayerSide, string), CardDefinition> _spying = [];
    private readonly Dictionary<PlayerSide, BountyHistory> _bounty = [];
    private readonly HashSet<string> _events = [];
    private readonly Stack<PendingValue> _undo = [];
    private readonly Dictionary<PlayerSide, int> _endedTurns = [];
    private readonly Dictionary<PlayerSide, int> _refunded = [];
    private readonly Dictionary<PlayerSide, int> _compatibleRefunds = [];
    private readonly Dictionary<PlayerSide, HashSet<string>> _spent = [];
    private readonly Dictionary<PlayerSide, Dictionary<string, int>> _spentCopies = [];
    private readonly HashSet<(PlayerSide, string)> _played = [];
    private readonly Dictionary<(PlayerSide, string), int> _playedCounts = [];
    private readonly Dictionary<(PlayerSide, string), int> _scenarioStages = [];
    private string? _session;
    private PlayerSide? _lastPlaySide;
    private IReadOnlyList<CardDefinition>? _catalogSource;
    private Dictionary<string, CardDefinition> _definitions = [];
    public IReadOnlyList<PendingValue> Pending => _entries.Values.Where(entry => !entry.Settled).OrderBy(entry => entry.Side).ThenByDescending(entry => entry.Maximum).ToArray();
    public IReadOnlyList<PendingValue> Entries => _entries.Values.ToArray();
    public IReadOnlyList<StoredCardEffect> Stored => _stored.Values.ToArray();
    public IReadOnlyList<(PlayerSide Side, CardDefinition Card)> SpyingGranted => _spying
        .Select(pair => (pair.Key.Item1, pair.Value)).ToArray();
    public IReadOnlyList<BountyHistory> Bounties => Enum.GetValues<PlayerSide>()
        .Select(side => _bounty.GetValueOrDefault(side) ?? new(side, 0, 0, 0)).ToArray();
    public IReadOnlyList<GrowingCardValue> Growing => _growth.Values.OrderBy(value => value.Side).ThenBy(value => value.Name).ToArray();
    public GrowingCardValue? Growth(PlayerSide side, string id) => _growth.GetValueOrDefault((side, id));
    public IReadOnlySet<string> Spent(PlayerSide side) => _spent.GetValueOrDefault(side) ?? [];
    public IReadOnlyDictionary<string, int> SpentCopies(PlayerSide side) => _spentCopies.GetValueOrDefault(side) ?? [];
    public void RecordStartingDeckEvidence(PlayerSide side, string cardId, int copies = 1)
    {
        if (string.IsNullOrWhiteSpace(cardId) || copies < 1) return;
        var startingId = EvolvingCardCatalog.StartingId(cardId);
        if (!_spent.TryGetValue(side, out var spent)) _spent[side] = spent = [];
        spent.Add(startingId);
        if (!_spentCopies.TryGetValue(side, out var recordedCopies)) _spentCopies[side] = recordedCopies = [];
        recordedCopies[startingId] = Math.Max(recordedCopies.GetValueOrDefault(startingId), copies);
    }
    public string CarryoverReadout(PlayerSide side)
    {
        var pending = Pending.Where(entry => entry.Side == side).ToArray();
        var boundedMaximum = pending.Where(entry => entry.Maximum is not null).Sum(entry => entry.Maximum!.Value);
        if (pending.Any(entry => entry.Maximum is null)) return $"Carryover {boundedMaximum}+ (unresolved max)";
        return pending.Any(entry => entry.Minimum != entry.Maximum)
            ? $"Carryover {boundedMaximum} (max)"
            : $"Carryover {boundedMaximum}";
    }
    public bool WasPlayed(PlayerSide side, string cardId) => _played.Contains((side, cardId));
    public int PlayedCount(PlayerSide side, string kind) => _playedCounts.GetValueOrDefault((side, kind));
    public int? ScenarioStage(PlayerSide side, string cardId) => _scenarioStages.GetValueOrDefault((side, cardId), -1) is var stage && stage >= 0 ? stage : null;
    public void Reset() { _entries.Clear(); _growth.Clear(); _stored.Clear(); _spying.Clear(); _bounty.Clear(); _events.Clear(); _undo.Clear(); _endedTurns.Clear(); _refunded.Clear(); _compatibleRefunds.Clear(); _spent.Clear(); _spentCopies.Clear(); _played.Clear(); _playedCounts.Clear(); _scenarioStages.Clear(); _session = null; _lastPlaySide = null; }
    private bool Once(string id) => _events.Count < 4096 && _events.Add(id);
    public void Add(PendingValue value)
    {
        if (value.Minimum < 0 || value.Maximum < value.Minimum) throw new ArgumentException("Invalid pending point range");
        if (_entries.Count < 256 && !_entries.ContainsKey(value.Id)) _entries[value.Id] = value;
    }
    public void Settle(string id, string reason)
    {
        if (!_entries.TryGetValue(id, out var value) || value.Settled) return;
        _undo.Push(value); _entries[id] = value with { Settled = true, Reason = value.Reason + " · " + reason };
    }
    public void Undo() { if (_undo.TryPop(out var value)) _entries[value.Id] = value; }
    public void Confirm(string id, int amount, string? targetId)
    {
        if (amount < 0 || !_entries.TryGetValue(id, out var value)) return;
        _undo.Push(value); _entries[id] = value with { Minimum = amount, Maximum = amount, TargetCardId = targetId,
            Reason = "User reviewed pending value", Settled = amount == 0 };
    }
    public void RealizeBoost(string eventId, PlayerSide side, string cardId, int amount, PendingValueKind kind)
    {
        if (amount <= 0 || kind is not (PendingValueKind.HandBoost or PendingValueKind.DeckBoost) || !Once(eventId)) return;
        // Caller must establish pre-entry boost, not merely notice a boosted board unit.
        foreach (var entry in Pending.Where(entry => entry.Side == side && entry.Kind == kind && (entry.TargetCardId is null || entry.TargetCardId == cardId))
                     .OrderByDescending(entry => entry.TargetCardId is not null).ThenBy(entry => entry.At))
        {
            if (entry.Maximum is null) continue;
            var consumed = Math.Min(amount, entry.Maximum.Value); _undo.Push(entry);
            _entries[entry.Id] = entry with { Minimum = Math.Max(0, entry.Minimum - consumed), Maximum = entry.Maximum - consumed,
                Settled = entry.Maximum == consumed, Reason = entry.Reason + $" · {consumed} boost realized on {cardId}" };
            amount -= consumed; if (amount == 0) break;
        }
    }
    public void RecordGrowth(GrowingCardValue value)
    { if (value.Minimum < 0 || value.Maximum < value.Minimum) throw new ArgumentException("Invalid growth range"); _growth[(value.Side, value.CardId)] = value; }
    public void ConfirmWanderersInHand(PlayerSide side, CardDefinition card)
    {
        if (card.Id != "202953" || _growth.ContainsKey((side, card.Id))) return;
        RecordGrowth(new(side, card.Id, card.Name, card.Power, null, "power",
            "Verified in hand; earlier boosts unknown. Only separately verified start-turn movements add +1; ordinary draws/reordering do not."));
    }
    public bool ObserveWanderersMovement(string eventId, PlayerSide side, CardDefinition card, int shifts = 1)
    {
        if (card.Id != "202953" || shifts is < 1 or > 30 || !Once("wanderers:" + eventId)) return false;
        ConfirmWanderersInHand(side, card);
        var value = _growth[(side, card.Id)];
        RecordGrowth(value with { Minimum = value.Minimum + shifts, Maximum = value.Maximum + shifts,
            Reason = $"{shifts} verified start-turn hand shift(s) added +{shifts}. Missing earlier movement/hand buffs remain unknown; no generic turn-count extrapolation." });
        return true;
    }
    public void ForgetGrowth(PlayerSide side, string cardId) => _growth.Remove((side, cardId));
    public void Store(PlayerSide side, CardDefinition target, string reason) => _stored[(side, "202192")] = new(side, "202192", target, reason);
    public void AddSpying(PlayerSide giver, CardDefinition target) => _spying[(giver, target.Id)] = target;
    public bool SetBountyHistory(PlayerSide side, int totalBasePower, int totalPlacements, int maximumBasePower)
    {
        if (totalBasePower < 0 || totalPlacements < 0 || maximumBasePower < 0 || maximumBasePower > totalBasePower ||
            totalBasePower > 999 || totalPlacements > 999) return false;
        _bounty[side] = new(side, totalBasePower, totalPlacements, maximumBasePower);
        return true;
    }
    public bool TributeRefund(string eventId, PlayerSide side, int paid, int refunded, bool refundRouteVerified, DateTimeOffset at, CardDefinition king)
    {
        if (!refundRouteVerified || paid < 1 || refunded < 1 || refunded > paid || refunded > 12 || !Once(eventId)) return false;
        var used = Math.Min(12, _refunded.GetValueOrDefault(side) + refunded); _refunded[side] = used;
        var possibleEarlier = _compatibleRefunds.GetValueOrDefault(side);
        RecordGrowth(new(side, king.Id, king.Name, Math.Max(0, 12 - used - possibleEarlier), 12 - used, "refund left",
            "Verified Tribute refund detects King of Beggars before its summon. The range includes earlier full-refund-compatible Tributes; missed Tributes may make the true counter lower."));
        return true;
    }
    public bool TributeRefundHint(string eventId, PlayerSide side, int paid, int possibleRefund, DateTimeOffset at, CardDefinition king)
    {
        if (paid < 1 || possibleRefund < 1 || possibleRefund > paid || possibleRefund > 12 || !Once(eventId)) return false;
        var compatible = Math.Min(12, _compatibleRefunds.GetValueOrDefault(side) + possibleRefund);
        _compatibleRefunds[side] = compatible;
        var verified = _refunded.GetValueOrDefault(side);
        RecordGrowth(new(side, king.Id, king.Name, Math.Max(0, 12 - verified - compatible), 12 - verified, "possible refund left",
            "A payable Tribute was followed by an unchanged coin counter. This is compatible with King refunding it, but the Tribute may instead have been declined."));
        return true;
    }
    public void Observe(GameStateUpdate update, IReadOnlyList<CardDefinition> catalog, IEnumerable<CardDefinition> userHypothesis,
        IEnumerable<CardDefinition> opponentHypothesis)
    {
        if (!update.Accepted || update.After.At is not { } at) return;
        var state = update.After;
        if (_session != state.SessionId) { Reset(); _session = state.SessionId; }
        // Unplayed hand boosts survive rounds, but not an exhausted hand or match.
        foreach (var entry in Pending.Where(entry => entry.Kind == PendingValueKind.HandBoost &&
            (state.Phase == GamePhase.Ended || state.Player(entry.Side).HandCount is { Value: 0 } hand &&
                hand.At == at && at > entry.At)).ToArray())
            Settle(entry.Id, state.Phase == GamePhase.Ended ? "Match ended; no future hand value" : "Hand exhausted; boost no longer banked");
        if (!ReferenceEquals(_catalogSource, catalog)) { _catalogSource = catalog; _definitions = catalog.ToDictionary(card => card.Id); }
        var cards = _definitions;
        var hypotheses = new Dictionary<PlayerSide, CardDefinition[]>
        {
            [PlayerSide.User] = userHypothesis.DistinctBy(card => card.Id).ToArray(),
            [PlayerSide.Opponent] = opponentHypothesis.DistinctBy(card => card.Id).ToArray()
        };
        void EndedTurn(PlayerSide ended, GameStateSnapshot scoreState, string reason)
        {
            _endedTurns[ended] = _endedTurns.GetValueOrDefault(ended) + 1;
            var hasAerondight = hypotheses[ended].Any(card => card.Id == "203102") ||
                state.Cards.Any(card => card.Card.Id == "203102" && card.Location.Value.Controller == ended);
            if (!hasAerondight) return;
            var value = _growth.GetValueOrDefault((ended, "203102"));
            var own = scoreState.Player(ended).Score?.Value;
            var other = scoreState.Player(ended == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User).Score?.Value;
            var ahead = own is not null && other is not null ? own > other : (bool?)null;
            value ??= new(ended, "203102", cards.GetValueOrDefault("203102")?.Name ?? "Aerondight", 0, 0, "damage",
                "Hypothesized from observed turn endings; earlier unseen growth may raise the value.");
            if (ahead == true) value = value with { Minimum = value.Minimum + 1,
                Maximum = value.Maximum is { } maximum ? maximum + 1 : null,
                Reason = $"{reason}; side was visibly ahead, so Aerondight grew by 1." };
            else if (ahead is null) value = value with { Maximum = value.Maximum is { } maximum ? maximum + 1 : null,
                Reason = $"{reason}; score was unread, so the possible Aerondight growth widened by 1." };
            else value = value with { Reason = $"{reason}; side was not ahead, so Aerondight did not grow." };
            RecordGrowth(value);
        }
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var wasPassed = update.Before.Player(side).Passed?.Value == true;
            var nowPassed = update.After.Player(side).Passed?.Value == true;
            if (!wasPassed && nowPassed) EndedTurn(side, update.After, "Observed pass ended the turn");
        }
        foreach (var expired in Pending.Where(entry => entry.Kind == PendingValueKind.Phoenix &&
            (state.Phase == GamePhase.Ended || entry.TargetRound is { } target && state.Round?.Value > target)).ToArray())
            Settle(expired.Id, "Return window ended; not counted as observed points");
        void PhoenixFuture(PlayerSide side)
        {
            if (state.Phase == GamePhase.Ended) return;
            if (state.Round?.Value is not (>= 1 and <= 3))
            {
                Add(new($"phoenix:{side}:unknown-round", side, "201579", "Phoenix", PendingValueKind.Phoenix, 0, null,
                    "Round unread; remaining returns cannot yet be counted", at)); return;
            }
            var currentRound = state.Round.Value;
            Settle($"phoenix:{side}:unknown-round", "Round now known; replaced by separate return windows");
            for (var target = currentRound + 1; target <= 3; target++)
                Add(new($"phoenix:{side}:return:{target}", side, "201579", "Phoenix", PendingValueKind.Phoenix, 0, 5,
                    $"Potential round {target} return · Hatchling Order → 5-point body, no Deploy Vitality. Requires reaching that round and avoiding banishment/disruption.",
                    at, currentRound, TargetRound: target));
        }
        foreach (var action in update.Events.Where(action => action.Kind is "PlayPreview" or "BoardEvidence" or "BoardContact" or "HistoricalAction" or "DeckRevealEvidence" && action.Side is not null && action.CardId is not null))
        {
            var side = action.Side!.Value;
            RecordStartingDeckEvidence(side, action.CardId!, action.ResolvedDeckCopies is > 0 and <= 25 ? action.ResolvedDeckCopies.Value : 1);
        }
        // Simultaneous fresh contacts can establish two committed copies. Do not sum
        // reacquired contacts, casts across rounds, or copies merely revealed in deck/hand.
        foreach (var group in state.Cards.Where(card => card.Presence == CardPresence.Visible && card.LastSeen == at &&
            card.Location.Value.Zone == CardZone.Board).GroupBy(card => (card.Location.Value.Controller, card.Card.Id)))
        {
            var (side, observedId) = group.Key;
            var id = EvolvingCardCatalog.StartingId(observedId);
            if (!_spentCopies.TryGetValue(side, out var copies) || !copies.ContainsKey(id)) continue;
            copies[id] = Math.Max(copies[id], group.Count());
        }
        foreach (var action in update.Events.Where(action => action.Kind == "PlayPreview" && action.Side is not null && action.CardId is not null))
        {
            if (!Once("play:" + action.Id) || !cards.TryGetValue(action.CardId!, out var card)) continue;
            var side = action.Side!.Value;
            _played.Add((side, card.Id));
            if (card.Kind == CardKind.Special)
                _playedCounts[(side, "specials-played")] = _playedCounts.GetValueOrDefault((side, "specials-played")) + 1;
            if (card.Id == "203113")
                _playedCounts[(side, "raid-damage")] = _playedCounts.GetValueOrDefault((side, "raid-damage")) + 1;
            var playedRule = PlayRules.Compile(card);
            if (playedRule.Reaction == "scenario") _scenarioStages[(side, card.Id)] = 0;
            else foreach (var scenario in _scenarioStages.Where(pair => pair.Key.Item1 == side && pair.Value < 2).ToArray())
            {
                if (cards.TryGetValue(scenario.Key.Item2, out var scenarioCard) && ScenarioRules.Matches(scenarioCard, playedRule))
                    _scenarioStages[scenario.Key] = scenario.Value + 1;
            }
            // A genuine replay can replenish a previously consumed asset. Artwork reacquisition alone cannot.
            var assetKey = $"asset:{side}:{card.Id}";
            if (_entries.TryGetValue(assetKey, out var asset) && asset.Settled && asset.At < at) _entries.Remove(assetKey);
            var otherSide = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            if (_lastPlaySide is { } previous && (previous != side || previous == side && state.Player(otherSide).Passed?.Value == true))
            {
                if (state.Player(previous).Passed?.Value != true)
                    EndedTurn(previous, update.Before, "Opponent's next play confirms the previous turn ended");
                foreach (var growth in _growth.Values.Where(value => value.Side == previous && value.Maximum is not null).ToArray())
                {
                    if (growth.CardId == "202360") RecordGrowth(growth with { Minimum = 1, Maximum = 10, Reason = "Value can reroll at turn end; hover again for the current roll" });
                    if (growth.CardId == "202953") RecordGrowth(growth with { Maximum = null, Reason = "Last observed power; hand movement/continued hand presence needs confirmation" });
                }
                foreach (var smuggler in update.Before.Cards.Where(unit => unit.Card.Id == "142315" && unit.Location.Value.Controller == previous &&
                    unit.Location.Value.Row == BoardRow.Melee && unit.Presence == CardPresence.Visible && unit.Status(CardStatus.Locked)?.Value != true))
                    Add(new($"smuggler:{action.Id}:{smuggler.InstanceId}", previous, smuggler.Card.Id, smuggler.Card.Name,
                        PendingValueKind.HandBoost, 0, 1, "Possible end-turn hand boost; hand target and uninterrupted trigger not confirmed", at, state.Round?.Value));
            }
            _lastPlaySide = side;
            if (card.Kind == CardKind.Unit && card.Faction == "Scoia'tael" && _growth.TryGetValue((side, "203072"), out var harmony))
                RecordGrowth(harmony with { Maximum = null, Reason = "Last observed Harmony power; later category triggers and hidden position remain uncertain" });
            void Buff(PendingValueKind kind, int amount, string reason) => Add(new("buff:" + action.Id, side, card.Id, card.Name, kind, 0, amount,
                reason + " · resolution/targets not fully observed", at, state.Round?.Value));
            if (card.Id == "202596")
            {
                var offerings = side == PlayerSide.User && state.User.StartingDeckReference is { } own ? own.CountOf("202601") : 2;
                Buff(PendingValueKind.DeckBoost, 2 * (3 + offerings), "Allgod; includes possible Offering targets");
            }
            else if (card.Id == "202601") Buff(PendingValueKind.DeckBoost, 2, "Offering deck boost");
            else if (card.Id is "203047" or "202677" or "202267")
            {
                var handCount = state.Player(side).HandCount;
                var knownHand = state.Cards.Where(item => item.Location.Value.Controller == side &&
                    item.Location.Value.Zone == CardZone.Hand && item.LastSeen == at && item.Presence == CardPresence.Visible).ToArray();
                var exhausted = handCount is { Value: 0 } && handCount.At == at;
                var noEligibleUnit = handCount is { Value: > 0 } && handCount.At == at &&
                    knownHand.Length == handCount.Value && knownHand.All(item => item.Card.Kind != CardKind.Unit ||
                        card.Id == "202677" && item.Card.Faction == "Neutral");
                if (state.Phase != GamePhase.Ended && !exhausted && !noEligibleUnit)
                    Buff(PendingValueKind.HandBoost, 2, card.Id == "202677"
                        ? "Circle of Life: requires a non-Neutral unit remaining in hand; Deathblow chooses the target instead of random"
                        : "Hand boost requires a remaining eligible target");
            }
            if (card.Id == "202192")
            {
                // Stored soul is reusable; playing Sword does not erase it. Deathblow may replace it, so freshness needs review.
                if (_stored.TryGetValue((side, card.Id), out var stored)) _stored[(side, card.Id)] = stored with { Reason = stored.Reason + " · Sword replayed; check whether a new Deathblow replaced this soul" };
            }
        }
        foreach (var card in state.Cards.Where(card => card.Presence == CardPresence.Visible && card.LastSeen == at && card.Location.Value.Zone == CardZone.Board))
        {
            var side = card.Location.Value.Controller;
            var ruleText = card.Card.AbilityText ?? "";
            if (card.Status(CardStatus.Spying)?.Value == true && !PlayRules.Compile(card.Card).Disloyal)
            {
                var giver = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
                _spying[(giver, card.Card.Id)] = card.Card;
            }
            if (card.Card.Id == "203100")
                RecordGrowth(new(side, card.Card.Id, card.Card.Name, 0, 0, "refund left",
                    "King of Beggars is visible on board; its Tribute-refund counter has been exhausted."));
            if (card.Card.Id == "201579")
            {
                foreach (var pending in Pending.Where(entry => entry.Side == side && entry.Kind == PendingValueKind.Phoenix &&
                    (entry.TargetRound is { } target ? state.Round?.Value >= target : entry.Round is not null && state.Round?.Value > entry.Round)).ToArray())
                    Settle(pending.Id, "Phoenix body observed; this return is realized");
                if (card.Status(CardStatus.Doomed)?.Value == true)
                {
                    foreach (var pending in Pending.Where(entry => entry.Side == side && entry.Kind == PendingValueKind.Phoenix).ToArray())
                        Settle(pending.Id, "Phoenix visibly Doomed; no unassisted graveyard return assumed");
                }
                else PhoenixFuture(side);
            }
            if (card.Card.Id == "202114")
            {
                // Hatchling is an artifact, but its Order is the same banked Phoenix return, not an extra asset.
                PhoenixFuture(side);
                if (state.Phase != GamePhase.Ended && state.Round?.Value is >= 1 and <= 3)
                    Add(new($"phoenix:{side}:return:{state.Round.Value}", side, "201579", "Phoenix", PendingValueKind.Phoenix, 0, 5,
                        "Hatchling visible; current return stays pending until its Order produces Phoenix", at,
                        state.Round.Value - 1, TargetRound: state.Round.Value));
            }
            var location = card.Card.Kind == CardKind.Artifact && card.Card.Id != "202114" && ruleText.Contains("Order:", StringComparison.Ordinal);
            // Card identity is a lower bound here. Reacquiring an artwork contact must not duplicate a gold asset.
            var key = $"asset:{side}:{card.Card.Id}";
            if (card.Card.Id == "202792")
            {
                foreach (var pending in Pending.Where(entry => entry.Side == side && entry.CardId == card.Card.Id &&
                    entry.Kind == PendingValueKind.GraveyardReturn && entry.Round is not null && state.Round?.Value > entry.Round).ToArray())
                    Settle(pending.Id, "Vypper body observed after the cross-graveyard return; carryover realized");
                if (card.Status(CardStatus.Doomed)?.Value == true)
                {
                    foreach (var pending in Pending.Where(entry => entry.Side == side && entry.CardId == card.Card.Id &&
                        entry.Kind == PendingValueKind.GraveyardReturn).ToArray())
                        Settle(pending.Id, "Returned Vypper is visibly Doomed; it cannot bank another unassisted return");
                }
                else if (state.Round?.Value is < 3)
                {
                    var body = card.BasePower?.Value ?? card.Card.Power;
                    Add(new($"vypper:{side}:{card.InstanceId}:{state.Round.Value}", side, card.Card.Id, card.Card.Name,
                        PendingValueKind.GraveyardReturn, 0, body,
                        "Potential later-round body: Vypper must survive round end, clear the opponent graveyard, and retain row space; current points are not counted again.",
                        at, state.Round.Value, InstanceId: card.InstanceId));
                }
            }
            // Locations already paid for remain banked Orders across rounds; their Resilience is not an extra point asset.
            if (location)
            {
                var points = card.Card.Id switch { "203061" => (int?)6, "203279" => 5, _ => null };
                var previous = update.Before.Cards.FirstOrDefault(old => old.InstanceId == card.InstanceId);
                if (card.Charges is { Value: > 0 } fresh && fresh.At == at && previous?.Charges?.Value == 0 &&
                    _entries.TryGetValue(key, out var depleted) && depleted.Settled) _entries.Remove(key);
                Add(new(key, side, card.Card.Id, card.Card.Name, PendingValueKind.LocationOrder, 0, points,
                    "Banked Order; target requirements apply. Armor/resources are not counted as raw points.", at, state.Round?.Value, InstanceId: card.InstanceId));
                if (card.Charges is { Value: 0 } exhausted && exhausted.At == at) Settle(key, "Order visibly exhausted");
            }
            else if (card.Card.Kind == CardKind.Unit && card.Card.Id != "201579" && card.Status(CardStatus.Resilience)?.Value != false &&
                (card.Status(CardStatus.Resilience)?.Value == true || ruleText.TrimStart().StartsWith("Resilience.", StringComparison.Ordinal)))
            {
                var basePower = card.BasePower?.Value ?? card.Card.Power;
                var power = card.Power?.Value;
                Add(new(key, side, card.Card.Id, card.Card.Name, PendingValueKind.Resilience, 0, power is null ? basePower : Math.Min(basePower, power.Value),
                    "Potential next-round body, not additional current points; boosts removed, removal before round-end remains possible.", at, state.Round?.Value, InstanceId: card.InstanceId));
            }
            foreach (var pending in Pending.Where(entry => entry.Side == side && entry.Kind == PendingValueKind.Resilience &&
                entry.CardId == card.Card.Id && entry.Round is not null && state.Round?.Value > entry.Round).ToArray())
                Settle(pending.Id, "Body observed in the later round; no longer pending");
            foreach (var pending in Pending.Where(entry => entry.Side == side && entry.InstanceId == card.InstanceId &&
                entry.Kind == PendingValueKind.Resilience && card.Status(CardStatus.Resilience) is { Value: false } status && status.At == at).ToArray())
                Settle(pending.Id, "Resilience visibly removed/consumed");
        }
        foreach (var zone in state.ZoneEvidence.Where(zone => zone.Side is not null && zone.CardId == "201579" && zone.Zone == CardZone.Graveyard && zone.LastSeen == at))
            PhoenixFuture(zone.Side!.Value);
        foreach (var side in Enum.GetValues<PlayerSide>())
        foreach (var definition in hypotheses[side])
        {
            if (definition.Id is not ("132205" or "203102" or "203072" or "202953")) continue;
            var onBoard = state.Cards.Any(card => card.Card.Id == definition.Id && card.Location.Value.Controller == side && card.Presence == CardPresence.Visible);
            if (onBoard) { _growth.Remove((side, definition.Id)); continue; }
            // Only Morvudd has unconditional off-board growth. Others need actual qualifying end-turn/category/movement facts.
            if (definition.Id == "203102" && !_growth.ContainsKey((side, definition.Id)))
                RecordGrowth(new(side, definition.Id, definition.Name, 0, _endedTurns.GetValueOrDefault(side), "damage",
                    "Hypothesized Aerondight value from observed turn endings; only turns ending while ahead grow it, and missed earlier turns can raise the true value."));
            if (definition.Id == "132205" && !_growth.ContainsKey((side, definition.Id)) ||
                definition.Id == "132205" && _growth[(side, definition.Id)].Reason.StartsWith("Hypothetical", StringComparison.Ordinal))
                RecordGrowth(new(side, definition.Id, definition.Name, definition.Power + _endedTurns.GetValueOrDefault(side), null, "power",
                    "Hypothetical off-board Morvudd throughout observed turn endings; missing earlier turns and time on board may change this estimate."));
        }
        // Explicit known hand/deck → board continuity can allocate a measured pre-entry boost. No board-power-minus-printed-power shortcut.
        foreach (var card in state.Cards.Where(card => card.Location.Value.Zone == CardZone.Board && card.Location.At == at && !card.IdentityContinuityUncertain))
        {
            var old = update.Before.Cards.FirstOrDefault(old => old.InstanceId == card.InstanceId);
            if (old?.Location.Value.Zone is not (CardZone.Hand or CardZone.Deck) || old.Power is null || old.BasePower is null || old.Power.Value <= old.BasePower.Value) continue;
            if (!old.Power.IsFresh(at, TimeSpan.FromSeconds(6)) || !old.BasePower.IsFresh(at, TimeSpan.FromSeconds(6))) continue;
            // An unassigned buff pool is not enough to attribute a board boost. Auto-allocation requires a reviewed matching target.
            if (!Pending.Any(entry => entry.Side == card.Location.Value.Controller && entry.TargetCardId == card.Card.Id && entry.Minimum == entry.Maximum &&
                entry.Kind == (old.Location.Value.Zone == CardZone.Hand ? PendingValueKind.HandBoost : PendingValueKind.DeckBoost))) continue;
            RealizeBoost("entry:" + card.InstanceId + ":" + card.Location.At, card.Location.Value.Controller, card.Card.Id, old.Power.Value - old.BasePower.Value,
                old.Location.Value.Zone == CardZone.Hand ? PendingValueKind.HandBoost : PendingValueKind.DeckBoost);
        }
    }
    public void ObserveInventory(IEnumerable<ZoneHypothesis> inventory, GameStateSnapshot state)
    {
        if (state.At is not { } at || state.Phase == GamePhase.Ended) return;
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            const string id = "203119";
            var grave = inventory.FirstOrDefault(entry => entry.Side == side && entry.CardId == id && entry.Zone == CardZone.Graveyard)?.Copies ?? 0;
            var banished = inventory.FirstOrDefault(entry => entry.Side == side && entry.CardId == id && entry.Zone == CardZone.Banished)?.Copies ?? 0;
            var key = $"grave-return:{side}:{id}";
            var onBoard = state.Cards.Any(card => card.Card.Id == id && card.Location.Value.Controller == side &&
                card.Location.Value.Zone == CardZone.Board && card.Presence is CardPresence.Visible or CardPresence.LastKnown);
            if (grave > 0)
                Add(new(key, side, id, "Giant Toad", PendingValueKind.GraveyardReturn, 0, 4,
                    "Giant Toad is recorded in the graveyard: a Deathwish unit played on Ranged can return its 4-point body, consume that unit and give Toad Doomed. Row space, lock/banish and the trigger remain conditional.",
                    at, state.Round?.Value));
            else if (onBoard)
                Settle(key, "Giant Toad body observed after leaving the graveyard; carryover realized");
            else if (banished > 0)
                Settle(key, "Giant Toad is recorded as banished; unassisted graveyard return unavailable");
        }
    }
    public static ProvisionUsage Provisions(IEnumerable<ObservedCard> observations, DeckDefinition? reference, int? allowance, IReadOnlySet<string>? spentIds = null,
        IReadOnlyDictionary<string, int>? committedCopies = null, int? startingSize = null, int minimumStartingSize = 25)
    {
        var committed = observations.Where(item => item.Card.CanBeInStartingDeck && item.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact &&
            (spentIds is null || spentIds.Contains(item.Card.Id))).ToArray();
        var seen = committed.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))
            .GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
        // A positive committed marker establishes that this identity left a hidden
        // zone. ObservedCopies is itself a separately corroborated physical lower
        // bound (two original play episodes or two simultaneous bodies), so retain
        // that lower bound even when the generic event ledger saw only one episode.
        // Zero still excludes a mere deck/hand reveal from the spent numerator.
        int Copies(ObservedCard item) => committedCopies is null ? item.ObservedCopies :
            committedCopies.GetValueOrDefault(item.Card.Id) > 0 ? item.ObservedCopies : 0;
        // The numerator and denominator must use the same card-cost snapshot. A
        // cached reference from another patch must not mix its total with current
        // recognition metadata (or charge non-reference generated cards).
        var referenceCosts = reference?.Cards.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.First().Card.Provision);
        // A selected player list is already proof of original-deck membership. If a
        // board/play commitment arrives before the slower tracker emits a second
        // provenance record, charge the committed reference identity once (and never
        // beyond the listed copy cap). Generated identities outside the reference stay
        // excluded. This keeps the known-player counter current without treating a
        // replay as another starting slot.
        var seenById = seen.ToDictionary(item => item.Card.Id, item => Copies(item));
        var referenceCommitted = reference is null ? null : reference.Cards
            .GroupBy(item => item.Card.Id)
            .Select(group =>
            {
                var id = group.Key;
                var listed = group.Sum(item => item.Count);
                var explicitlyUncertain = committed.Any(item => item.Card.Id == id &&
                    !StartingDeckRules.CountsAgainstStartingDeck(item.Provenance));
                var marker = !explicitlyUncertain && spentIds?.Contains(id) == true ? Math.Max(1, committedCopies?.GetValueOrDefault(id) ?? 0) : 0;
                // Once origin-aware copy evidence exists, generic board contacts
                // cannot override its floor (generated duplicates also make bodies).
                // The marker remains a one-copy latency fallback, not copy provenance.
                if (seenById.ContainsKey(id)) marker=Math.Min(1,marker);
                return (Id: id, Copies: Math.Min(listed, Math.Max(seenById.GetValueOrDefault(id), marker)));
            }).Where(item => item.Copies > 0).ToArray();
        var spent = reference is null
            ? seen.Sum(item => item.Card.Provision * Copies(item))
            : referenceCommitted!.Sum(item => referenceCosts!.GetValueOrDefault(item.Id) * item.Copies);
        var total = reference?.ProvisionTotal ?? allowance;
        var count = reference is null ? seen.Sum(Copies) : referenceCommitted!.Sum(item => item.Copies);
        var remainingCards = Math.Max(0, (reference?.CardCount ?? startingSize ?? Math.Max(25, minimumStartingSize)) - count);
        var remaining = total is { } max && max >= spent ? max - spent : (int?)null;
        return new(spent, total, remaining,
            "Distinct original-copy provision floor, not repeated cast costs. Generated/replayed cards do not spend a second starting slot. " +
            "Left p / cards = unaccounted starting provisions / unaccounted starting slots, NOT the draw pile or last hand. * uses the minimum supported starting size (normally 25) until exact size is read. Average is a rough estimate, not a bound on any particular card. " +
            (reference is null ? "Denominator is the maximum starting-leader allowance, not proven deck expenditure. Unaccounted includes deck, hand, missed cards and unused allowance." : "Full player deck is known: denominator is its actual provision sum. Only recognized committed copies are counted; missed plays and unresolved duplicate copies can raise the numerator. Remaining is an upper bound, not a claim about cards still in deck."),
            count, remainingCards, remainingCards > 0 && remaining is not null ? remaining.Value / (double)remainingCards : null,
            reference is null && startingSize is null);
    }
}
