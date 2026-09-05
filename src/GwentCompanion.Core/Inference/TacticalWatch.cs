using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public enum TacticalCondition { Unknown, Dwarf, DwarfOrCrownsplitter, FiveElves, Dominance, EnemyStatus, Hoard4, Hoard9, Bloodthirst3, Crew, FrostBothRows, RainBothRows, LeaderSpent, TenOrders }
public enum TacticalState { Unknown, NotMet, Ready, Pending, Missed, Seen, Unavailable }
public enum WatchReview { Missed, Arrived, Unavailable }
public sealed record TacticalRule(CardDefinition Card, string Requirement, TacticalCondition Condition,
    bool Summon, bool SelfArrival = false, int ExpectedCopies = 1, bool Random = false,
    string? Synergy = null, bool LocalCondition = false)
{
    public string Key => Card.Id + "|" + (Summon ? "summon" : Synergy ?? "bonus");
}
public sealed record TacticalWatchRow(TacticalRule Rule, TacticalState State, string Label, string Detail,
    bool Promoted, string Promotion, bool OutsideFaction);
public sealed record TacticalReport(IReadOnlyList<TacticalWatchRow> Summons, IReadOnlyList<TacticalWatchRow> Plays);

/// <summary>Fresh positive presence, never an inventory of everything played earlier.</summary>
public sealed class CurrentBoardPresence
{
    private List<(CardSighting Sighting, int Votes)> _current = [];
    private DateTimeOffset _at;
    private DateTimeOffset _lastFrame;
    public DateTimeOffset At => _at;
    public void Reset(bool newTimeline = false) { _current.Clear(); _at = default; if (newTimeline) _lastFrame = default; }
    public void Observe(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings, bool scanned,
        bool allowUnobscuredDuringPreview = false)
    {
        if (at <= _lastFrame) return;
        _lastFrame = at;
        if (screen.View != GwentViewKind.Board || screen.IsCardSelectionOverlay || screen.HasCardTooltip && screen.TooltipRegion is null ||
            !allowUnobscuredDuringPreview && sightings.Any(item => item.Source == CardSightSource.PlayPreview))
        { Reset(); return; }
        if (!scanned) return;
        var previous = at - _at <= TimeSpan.FromSeconds(8) ? _current : [];
        var distinct = new List<CardSighting>();
        foreach (var sight in sightings.Where(item => item.Source == CardSightSource.Board && item.Card.Kind == CardKind.Unit &&
                     (!allowUnobscuredDuringPreview || !PreviewCovers(item.Region, sightings)) &&
                     item.Distance <= .4 && !(screen.HasCardTooltip && screen.TooltipRegion is {} tip &&
                         item.Region.Left < tip.Right+.02 && item.Region.Right > tip.Left-.02 &&
                         item.Region.Top < tip.Bottom+.02 && item.Region.Bottom > tip.Top-.12)).OrderBy(item => item.Distance))
            if (!distinct.Any(other => other.Side == sight.Side && Near(other.Region, sight.Region))) distinct.Add(sight);
        _current = distinct.Take(36).Select(sight => (sight,
            previous.Any(item => item.Sighting.Side == sight.Side && item.Sighting.Card.Id == sight.Card.Id && Near(item.Sighting.Region, sight.Region)) ? 2 : 1)).ToList();
        _at = at;
    }
    public IReadOnlyList<CardSighting> Confirmed(DateTimeOffset now) => now >= _at && now - _at <= TimeSpan.FromSeconds(8)
        ? _current.Where(item => item.Votes >= 2).Select(item => item.Sighting).ToArray() : [];
    public int CountOf(string id, DateTimeOffset now) => Confirmed(now).Count(item => item.Side == PlayerSide.Opponent && item.Card.Id == id);
    public bool Positive(TacticalCondition condition, DateTimeOffset now)
    {
        var units = Confirmed(now).Where(item => item.Side == PlayerSide.Opponent).ToArray();
        return condition switch
        {
            TacticalCondition.Dwarf => units.Any(item => item.Card.HasCategory("Dwarf")),
            TacticalCondition.DwarfOrCrownsplitter => units.Any(item => item.Card.HasCategory("Dwarf") || item.Card.HasCategory("Crownsplitters")),
            TacticalCondition.FiveElves => units.Count(item => item.Card.HasCategory("Elf")) >= 5,
            _ => false // Printed base power, old plays and faction are NOT current power / weather / coins.
        };
    }
    private static bool Near(NormalizedRegion a, NormalizedRegion b) =>
        Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2) < .025 && Math.Abs((a.Top + a.Bottom - b.Top - b.Bottom) / 2) < .04;

    // A preview and its adjacent description can hide the right end of a row, not
    // the entire board. Use only positive, separate board evidence outside that area.
    internal static bool PreviewCovers(NormalizedRegion region, IReadOnlyList<CardSighting> sightings) =>
        sightings.Where(s => s.Source == CardSightSource.PlayPreview).Any(s =>
            region.Right > s.Region.Left - .22 && region.Left < s.Region.Right + .02 &&
            region.Bottom > s.Region.Top - .02 && region.Top < s.Region.Bottom + .08);
}

