using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;
using System.Diagnostics;

namespace GwentCompanion.Platform.Windows.Vision;

public enum VisionReferenceScope { FullCatalog, CandidateDecks }

/// <summary>Locates artwork by independent feature correspondences, not by the shape of a rectangle.</summary>
public sealed class FeatureCardRecognizer : IDisposable
{
    private readonly SIFT _sift = SIFT.Create(9000, 3, 0.018, 10, 1.6);
    private FlannBasedMatcher? _matcher;
    private readonly List<Reference> _references = [];
    private readonly List<CardArtReference> _artReferences = [];
    private readonly Source[] _sources;
    private readonly SiftFeatureCache? _featureCache;
    private readonly bool _useCachedFeatures;
    private readonly HashSet<string> _loadedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _candidateMatcherLock = new();
    private FlannBasedMatcher? _knownPlayerMatcher;
    private List<Reference> _knownPlayerReferences = [];
    private string? _knownPlayerKey;
    private string[] _startingPlayerIds = [];
    private readonly HashSet<string> _observedPlayerIds = new(StringComparer.Ordinal);
    private FlannBasedMatcher? _likelyOpponentMatcher;
    private List<Reference> _likelyOpponentReferences = [];
    private string? _likelyOpponentKey;
    private string[] _projectedOpponentIds = [];
    private readonly HashSet<string> _observedOpponentIds = new(StringComparer.Ordinal);
    public IReadOnlyList<CardArtReference> ArtReferences { get { lock (_candidateMatcherLock) return _artReferences.ToArray(); } }
    public int ReferenceGeneration { get; private set; }
    public Action<string>? Trace { get; set; }
    public int CachedImages { get; private set; }
    public int ComputedImages { get; private set; }
    public int ReferenceCount { get { lock (_candidateMatcherLock) return _references.Count; } }
    public double ReferenceLoadMilliseconds { get; private set; }
    public double MatcherTrainMilliseconds { get; private set; }
    public VisionStageTimings Timings { get; } = new();

