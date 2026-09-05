using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public sealed record OpponentSequenceEvidence(string RuleKey, DateTimeOffset At, int Round, double Confidence, string Reason);
public sealed record OpponentSequenceRule(string Key, string Faction, string? Leader, string TargetCardId,
    string[] RequiredOriginalCards, double Factor, string SourceUrl, string SourceDate);

/// <summary>Expert hypotheses from reviewed archive passages, NOT fitted likelihood ratios.</summary>
public static class OpponentSequenceRules
{
    public static IReadOnlyList<OpponentSequenceRule> All { get; } = [
        new("pf-opening-sihil", "Skellige", "Patricidal Fury", "201632", [], 3,
            "https://www.youtube.com/watch?v=62tWr9upxvI&t=916s", "2026-04-28"),
        new("allgod-offering-false-ciri", "Nilfgaard", null, "162212", ["202596", "202601"], 2,
            "https://www.youtube.com/watch?v=9vIp8CQrxBU&t=24s", "2025-11-27"),
        new("cache-sausage-huntress", "Syndicate", "Hidden Cache", "203086", ["202388"], 1.7,
            "https://www.youtube.com/watch?v=CYOHnKNetno&t=914s", "2026-04-12")
    ];

    public static IReadOnlyList<OpponentSequenceEvidence> Applicable(IEnumerable<OpponentSequenceEvidence>? clues,
        IReadOnlyList<ObservedCard> observed, string? faction, string? startingLeader) => (clues ?? [])
        .Where(c => double.IsFinite(c.Confidence) && c.Confidence >= .8 && c.Round is >= 1 and <= 3)
        .Where(c => All.Any(r => r.Key == c.RuleKey && r.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase) &&
            (r.Leader is null || r.Leader.Equals(startingLeader, StringComparison.OrdinalIgnoreCase)) &&
            !observed.Any(o => o.Card.Id == r.TargetCardId) &&
            r.RequiredOriginalCards.All(id => observed.Any(o => o.Card.Id == id && o.Confidence >= .8 &&
                StartingDeckRules.CountsAgainstStartingDeck(o.Provenance)))))
        .GroupBy(c => c.RuleKey).Select(g => g.MaxBy(c => c.Confidence)!).ToArray();

    public static double Weight(DeckDefinition deck, IReadOnlyList<OpponentSequenceEvidence> clues)
    {
        // One contribution per target; repeated frames and overlapping rules cannot multiply it.
        var log = clues.Select(c => (Clue: c, Rule: All.Single(r => r.Key == c.RuleKey)))
            .Where(x => deck.CountOf(x.Rule.TargetCardId) > 0).GroupBy(x => x.Rule.TargetCardId)
            .Sum(g => g.Max(x => Math.Log(x.Rule.Factor) * Math.Clamp(x.Clue.Confidence, 0, 1)));
        return Math.Exp(Math.Min(Math.Log(4), log));
    }
}

/// <summary>Only live, same-side, ordered evidence. Board presence alone is never a card play.</summary>
public sealed class OpponentSequenceTracker
{
    private readonly Dictionary<string, OpponentSequenceEvidence> _evidence = [];
    private readonly List<(string Id, DateTimeOffset At, double Confidence)> _plays = [];
    private readonly HashSet<(DateTimeOffset At, PlayerSide Side, string Id)> _episodes = [];
    private DateTimeOffset _frameAt, _eventAt, _openingAt, _sirenAt;
    private int? _round, _hand;
    private int _openingPlays, _sirenVotes;
    private bool _usable, _opening, _openingClosed, _openingOpponentActed;
    private string? _faction, _leader, _currentLeader;
    public IReadOnlyList<OpponentSequenceEvidence> Evidence => _evidence.Values.OrderBy(c => c.RuleKey).ToArray();

    public void Reset()
    {
        _evidence.Clear(); _plays.Clear(); _episodes.Clear(); _round = _hand = null;
        _frameAt = _eventAt = _openingAt = _sirenAt = default;
        _openingPlays = _sirenVotes = 0; _usable = _opening = _openingClosed = _openingOpponentActed = false;
        _faction = _leader = _currentLeader = null;
    }

