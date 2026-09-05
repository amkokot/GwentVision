using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Vision;

public enum CardSightSource { PlayPreview, Board, History, DeckReveal, DeckRevealCandidate }

public sealed record CardSighting(
    CardDefinition Card, PlayerSide Side, CardSightSource Source,
    NormalizedRegion Region, double Distance, double Margin, string? Evidence = null,
    bool NeedsTemporalConfirmation = false, bool IsSupplementalTitle = false);

/// <summary>Used by live capture and offline replay. A rectangle alone is never a card event.</summary>
public sealed class CardFrameRecognizer(CardArtMatcher matcher)
{
    private readonly MoveHistoryCardLocator _history = new();
    private readonly Dictionary<PlayerSide, (CardDefinition Card, DateTimeOffset At)> _recentThinningPlays = [];
    public Action<string>? Trace { get; set; }

    public void ObserveEvents(IEnumerable<VisionEvidenceEvent> events)
    {
        foreach (var evidence in events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview &&
                     CompanionCardRules.ThinningPairs.Contains(item.Sighting.Card.Id)))
            _recentThinningPlays[evidence.Sighting.Side] = (evidence.Sighting.Card, evidence.ObservedAt);
    }

    public void Reset() => _recentThinningPlays.Clear();

    /// <summary>
    /// Reference images can be loaded lazily after a committed opponent play. Keep
    /// the small, time-bounded resolution context when the matcher is rebuilt so a
    /// live capture does not lose the chance to verify the settled second copy.
    /// </summary>
    public void CopyRecentFrom(CardFrameRecognizer previous, DateTimeOffset at)
    {
        previous.Expire(at);
        foreach (var item in previous._recentThinningPlays)
            _recentThinningPlays[item.Key] = item.Value;
    }

    public IReadOnlyList<CardSighting> Recognize(PixelFrame frame, GwentVisualObservation observation, bool includeBoard = true,
        IReadOnlyList<CardSighting>? accepted = null)
    {
        if (observation.IsCardSelectionOverlay) return [];
        var result = new List<CardSighting>();
        if (observation.View == GwentViewKind.MoveHistory)
        {
            foreach (var candidate in _history.Locate(frame))
                Match(frame, candidate.Region, candidate.Region.Left < 0.08 ? PlayerSide.User : PlayerSide.Opponent,
                    CardSightSource.History, result, accepted);
        }
        // The preview can remain visible while Move History is open.
        foreach (var left in new[] { 0.800, 0.815 })
            Match(frame, new NormalizedRegion(left, 0.134, left + 0.101, 0.399), PlayerSide.Opponent,
                CardSightSource.PlayPreview, result, accepted);
        foreach (var left in new[] { 0.815, 0.833 })
            Match(frame, new NormalizedRegion(left, 0.413, left + 0.103, 0.665), PlayerSide.User,
                CardSightSource.PlayPreview, result, accepted);

        if (includeBoard)
        {
            // Verified card geometry locates evenly spaced, low-texture row neighbours.
            foreach (var seed in (accepted ?? []).Where(item => item.Source == CardSightSource.Board).OrderByDescending(item => item.Side == PlayerSide.Opponent))
            foreach (var offset in new[] { -2, -1, 1, 2 })
            {
                var width = seed.Region.Right - seed.Region.Left;
                var shifted = seed.Region with { Left = seed.Region.Left + width * offset, Right = seed.Region.Right + width * offset };
                if (shifted.Left < 0.20 || shifted.Right > 0.80) continue;
                Match(frame, shifted, seed.Side, CardSightSource.Board, result, accepted);
            }
            if (!observation.IsCardSelectionOverlay && observation.MatchHudVisible == true && !observation.HasCardTooltip)
                foreach (var recent in _recentThinningPlays.ToArray())
                {
                    result.AddRange(TargetRecentThinningPair(frame, recent.Key, recent.Value.Card));
                }
        }

        var unique = new List<CardSighting>();
        foreach (var sighting in result.OrderBy(item => item.Distance))
        {
            // The feature pass may already own one body in a targeted pair. Keep
            // the missing neighbour, but do not expose the same physical region
            // twice merely because both matchers independently recognized it.
            if (accepted?.Any(item => item.Source == sighting.Source && item.Side == sighting.Side &&
                Overlap(item.Region, sighting.Region) > 0.35) == true) continue;
            if (unique.Any(item => item.Source == sighting.Source && Overlap(item.Region, sighting.Region) > 0.35)) continue;
            unique.Add(sighting);
        }
        return unique;
    }

    public void Expire(DateTimeOffset at)
    {
        foreach (var side in _recentThinningPlays.Where(pair => at < pair.Value.At || at - pair.Value.At > TimeSpan.FromSeconds(12))
                     .Select(pair => pair.Key).ToArray()) _recentThinningPlays.Remove(side);
    }

    private IReadOnlyList<CardSighting> TargetRecentThinningPair(PixelFrame frame, PlayerSide side, CardDefinition card)
    {
        var rowCenters = side == PlayerSide.Opponent ? new[] { .225, .385 } : new[] { .535, .695 };
        foreach (var width in new[] { .055, .0625, .070 })
        {
            var candidates = new List<(NormalizedRegion Region, double Distance)>();
            var height = width * 2.42;
            foreach (var rowCenter in rowCenters)
            foreach (var yOffset in new[] { -.018, 0d, .018 })
            for (var center = .23; center <= .77; center += .008)
            {
                var region = new NormalizedRegion(center - width / 2, rowCenter + yOffset - height / 2,
                    center + width / 2, rowCenter + yOffset + height / 2);
                var distance = matcher.IdentityDistance(frame, region, card.Id);
                if (distance <= .58) candidates.Add((region, distance));
            }
            var distinct = new List<(NormalizedRegion Region, double Distance)>();
            foreach (var candidate in candidates.OrderBy(item => item.Distance))
                if (!distinct.Any(item => Overlap(item.Region, candidate.Region) > .35)) distinct.Add(candidate);
            Trace?.Invoke($"{card.Name} targeted {width:F4} candidates: "+string.Join(", ",distinct.Take(12).Select(item =>
                $"{item.Region.Left:F3}-{item.Region.Right:F3}/{(item.Region.Top+item.Region.Bottom)/2:F3}={item.Distance:F3}")));
            var pair = (from left in distinct from right in distinct
                let dx = (right.Region.Left + right.Region.Right - left.Region.Left - left.Region.Right) / 2
                let dy = Math.Abs((right.Region.Top + right.Region.Bottom - left.Region.Top - left.Region.Bottom) / 2)
                where dx >= width * .78 && dx <= width * 1.30 && dy <= width * .45 &&
                      Math.Min(left.Distance, right.Distance) <= .45
                orderby left.Distance + right.Distance
                select new[] { left, right }).FirstOrDefault();
            if (pair is null) continue;
            Trace?.Invoke($"{card.Name} targeted pair accepted: {pair[0].Distance:F3}/{pair[1].Distance:F3}");
            return pair.Select(candidate => new CardSighting(card, side, CardSightSource.Board, candidate.Region,
                candidate.Distance, .20,
                $"Recent exact self-thinning play; two adjacent same-art regions matched at {pair[0].Distance:F3}/{pair[1].Distance:F3}. Repetition is still required.",
                NeedsTemporalConfirmation: true)).ToArray();
        }
        Trace?.Invoke(card.Name+" targeted pair: no adjacent pair passed.");
        return [];
    }

    private void Match(PixelFrame frame, NormalizedRegion region, PlayerSide side, CardSightSource source, List<CardSighting> result,
        IReadOnlyList<CardSighting>? accepted)
    {
        if (accepted?.Any(item => Overlap(item.Region, region) > 0.40) == true) return;
        if (source == CardSightSource.Board && region.Bottom >= 0.90) return;
        if (source == CardSightSource.PlayPreview && (region.Left < 0.78 ||
            (side == PlayerSide.User ? region.Top < 0.37 || region.Bottom > 0.74 : region.Top < 0.09 || region.Bottom > 0.46))) return;
        var rough = matcher.Rank(frame, region, 2);
        if (rough.Count < 2 || rough[0].Distance > 0.62) return;
        var matches = matcher.RankAligned(frame, region, 2);
        if (matches.Count < 2) return;
        var best = matches[0];
        var gap = matches[1].Distance - best.Distance;
        var limit = source == CardSightSource.Board ? 0.30 : 0.38;
        // Low-texture art may lack six SIFT features. A wider colour tolerance is
        // allowed only with stronger separation from every other identity AND two
        // independently recognized cards establishing this row's size and position.
        var anchoredRow = source == CardSightSource.Board && best.Distance <= .40 && gap >= .30 &&
            HasRowAnchors(best.Region, side, accepted);
        if (!anchoredRow && (best.Distance > limit || gap < 0.20)) return;
        // Stratagem/leader art appearing at the start is not a deck card.
        if (best.Card.Kind is CardKind.Stratagem or CardKind.Leader) return;
        result.Add(new CardSighting(best.Card, side, source, best.Region, best.Distance, gap,
            $"Premium-aware colour/layout match: distance {best.Distance:F3}, distinct-card margin {gap:F3}" +
            (anchoredRow ? "; row geometry corroborated by two recognized cards" : "")));
    }

    private static bool HasRowAnchors(NormalizedRegion region, PlayerSide side, IReadOnlyList<CardSighting>? accepted)
    {
        var width = region.Right - region.Left;
        var height = region.Bottom - region.Top;
        var x = (region.Left + region.Right) / 2;
        var y = (region.Top + region.Bottom) / 2;
        var anchors = (accepted ?? []).Where(card => card.Source == CardSightSource.Board && card.Side == side && card.Distance <= .30 &&
            Overlap(card.Region, region) < .10 &&
            Math.Abs((card.Region.Top + card.Region.Bottom) / 2 - y) <= height * .20 &&
            Math.Abs((card.Region.Left + card.Region.Right) / 2 - x) <= width * 3.5 &&
            (card.Region.Right - card.Region.Left) / width is >= .80 and <= 1.20 &&
            (card.Region.Bottom - card.Region.Top) / height is >= .80 and <= 1.20).ToArray();
        return anchors.Any(a => anchors.Any(b => Overlap(a.Region, b.Region) < .10));
    }

    private static double Overlap(NormalizedRegion a, NormalizedRegion b)
    {
        var area = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
                   Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return area / Math.Max(0.000001, Math.Min((a.Right - a.Left) * (a.Bottom - a.Top), (b.Right - b.Left) * (b.Bottom - b.Top)));
    }

}
