using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public enum VisibleCardZone { Graveyard, Deck }
public enum ZoneEntryRoute { Unknown, StartingOriginal, Generated, DiscardedUnknownOrigin, HeulynSetup, RioghanSetup, ReturnedToDeck }
public sealed record ZoneCardEvidence(string Key, CardDefinition Card, PlayerSide? Side, VisibleCardZone Zone,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, ZoneEntryRoute Route, string Evidence);

/// <summary>A partial inspection log, not a live zone inventory. Repeated/scrolling sightings never add copies.</summary>
public sealed class ZoneEvidenceLedger
{
    private readonly Dictionary<string, ZoneCardEvidence> _entries = [];
    public IReadOnlyCollection<ZoneCardEvidence> Entries => _entries.Values;
    public void Reset() => _entries.Clear();
    public ZoneCardEvidence Record(CardDefinition card, PlayerSide? side, VisibleCardZone zone, DateTimeOffset at,
        ZoneEntryRoute route = ZoneEntryRoute.Unknown, string evidence = "Visible inspection; entry route and origin unverified.")
    {
        var key = $"{side}/{zone}/{card.Id}";
        if (_entries.TryGetValue(key, out var old))
        {
            // A later unknown observation cannot erase an explicit review.
            if (route == ZoneEntryRoute.Unknown) { route = old.Route; evidence = old.Evidence; }
            var update = old with { LastSeen = at > old.LastSeen ? at : old.LastSeen, Route = route, Evidence = evidence };
            _entries[key] = update; return update;
        }
        var entry = new ZoneCardEvidence(key, card, side, zone, at, at, route, evidence);
        if (_entries.Count < 256) _entries[key] = entry;
        return entry;
    }
    public void Remove(string key) => _entries.Remove(key);
    public PlayOriginAssessment? OriginRisk(CardSighting sight, IEnumerable<ObservedCard> prior)
    {
        // Identity-level evidence may describe both a generated copy and an original copy.
        // Preserve an independently established original; never mark every Human generated.
        if (prior.Any(item => item.Card.Id == sight.Card.Id && StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))) return null;
        var entries = Entries.Where(item => item.Side == sight.Side && item.Card.Id == sight.Card.Id).ToArray();
        if (entries.Any(item => item.Route is ZoneEntryRoute.StartingOriginal or ZoneEntryRoute.RioghanSetup))
            return new(CardProvenance.ProbableStartingDeck, "Reviewed original seen in a zone; one identity lower bound, not another copy/play.");
        if (entries.Any(item => item.Route is ZoneEntryRoute.Generated or ZoneEntryRoute.HeulynSetup))
            return new(CardProvenance.Unknown, "This identity was also observed as generated in a zone. Later play may be a resurrection or a separate original; origin unresolved.");
        return null;
    }
}

public enum TrapOpportunity { FaceUpCard, UnitOnOwnSide, Special, Pass, TwoOwnerTurnEnds, ThreeOwnerTurnEnds, LowPowerUnit }
public sealed record HiddenTrapEvidence(int Number, DateTimeOffset SetAt, IReadOnlyList<TrapOpportunity> Survived,
    string? RevealedId = null, bool Removed = false);
public sealed record TrapCandidate(CardDefinition Card, bool Weakened, string Reason);
public sealed class HiddenTrapLedger
{
    private readonly List<HiddenTrapEvidence> _traps = [];
    public IReadOnlyList<HiddenTrapEvidence> Entries => _traps;
    public void Reset() => _traps.Clear();
    public void Add(DateTimeOffset at) { if (_traps.Count < 50) _traps.Add(new(_traps.Count + 1, at, [])); }
    public void Survived(int number, TrapOpportunity opportunity)
    {
        var index = _traps.FindIndex(item => item.Number == number && item.RevealedId is null && !item.Removed);
        if (index >= 0) _traps[index] = _traps[index] with { Survived = _traps[index].Survived.Append(opportunity).Distinct().ToArray() };
    }
    public void Reveal(int number, CardDefinition card)
    {
        if (!card.HasCategory("Trap")) throw new ArgumentException("An exact Trap identity is required.");
        var index = _traps.FindIndex(item => item.Number == number);
        if (index >= 0) _traps[index] = _traps[index] with { RevealedId = card.Id };
    }
    public void Remove(int number)
    { var index = _traps.FindIndex(item => item.Number == number); if (index >= 0) _traps[index] = _traps[index] with { Removed = true }; }
    public IReadOnlyList<TrapCandidate> Candidates(int number, IEnumerable<CardDefinition> catalog)
    {
        var trap = _traps.Single(item => item.Number == number);
        return catalog.Where(card => card.HasCategory("Trap") && card.CanBeInStartingDeck && (trap.RevealedId is null || trap.RevealedId == card.Id))
            .Select(card =>
            {
                TrapOpportunity? trigger = card.Id switch { "143201" => TrapOpportunity.Special, "143301" => TrapOpportunity.UnitOnOwnSide,
                    "200228" => TrapOpportunity.FaceUpCard, "201645" => TrapOpportunity.TwoOwnerTurnEnds, "201653" => TrapOpportunity.Pass,
                    "203253" => TrapOpportunity.ThreeOwnerTurnEnds, "203254" => TrapOpportunity.LowPowerUnit, _ => null };
                var weak = trap.RevealedId is null && trigger is { } value && trap.Survived.Contains(value);
                return new TrapCandidate(card, weak, weak ? "Survived a reviewed trigger: less likely, not excluded. Check lock, timing and capture continuity." : "Possible; elapsed wall time alone does not identify a trap.");
            }).OrderBy(item => item.Weakened).ThenBy(item => item.Card.Name).ToArray();
    }
}
