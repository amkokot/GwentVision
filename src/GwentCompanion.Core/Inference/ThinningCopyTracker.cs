using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

/// <summary>Promotes an unseen pair assumption only after two physical units are repeatedly visible.</summary>
public sealed class ThinningCopyTracker
{
    private readonly CurrentBoardPresence _board = new();
    private readonly Dictionary<PlayerSide, (string Id, DateTimeOffset At)> _played = [];
    private readonly Dictionary<string, DateTimeOffset> _knownPairVotes = [];
    private readonly Dictionary<string, (DateTimeOffset At, int Copies)> _knownCardVotes = [];
    private readonly Dictionary<(PlayerSide Side, string Id), (DateTimeOffset At, int Copies)> _originalPlayEpisodes = [];

    public void Reset() { _board.Reset(true); _played.Clear(); _knownPairVotes.Clear(); _knownCardVotes.Clear(); _originalPlayEpisodes.Clear(); }

    public bool ObserveEvent(VisionEvidenceEvent evidence, CardProvenance origin, LiveDeckTracker? tracker = null, bool graveReplayRisk = false)
    {
        var sight = evidence.Sighting;
        if (evidence.ResolvedDeckCopies is > 0 and <= 25)
            return tracker?.Side == sight.Side && StartingDeckRules.CountsAgainstStartingDeck(origin) && !graveReplayRisk &&
                tracker.SetObservedCopyLowerBound(sight.Card.Id, evidence.ResolvedDeckCopies.Value,
                    "Tutor choices corroborated by a repeated matching deck decrement; maximum physical-copy floor, not repeated cast count.");
        if (sight.Source == CardSightSource.PlayPreview)
        {
            _played.Remove(sight.Side);
            if (CompanionCardRules.ThinningPairs.Contains(sight.Card.Id))
                _played[sight.Side] = (sight.Card.Id, evidence.ObservedAt);
        }
        // Two distinct, original-provenance bronze play episodes are direct
        // singleton evidence even when the second body is not simultaneously on
        // the board. Suppress this promotion around observed graveyard replay
        // sources (Harald, Freya, etc.), where another cast is not another copy.
        if (sight.Source is not CardSightSource.PlayPreview and not CardSightSource.Board ||
            tracker is null || tracker.Side != sight.Side || sight.Card.IsGold || !sight.Card.CanBeInStartingDeck ||
            !StartingDeckRules.CountsAgainstStartingDeck(origin) || graveReplayRisk) return false;
        var key = (sight.Side, sight.Card.Id);
        if (!_originalPlayEpisodes.TryGetValue(key, out var prior))
        {
            _originalPlayEpisodes[key] = (evidence.ObservedAt, 1);
            return false;
        }
        if (evidence.ObservedAt <= prior.At || evidence.ObservedAt - prior.At < TimeSpan.FromSeconds(3)) return false;
        var copies = Math.Min(2, prior.Copies + 1);
        _originalPlayEpisodes[key] = (evidence.ObservedAt, copies);
        return copies == 2 && tracker.SetObservedCopyLowerBound(sight.Card.Id, 2,
            "Two distinct original-provenance play episodes; no observed graveyard replay/create route for the second cast.");
    }

