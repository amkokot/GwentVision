using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public enum DeckCondition { Singleton, GoldenNekker, Renfri, Devotion, Musicians }
public sealed record ResolvedDeckCondition(DeckCondition Condition, DateTimeOffset At, string Evidence, bool Suggested = false);
public sealed record OpponentClue(string Key, string Text, string Detail, int Priority);
public sealed record TriggerOpportunity(string CardId, DateTimeOffset At, string Trigger,
    bool ConditionsVerified = false, bool AbsenceVerified = false, int ExpectedCopies = 1);

/// <summary>Evidence about the starting list, never a claim to see the opponent's hand.</summary>
public sealed class OpponentKnowledge
{
    public OpponentSequenceTracker Sequences { get; } = new();
    public SummonAbsenceTracker SummonAbsence { get; } = new();
    private readonly Dictionary<DeckCondition, ResolvedDeckCondition> _resolved = [];
    private readonly Dictionary<string, TriggerOpportunity> _opportunities = [];
    private readonly HashSet<string> _seen = [];
    private readonly HashSet<string> _livePlayed = [];
    private int? _pendingRound;
    private int _roundVotes;
    private DateTimeOffset _lastScreenAt;
    private DateTimeOffset? _lastZoltanAt;
    private DateTimeOffset _lastHandAt;
    private DateTimeOffset _lastDrawPileAt;
    private bool _anyLivePlay;
    private DateTimeOffset _lastOpponentPlayAt;
    private bool _saskiaCommanderActive;
    private int _saskiaTurns, _saskiaSummonCredits;
    private (int Size, DateTimeOffset At)? _predeal;
    private (int Size, DateTimeOffset At, int Votes)? _conservedSize;
    public string? StartingSizeEvidence { get; private set; }
    private readonly DevotionEvidenceTracker _devotion = new();
    private readonly DeckConditionEvidenceTracker _conditions = new();
    private CardDefinition? _renfriDefinition;
    private int _unitProvisionFloor, _maximumStartingAllowance;
    private int _shupeProvisions=13;
    public void ConfigureShupe(CardDefinition shupe) { if(shupe.Id=="201627") _shupeProvisions=shupe.Provision; }
    public void ConfigureRenfriBudget(CardDefinition renfri, int unitProvisionFloor, int maximumStartingAllowance)
    {
        if (renfri.Name != "Renfri" || unitProvisionFloor < 1 || maximumStartingAllowance < 1) throw new ArgumentException("Invalid Renfri budget context");
        _renfriDefinition = renfri; _unitProvisionFloor = unitProvisionFloor; _maximumStartingAllowance = maximumStartingAllowance;
    }
    public IReadOnlyCollection<ResolvedDeckCondition> Resolved => _resolved.Values;
    public IReadOnlyCollection<TriggerOpportunity> Opportunities => _opportunities.Values;
    public int? Round { get; private set; }
    public bool Ended { get; private set; }
    public int? OpponentHand { get; private set; }
    public int? OpponentDrawPile { get; private set; }
    public int StartingSizeMinimum { get; private set; } = 25;
    public int? StartingSize { get; private set; }
    public string? StartingLeader { get; set; }
    public string? StartingLeaderId { get; private set; }
    public string? CurrentLeader { get; private set; }
    public string? CurrentLeaderId { get; private set; }
    public double VisibleLeaderConfidence { get; private set; }
    public string? StartingStratagemId { get; set; }
    public int? LeaderBonus { get; set; }
    public bool HasOpeningWindow { get; private set; }
    public bool HasOpponentPlay => _livePlayed.Count > 0;