    public bool ObserveFrame(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings,
        bool boardScanned, int? round, bool openingObserved, string? faction, string? leader, string? currentLeader)
    {
        if (at <= _frameAt) { _usable = false; return false; }
        if (_round != round || (_frameAt != default && at - _frameAt > TimeSpan.FromSeconds(15)))
        {
            _plays.Clear(); _episodes.Clear(); _sirenVotes = 0;
            // A capture gap cannot turn the first captured play into turn one.
            if (_opening) _openingClosed = true;
        }
        _round = round; _hand = screen.OpponentHandCount; _frameAt = at; _faction = faction; _leader = leader; _currentLeader = currentLeader;
        _usable = round is >= 1 and <= 3 && screen.View == GwentViewKind.Board && !screen.IsCardSelectionOverlay &&
            !screen.HasCardTooltip && screen.ScreenHeader?.Trim().ToUpperInvariant() is not ("VICTORY" or "DEFEAT" or "DRAW");
        if (!_usable) { _sirenVotes = 0; return false; }
        if (!_opening && !_openingClosed && !_openingOpponentActed && round == 1 && openingObserved && screen.OpponentHandCount == 10)
        { _opening = true; _openingAt = at; }
        if (round != 1 || screen.OpponentHandCount is < 9 || (_opening && at - _openingAt > TimeSpan.FromSeconds(120)))
            _openingClosed = true;
        // PF can spend only some charges; Arnjolf is not required. Demand consecutive
        // fresh board corroboration of a Siren on OUR side during the observed opening.
        var siren = boardScanned && sightings.Any(s => s.Source == CardSightSource.Board && s.Side == PlayerSide.User &&
            s.Card.Id == "202181" && double.IsFinite(s.Distance) && s.Distance is >= 0 and <= .15);
        if (!siren || !OpeningFury || screen.OpponentHandCount is not (9 or 10)) { _sirenVotes = 0; return false; }
        _sirenVotes = at - _sirenAt <= TimeSpan.FromSeconds(3) ? _sirenVotes + 1 : 1; _sirenAt = at;
        if (_sirenVotes >= 2) _openingOpponentActed = true;
        return _sirenVotes >= 2 && Add("pf-opening-sihil", at, .85,
            "Patricidal Fury with a repeatedly visible enemy-spawned Siren during the captured round-one opening suggests Sihil; leader use is inferred from pixels, not a measured turn counter.");
    }

    private bool OpeningFury => _opening && !_openingClosed && _round == 1 && _hand is 9 or 10 && _faction == "Skellige" &&
        _leader == "Patricidal Fury" && _currentLeader == "Patricidal Fury";

    public bool ObserveEvent(VisionEvidenceEvent evidence, CardProvenance origin)
    {
        var sight = evidence.Sighting; var at = evidence.ObservedAt;
        if (!_usable || at != _frameAt || at < _eventAt || sight.Source != CardSightSource.PlayPreview ||
            !double.IsFinite(sight.Distance) || sight.Distance is < 0 or > .2) return false;
        if (!_episodes.Add((at, sight.Side, sight.Card.Id))) return false;
        _eventAt = at;
        if (_episodes.Count > 128)
            foreach (var old in _episodes.OrderBy(e => e.At).Take(_episodes.Count - 128).ToArray()) _episodes.Remove(old);
        if (sight.Side == PlayerSide.User)
        { if (_openingOpponentActed) _openingClosed = true; return false; }
        _openingOpponentActed = true;
        // A leader-spawned Arnjolf is expected to be non-original. No other generated
        // identity may condition the opponent's starting list through this tracker.
        if (sight.Card.Id == "202182" && OpeningFury && origin == CardProvenance.Spawned)
            return Add("pf-opening-sihil", at, 1 - sight.Distance,
                "Patricidal Fury spent to Arnjolf during the captured round-one opening suggests a Sihil setup. Preserve Initiative by expecting Sihil on a later turn, not necessarily in this action.");
        if (sight.Card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact)) return false;
        if (++_openingPlays > 1) _openingClosed = true;
        if (!StartingDeckRules.CountsAgainstStartingDeck(origin)) { _plays.Clear(); return false; }
        _plays.RemoveAll(p => at - p.At > TimeSpan.FromSeconds(180));
        var changed = false;
        if (_faction == "Nilfgaard" && sight.Card.Id == "202601" &&
            _plays.LastOrDefault(p => p.Id == "202596") is { Id: not null } allgod)
            changed |= Add("allgod-offering-false-ciri", at, Math.Min(allgod.Confidence, 1 - sight.Distance),
                "Allgod followed by Offering in the same round suggests the deck-buffed False Ciri package. Buff targets remain unknown; this is not proof of False Ciri or its current power.");
        if (_faction == "Syndicate" && _leader == "Hidden Cache" && sight.Card.Id == "202388")
            changed |= Add("cache-sausage-huntress", at, 1 - sight.Distance,
                "Hidden Cache followed by The Sausage Maker suggests Treasure Huntress carryover. This is an archive expert read, not proof of a paid Resilience fee or an Infusion.");
        _plays.Add((sight.Card.Id, at, 1 - sight.Distance));
        if (_plays.Count > 16) _plays.RemoveAt(0);
        return changed;
    }

    private bool Add(string key, DateTimeOffset at, double confidence, string reason)
    {
        if (_evidence.TryGetValue(key, out var prior) && prior.Confidence >= confidence) return false;
        _evidence[key] = new(key, at, _round!.Value, confidence, reason);
        return true;
    }
}