/// <summary>Presentation/evidence state only: colors never rewrite original cards or deck probabilities.</summary>
public sealed class TacticalWatch
{
    public CurrentBoardPresence Board { get; } = new();
    private readonly TacticalRule[] _summons;
    private readonly TacticalRule[] _plays;
    private readonly Dictionary<TacticalCondition, (bool Value, DateTimeOffset At)> _manual = [];
    private readonly Dictionary<string, (bool Value, DateTimeOffset At)> _localConditions = [];
    private readonly Dictionary<string, (WatchReview Review, DateTimeOffset At)> _reviews = [];
    private readonly Dictionary<string, DateTimeOffset> _seen = [];
    private readonly HashSet<string> _kept = [];
    private readonly Dictionary<string, DateTimeOffset> _returned = [];
    private readonly Dictionary<(PlayerSide Side, string CardId), int> _inventory = [];
    private readonly Dictionary<string, DateTimeOffset> _graveDepartures = [];
    private readonly Dictionary<string, DateTimeOffset> _resolvedSummons = [];
    private DateTimeOffset _lastPlay;
    private (DateTimeOffset At, string Source)? _recovery;
    private int? _round;
    private string? _linkKey;
    private Dictionary<string, string> _leaderLinks = [];

    public TacticalWatch(IEnumerable<CardDefinition> catalog)
    {
        var cards = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        _summons = DeckInteractionCatalog.All.Where(rule => cards.TryGetValue(rule.CardId, out var card) && card.CanBeInStartingDeck &&
                (Regex.IsMatch(card.AbilityText ?? "", @"\bSummon\b") || card.Id == "202397"))
            .Select(rule =>
            {
                var card = cards[rule.CardId];
                var pair = Regex.IsMatch(card.AbilityText ?? "", @"Summon all copies", RegexOptions.IgnoreCase);
                var self = pair || Regex.IsMatch(card.AbilityText ?? "", @"Summon (self|this card|Crowmother)", RegexOptions.IgnoreCase) || card.Id == "202397";
                return new TacticalRule(card, rule.Trigger + " " + rule.Caution, ConditionFor(card.Id), true, self, pair ? 2 : 1, card.Id == "202397");
            }).ToArray();
        var plays = new List<TacticalRule>();
        void Add(string id, string text, TacticalCondition condition)
        { if (cards.TryGetValue(id, out var card)) plays.Add(new(card, text, condition, false)); }
        Add("202384", "Extra Cleaver's Muscle: already control a Dwarf OR Crownsplitter. The tutor itself remains usable without this bonus; deck target and melee space are unverified.", TacticalCondition.DwarfOrCrownsplitter);
        Add("201559", "Dwarf condition for the deploy summon. A matching copy must still be in deck and the row must have space.", TacticalCondition.Dwarf);
        Add("132310", "Dominance at deploy resolution. Uses CURRENT power, including the played Rider; matching deck copies and row space remain unverified.", TacticalCondition.Dominance);
        Add("202549", "An enemy unit has a status at resolution. Matching deck copies and row space remain unverified.", TacticalCondition.EnemyStatus);
        Add("202334", "Hoard 4 at deploy resolution. A matching deck copy and row space remain unverified.", TacticalCondition.Hoard4);
        foreach (var card in cards.Values.Where(card => card.CanBeInStartingDeck && card.Kind == CardKind.Unit &&
                     card.AbilityText?.Contains("Dominance:", StringComparison.Ordinal) == true && card.Id != "132310"))
            plays.Add(new(card, "Dominance bonus: control a highest-current-power unit. Other row, target, timing and ability requirements are not asserted.", TacticalCondition.Dominance, false));
        foreach (var card in cards.Values.Where(card => card.CanBeInStartingDeck &&
                     (card.AbilityText?.Contains("Bloodthirst 3", StringComparison.Ordinal) == true || card.AbilityText?.Contains("Crew:", StringComparison.Ordinal) == true)))
            plays.Add(new(card, card.AbilityText!.Contains("Bloodthirst 3", StringComparison.Ordinal)
                ? "Bloodthirst 3: at least three currently damaged enemy units. Other requirements remain unverified."
                : "Crew bonus: the relevant unit is between two Soldiers; category counts alone cannot establish adjacency.",
                card.AbilityText.Contains("Bloodthirst 3", StringComparison.Ordinal) ? TacticalCondition.Bloodthirst3 : TacticalCondition.Crew, false));
        var synergies = TacticalSynergies.Rules(cards.Values).ToArray();
        _plays = plays.Where(rule => !synergies.Any(other => other.Card.Id == rule.Card.Id &&
                (other.Condition == rule.Condition || rule.Condition == TacticalCondition.Crew && other.Synergy == "Crew")))
            .Concat(synergies).DistinctBy(rule => rule.Key).ToArray();
    }