    public void Reset()
    {
        Sequences.Reset();
        _resolved.Clear(); _opportunities.Clear(); _seen.Clear(); _livePlayed.Clear(); _devotion.Reset(); _conditions.Reset(); SummonAbsence.Reset();
        Round = null; Ended = false; OpponentHand = null; OpponentDrawPile = null;
        StartingSize = null; StartingSizeMinimum = 25; StartingLeader = null; StartingLeaderId = null;
        CurrentLeader = null; CurrentLeaderId = null; VisibleLeaderConfidence = 0; LeaderBonus = null;
        _predeal = null; _conservedSize = null; StartingSizeEvidence = null;
        StartingStratagemId = null; HasOpeningWindow = false; _pendingRound = null; _roundVotes = 0;
        _lastScreenAt = default; _lastZoltanAt = null; _lastHandAt = default; _lastDrawPileAt = default; _anyLivePlay = false; _lastOpponentPlayAt = default;
        _saskiaCommanderActive=false; _saskiaTurns=0; _saskiaSummonCredits=0;
    }

    public void SetRound(int? round) { Round = round is >= 1 and <= 3 ? round : null; }
    public void SetStartingSize(int? size)
    {
        if (size is not null && size is < 25 or > 100) throw new ArgumentOutOfRangeException(nameof(size));
        StartingSize = size;
    }
    public bool ObserveVisibleLeader(CardDefinition leader, double confidence, bool mayEstablishStartingLeader)
    {
        if (leader.Kind != CardKind.Leader || confidence < .60) return false;
        var changed = CurrentLeaderId != leader.Id || confidence > VisibleLeaderConfidence + .04;
        CurrentLeader = leader.Name; CurrentLeaderId = leader.Id;
        VisibleLeaderConfidence = Math.Max(VisibleLeaderConfidence, confidence);
        if (mayEstablishStartingLeader && string.IsNullOrWhiteSpace(StartingLeader))
        {
            StartingLeader = leader.Name; StartingLeaderId = leader.Id; LeaderBonus = leader.Provision;
            changed = true;
        }
        return changed;
    }
    public void Resolve(DeckCondition condition, DateTimeOffset at, string reason) => _resolved[condition] = new(condition, at, reason);
    public void Suggest(DeckCondition condition, DateTimeOffset at, string reason)
    {
        // Repeated visual hints must not downgrade an explicitly reviewed resolution.
        if (!_resolved.TryGetValue(condition, out var prior) || prior.Suggested)
            _resolved[condition] = new(condition, at, reason, Suggested: true);
    }
    public void ClearResolution(DeckCondition condition) => _resolved.Remove(condition);
    public void Opportunity(TriggerOpportunity opportunity) => _opportunities[opportunity.CardId] = opportunity;
    public void ClearOpportunity(string cardId) => _opportunities.Remove(cardId);