    public bool ObserveFrame(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings,
        bool scanned, IEnumerable<LiveDeckTracker> trackers, Func<CardSighting, bool> hasCopyRisk, DeckDefinition? userReference = null,
        Func<CardSighting,int?>? generatedInitiators = null)
    {
        // The narrow exact-identity resolver already required an adjacent pair,
        // a strong member, a recent matching play and a clean board. Let those
        // bounded sightings enter this tracker's repeated-frame vote at its .58
        // identity threshold; other board consumers keep the ordinary .40 gate.
        var copySightings = sightings.Select(item => item.Source == CardSightSource.Board && item.Distance <= .58 &&
            item.Evidence?.StartsWith("Recent exact self-thinning play", StringComparison.Ordinal) == true
                ? item with { Distance = Math.Min(item.Distance, .39) } : item).ToArray();
        _board.Observe(at, screen, copySightings, scanned, allowUnobscuredDuringPreview: true);
        if (!scanned || _board.At != at) return false;
        var changed = false;
        foreach (var tracker in trackers)
        {
            // Partial intervening scans do not contradict a simultaneously visible pair.
            // Keep two-copy corroboration for a short rolling window, independently of
            // current-board continuity (which intentionally expires much sooner).
            if (tracker.Side == PlayerSide.User && userReference is not null)
            foreach (var group in sightings.Where(s => s.Side == PlayerSide.User && s.Source == CardSightSource.Board && s.Distance <= .4 &&
                         !CurrentBoardPresence.PreviewCovers(s.Region, sightings) &&
                         !(screen.HasCardTooltip && screen.TooltipRegion is {} tip && s.Region.Left < tip.Right+.02 &&
                             s.Region.Right > tip.Left-.02 && s.Region.Top < tip.Bottom+.02 && s.Region.Bottom > tip.Top-.12))
                         .GroupBy(s => s.Card.Id))
            {
                var id = group.Key;
                var listedCopies = userReference.CountOf(id);
                if (listedCopies == 0 || group.Any(hasCopyRisk)) { _knownPairVotes.Remove(id); _knownCardVotes.Remove(id); continue; }
                var distinct = new List<CardSighting>();
                foreach (var sight in group.OrderBy(s => s.Distance))
                    if (!distinct.Any(s => Math.Abs((s.Region.Left+s.Region.Right-sight.Region.Left-sight.Region.Right)/2) < .035 &&
                        Math.Abs((s.Region.Top+s.Region.Bottom-sight.Region.Top-sight.Region.Bottom)/2) < .055)) distinct.Add(sight);
                if (distinct.Count > listedCopies) { _knownPairVotes.Remove(id); _knownCardVotes.Remove(id); continue; }
                var previous = tracker.Observations.FirstOrDefault(c => c.Card.Id == id);
                if (previous is not null && !StartingDeckRules.CountsAgainstStartingDeck(previous.Provenance))
                { _knownPairVotes.Remove(id); _knownCardVotes.Remove(id); continue; }
                // A row can reflow and a partial/tooltip-obscured scan can miss one card.
                // For the player's fixed list, two clean sightings within 30 seconds are
                // enough to account for at least the lower copy count seen in both scans.
                if (_knownCardVotes.TryGetValue(id, out var cardVote) && at-cardVote.At >= TimeSpan.FromMilliseconds(500) &&
                    at-cardVote.At < TimeSpan.FromSeconds(30))
                {
                    var corroboratedCopies = Math.Min(cardVote.Copies, distinct.Count);
                    changed |= tracker.ConsiderDirectPlay(distinct[0].Card, 1-distinct[0].Distance, at,
                        "Known player card independently visible twice in the rolling 30-second buffer; an intervening partial scan is not absence.",
                        CardProvenance.ConfirmedStartingDeck);
                    changed |= tracker.SetObservedCopyLowerBound(id, corroboratedCopies,
                        "Separately visible known-player copies corroborated across non-obscured scans.");
                }
                _knownCardVotes[id] = (at, distinct.Count);
                if (listedCopies != 2 || distinct.Count != 2) { if (distinct.Count > 2) _knownPairVotes.Remove(id); continue; }
                if (_knownPairVotes.TryGetValue(id, out var first) && at-first >= TimeSpan.FromMilliseconds(500) && at-first <= TimeSpan.FromSeconds(20))
                {
                    changed |= tracker.ConsiderDirectPlay(distinct[0].Card, 1-distinct[0].Distance, at,
                        "Two simultaneous known-player copies corroborated in the rolling 20-second window; partial intermediate scans are not absence.", CardProvenance.ConfirmedStartingDeck);
                    changed |= tracker.SetObservedCopyLowerBound(id, 2, "Two separately visible copies in two scans; no observed creation route.");
                }
                else if (!_knownPairVotes.ContainsKey(id) || at-first > TimeSpan.FromSeconds(20)) _knownPairVotes[id] = at;
            }
            // The player's list is known. Summons need no play preview, and every
            // bronze (not just thinning pairs) can establish two physical copies.
            if (tracker.Side == PlayerSide.User && userReference is not null)
            foreach (var group in _board.Confirmed(at).Where(sight => sight.Side == PlayerSide.User)
                         .GroupBy(sight => sight.Card.Id))
            {
                var count = group.Count(); var sight = group.First();
                if (count > userReference.CountOf(group.Key) || group.Any(hasCopyRisk)) continue;
                var previous = tracker.Observations.FirstOrDefault(item => item.Card.Id == group.Key);
                if (previous is not null && !StartingDeckRules.CountsAgainstStartingDeck(previous.Provenance)) continue;
                changed |= tracker.ConsiderDirectPlay(sight.Card, 1 - sight.Distance, at,
                    "Known player card on board in two corroborating scans; play or summon, not a deck/hand reveal.", CardProvenance.ConfirmedStartingDeck);
                changed |= tracker.SetObservedCopyLowerBound(group.Key, count,
                    "Distinct simultaneous known-player copies in two scans; no observed creation/copy route.");
            }
            // A self-thinning bronze can lose its enlarged preview while both resulting
            // bodies remain readable. Two distinct, repeatedly confirmed bodies recover
            // the physical pair only when no observed create/copy route competes.
            foreach (var pair in _board.Confirmed(at).Where(s => s.Side == tracker.Side && s.Source == CardSightSource.Board &&
                         CompanionCardRules.ThinningPairs.Contains(s.Card.Id)).GroupBy(s => s.Card.Id))
            {
                var bodies = pair.ToArray();
                if (bodies.Length != 2 || bodies.Any(hasCopyRisk) || !bodies[0].Card.CanBeInStartingDeck) continue;
                changed |= tracker.ConsiderDirectPlay(bodies[0].Card, 1 - bodies.Min(s => s.Distance), at,
                    "Two distinct self-thinning bodies corroborated on the board; enlarged play preview was not required.",
                    CardProvenance.ProbableStartingDeck);
                changed |= tracker.SetObservedCopyLowerBound(pair.Key, 2,
                    "Two repeatedly visible self-thinning bodies with no observed create/copy route.");
            }
            if (!_played.TryGetValue(tracker.Side, out var play) || at <= play.At || at - play.At > TimeSpan.FromSeconds(30)) continue;
            var pairBodies=_board.Confirmed(at).Where(s=>s.Side==tracker.Side && s.Card.Id==play.Id).ToArray();
            if (pairBodies.Length==3 && generatedInitiators is not null && pairBodies.All(s=>generatedInitiators(s)==1))
            {
                // One printed Spawn initiator plus two separately corroborated
                // deck-summoned bodies. Do not count the generated initiator.
                changed |= tracker.ConsiderDirectPlay(pairBodies[0].Card,1-pairBodies[0].Distance,at,
                    "Three distinct thinning bodies corroborated; one observed single-card Spawn route accounts for the initiator, leaving two probable deck originals.",CardProvenance.ProbableStartingDeck);
                changed |= tracker.SetObservedCopyLowerBound(play.Id,2,"Two original copies after excluding one spawned initiator from three corroborated bodies.");
                continue;
            }
            var original = tracker.DeckBuildingObservations.FirstOrDefault(item => item.Card.Id == play.Id);
            if (original is null || original.ObservedCopies >= 2) continue;
            // Precision Strike can generate the initiating Brokilon Sentinel. Two
            // visible Sentinels then need not be two originals. Until leader-spawn
            // attribution is resolved, keep its second original as a deck assumption.
            if (play.Id == "202273") continue;
            var visible = _board.Confirmed(at).Where(sight => sight.Side == tracker.Side && sight.Card.Id == play.Id).ToArray();
            // Extra copies beyond the legal pair suggest a copying effect, not extra originals.
            if (visible.Length != 2 || visible.Any(hasCopyRisk)) continue;
            changed |= tracker.SetObservedCopyLowerBound(play.Id, 2,
                "distinct board copies after a matching play, corroborated across two frames (original membership probable)");
        }
        return changed;
    }
}