    public void Reset()
    { Board.Reset(true); _manual.Clear(); _localConditions.Clear(); _reviews.Clear(); _seen.Clear(); _kept.Clear(); _returned.Clear();
        _inventory.Clear(); _graveDepartures.Clear(); _resolvedSummons.Clear(); _lastPlay = default; _round = null; _recovery = null; }
    public void ObserveEvent(VisionEvidenceEvent evidence)
    {
        if (evidence.Sighting.Source == CardSightSource.PlayPreview)
        {
            if (evidence.ObservedAt > _lastPlay) { _lastPlay = evidence.ObservedAt; _manual.Clear(); _localConditions.Clear(); Board.Reset(); }
        }
        if (evidence.Sighting.Side == PlayerSide.Opponent)
        {
            if (evidence.Sighting.Source != CardSightSource.History) _seen[evidence.Sighting.Card.Id] = evidence.ObservedAt;
            var ability = evidence.Sighting.Card.AbilityText ?? "";
            if (evidence.Sighting.Source == CardSightSource.PlayPreview &&
                Regex.IsMatch(ability, @"\b(Play|Summon|Shuffle|Move)\b[^.\n]*\b(from|in) (your |a |the )?graveyard\b", RegexOptions.IgnoreCase))
                _recovery = (evidence.ObservedAt, evidence.Sighting.Card.Name);
        }
    }
    public void ObserveFrame(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings, bool scanned, int? round)
    {
        if (round is not null && _round is not null && round != _round) { Board.Reset(); _manual.Clear(); _localConditions.Clear(); }
        _round = round ?? _round;
        Board.Observe(at, screen, sightings, scanned);
        foreach (var sight in Board.Confirmed(at).Where(item => item.Side == PlayerSide.Opponent)) _seen[sight.Card.Id] = at;
    }
    public void ObserveInventory(IEnumerable<ZoneHypothesis> inventory, DateTimeOffset at)
    {
        var current = inventory.Where(entry => entry.Zone == CardZone.Graveyard)
            .GroupBy(entry => (entry.Side, entry.CardId)).ToDictionary(group => group.Key, group => group.Max(entry => entry.Copies));
        foreach (var key in _inventory.Keys.Union(current.Keys).ToArray())
        {
            var before = _inventory.GetValueOrDefault(key); var after = current.GetValueOrDefault(key);
            if (key.Side == PlayerSide.Opponent && before > after) _graveDepartures[key.CardId] = at;
            _inventory[key] = after;
        }
        if (_graveDepartures.GetValueOrDefault("203119") is { } toadAt && toadAt != default && at - toadAt <= TimeSpan.FromSeconds(20) &&
            Board.CountOf("203119", at) > 0) _resolvedSummons["203119"] = at;
        if (_seen.GetValueOrDefault("202995") is { } mammunaAt && mammunaAt != default && at >= mammunaAt && at - mammunaAt <= TimeSpan.FromSeconds(30) &&
            new[] { "132307", "112405" }.Any(id => _graveDepartures.GetValueOrDefault(id) is { } left && left >= mammunaAt &&
                at - left <= TimeSpan.FromSeconds(20) && Board.CountOf(id, at) > 0))
            _resolvedSummons["202995"] = at;
    }
    public void SetCondition(TacticalCondition condition, bool? value, DateTimeOffset at)
    {
        if (condition == TacticalCondition.Unknown) return;
        if (value is null) _manual.Remove(condition); else _manual[condition] = (value.Value, at);
    }
    public void SetRuleCondition(TacticalRule rule, bool? value, DateTimeOffset at)
    {
        if (!rule.LocalCondition) { SetCondition(rule.Condition, value, at); return; }
        if (value is null) _localConditions.Remove(rule.Key); else _localConditions[rule.Key] = (value.Value, at);
    }
    public void Review(string id, WatchReview? review, DateTimeOffset at)
    {
        if (review is null) _reviews.Remove(id);
        else { _reviews[id] = (review.Value, at); _returned.Remove(id); }
    }
    public void Keep(string id, bool keep) { if (keep) _kept.Add(id); else _kept.Remove(id); }
    public bool IsKept(string id) => _kept.Contains(id);
    public void ReturnedToDeck(string id, DateTimeOffset at) { _returned[id] = at; _reviews.Remove(id); }
    public void ClearReturn(string id) => _returned.Remove(id);
    public PlayOriginAssessment? ArrivalOrigin(VisionEvidenceEvent evidence, IEnumerable<TriggerOpportunity> opportunities)
    {
        if (evidence.Sighting.Side != PlayerSide.Opponent || evidence.Sighting.Source != CardSightSource.Board) return null;
        var rule = _summons.FirstOrDefault(rule => rule.Card.Id == evidence.Sighting.Card.Id && rule.SelfArrival);
        if (rule is null) return null;
        var at = evidence.ObservedAt;
        var supported = opportunities.Any(item => item.CardId == rule.Card.Id && at >= item.At && at - item.At <= TimeSpan.FromSeconds(12)) ||
            Board.Positive(rule.Condition, at) || _manual.TryGetValue(rule.Condition, out var fact) && fact.Value && at >= fact.At && at - fact.At <= TimeSpan.FromSeconds(30);
        return supported ? new(CardProvenance.ProbableStartingDeck,
            "Board arrival with corroborating summon condition; probable original identity, not another play/copy. Acquired/generated risks take precedence.") : null;
    }