    public bool ObserveScreen(GwentVisualObservation screen, DateTimeOffset at)
    {
        if (at <= _lastScreenAt) return false;
        _lastScreenAt = at;
        var before = (Round, Ended, OpponentHand, OpponentDrawPile, StartingSize);
        var header = (screen.ScreenHeader ?? "").Trim().ToUpperInvariant();
        if (header == "FINAL ROUND") header = "ROUND 3";
        var match = System.Text.RegularExpressions.Regex.Match(header, @"^ROUND\s+([123])$");
        if (match.Success)
        {
            var round = int.Parse(match.Groups[1].Value);
            _roundVotes = _pendingRound == round ? _roundVotes + 1 : 1; _pendingRound = round;
            if (_roundVotes >= 2 && (Round is null || round >= Round))
            {
                Round = round;
                if (round == 1 && !HasOpponentPlay) HasOpeningWindow = true;
            }
        }
        else { _roundVotes = 0; _pendingRound = null; }
        // Only whole result headings, not ROUND LOST or arbitrary card text.
        if (header is "VICTORY" or "DEFEAT" or "DRAW" or "GAME OVER") Ended = true;
        if (screen.OpponentHandCount is >= 0 and <= 10) { OpponentHand = screen.OpponentHandCount; _lastHandAt = at; }
        else if (at - _lastHandAt > TimeSpan.FromSeconds(6)) OpponentHand = null;
        if (screen.OpponentDeckCount is >= 0 and <= 100) { OpponentDrawPile = screen.OpponentDeckCount; _lastDrawPileAt = at; }
        else if (at - _lastDrawPileAt > TimeSpan.FromSeconds(6)) OpponentDrawPile = null;
        // Capture the pre-deal pile, then require conservation through the round-one
        // deal. A 10+15 snapshot alone cannot exclude setup thinning in a larger deck.
        // HUD values have already passed the recognizer's two-sample confirmation.
        if (!_anyLivePlay && Round is null && screen.View == GwentViewKind.Board && !screen.IsCardSelectionOverlay &&
            screen.OpponentHandCount == 0 && screen.OpponentDeckCount is >= 25 and <= 100 &&
            screen.UserScore == 0 && screen.OpponentScore == 0)
            _predeal = (screen.OpponentDeckCount.Value, at);
        if (StartingSize is null && !_anyLivePlay && HasOpeningWindow && Round == 1 &&
            _predeal is { } deal && at - deal.At <= TimeSpan.FromMinutes(2) &&
            screen.OpponentHandCount == 10 && screen.OpponentDeckCount is { } pile &&
            screen.UserScore == 0 && screen.OpponentScore == 0 && pile + 10 == deal.Size)
        {
            StartingSize = deal.Size;
            StartingSizeEvidence = $"Pre-deal pile {deal.Size}; round-one hand 10 + pile {pile}, before any detected play.";
        }
        if (Ended || before.Round != Round) _conditions.Reset();
        return before != (Round, Ended, OpponentHand, OpponentDrawPile, StartingSize);
    }

    public void Observe(VisionEvidenceEvent evidence, string? faction = null, CardProvenance origin = CardProvenance.Unknown)
    {
        SummonAbsence.ObserveEvent(evidence,Round);
        if (_conditions.Observe(evidence) is { } activation) Resolve(activation.Condition, activation.At, activation.Evidence);
        if (_devotion.ObserveEvent(evidence, faction, origin) is { } clue) Suggest(DeckCondition.Devotion, evidence.ObservedAt, clue);
        var sighting = evidence.Sighting;
        if (sighting.Side != PlayerSide.Opponent) return;
        if (sighting.Source == CardSightSource.PlayPreview) { _anyLivePlay = true; _lastOpponentPlayAt = evidence.ObservedAt; }
        _seen.Add(sighting.Card.Id);
        if (sighting.Source != CardSightSource.PlayPreview) return; // History has no reliable trigger time.
        if (sighting.Card.Id=="203090")
        {
            _saskiaCommanderActive=true; _saskiaTurns=0;
            _saskiaSummonCredits=Math.Min(4,_saskiaSummonCredits+1); // Deploy resolution.
        }
        else if (_saskiaCommanderActive)
        {
            _saskiaTurns++;
            if (_saskiaTurns>=3)
            {
                _saskiaTurns=0;
                _saskiaSummonCredits=Math.Min(4,_saskiaSummonCredits+1); // Timer 3 resolution.
            }
        }
        _livePlayed.Add(sighting.Card.Id);
        if (sighting.Card.Name.StartsWith("Zoltan", StringComparison.OrdinalIgnoreCase)) _lastZoltanAt = evidence.ObservedAt;
        if (sighting.Card.IsGold && sighting.Card.Id != "112210")
            Opportunity(new("112210", evidence.ObservedAt, "a gold play"));
        if (sighting.Card.HasCategory("Bomb")) Opportunity(new("202879", evidence.ObservedAt, "a Bomb play"));
    }

    public bool ObserveConditions(GwentCompanion.Core.GameState.GameStateUpdate update)
    {
        var changed = false;
        if (_conditions.Observe(update) is { } activation)
        { Resolve(activation.Condition, activation.At, activation.Evidence); changed = true; }
        if (_devotion.ObserveState(update) is { } clue && update.After.At is { } at)
        { Suggest(DeckCondition.Devotion, at, clue); changed = true; }
        return changed;
    }