    public FeatureCardRecognizer(IEnumerable<(CardDefinition Card, string Path)> cards, string? featureCacheDirectory = null,
        bool useCachedFeatures = true, VisionReferenceScope scope = VisionReferenceScope.FullCatalog)
    {
        // Small reference images/preview crops get slower with many native worker threads.
        // Keep the companion's CPU use bounded while the game is running.
        Cv2.SetNumThreads(2);
        Cv2.SetTheRNG(20260826);
        var inputs = cards.ToArray();
        _featureCache = featureCacheDirectory is null ? null : new SiftFeatureCache(featureCacheDirectory);
        _useCachedFeatures = useCachedFeatures;
        var animatedIds = inputs.Where(item => item.Path.Replace('\\', '/').Contains("/premium-frames/", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Card.Id).ToHashSet();
        _sources = inputs.Select(item => new Source(item.Card, item.Path,
            !item.Path.Replace('\\', '/').Contains("/portraits/", StringComparison.OrdinalIgnoreCase) || animatedIds.Contains(item.Card.Id))).ToArray();
        if (scope == VisionReferenceScope.CandidateDecks) return;
        var startupClock = Stopwatch.StartNew();
        LoadReferences(_sources);
        ReferenceLoadMilliseconds = startupClock.Elapsed.TotalMilliseconds;
        startupClock.Restart();
        // The full catalog needs more search checks as animation variants accumulate.
        // Live side-specific indices remain small and retain their original budget.
        _matcher = BuildMatcher(_references, searchChecks: 256);
        MatcherTrainMilliseconds = startupClock.Elapsed.TotalMilliseconds;
    }

    private void LoadReferences(IEnumerable<Source> sources)
    {
        var changed = false;
        using var referenceSift = SIFT.Create(500, 3, 0.018, 10, 1.6);
        using var staticSift = SIFT.Create(300, 3, 0.018, 10, 1.6);
        foreach (var item in sources)
        {
            if (!_loadedPaths.Add(item.Path)) continue;
            var key = _featureCache is null ? null : SiftFeatureCache.Key(item.Path, item.Variant);
            using var referenceColor = Cv2.ImRead(item.Path, ImreadModes.Color);
            if (referenceColor.Empty()) continue;
            using var referenceBgra = new Mat();
            Cv2.CvtColor(referenceColor, referenceBgra, ColorConversionCodes.BGR2BGRA);
            var colorPixels = new byte[referenceBgra.Width * referenceBgra.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(referenceBgra.Data, colorPixels, 0, colorPixels.Length);
            var appearance = VisualDescriptor.Create(new PixelFrame(referenceBgra.Width, referenceBgra.Height, colorPixels));
            _artReferences.Add(new(item.Card, appearance));
            changed = true;
            if (_useCachedFeatures && key is not null && _featureCache!.Load(key) is { } cached)
            {
                CachedImages++;
                _references.AddRange(cached.Select(f => new Reference(item.Card, f.Width, f.Height, f.Points, f.Descriptors, appearance)));
                continue;
            }
            ComputedImages++;
            using var original = Cv2.ImRead(item.Path, ImreadModes.Grayscale);
            if (original.Empty()) continue;
            var computed = new List<CachedSiftFeatures>();
            using var image = new Mat();
            // Verified, tightly cropped board appearances can be only 60 pixels wide.
            // Sample them at the same working scale as the upsampled scene before SIFT.
            var referenceScale = original.Width < 160 ? 2.0 : 1.0;
            Cv2.Resize(original, image, new Size(), referenceScale, referenceScale, InterpolationFlags.Cubic);
            // Match the sampling of small board cards as well as enlarged previews. SIFT's
            // scale invariance alone cannot restore detail removed by the game's downsampling.
            foreach (var scale in item.Variant ? new[] { 0.4, 0.65, 1.0 } : new[] { 0.5, 1.0 })
            {
                using var sampled = new Mat();
                Cv2.Resize(image, sampled, new Size(), scale, scale, InterpolationFlags.Area);
                using var referenceImage = new Mat();
                Cv2.Resize(sampled, referenceImage, new Size(image.Width, image.Height), 0, 0, InterpolationFlags.Cubic);
                var descriptors = new Mat();
                (item.Variant ? referenceSift : staticSift).DetectAndCompute(referenceImage, null, out var points, descriptors);
                if (points.Length < 8) { descriptors.Dispose(); continue; }
                _references.Add(new Reference(item.Card, image.Width, image.Height, points, descriptors, appearance));
                computed.Add(new(image.Width, image.Height, points, descriptors));
            }
            if (key is not null) _featureCache!.Save(key, computed);
        }
        if (changed) ReferenceGeneration++;
    }

    public void SetKnownPlayerDeck(IEnumerable<string> cardIds)
    {
        lock (_candidateMatcherLock)
        {
            _startingPlayerIds = cardIds.Distinct(StringComparer.Ordinal).ToArray();
            RebuildKnownPlayerMatcher();
        }
    }

    /// <summary>
    /// Adds identities established by player actions, including exact named Spawn
    /// outputs, to the bounded player matcher. This lets generated cards compete
    /// with visually similar starting-deck art instead of forcing a false deck-card
    /// match merely because tokens were absent from the initial index.
    /// </summary>
    public void ObservePlayerCards(IEnumerable<string> cardIds)
    {
        lock (_candidateMatcherLock)
        {
            var changed = false;
            foreach (var id in cardIds.Where(id => !string.IsNullOrWhiteSpace(id))) changed |= _observedPlayerIds.Add(id);
            if (changed) RebuildKnownPlayerMatcher();
        }
    }

    public void ResetObservedPlayerCards()
    {
        lock (_candidateMatcherLock)
        {
            if (_observedPlayerIds.Count == 0) return;
            _observedPlayerIds.Clear();
            RebuildKnownPlayerMatcher();
        }
    }

    private void RebuildKnownPlayerMatcher()
    {
        var ids = _startingPlayerIds.Concat(_observedPlayerIds.Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(96).ToHashSet(StringComparer.Ordinal);
        var key = string.Join(',',ids.Order());
        lock (_candidateMatcherLock)
        {
            if (key == _knownPlayerKey) return;
            var loadClock = Stopwatch.StartNew();
            LoadReferences(_sources.Where(source => ids.Contains(source.Card.Id)));
            ReferenceLoadMilliseconds += loadClock.Elapsed.TotalMilliseconds;
            _knownPlayerMatcher?.Dispose(); _knownPlayerMatcher = null; _knownPlayerKey = key;
            _knownPlayerReferences = _references.Where(r => ids.Contains(r.Card.Id)).ToList();
            if (_knownPlayerReferences.Count == 0) return;
            var matcherClock = Stopwatch.StartNew();
            _knownPlayerMatcher = BuildMatcher(_knownPlayerReferences);
            MatcherTrainMilliseconds += matcherClock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Supplies a soft identity prior from the live deck projection. Geometry, ownership,
    /// appearance and temporal checks are still required; a suggested card is never a
    /// sighting by itself. The smaller index recovers tiny board art crowded out by the
    /// full-catalog nearest-neighbour search.
    /// </summary>
    public void SetLikelyOpponentCards(IEnumerable<string> cardIds)
    {
        lock (_candidateMatcherLock)
        {
            _projectedOpponentIds = cardIds.Distinct(StringComparer.Ordinal).Take(80).ToArray();
            RebuildLikelyOpponentMatcher();
        }
    }

    /// <summary>
    /// Keeps identities established by visible opponent actions in the small board-art
    /// index even when an older statistical top-70 list did not contain them. Pixels,
    /// geometry and ownership still decide every sighting; this is only search recall.
    /// </summary>
    public void ObserveOpponentCards(IEnumerable<string> cardIds)
    {
        lock (_candidateMatcherLock)
        {
            var changed = false;
            foreach (var id in cardIds.Where(id => !string.IsNullOrWhiteSpace(id))) changed |= _observedOpponentIds.Add(id);
            if (changed) RebuildLikelyOpponentMatcher();
        }
    }

    public void ResetObservedOpponentCards()
    {
        lock (_candidateMatcherLock)
        {
            if (_observedOpponentIds.Count == 0) return;
            _observedOpponentIds.Clear();
            RebuildLikelyOpponentMatcher();
        }
    }

    private void RebuildLikelyOpponentMatcher()
    {
        // Measured identities win the bounded slots; projection priors fill the tail.
        var ids = _observedOpponentIds.Order(StringComparer.Ordinal)
            .Concat(_projectedOpponentIds).Distinct(StringComparer.Ordinal).Take(80).ToHashSet(StringComparer.Ordinal);
        var key = string.Join(',', ids.Order(StringComparer.Ordinal));
        if (key == _likelyOpponentKey) return;
        var loadClock = Stopwatch.StartNew();
        LoadReferences(_sources.Where(source => ids.Contains(source.Card.Id)));
        ReferenceLoadMilliseconds += loadClock.Elapsed.TotalMilliseconds;
        _likelyOpponentMatcher?.Dispose(); _likelyOpponentMatcher = null; _likelyOpponentKey = key;
        _likelyOpponentReferences = _references.Where(reference => ids.Contains(reference.Card.Id)).ToList();
        if (_likelyOpponentReferences.Count == 0) return;
        var matcherClock = Stopwatch.StartNew();
        _likelyOpponentMatcher = BuildMatcher(_likelyOpponentReferences);
        MatcherTrainMilliseconds += matcherClock.Elapsed.TotalMilliseconds;
    }

    private static FlannBasedMatcher BuildMatcher(IReadOnlyList<Reference> references, int searchChecks = 128)
    {
        if (references.Count == 0) throw new ArgumentException("A feature matcher needs at least one reference.", nameof(references));
        var matcher = new FlannBasedMatcher(new KDTreeIndexParams(4), new SearchParams(searchChecks));
        matcher.Add(references.Select(r => r.Descriptors)); matcher.Train(); return matcher;
    }

    public IReadOnlyList<CardSighting> Recognize(PixelFrame frame, GwentVisualObservation observation, bool includeBoard = true)
    {
        if (observation.IsCardSelectionOverlay || _references.Count == 0) return [];
        lock (_candidateMatcherLock)
            if (_matcher is null && _knownPlayerMatcher is null && _likelyOpponentMatcher is null) return [];
        // RANSAC should not depend on which earlier frames the live queue dropped.
        Cv2.SetTheRNG(20260826);
        using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        var cropX = includeBoard ? 0 : (int)(frame.Width * 0.775);
        var cropY = includeBoard ? 0 : (int)(frame.Height * 0.095);
        var cropWidth = includeBoard ? frame.Width : (int)(frame.Width * 0.20);
        var cropHeight = includeBoard ? frame.Height : (int)(frame.Height * 0.65);
        using var crop = new Mat(bgra, new Rect(cropX, cropY, cropWidth, cropHeight));
        using var gray = new Mat();
        using var enlarged = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Resize(gray, enlarged, new Size(cropWidth * 2, cropHeight * 2), 0, 0, InterpolationFlags.Cubic);
        using var descriptors = new Mat();
        using var sceneMask = new Mat(enlarged.Size(), MatType.CV_8UC1, includeBoard ? Scalar.Black : Scalar.White);
        if (includeBoard)
        {
            // Spend the feature budget on cards, not the animated 3-D leaders or hands.
            foreach (var region in new[] { new NormalizedRegion(.215, .09, .79, .90), new NormalizedRegion(.775, .095, .975, .745) }
                .Concat(observation.View == GwentViewKind.MoveHistory ? [new NormalizedRegion(.02, .20, .19, .81)] : Array.Empty<NormalizedRegion>()))
                Cv2.Rectangle(sceneMask, new Rect(region.PixelLeft(enlarged.Width), region.PixelTop(enlarged.Height),
                    region.PixelRight(enlarged.Width) - region.PixelLeft(enlarged.Width),
                    region.PixelBottom(enlarged.Height) - region.PixelTop(enlarged.Height)), Scalar.White, -1);
        }
        var clock = Stopwatch.GetTimestamp();
        _sift.DetectAndCompute(enlarged, sceneMask, out var points, descriptors);
        Timings.Record(includeBoard ? "BoardSift" : "PreviewSift", clock);
        if (points.Length < 8) return [];
        var result = new List<CardSighting>();
        // Reuse the same scene features. Small side-specific indices recover low-texture
        // originals crowded out by the full-catalog ANN search. They are priors only:
        // every result still has to pass visual geometry, appearance and side checks.
        lock (_candidateMatcherLock)
        {
        var passes = new List<(FlannBasedMatcher Matcher,List<Reference> References,PlayerSide? RestrictedSide)>();
        if (_likelyOpponentMatcher is not null) passes.Add((_likelyOpponentMatcher,_likelyOpponentReferences,PlayerSide.Opponent));
        if (_matcher is not null) passes.Add((_matcher,_references,null));
        if (_knownPlayerMatcher is not null) passes.Add((_knownPlayerMatcher,_knownPlayerReferences,PlayerSide.User));
        foreach (var pass in passes)
        {
        // A side-specific index cannot accept geometry from the other lane. Avoid
        // querying those descriptors while retaining every point that can lie inside
        // an admissible rectangle (board centre split plus half the maximum height).
        // History uses a different layout and retains the complete query set.
        var indices = Enumerable.Range(0, points.Length).Where(i => pass.RestrictedSide is null ||
            observation.View == GwentViewKind.MoveHistory || (pass.RestrictedSide == PlayerSide.Opponent
                ? (points[i].Pt.Y + cropY * 2) / (frame.Height * 2) <= (includeBoard ? .665 : .46)
                : (points[i].Pt.Y + cropY * 2) / (frame.Height * 2) >= (includeBoard ? .265 : .37))).ToArray();
        if (indices.Length < 6) continue;
        using var query = new Mat(indices.Length, descriptors.Cols, descriptors.Type());
        for (var i = 0; i < indices.Length; i++)
        {
            using var sourceRow = descriptors.Row(indices[i]);
            using var targetRow = query.Row(i);
            sourceRow.CopyTo(targetRow);
        }
        // A six-neighbour list can fill with near-duplicate premium scales before
        // the observed variant gathers enough independent features. Retain twenty-four
        // neighbours on board scans; keep the fast preview-only budget unchanged.
        // Identity ratio, feature spread and appearance gates are unchanged.
        clock = Stopwatch.GetTimestamp();
        var nearest = pass.Matcher.KnnMatch(query, Math.Min(includeBoard ? 24 : 6, pass.References.Sum(r => r.Descriptors.Rows)));
        foreach (var neighbors in nearest)
            for (var i = 0; i < neighbors.Length; i++)
                neighbors[i] = new DMatch(indices[neighbors[i].QueryIdx], neighbors[i].TrainIdx, neighbors[i].ImgIdx, neighbors[i].Distance);
        clock = Timings.Record(includeBoard ? "BoardKnn" : "PreviewKnn", clock);
        Trace?.Invoke($"Scene points: {points.Length}");
        var groups = nearest.Where(pair =>
            {
                if (pair.Length < 2) return false;
                var cardId = pass.References[pair[0].ImgIdx].Card.Id;
                var alternatives = pair.Skip(1).Where(match => pass.References[match.ImgIdx].Card.Id != cardId).ToArray();
                // A tiny candidate index can contain only variants of one identity. In
                // that case there is no ratio-test distractor; accepting every scene
                // descriptor creates thousands of random correspondences and destroys
                // localization. Keep only exceptionally close descriptors instead.
                return alternatives.Length == 0 ? pair[0].Distance <= 165 : pair[0].Distance < alternatives[0].Distance * 0.70;
            })
            .SelectMany(pair => pair.Where(match => pass.References[match.ImgIdx].Card.Id == pass.References[pair[0].ImgIdx].Card.Id)
                .GroupBy(match => match.ImgIdx).Select(group => group.First()))
            .GroupBy(match => match.ImgIdx);
        foreach (var group in groups)
        {
            var reference = pass.References[group.Key];
            if (reference.Card.Kind is CardKind.Stratagem or CardKind.Leader) continue;
            // Premium/static variants can repeatedly fit the same four or more
            // physical copies. A match already at the confidence ceiling wins the
            // final overlap deduplication anyway. Keep unexplained points (including
            // other copies), rather than refitting those same card interiors for
            // every appearance variant. Weak matches never suppress further work.
            var explained = result.Where(s => s.Card.Id == reference.Card.Id && s.Distance <= .08 &&
                (pass.RestrictedSide is null || s.Side == pass.RestrictedSide)).Select(s => s.Region).ToArray();
            var remaining = group.Where(match => !explained.Any(region =>
            {
                var x = (points[match.QueryIdx].Pt.X + cropX * 2) / (frame.Width * 2);
                var y = (points[match.QueryIdx].Pt.Y + cropY * 2) / (frame.Height * 2);
                var insetX = (region.Right-region.Left)*.03; var insetY = (region.Bottom-region.Top)*.03;
                return x > region.Left+insetX && x < region.Right-insetX && y > region.Top+insetY && y < region.Bottom-insetY;
            })).ToList();
            if (remaining.Count >= 4) Trace?.Invoke($"{reference.Card.Name}: {remaining.Count} correspondences");
            // Four consistent correspondences are insufficient for an ordinary card
            // event, but can retain a candidate from a small, side-specific deck index.
            // It is tagged separately and can only become evidence when the ledger has
            // just observed printed Reveal-from-deck text and sees it again in time.
            var allowRevealCandidate = pass.RestrictedSide is not null;
            for (var instance = 0; instance < 9 && remaining.Count >= (allowRevealCandidate ? 4 : 6); instance++)
            {
                var from = remaining.Select(match => reference.Points[match.TrainIdx].Pt)
                    .Select(point => new Point2d(point.X, point.Y)).ToArray();
                var to = remaining.Select(match => points[match.QueryIdx].Pt)
                    .Select(point => new Point2d(point.X + cropX * 2, point.Y + cropY * 2)).ToArray();
                using var mask = new Mat();
                using var transform = Cv2.FindHomography(from, to, HomographyMethods.Ransac, 4, mask, 2000, 0.995);
                if (transform.Empty()) break;
                var inliers = Enumerable.Range(0, remaining.Count).Where(index => mask.At<byte>(index) != 0).ToArray();
                Trace?.Invoke($"  inliers {inliers.Length}");
                if (inliers.Length < (allowRevealCandidate ? 4 : 6)) break;
                var corners = Cv2.PerspectiveTransform(new[]
                {
                    new Point2d(0, 0), new Point2d(reference.Width, 0),
                    new Point2d(reference.Width, reference.Height), new Point2d(0, reference.Height),
                }, transform);
                var geometry = TryRegion(corners, frame.Width * 2, frame.Height * 2, out var region);
                var weakRevealGeometry = !geometry && inliers.Length < 6 && pass.RestrictedSide is { } restrictedSide &&
                    TryWeakRevealRegion(to, inliers, frame.Width * 2, frame.Height * 2, restrictedSide, out region);
                var spread = HasSpread(from, inliers, reference.Width, reference.Height);
                Trace?.Invoke($"  {reference.Card.Name} geometry={geometry} weakReveal={weakRevealGeometry} region={region} spread={spread}");
                var appearanceLimit = weakRevealGeometry ? .65 : .75;
                var classification = weakRevealGeometry && pass.RestrictedSide is { } revealSide
                    ? (revealSide, CardSightSource.DeckRevealCandidate)
                    : geometry ? Classify(region, observation, includeBoard) : null;
                if ((geometry || weakRevealGeometry) && spread &&
                    (inliers.Length >= 20 || reference.Appearance.DistanceTo(VisualDescriptor.Create(frame, region)) <= appearanceLimit) &&
                    classification is { } acceptedClassification &&
                    (pass.RestrictedSide is null || acceptedClassification.Item1 == pass.RestrictedSide))
                {
                    var source = inliers.Length < 6 && acceptedClassification.Item2 == CardSightSource.PlayPreview
                        ? CardSightSource.DeckRevealCandidate : acceptedClassification.Item2;
                    // Never weaken board/history acceptance. A four/five-point result
                    // is useful only in the tall, side-specific enlarged-card lane.
                    if (inliers.Length < 6 && source != CardSightSource.DeckRevealCandidate) goto RemoveInliers;
                    result.Add(new CardSighting(reference.Card, acceptedClassification.Item1, source,
                        region, Math.Max(0.08, 0.50 - inliers.Length * 0.02),
                        inliers.Length / (double)remaining.Count,
                        $"{inliers.Length} spatially consistent SIFT features" + (pass.RestrictedSide is { } side ?
                            $" · {side.ToString().ToLowerInvariant()} candidate reference index; ownership still checked separately" : ""),
                        pass.RestrictedSide is not null));
                }
                RemoveInliers:
                var used = inliers.ToHashSet();
                remaining = remaining.Where((_, index) => !used.Contains(index)).ToList();
            }
        }
        }
        }
        var distinct = new List<CardSighting>();
        foreach (var sighting in result.OrderBy(item => item.Distance))
        {
            if (distinct.Any(item => Overlap(item.Region, sighting.Region) > 0.5)) continue;
            distinct.Add(sighting);
        }
        return distinct;
    }

    private static double Overlap(NormalizedRegion first, NormalizedRegion second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left)) *
                      Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return overlap / Math.Min((first.Right - first.Left) * (first.Bottom - first.Top),
            (second.Right - second.Left) * (second.Bottom - second.Top));
    }

    private static bool HasSpread(Point2d[] points, int[] indices, int width, int height)
    {
        var selected = indices.Select(index => points[index]).ToArray();
        return selected.Max(point => point.X) - selected.Min(point => point.X) > width * 0.25 &&
               selected.Max(point => point.Y) - selected.Min(point => point.Y) > height * 0.25;
    }

    private static bool TryRegion(Point2d[] corners, int width, int height, out NormalizedRegion region)
    {
        region = default;
        if (corners.Length != 4 || corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        var left = corners.Min(point => point.X); var right = corners.Max(point => point.X);
        var top = corners.Min(point => point.Y); var bottom = corners.Max(point => point.Y);
        var w = right - left; var h = bottom - top;
        if (w < width * 0.033 || w > width * 0.145 || h < height * 0.085 || h > height * 0.40 ||
            left < 0 || top < 0 || right > width || bottom > height || w / h < 0.48 || w / h > 0.95) return false;
        // Reject folded/extreme homographies which happen to fit repeated background texture.
        if (corners[0].X >= corners[1].X || corners[3].X >= corners[2].X ||
            corners[0].Y >= corners[3].Y || corners[1].Y >= corners[2].Y) return false;
        if (Math.Abs(corners[0].Y - corners[1].Y) > h * 0.25 || Math.Abs(corners[3].Y - corners[2].Y) > h * 0.25) return false;
        region = new NormalizedRegion(left / width, top / height, right / width, bottom / height);
        return true;
    }

    private static bool TryWeakRevealRegion(Point2d[] scene, int[] inliers, int width, int height,
        PlayerSide side, out NormalizedRegion region)
    {
        region = default;
        var points = inliers.Select(index => scene[index]).ToArray();
        if (points.Length is < 4 or > 5) return false;
        var left = points.Min(point => point.X) / width; var right = points.Max(point => point.X) / width;
        var top = points.Min(point => point.Y) / height; var bottom = points.Max(point => point.Y) / height;
        var w = right - left; var h = bottom - top; var x = (left + right) / 2; var y = (top + bottom) / 2;
        if (w is < .025 or > .15 || h is < .06 or > .36 || x is < .76 or > .96 ||
            (side == PlayerSide.Opponent ? y is < .11 or > .43 : y is < .43 or > .72)) return false;
        // Correspondences cover only the textured interior. Expand modestly for the
        // appearance check; the region remains evidence geometry, not a claimed frame.
        region = new(Math.Max(.74, left - w * .18), Math.Max(0, top - h * .15),
            Math.Min(.98, right + w * .18), Math.Min(1, bottom + h * .15));
        return true;
    }

    private static (PlayerSide Side, CardSightSource Source)? Classify(NormalizedRegion region, GwentVisualObservation observation, bool includeBoard)
    {
        var x = (region.Left + region.Right) / 2;
        var y = (region.Top + region.Bottom) / 2;
        // Current-client captures place the enlarged preview around x=.80-.92.
        // Older Gwent footage (including the reviewed Shinmiri archive) uses the
        // same distinctive tall geometry around x=.70-.81. Accept both layouts;
        // the minimum width/height keeps ordinary far-right board cards out.
        if (x > 0.69 && x < 0.95 && region.Right - region.Left > 0.075 && region.Bottom - region.Top > 0.20)
        {
            if (region.Top is > 0.09 and < 0.20 && region.Bottom is > 0.34 and < 0.46)
                return (PlayerSide.Opponent, CardSightSource.PlayPreview);
            if (region.Top is > 0.37 and < 0.48 && region.Bottom is > 0.61 and < 0.73)
                return (PlayerSide.User, CardSightSource.PlayPreview);
        }
        if (observation.View == GwentViewKind.MoveHistory && x is > 0.03 and < 0.18 && y is > 0.235 and < 0.78)
            return (x < 0.10 ? PlayerSide.User : PlayerSide.Opponent, CardSightSource.History);
        if (includeBoard && observation.MatchHudVisible == true && x is > 0.215 and < 0.79 && y is > 0.13 and < 0.83 &&
            region.Right - region.Left < 0.087 && region.Bottom < 0.90)
            return (y < 0.465 ? PlayerSide.Opponent : PlayerSide.User, CardSightSource.Board);
        return null;
    }

    public void Dispose()
    {
        lock (_candidateMatcherLock)
        {
            _knownPlayerMatcher?.Dispose();
            _likelyOpponentMatcher?.Dispose();
        }
        _matcher?.Dispose(); _sift.Dispose();
        foreach (var reference in _references) reference.Descriptors.Dispose();
    }

    public string FeatureFingerprint()
    {
        lock (_candidateMatcherLock)
        {
        using var hash = System.Security.Cryptography.SHA256.Create();
        using var sink = new System.Security.Cryptography.CryptoStream(System.IO.Stream.Null, hash, System.Security.Cryptography.CryptoStreamMode.Write);
        using (var writer = new System.IO.BinaryWriter(sink, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var r in _references) SiftFeatureCache.Write(writer, r.Card.Id, [new(r.Width, r.Height, r.Points, r.Descriptors)]);
        sink.FlushFinalBlock(); return Convert.ToHexString(hash.Hash!);
        }
    }

    private sealed record Source(CardDefinition Card, string Path, bool Variant);
    private sealed record Reference(CardDefinition Card, int Width, int Height, KeyPoint[] Points, Mat Descriptors, VisualDescriptor Appearance);
}