    public TacticalReport Build(string? faction, string? leader, DeckDefinition? pin, IEnumerable<CardDefinition> assumed,
        IEnumerable<ObservedCard> observations, DeckMetaReport? meta, IEnumerable<DeckDefinition> decks,
        IEnumerable<TriggerOpportunity> opportunities, DateTimeOffset now, IEnumerable<ZoneHypothesis>? inventory = null,
        IEnumerable<SummonCandidateEvidence>? summonEvidence = null)
    {
        var seen = observations.GroupBy(item => item.Card.Id).ToDictionary(group => group.Key, group => group.First());
        var picks = assumed.Select(card => card.Id).ToHashSet();
        var effectiveFaction = faction ?? pin?.Faction;
        var effectiveLeader = leader ?? pin?.Leader;
        var corpus = decks.ToArray();
        var graves = (inventory ?? []).Where(entry => entry.Side == PlayerSide.Opponent && entry.Zone == CardZone.Graveyard)
            .GroupBy(entry => entry.CardId).ToDictionary(group => group.Key, group => group.Max(entry => entry.Copies));
        var key = effectiveFaction + "|" + effectiveLeader + "|" + string.Join(';', corpus.Select(deck => deck.Id + ":" + deck.LastEdited));
        if (key != _linkKey) { _linkKey = key; _leaderLinks = LeaderLinks(corpus, effectiveFaction, effectiveLeader); }
        var triggers = opportunities.GroupBy(item => item.CardId).ToDictionary(group => group.Key, group => group.MaxBy(item => item.At)!);
        var softMisses = (summonEvidence ?? []).GroupBy(item => item.CardId)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.Opportunities)!);
        TacticalWatchRow? Row(TacticalRule rule)
        {
            var card = rule.Card; var listed = pin?.CountOf(card.Id) > 0;
            var outside = !string.IsNullOrWhiteSpace(effectiveFaction) && !FactionCompatibility.IsPlayableBy(card, effectiveFaction);
            if (outside && !listed && !picks.Contains(card.Id) && !seen.ContainsKey(card.Id) && !_kept.Contains(card.Id)) return null;
            var promotion = _kept.Contains(card.Id) ? "Kept visible" : listed ? "Pinned list (assumption)" : picks.Contains(card.Id) ? "Your card assumption" : "";
            if (promotion.Length == 0 && _leaderLinks.TryGetValue(card.Id, out var linked)) promotion = linked;
            if (promotion.Length == 0 && meta is { BestObservedCoverage: >= .5 } &&
                meta.Cards.FirstOrDefault(item => item.Card.Id == card.Id) is { ConditionalPresence: >= .80, SupportingDecks: >= 4, StrategyLinked: true } signal)
                promotion = $"Card-linked sample: {signal.ConditionalPresence:P0}, {signal.SupportingDecks} lists (not certainty)";
            var state = TacticalState.Unknown; var label = "? Unknown";
            var detail = rule.Requirement;
            var current = Board.CountOf(card.Id, now);
            var arrived = rule.SelfArrival && (current >= rule.ExpectedCopies || rule.ExpectedCopies == 1 &&
                (seen.ContainsKey(card.Id) || _seen.ContainsKey(card.Id)));
            if (arrived) { state = TacticalState.Seen; label = "● Seen"; detail += " Seen does not establish original-deck origin or current hand/deck location."; }
            if (_reviews.TryGetValue(card.Id, out var review) && rule.Summon)
            {
                var seenAfter = rule.SelfArrival && _seen.GetValueOrDefault(card.Id) > review.At &&
                    (rule.ExpectedCopies == 1 || current >= rule.ExpectedCopies);
                if (seenAfter && review.Review == WatchReview.Missed) { state = TacticalState.Seen; label = "● Seen after review"; }
                else if (review.Review == WatchReview.Unavailable) { state = TacticalState.Unavailable; label = "— Not expected"; }
                else if (review.Review == WatchReview.Arrived) { state = TacticalState.Seen; label = "● Arrived (reviewed)"; }
                else if (review.Review == WatchReview.Missed && !rule.Random)
                {
                    state = TacticalState.Missed; label = "! Missed (reviewed)";
                    detail += " You verified the trigger, timing, available row space and no arrival. In hand / not in deck / moved or removed are possible; not a hard exclusion.";
                    if (current >= rule.ExpectedCopies && rule.SelfArrival) { state = TacticalState.Pending; label = "! Review conflict"; }
                }
            }
            else if (rule.Summon && !arrived && triggers.TryGetValue(card.Id, out var trigger) && now >= trigger.At && now - trigger.At < TimeSpan.FromSeconds(45))
            {
                state = trigger.ConditionsVerified && trigger.AbsenceVerified && !rule.Random ? TacticalState.Missed : TacticalState.Pending;
                label = state == TacticalState.Missed ? "Missed" : "Trigger seen";
                detail += state == TacticalState.Missed ? " Absence explicitly reviewed; location remains uncertain." : " Trigger seen, but condition/coverage or absence is not verified. No red exclusion from a missed detection.";
            }
            // Automated coverage can support a durable amber caution, but only a
            // player review may turn an absence red. This keeps Roach visible from
            // the first covered missed gold instead of losing it after the 45s trigger window.
            if (card.Id == "112210" && !arrived && state is not (TacticalState.Missed or TacticalState.Unavailable or TacticalState.Seen) &&
                softMisses.TryGetValue(card.Id, out var softMiss))
            {
                state = TacticalState.Pending; label = $"Gold missed ×{softMiss.Opportunities}";
                detail += " " + softMiss.Reason + " Amber is a location/deck caution, not proof that Roach is absent.";
                if (promotion.Length == 0) promotion = "Covered missed gold play";
            }
            if (!rule.Summon || state == TacticalState.Unknown)
            {
                bool? met = null; var source = "";
                if (rule.LocalCondition && _localConditions.TryGetValue(rule.Key, out var local) && now >= local.At && now - local.At <= TimeSpan.FromSeconds(30))
                { met = local.Value; source = "user-confirmed for this card/condition only; expires after 30s or the next play"; }
                else if (!rule.LocalCondition && _manual.TryGetValue(rule.Condition, out var fact) && now >= fact.At && now - fact.At <= TimeSpan.FromSeconds(30))
                { met = fact.Value; source = "user-confirmed; expires after 30s or the next play"; }
                else if (Board.Positive(rule.Condition, now)) { met = true; source = "fresh board identity in two scans"; }
                if (met is not null)
                {
                    state = met.Value ? rule.Summon ? TacticalState.Pending : TacticalState.Ready : TacticalState.NotMet;
                    label = met.Value ? "Condition met" : "Not met now";
                    detail += " Condition source: " + source + ".";
                }
                else detail += " Current condition unverified. Missing board detections and printed base power are not negative/current-power evidence.";
            }
            if (rule.Summon && card.Id == "203119" && state is not (TacticalState.Missed or TacticalState.Unavailable))
            {
                if (_resolvedSummons.ContainsKey(card.Id) || _reviews.GetValueOrDefault(card.Id).Review == WatchReview.Arrived)
                { state = TacticalState.Seen; label = "✓ Returned"; detail += " Giant Toad was observed leaving the graveyard and returning to the board."; }
                else if (graves.GetValueOrDefault(card.Id) > 0)
                { state = TacticalState.Ready; label = "✓ In graveyard"; detail += " The stored opponent graveyard inventory currently contains Giant Toad, so its Deathwish trigger is live."; }
                else if (state == TacticalState.Seen)
                { state = TacticalState.Unknown; label = "? Graveyard unverified"; }
            }
            if (rule.Summon && card.Id == "202995" && state is not (TacticalState.Missed or TacticalState.Unavailable))
            {
                if (_resolvedSummons.ContainsKey(card.Id) || _reviews.GetValueOrDefault(card.Id).Review == WatchReview.Arrived)
                { state = TacticalState.Seen; label = "✓ Order resolved"; detail += " A Griffin/Fiend graveyard departure and matching board arrival followed Mammuna."; }
                else
                {
                    var targets = new[] { (Id: "132307", Name: "Griffin"), (Id: "112405", Name: "Fiend") };
                    var ready = targets.Where(target => graves.GetValueOrDefault(target.Id) == 1 && Board.CountOf(target.Id, now) == 0).ToArray();
                    bool Exhausted((string Id, string Name) target)
                    {
                        var grave = graves.GetValueOrDefault(target.Id); var board = Board.CountOf(target.Id, now);
                        return grave >= 2 || grave >= 1 && board > 0 || _round == 3 && grave == 0;
                    }
                    if (ready.Length > 0)
                    {
                        state = TacticalState.Ready; label = "✓ " + string.Join(" / ", ready.Select(target => target.Name));
                        detail += " Exactly one matching bronze is recorded in the opponent graveyard and none is currently recognized on board; the paired copy can remain in deck.";
                    }
                    else if (_round == 3 && targets.All(Exhausted))
                    {
                        state = TacticalState.NotMet; label = "— No live Griffin / Fiend line";
                        detail += " Round 3 inventory has no eligible paired line: zero graveyard copies, two graveyard copies, or a graveyard copy plus its matching board copy exhausts each tracked target.";
                    }
                }
            }
            if (rule.Random) detail += " Random non-arrival never becomes red evidence.";
            if (state == TacticalState.Seen)
            {
                var recurring = rule.Card.AbilityText?.Contains("graveyard", StringComparison.OrdinalIgnoreCase) == true || !rule.SelfArrival;
                detail += recurring ? " This interaction can occur again if its source, target and trigger are available; blue is not permanently spent."
                    : " No fresh summon from the draw pile is expected for that seen copy unless it returns there. Additional copies, generated copies and returns remain possible.";
                var seenAt = _seen.GetValueOrDefault(card.Id);
                if (_reviews.TryGetValue(card.Id, out var arrivedReview) && arrivedReview.Review == WatchReview.Arrived && arrivedReview.At > seenAt) seenAt = arrivedReview.At;
                var canReopen = card.Id == "203217"; // Anglerfish is the deliberately reusable summon watch.
                if (canReopen && rule.Summon && rule.SelfArrival && promotion.Length > 0 && current == 0 && _recovery is { } recovery &&
                    recovery.At > seenAt && now >= recovery.At)
                {
                    state = now - recovery.At < TimeSpan.FromSeconds(30) ? TacticalState.Pending : TacticalState.Unknown;
                    label = state == TacticalState.Pending ? "Recovery seen" : "Return not seen";
                    detail += $" {recovery.Source} was played after this card was seen. Its target/resolution is unknown: recheck only, not a confirmed return.";
                }
                else if (canReopen && rule.Summon && current == 0 && triggers.TryGetValue(card.Id, out var next) && next.At > seenAt && now >= next.At && now - next.At < TimeSpan.FromSeconds(45))
                { state = TacticalState.Pending; label = "New trigger"; detail += " A later trigger reopened the watch; location and availability remain unverified."; }
            }
            if (outside) detail += " Outside the inferred faction: retained because of explicit/observed evidence, not assumed original membership.";
            if (rule.Summon && card.Id == "203217" && _returned.TryGetValue(card.Id, out var returned) && returned >= _seen.GetValueOrDefault(card.Id))
            {
                state = TacticalState.Pending; label = "↩ Returned to deck";
                detail += " Return target explicitly reviewed. This is a location update, not another starting copy or provision charge.";
                if (promotion.Length == 0) promotion = "Verified return target";
            }
            if (state == TacticalState.Missed && promotion.Length == 0) promotion = "Reviewed missed opportunity";
            return new(rule, state, label, detail, promotion.Length > 0, promotion, outside);
        }
        TacticalWatchRow[] Rows(IEnumerable<TacticalRule> rules) => rules.Select(Row).OfType<TacticalWatchRow>()
            .OrderByDescending(row => row.State == TacticalState.Missed).ThenByDescending(row => row.Promoted).ThenBy(row => row.Rule.Card.Name).ToArray();
        return new(Rows(_summons), Rows(_plays));
    }

    private static TacticalCondition ConditionFor(string id) => id switch
    {
        "201559" => TacticalCondition.Dwarf, "132310" => TacticalCondition.Dominance, "202549" => TacticalCondition.EnemyStatus,
        "202334" => TacticalCondition.Hoard4, "202367" => TacticalCondition.Hoard9, "202883" => TacticalCondition.Bloodthirst3,
        "142211" => TacticalCondition.FiveElves, "202608" => TacticalCondition.FrostBothRows, "203217" => TacticalCondition.RainBothRows,
        "202445" or "203282" => TacticalCondition.LeaderSpent, "200088" => TacticalCondition.TenOrders, _ => TacticalCondition.Unknown
    };
    private static Dictionary<string, string> LeaderLinks(IEnumerable<DeckDefinition> decks, string? faction, string? leader)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(leader)) return result;
        if (leader == "White Frost" && faction is null or "Monsters") result["202608"] = "White Frost synergy (not proof)";
        if (leader == "Rage of the Sea" && faction is null or "Skellige") result["203217"] = "Rage of the Sea synergy (not proof)";
        var corpus = decks.Where(deck => deck.CardCount >= 25 && (string.IsNullOrWhiteSpace(faction) || deck.Faction == faction))
            .GroupBy(deck => deck.Faction + "|" + deck.Leader + "|" + string.Join(',', deck.Cards.OrderBy(item => item.Card.Id).Select(item => item.Card.Id + ":" + item.Count)))
            .Select(group => group.OrderByDescending(deck => deck.LastEdited).First())
            .OrderByDescending(deck => deck.LastEdited).ThenBy(deck => deck.RecencyRank).Take(100).ToArray();
        var matching = corpus.Where(deck => deck.Leader.Equals(leader, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length < 3) return result;
        foreach (var card in matching.SelectMany(deck => deck.Cards).Select(item => item.Card).DistinctBy(card => card.Id))
        {
            var count = matching.Count(deck => deck.CountOf(card.Id) > 0);
            var rate = (count + 1d) / (matching.Length + 2);
            var baseline = (corpus.Count(deck => deck.CountOf(card.Id) > 0) + 1d) / (corpus.Length + 2);
            if (count >= 3 && rate >= .65 && rate / baseline >= 1.35)
                result.TryAdd(card.Id, $"Leader-linked sample: {count}/{matching.Length} {leader} lists, lift ×{rate / baseline:F1}");
        }
        return result;
    }
}