    public bool ObserveDevotionFrame(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings, bool boardScanned)
    {
        if (_devotion.ObserveFrame(at, screen, sightings, boardScanned) is not { } clue) return false;
        Suggest(DeckCondition.Devotion, at, clue); return true;
    }

    public PlayOriginAssessment? BoardOrigin(VisionEvidenceEvent evidence, string? opponentFaction = null)
    {
        var sighting = evidence.Sighting;
        if (sighting.Side != PlayerSide.Opponent || sighting.Source != CardSightSource.Board) return null;
        if (sighting.Card.Id == "202200" && HasOpeningWindow && !_anyLivePlay)
        {
            Resolve(DeckCondition.Musicians, evidence.ObservedAt,
                "Musicians recognized on the opening board after the round-one heading, before an opponent preview. Setup origin is probable; missing previews remain possible.");
            return new(CardProvenance.ProbableStartingDeck, "Opening Musicians appearance; original membership probable, not an extra play.");
        }
        if (sighting.Card.Id == "203280" && _lastZoltanAt is { } zoltan &&
            evidence.ObservedAt - zoltan >= TimeSpan.Zero && evidence.ObservedAt - zoltan < TimeSpan.FromSeconds(15))
            return new(CardProvenance.ProbableStartingDeck,
                "Eudora arrival after Zoltan suggests the setup infusion: account once for the banished starting original, not each spawned copy. Infusion transfer remains possible.");
        if (_saskiaCommanderActive && _saskiaSummonCredits>0 && !_livePlayed.Contains(sighting.Card.Id) && !_seen.Contains(sighting.Card.Id) &&
            sighting.Card.Kind==CardKind.Unit && !sighting.Card.IsGold && sighting.Card.Faction!="Neutral" &&
            (string.IsNullOrWhiteSpace(opponentFaction) || FactionCompatibility.IsPlayableBy(sighting.Card,opponentFaction)))
        {
            _saskiaSummonCredits--;
            return new(CardProvenance.ProbableStartingDeck,
                "Previously observed Saskia: Commander has a bounded Deploy/Timer summon credit; this fresh bronze board-only identity is a probable deck target. Generated-name risks take precedence upstream.");
        }
        if (!_livePlayed.Contains(sighting.Card.Id) && sighting.Card.Id is "202397" or "152313")
            return new(CardProvenance.ProbableStartingDeck, sighting.Card.Id == "202397"
                ? "Knickers appeared on the board without a matching play preview; count the automatic arrival as one probable starting original. A verified generated/deck-added origin takes precedence upstream."
                : "Tuirseach Skirmisher appeared on the board without a matching play preview; count the discard summon as one probable starting original. A verified generated/deck-added origin takes precedence upstream.");
        var ability = sighting.Card.AbilityText ?? "";
        if (!_livePlayed.Contains(sighting.Card.Id) &&
            !ability.Contains("deck or graveyard", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(ability,
                @"\bSummon (?:self|this card) from (?:your |the )?deck\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new(CardProvenance.ProbableStartingDeck,
                "Recognized board arrival has an inherent self-summon-from-deck ability and no matching play preview; count one probable original. A verified generated/deck-added origin takes precedence upstream.");
        if (_opportunities.TryGetValue(sighting.Card.Id, out var trigger) &&
            evidence.ObservedAt >= trigger.At && evidence.ObservedAt - trigger.At < TimeSpan.FromSeconds(12) &&
            !_livePlayed.Contains(sighting.Card.Id))
            return new(CardProvenance.ProbableStartingDeck, $"Board arrival following {trigger.Trigger}; likely automatic summon, not another starting copy.");
        if (!_livePlayed.Contains(sighting.Card.Id) && sighting.Card.IsGold && sighting.Card.CanBeInStartingDeck &&
            !string.IsNullOrWhiteSpace(opponentFaction) && FactionCompatibility.IsPlayableBy(sighting.Card,opponentFaction))
            return new(CardProvenance.ProbableStartingDeck,
                "Faction-compatible gold identity repeatedly recognized on the opponent board after its preview was missed. Known generation, mutation and zone risks take precedence upstream; account one probable original only.");
        return null;
    }

    public ObservedStartingDeckAssessment Assess(IEnumerable<ObservedCard> observations)
    {
        var cards = observations.ToArray();
        var rules = StartingDeckRules.EvaluateObservedDeck(cards);
        var nonUnits = cards.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) && item.Card.CanBeInStartingDeck && item.Card.Kind != CardKind.Unit)
            .GroupBy(item => item.Card.Id).Sum(group => group.Max(item => item.ObservedCopies));
        if (StartingSize is { } exactSize && exactSize - nonUnits < 25)
            rules = rules with { Renfri = new("Renfri", ConstraintState.RuledOut,
                $"Known {exactSize}-card starting deck and {nonUnits} observed original non-unit(s) leave at most {exactSize - nonUnits} units; Renfri requires at least 25. Generated/stolen cards are excluded.") };
        else if (nonUnits > 0 && StartingSize is null && rules.Renfri.State == ConstraintState.Possible)
            rules = rules with { Renfri = rules.Renfri with { Reason = $"{nonUnits} original non-unit(s) observed: not Renfri-compatible at 25 cards. A {25 + nonUnits}+ card list is still possible because starting size was not read." } };
        if (_renfriDefinition is { } renfri)
        {
            var originals = cards.Where(item => item.Card.CanBeInStartingDeck && StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) &&
                item.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact).GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
            var units = originals.Where(item => item.Card.Kind == CardKind.Unit).Sum(item => item.ObservedCopies);
            var cost = originals.Sum(item => item.Card.Provision * item.ObservedCopies);
            var missingRenfri = originals.Any(item => item.Card.Id == renfri.Id) ? 0 : 1;
            var minimumRenfri = cost + missingRenfri * renfri.Provision + Math.Max(0, 25 - units - missingRenfri) * _unitProvisionFloor;
            var capacity = LeaderBonus is { } bonus ? 150 + bonus : _maximumStartingAllowance;
            var ordinaryMinimum = cost + Math.Max(0, (StartingSize ?? 25) - originals.Sum(item => item.ObservedCopies)) * _unitProvisionFloor;
            // Do not turn already inconsistent origin/copy data into another hard exclusion.
            if (ordinaryMinimum <= capacity && minimumRenfri > capacity)
                rules = rules with { Renfri = new("Renfri", ConstraintState.RuledOut,
                    $"An original Renfri plus at least 25 units and the observed originals would require ≥{minimumRenfri}p, exceeding the maximum {capacity}p allowance. Generated non-units are excluded; no assumed 25-card size is needed.") };
        }
        ConstraintAssessment Apply(DeckCondition key, ConstraintAssessment prior)
        {
            if (!_resolved.TryGetValue(key, out var fact)) return prior;
            if (fact.Suggested)
                return prior.State == ConstraintState.RuledOut ? prior with { Reason = prior.Reason + " Conflicting visual hint: " + fact.Evidence }
                    : new(prior.Name, ConstraintState.Likely, fact.Evidence);
            return new(prior.Name, prior.State == ConstraintState.RuledOut ? ConstraintState.Unknown : ConstraintState.Confirmed,
                prior.State == ConstraintState.RuledOut ? "CONFLICT: resolved effect disagrees with starting-card evidence. Review card origin/copies. " + fact.Evidence : fact.Evidence);
        }
        var four = cards.FirstOrDefault(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) &&
            item.Card.Provision == 4 && item.Card.Id != "202200");
        var musicians = Apply(DeckCondition.Musicians, new("Musicians", four is null ? ConstraintState.Possible : ConstraintState.RuledOut,
            four is null ? "Opening effect not established; Musicians itself is exempt from the 4p restriction." : $"{four.Card.Name} is another likely starting 4p card."));
        // Visual opening timing is not proof of full capture coverage. Keep it a hypothesis.
        if (musicians.State == ConstraintState.Confirmed && musicians.Reason.StartsWith("Musicians recognized", StringComparison.Ordinal))
            musicians = musicians with { State = ConstraintState.Likely };
        var singleton = Apply(DeckCondition.Singleton, rules.Shupe);
        var assessed = rules with { Shupe = singleton, Radeyah = singleton with { Name = "Radeyah" },
            GoldenNekker = Apply(DeckCondition.GoldenNekker, rules.GoldenNekker),
            Renfri = Apply(DeckCondition.Renfri, rules.Renfri), Devotion = Apply(DeckCondition.Devotion, rules.Devotion), Musicians = musicians };
        if (assessed.Devotion.State == ConstraintState.Confirmed && assessed.Renfri.State == ConstraintState.Confirmed)
        {
            var conflict = "CONFLICT: both Devotion and Renfri payoffs were resolved even though the starting-deck constructions are mutually exclusive. Review generated/copied origins and effect attribution.";
            assessed = assessed with { Devotion = new("Devotion", ConstraintState.Unknown, conflict),
                Renfri = new("Renfri", ConstraintState.Unknown, conflict) };
        }
        else if (assessed.Devotion.State is ConstraintState.Likely or ConstraintState.Confirmed &&
            assessed.Renfri.State is not ConstraintState.Confirmed)
            assessed = assessed with { Renfri = new("Renfri", ConstraintState.RuledOut,
                "Devotion is active only when the starting deck has no Neutral cards; starting-deck Renfri is Neutral, so the two constructions are mutually exclusive. " + assessed.Devotion.Reason) };
        else if (assessed.Renfri.State is ConstraintState.Likely or ConstraintState.Confirmed &&
            assessed.Devotion.State is not ConstraintState.Confirmed)
            assessed = assessed with { Devotion = new("Devotion", ConstraintState.RuledOut,
                "Renfri and Devotion are mutually exclusive starting-deck constructions because Renfri is Neutral. " + assessed.Renfri.Reason) };
        // Shupe payoff feasibility is distinct from singleton legality (Radeyah can cost 9p).
        if(_shupeProvisions>=10 && assessed.GoldenNekker.State is ConstraintState.Likely or ConstraintState.Confirmed)
            assessed = assessed with { Shupe = new("Shupe",ConstraintState.RuledOut,
                $"Golden Nekker's starting-deck restriction excludes Shupe's Day Off ({_shupeProvisions}p). A generated Shupe is still possible; singleton/Radeyah legality is separate.") };
        return assessed;
    }

    public int MinimumSize(IEnumerable<ObservedCard> observations)
    {
        var cards = observations.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance)).ToArray();
        var assessment = Assess(cards);
        var minimum = Math.Max(StartingSizeMinimum, cards.Sum(item => item.ObservedCopies));
        if (assessment.Renfri.State is ConstraintState.Confirmed or ConstraintState.Likely)
            minimum = Math.Max(minimum, 25 + cards.Where(item => item.Card.Kind != CardKind.Unit).Sum(item => item.ObservedCopies));
        return Math.Max(minimum, StartingSize ?? 25);
    }

    // Visible hand + draw pile are a lower bound only during verified setup. Generated
    // Daerlan copies can add up to four; unknown faction must allow that exception too.
    public void ObserveOpeningCounts(string? faction)
    {
        if (!HasOpeningWindow || Round != 1 || _anyLivePlay || OpponentHand != 10 || OpponentDrawPile is null ||
            (_lastHandAt - _lastDrawPileAt).Duration() > TimeSpan.FromSeconds(2)) return;
        var possibleAdded = faction is null || faction.Equals("Nilfgaard", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        StartingSizeMinimum = Math.Max(StartingSizeMinimum, OpponentHand.Value + OpponentDrawPile.Value - possibleAdded);
    }

    /// <summary>
    /// Recovers the starting size when recording begins just after the first play:
    /// cards still in hand + draw pile + independently observed committed originals
    /// must conserve the starting list. Two stable observations are required. Nilfgaard
    /// is excluded because Daerlan setup can add untracked deck copies.
    /// </summary>
    public bool ObserveStartingConservation(DateTimeOffset at, string? faction, int committedOriginalCopies)
    {
        if (StartingSize is not null || !_anyLivePlay || Ended || committedOriginalCopies < 1 ||
            faction?.Equals("Nilfgaard", StringComparison.OrdinalIgnoreCase) == true ||
            OpponentHand is null || OpponentDrawPile is null ||
            at < _lastOpponentPlayAt || at - _lastOpponentPlayAt < TimeSpan.FromSeconds(3) ||
            at - _lastHandAt > TimeSpan.FromSeconds(3) || at - _lastDrawPileAt > TimeSpan.FromSeconds(3)) return false;
        var size = OpponentHand.Value + OpponentDrawPile.Value + committedOriginalCopies;
        if (size is < 25 or > 100) { _conservedSize = null; return false; }
        if (_conservedSize is { } prior && prior.Size == size && at > prior.At && at - prior.At <= TimeSpan.FromSeconds(8))
        {
            var votes = prior.Votes + 1; _conservedSize = (size, at, votes);
            if (votes < 2) return false;
            StartingSize = size;
            StartingSizeEvidence = $"Conserved {OpponentHand} hand + {OpponentDrawPile} draw pile + {committedOriginalCopies} observed committed original(s) across stable HUD reads.";
            return true;
        }
        _conservedSize = (size, at, 1); return false;
    }

    public IReadOnlyList<OpponentClue> Clues(IEnumerable<ObservedCard> observations, DeckDefinition? pin,
        DeckMetaReport? meta, DateTimeOffset now)
    {
        var cards = observations.ToArray(); var rules = Assess(cards); var result = new List<OpponentClue>();
        foreach (var rule in new[] { rules.Shupe with { Name = "Shupe / Radeyah" }, rules.GoldenNekker, rules.Renfri, rules.Devotion, rules.Musicians! })
            if (rule.State is ConstraintState.Confirmed or ConstraintState.Likely || rule.Reason.StartsWith("CONFLICT"))
                result.Add(new(rule.Name, $"{rule.Name}: {rule.State.ToString().ToLowerInvariant()}", rule.Reason, rule.State == ConstraintState.Unknown ? 110 : 100));
        if (MinimumSize(cards) > 25) result.Add(new("size", $"At least {MinimumSize(cards)} starting slots", "Based on observed copies, opening counts or the 25-unit Renfri requirement. Current draw-pile size is not starting-deck size.", 95));
        foreach (var rule in DeckInteractionCatalog.All)
        {
            var observed = cards.Any(item => item.Card.Id == rule.CardId);
            var listed = pin?.CountOf(rule.CardId) > 0;
            var signal = meta?.Cards.FirstOrDefault(item => item.Card.Id == rule.CardId);
            var relevant = listed || signal is { ConditionalPresence: >= .55, SupportingDecks: >= 3 };
            if (_opportunities.TryGetValue(rule.CardId, out var trigger) &&
                (trigger.ExpectedCopies > 1 ? cards.Where(item => item.Card.Id == rule.CardId).Select(item => item.ObservedCopies).DefaultIfEmpty(0).Max() < trigger.ExpectedCopies : !_seen.Contains(rule.CardId)) && relevant &&
                now - trigger.At >= TimeSpan.FromSeconds(5) && now - trigger.At <= TimeSpan.FromSeconds(45))
            {
                var name = rule.Name + (trigger.ExpectedCopies > 1 ? " extra copy" : "");
                var text = trigger.ConditionsVerified && trigger.AbsenceVerified ?
                    (listed ? $"{name}: absent after trigger; possibly in hand" : $"{name}: absence weakens this candidate") :
                    $"{name}: not detected after {trigger.Trigger}";
                result.Add(new("trigger:" + rule.CardId, text,
                    rule.Trigger + " " + rule.Caution + (trigger.AbsenceVerified ? " Absence was explicitly reviewed." :
                        " Capture does not prove absence; this does not remove the card or change its probability."), listed ? 85 : 65));
            }
            else if (observed && rule.CardId is "203280" or "203278" or "162301" or "203042" or "202953" or "122318")
                result.Add(new(rule.CardId, rule.Name + ": special origin", rule.Trigger + " " + rule.Caution, 70));
        }
        return result.OrderByDescending(item => item.Priority).ThenBy(item => item.Key).ToArray();
    }
}

public sealed record OpponentProvisionBudget(int ObservedFloor, int? Capacity, int UnknownStartingSlots,
    int? UnaccountedCeiling, int? SingleUnknownCardCeiling, int? BigCardCeiling, bool Conflict, string Summary, string Detail);

public static class OpponentProvisionCalculator
{
    public static OpponentProvisionBudget Calculate(IEnumerable<ObservedCard> evidence, int? capacity,
        int minimumSize = 25, bool noOtherFours = false, int? round = null, int? hand = null,
        string capacitySource = "original leader", IEnumerable<CardDefinition>? catalog = null)
    {
        var cards = evidence.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) && item.Card.CanBeInStartingDeck &&
                item.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
        var floor = cards.Sum(item => item.Card.Provision * item.ObservedCopies);
        var unknown = Math.Max(0, minimumSize - cards.Sum(item => item.ObservedCopies));
        // Catalog-derived floor permits future balance changes below 4p.
        var minimum = catalog?.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(card => !noOtherFours || card.Provision != 4 || card.Id == "202200" && !cards.Any(item => item.Card.Id == card.Id))
            .Select(card => card.Provision).DefaultIfEmpty(noOtherFours ? 5 : 4).Min() ?? (noOtherFours ? 5 : 4);
        var remaining = capacity - floor;
        var conflict = remaining is < 0 || remaining is { } left && left < unknown * minimum;
        int? single = remaining is { } budget && unknown > 0 && !conflict ? budget - minimum * (unknown - 1) : null;
        int? big = remaining is { } limit && !conflict && minimum < 10 ? Math.Min(unknown, (limit - minimum * unknown) / (10 - minimum)) : null;
        var prefix = round == 3 ? "ROUND 3 · " : "";
        var summary = conflict ? prefix + "Provision evidence conflicts — review origins / leader / copies" :
            remaining is null ? prefix + $"{floor}p accounted · remaining allowance unknown" :
                prefix + $"≤{remaining}p unaccounted · {unknown} unidentified starting slots";
        if (hand == 1 && round == 3 && !conflict && single is { } max)
            summary += max < 10 ? $" · any unidentified original card ≤{max}p" : " · a 10+p last card is still possible";
        var detail = $"{floor}p likely starting-card floor. " + (capacity is null ? "Confirm the original leader or pin a reference to set an allowance. " : $"Capacity {capacity}p from {capacitySource}. ") +
            "Unaccounted means ALL unidentified starting cards, including cards still in the deck, discarded or banished, and unused allowance. Not hand value or points. " +
            (single is null ? "" : $"Each unidentified original card is at most {single}p after reserving at least {minimum}p per other unknown slot; at most {big} unidentified 10+p originals. ") +
            "A previously seen/replayed, generated or transformed last card is not bounded by this estimate. Guesses/manual picks are not deducted.";
        return new(floor, capacity, unknown, conflict ? null : remaining, single, big, conflict, summary, detail);
    }
}
