using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
    private readonly bool _candidateScoped;
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
    private readonly HashSet<string> _opponentSummonCandidateIds = new(StringComparer.Ordinal);
    private CardDefinition? _opponentFixedSummonSource;
    private FlannBasedMatcher? _namedSummonMatcher;
    private List<Reference> _namedSummonReferences = [];
    private CardDefinition? _namedSummonSource;
    private string[] _orderedNamedSummonTargetIds = [];
    private bool _namedSummonSequenceFound;
    private int? _observedRound;
    private readonly Dictionary<(PlayerSide Side, string Id), PendingAutomaticAppearance> _pendingAutomaticAppearances = [];
    private int _boardGeneration;
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
        _candidateScoped = scope == VisionReferenceScope.CandidateDecks;
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
    /// An exact hover over an arbitrary board card does not establish its
    /// controller. When the player's complete deck is known, do not let a card
    /// first seen on the opponent side enter the player matcher merely because
    /// it was hovered. Known deck cards and identities already established by a
    /// player play/generated-output pathway remain eligible.
    /// </summary>
    public bool CanSeedPlayerCandidateFromAmbiguousHover(string cardId)
    {
        lock (_candidateMatcherLock)
            return _startingPlayerIds.Length == 0 || _startingPlayerIds.Contains(cardId, StringComparer.Ordinal) ||
                _observedPlayerIds.Contains(cardId);
    }

    /// <summary>
    /// The small colour/layout fallback shares the lazily loaded image cache, but
    /// candidate-scoped recognition must not share controller authority. An image
    /// learned from an opponent hover cannot label an unrelated player preview,
    /// and a pinned player-deck image cannot invent the same card for the opponent.
    /// Explicitly observed/generated identities remain eligible on their side.
    /// </summary>
    public bool CanUseFallback(string cardId, PlayerSide side)
    {
        if (!_candidateScoped) return true;
        lock (_candidateMatcherLock)
        {
            if (side == PlayerSide.User)
                return _startingPlayerIds.Length == 0 || _startingPlayerIds.Contains(cardId, StringComparer.Ordinal) ||
                    _observedPlayerIds.Contains(cardId);
            return _projectedOpponentIds.Contains(cardId, StringComparer.Ordinal) ||
                _observedOpponentIds.Contains(cardId) || _opponentSummonCandidateIds.Contains(cardId) ||
                _orderedNamedSummonTargetIds.Contains(cardId, StringComparer.Ordinal);
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
            // These side-specific indices are small enough that a deeper ANN
            // search is cheap. It materially improves localization when only one
            // or two candidate identities are loaded, where approximate misses
            // otherwise dominate the absolute-distance fallback.
            _knownPlayerMatcher = BuildMatcher(_knownPlayerReferences, searchChecks: 256);
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

    public void ObserveOpponentSummonCandidates(IEnumerable<string> cardIds)
    {
        lock (_candidateMatcherLock)
        {
            var changed = false;
            foreach (var id in cardIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                changed |= _opponentSummonCandidateIds.Add(id);
            if (changed) RebuildLikelyOpponentMatcher();
        }
    }

    public void ObserveOpponentSummonSource(CardDefinition source, IEnumerable<string> cardIds,
        IEnumerable<string>? orderedNamedTargets = null)
    {
        var recurringIds = cardIds.ToArray();
        var namedIds = orderedNamedTargets?.ToArray() ?? [];
        ObserveOpponentSummonCandidates(recurringIds.Concat(namedIds));
        if (Regex.IsMatch(source.AbilityText ?? "", @"\bfrom your deck to the left of this card\b[\s\S]*\bfrom your deck to the right of this card\b",
                RegexOptions.IgnoreCase))
            lock (_candidateMatcherLock) _opponentFixedSummonSource = source;
        lock (_candidateMatcherLock)
        {
            RebuildNamedSummonMatcher(namedIds.Length >= 2 ? namedIds.Prepend(source.Id) : []);
            _namedSummonSource = namedIds.Length >= 2 ? source : null;
            _orderedNamedSummonTargetIds = namedIds.Length >= 2 ? namedIds : [];
            _namedSummonSequenceFound = false;
        }
    }

    private void RebuildNamedSummonMatcher(IEnumerable<string> cardIds)
    {
        var ids = cardIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        _namedSummonMatcher?.Dispose(); _namedSummonMatcher = null;
        _namedSummonReferences = [];
        if (ids.Count < 3)
        {
            _namedSummonSource = null;
            _orderedNamedSummonTargetIds = [];
            _namedSummonSequenceFound = false;
            return;
        }
        var loadClock = Stopwatch.StartNew();
        LoadReferences(_sources.Where(source => ids.Contains(source.Card.Id)));
        ReferenceLoadMilliseconds += loadClock.Elapsed.TotalMilliseconds;
        _namedSummonReferences = _references.Where(reference => ids.Contains(reference.Card.Id)).ToList();
        if (_namedSummonReferences.Select(reference => reference.Card.Id).Distinct(StringComparer.Ordinal).Count() < ids.Count)
            return;
        var matcherClock = Stopwatch.StartNew();
        _namedSummonMatcher = BuildMatcher(_namedSummonReferences, searchChecks: 256);
        MatcherTrainMilliseconds += matcherClock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Retires board-bound candidate indices as soon as a new round banner is authenticated.</summary>
    public void ObserveRoundBoundary(GwentVisualObservation screen)
    {
        var header = (screen.ScreenHeader ?? "").Trim();
        var round = header.Equals("FINAL ROUND", StringComparison.OrdinalIgnoreCase) ? 3 :
            Regex.Match(header, @"^ROUND\s+([123])$", RegexOptions.IgnoreCase) is { Success: true } match
                ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        if (round is null || _observedRound == round) return;
        var crossedRound = round > 1 || _observedRound is not null;
        _observedRound = round;
        if (!crossedRound) return;
        _pendingAutomaticAppearances.Clear();
        _boardGeneration = 0;
        lock (_candidateMatcherLock)
        {
            if (_opponentSummonCandidateIds.Count == 0 && _opponentFixedSummonSource is null && _namedSummonMatcher is null) return;
            _opponentSummonCandidateIds.Clear();
            _opponentFixedSummonSource = null;
            RebuildNamedSummonMatcher([]);
            RebuildLikelyOpponentMatcher();
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

    public void ResetTransientCandidates()
    {
        _pendingAutomaticAppearances.Clear();
        _boardGeneration = 0;
        lock (_candidateMatcherLock)
        {
            _observedRound = null;
            _opponentFixedSummonSource = null;
            var hadTransientCandidates = _opponentSummonCandidateIds.Count > 0 || _namedSummonMatcher is not null;
            _opponentSummonCandidateIds.Clear();
            RebuildNamedSummonMatcher([]);
            if (!hadTransientCandidates) return;
            RebuildLikelyOpponentMatcher();
        }
    }

    private void RebuildLikelyOpponentMatcher()
    {
        // Measured identities win the bounded slots; projection priors fill the tail.
        var ids = _observedOpponentIds.Order(StringComparer.Ordinal)
            .Concat(_opponentSummonCandidateIds.Order(StringComparer.Ordinal))
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
        _likelyOpponentMatcher = BuildMatcher(_likelyOpponentReferences, searchChecks: 256);
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
        // Ordinary play never places cards in the animated leader/avatar margins.
        // Crop those pixels before the 2x SIFT expansion; Move History retains the
        // full frame because its evidence lane is at the far left.
        var croppedBoard = includeBoard && observation.View != GwentViewKind.MoveHistory;
        var cropX = croppedBoard ? (int)(frame.Width * .20) : includeBoard ? 0 : (int)(frame.Width * 0.775);
        var cropY = croppedBoard ? (int)(frame.Height * .08) : includeBoard ? 0 : (int)(frame.Height * 0.095);
        var cropWidth = croppedBoard ? (int)(frame.Width * .78) : includeBoard ? frame.Width : (int)(frame.Width * 0.20);
        var cropHeight = croppedBoard ? (int)(frame.Height * .82) : includeBoard ? frame.Height : (int)(frame.Height * 0.65);
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
                if (CropRectangle(region, frame.Width, frame.Height, cropX, cropY, enlarged.Width, enlarged.Height) is { } rectangle)
                    Cv2.Rectangle(sceneMask, rectangle, Scalar.White, -1);
        }
        var clock = Stopwatch.GetTimestamp();
        _sift.DetectAndCompute(enlarged, sceneMask, out var points, descriptors);
        Timings.Record(includeBoard ? "BoardSift" : "PreviewSift", clock);
        if (points.Length < 8) return [];
        var result = new List<CardSighting>();
        var weakAutomaticAnchors = new List<(CardDefinition Card, PlayerSide Side, Reference Reference,
            int Query, NormalizedRegion Region)>();
        // Reuse the same scene features. Small side-specific indices recover low-texture
        // originals crowded out by the full-catalog ANN search. They are priors only:
        // every result still has to pass visual geometry, appearance and side checks.
        lock (_candidateMatcherLock)
        {
        var passes = new List<(FlannBasedMatcher Matcher,List<Reference> References,PlayerSide? RestrictedSide)>();
        if (_namedSummonMatcher is not null) passes.Add((_namedSummonMatcher,_namedSummonReferences,PlayerSide.Opponent));
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
                ? (points[i].Pt.Y + cropY * 2) / (frame.Height * 2) <= (includeBoard ? .54 : .46)
                : (points[i].Pt.Y + cropY * 2) / (frame.Height * 2) >= (includeBoard ? .39 : .37))).ToArray();
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
        IGrouping<int, DMatch>[] groups;
        var identityCount = pass.References.Select(reference => reference.Card.Id)
            .Distinct(StringComparer.Ordinal).Take(3).Count();
        if (identityCount <= 2)
        {
            // Query->catalog matching needs a different-card neighbour for a useful
            // ratio test. A one-card projection has no such distractor, so match each
            // reference descriptor back into the scene and apply the ordinary spatial
            // nearest-neighbour ratio instead. This preserves localization without
            // loading unrelated card identities or accepting a broad absolute cutoff.
            var reverseMatches = new List<DMatch>();
            using var sceneMatcher = new FlannBasedMatcher(new KDTreeIndexParams(4), new SearchParams(256));
            sceneMatcher.Add([query]); sceneMatcher.Train();
            for (var referenceIndex = 0; referenceIndex < pass.References.Count; referenceIndex++)
            {
                var pairs = sceneMatcher.KnnMatch(pass.References[referenceIndex].Descriptors, 2);
                foreach (var pair in pairs.Where(pair => pair.Length >= 2 && pair[0].Distance < pair[1].Distance * .72))
                    reverseMatches.Add(new DMatch(indices[pair[0].TrainIdx], pair[0].QueryIdx,
                        referenceIndex, pair[0].Distance));
            }
            groups = reverseMatches.GroupBy(match => match.ImgIdx).ToArray();
        }
        else
        {
            var nearest = pass.Matcher.KnnMatch(query,
                Math.Min(includeBoard ? 24 : 6, pass.References.Sum(reference => reference.Descriptors.Rows)));
            foreach (var neighbors in nearest)
                for (var i = 0; i < neighbors.Length; i++)
                    neighbors[i] = new DMatch(indices[neighbors[i].QueryIdx], neighbors[i].TrainIdx,
                        neighbors[i].ImgIdx, neighbors[i].Distance);
            groups = nearest.Where(pair =>
                {
                    if (pair.Length < 2) return false;
                    var cardId = pass.References[pair[0].ImgIdx].Card.Id;
                    var alternatives = pair.Skip(1)
                        .Where(match => pass.References[match.ImgIdx].Card.Id != cardId).ToArray();
                    return alternatives.Length > 0 && pair[0].Distance < alternatives[0].Distance * .70;
                })
                .SelectMany(pair => pair
                    .Where(match => pass.References[match.ImgIdx].Card.Id == pass.References[pair[0].ImgIdx].Card.Id)
                    .GroupBy(match => match.ImgIdx).Select(group => group.First()))
                .GroupBy(match => match.ImgIdx).ToArray();
        }
        clock = Timings.Record(includeBoard ? "BoardKnn" : "PreviewKnn", clock);
        Trace?.Invoke($"Scene points: {points.Length}");
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
            // Five ratio-separated descriptors can be enough for a tiny settled
            // self-summon even when RANSAC cannot solve a stable full-card
            // quadrilateral. Search only compact board-local clusters for printed
            // inherent deck arrivals, then require a strict colour/layout check.
            // The resulting weak sighting is unusable without the ledger's
            // independently observed same-hand deck decrement.
            if (includeBoard && pass.RestrictedSide is { } automaticSide &&
                CompanionCardRules.IsInherentDeckArrival(reference.Card))
            {
                var automaticCandidates = WeakAutomaticRegions(remaining, points, reference.Points, reference.Width, reference.Height,
                        frame.Width * 2, frame.Height * 2, cropX * 2, cropY * 2, automaticSide)
                    .Select(region => (Region: region, Distance: reference.Appearance.DistanceTo(VisualDescriptor.Create(frame, region))))
                    .OrderBy(item => item.Distance).ToArray();
                Trace?.Invoke($"{reference.Card.Name} automatic candidates: " + string.Join(", ",
                    automaticCandidates.Take(8).Select(item => $"{item.Region.Left:F3}/{item.Region.Top:F3}/{item.Region.Right:F3}/{item.Region.Bottom:F3}={item.Distance:F3}")));
                var automatic = automaticCandidates.FirstOrDefault();
                if (automatic.Region != default)
                {
                    result.Add(new CardSighting(reference.Card, automaticSide, CardSightSource.Board, automatic.Region,
                        .40, 1, $"Two geometrically agreeing anchors among five ratio-separated SIFT candidates; appearance distance {automatic.Distance:F3} is diagnostic only · bounded automatic-arrival board fallback; repetition and deck conservation still required · {automaticSide.ToString().ToLowerInvariant()} candidate reference index",
                        NeedsTemporalConfirmation: true));
                    continue;
                }
                weakAutomaticAnchors.AddRange(WeakAutomaticSingleAnchorRegions(remaining, points, reference.Points,
                    reference.Width, reference.Height, frame.Width * 2, frame.Height * 2, cropX * 2, cropY * 2, automaticSide)
                    .Select(candidate => (reference.Card, automaticSide, reference, candidate.Query, candidate.Region)));
            }
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
                // Tiny/low-contrast automatic summons can retain six or more
                // internally consistent features while a border animation folds
                // the projected homography. Only a side-specific candidate whose
                // own printed text permits self-arrival from deck gets a bounded
                // board-region fallback. The ledger still requires an independent
                // same-hand deck decrement before this weak sighting can commit.
                var weakBoardGeometry = !geometry && includeBoard && spread && inliers.Length >= 6 &&
                    pass.RestrictedSide is { } boardSide && CompanionCardRules.IsInherentDeckArrival(reference.Card) &&
                    TryWeakBoardRegion(to, inliers, frame.Width * 2, frame.Height * 2, boardSide, out region);
                Trace?.Invoke($"  {reference.Card.Name} geometry={geometry} weakReveal={weakRevealGeometry} weakBoard={weakBoardGeometry} region={region} spread={spread}");
                var appearanceLimit = weakRevealGeometry ? .65 : weakBoardGeometry ? .58 : .75;
                var appearanceDistance = (geometry || weakRevealGeometry || weakBoardGeometry)
                    ? reference.Appearance.DistanceTo(VisualDescriptor.Create(frame, region)) : double.PositiveInfinity;
                var classification = weakRevealGeometry && pass.RestrictedSide is { } revealSide
                    ? (revealSide, CardSightSource.DeckRevealCandidate)
                    : weakBoardGeometry && pass.RestrictedSide is { } acceptedAutoSide
                        ? (acceptedAutoSide, CardSightSource.Board)
                        : geometry ? Classify(region, observation, includeBoard) : null;
                Trace?.Invoke($"  {reference.Card.Name} appearance={appearanceDistance:F3}/{appearanceLimit:F3} classification={classification}");
                var localizedCandidateBoard = classification?.Item2 == CardSightSource.Board && geometry &&
                    pass.RestrictedSide is not null && inliers.Length >= 8;
                // A side-specific deck/candidate index already bounds identity and
                // controller, and the resulting sighting remains temporally
                // provisional. Twelve ratio-separated, spatially spread RANSAC
                // inliers are therefore sufficient to retain animated popup art;
                // the unrestricted catalog keeps the stricter twenty-inlier gate.
                var nonBoardFeatureOverride = classification?.Item2 != CardSightSource.Board &&
                    (inliers.Length >= 20 || pass.RestrictedSide is not null && inliers.Length >= 12);
                if ((geometry || weakRevealGeometry || weakBoardGeometry) && spread &&
                    (appearanceDistance <= appearanceLimit || localizedCandidateBoard || nonBoardFeatureOverride) &&
                    classification is { } acceptedClassification &&
                    (pass.RestrictedSide is null || acceptedClassification.Item1 == pass.RestrictedSide))
                {
                    var source = inliers.Length < 6 && acceptedClassification.Item2 == CardSightSource.PlayPreview
                        ? CardSightSource.DeckRevealCandidate : acceptedClassification.Item2;
                    // Never weaken board/history acceptance. A four/five-point result
                    // is useful only in the tall, side-specific enlarged-card lane.
                    if (inliers.Length < 6 && source != CardSightSource.DeckRevealCandidate) goto RemoveInliers;
                    var acceptedCard = reference.Card;
                    var appearanceEvidence = "";
                    var relabeled = false;
                    if (source == CardSightSource.Board && geometry && pass.RestrictedSide is not null)
                    {
                        var verified = VerifyBoardIdentity(frame, region, reference.Card, pass.References);
                        if (verified is null) goto RemoveInliers;
                        acceptedCard = verified.Value.Card;
                        appearanceDistance = verified.Value.Distance;
                        appearanceEvidence = verified.Value.Evidence;
                        relabeled = acceptedCard.Id != reference.Card.Id;
                    }
                    var triggeredSummonGeometry = source == CardSightSource.Board &&
                        pass.RestrictedSide == PlayerSide.Opponent &&
                        _opponentSummonCandidateIds.Contains(acceptedCard.Id);
                    result.Add(new CardSighting(acceptedCard, acceptedClassification.Item1, source,
                        region, relabeled ? .32 : Math.Max(0.08, 0.50 - inliers.Length * 0.02),
                        inliers.Length / (double)remaining.Count,
                        $"{inliers.Length} spatially consistent SIFT features" + appearanceEvidence + (triggeredSummonGeometry ?
                            " · legal target of a recently committed printed deck summon; independent settled-board confirmation still required" : weakBoardGeometry ?
                            " · bounded automatic-arrival board fallback; deck conservation still required" : "") +
                        (pass.RestrictedSide is { } side ?
                            $" · {side.ToString().ToLowerInvariant()} candidate reference index; ownership still checked separately" : ""),
                        pass.RestrictedSide is not null || relabeled));
                }
                RemoveInliers:
                var used = inliers.ToHashSet();
                remaining = remaining.Where((_, index) => !used.Contains(index)).ToList();
            }
        }
        }
        }
        // Extremely small/animated automatic arrivals can leave only one stable
        // scene feature per reference scale. Accept a region candidate only when
        // the very same scene keypoint independently selects two scaled references
        // of the same printed self-summoning card. A bounded appearance check is
        // retained here; the ledger additionally requires a second frame and a
        // same-hand deck decrement before this can become an event.
        foreach (var group in weakAutomaticAnchors.GroupBy(item => (item.Card.Id, item.Side)))
        {
            var anchors = group.ToArray();
            (NormalizedRegion Region, double Distance)? best = null;
            for (var first = 0; first < anchors.Length; first++)
            for (var second = first + 1; second < anchors.Length; second++)
            {
                if (anchors[first].Query != anchors[second].Query ||
                    ReferenceEquals(anchors[first].Reference, anchors[second].Reference) ||
                    Overlap(anchors[first].Region, anchors[second].Region) < .85) continue;
                var a = anchors[first].Region; var b = anchors[second].Region;
                var region = new NormalizedRegion((a.Left + b.Left) / 2, (a.Top + b.Top) / 2,
                    (a.Right + b.Right) / 2, (a.Bottom + b.Bottom) / 2);
                var distance = anchors[first].Reference.Appearance.DistanceTo(VisualDescriptor.Create(frame, region));
                if (distance > .80 || best is { } prior && prior.Distance <= distance) continue;
                best = (region, distance);
            }
            if (best is not { } accepted) continue;
            var sample = anchors[0];
            Trace?.Invoke($"{sample.Card.Name} scale-agreeing region {accepted.Region} appearance {accepted.Distance:F3}");
            result.Add(new CardSighting(sample.Card, sample.Side, CardSightSource.Board, accepted.Region,
                .40, 1, $"One scene anchor independently selected two scaled references; appearance distance {accepted.Distance:F3} · bounded automatic-arrival board fallback; repetition and deck conservation still required · {sample.Side.ToString().ToLowerInvariant()} candidate reference index",
                NeedsTemporalConfirmation: true));
        }
        if (includeBoard)
        {
            AddWeakAutomaticPersistence(frame, result);
            AddFixedSummonNeighbors(frame, result);
            AddNamedSummonSequence(frame, result);
        }
        var distinct = new List<CardSighting>();
        foreach (var sighting in result.OrderBy(item => item.Distance))
        {
            if (distinct.Any(item => Overlap(item.Region, sighting.Region) > 0.5)) continue;
            distinct.Add(sighting);
        }
        return distinct;
    }

    private void AddNamedSummonSequence(PixelFrame frame, List<CardSighting> result)
    {
        CardDefinition? source;
        string[] orderedIds;
        CardArtReference[] targetReferences;
        lock (_candidateMatcherLock)
        {
            source = _namedSummonSource;
            orderedIds = _orderedNamedSummonTargetIds.ToArray();
            if (source is null || orderedIds.Length < 2 || _namedSummonSequenceFound) return;
            targetReferences = _artReferences.Where(item => orderedIds.Contains(item.Card.Id, StringComparer.Ordinal)).ToArray();
        }
        if (orderedIds.Any(id => targetReferences.All(item => item.Card.Id != id))) return;
        if (orderedIds.All(id => result.Any(item => item.Side == PlayerSide.Opponent &&
                item.Source == CardSightSource.Board && item.Card.Id == id)))
        {
            lock (_candidateMatcherLock) _namedSummonSequenceFound = true;
            return;
        }

        // This fallback is armed only by an exact freshly committed source whose
        // rules name every target. A cheap normalized-colour sweep nominates a few
        // opponent-row slots; expensive alignment is then paid only for those
        // finalists. The complete adjacent pair/group and mirror order are checked
        // before emitting anything, so global board thresholds remain unchanged.
        var matcher = new CardArtMatcher(targetReferences);
        var seeds = new List<(string Id, double Distance, NormalizedRegion Region)>();
        const double width = .062;
        const double height = .138;
        foreach (var centerY in new[] { .21, .37 })
        for (var centerX = .245; centerX <= .765; centerX += .015)
        {
            var region = new NormalizedRegion(centerX - width / 2, centerY - height / 2,
                centerX + width / 2, centerY + height / 2);
            foreach (var hit in matcher.Rank(frame, region, orderedIds.Length))
                seeds.Add((hit.Card.Id, hit.Distance, region));
        }
        var aligned = new List<(string Id, double Distance, double Margin, NormalizedRegion Region)>();
        foreach (var id in orderedIds)
        foreach (var seed in seeds.Where(item => item.Id == id).OrderBy(item => item.Distance).Take(10))
        {
            var ranked = matcher.RankAligned(frame, seed.Region, orderedIds.Length);
            if (ranked.Count < 2 || ranked[0].Card.Id != id) continue;
            var margin = ranked[1].Distance - ranked[0].Distance;
            if (ranked[0].Distance <= .75 && margin >= .08)
                aligned.Add((id, ranked[0].Distance, margin, ranked[0].Region));
        }
        var candidates = orderedIds.Select(id => aligned.Where(item => item.Id == id)
                .OrderBy(item => item.Distance).Take(8).ToArray()).ToArray();
        if (candidates.Any(items => items.Length == 0)) return;

        (double Score, (string Id, double Distance, double Margin, NormalizedRegion Region)[] Hits)? best = null;
        void Search(int index, List<(string Id, double Distance, double Margin, NormalizedRegion Region)> chosen)
        {
            if (index < candidates.Length)
            {
                foreach (var candidate in candidates[index])
                {
                    if (chosen.Any(item => Overlap(item.Region, candidate.Region) > .35)) continue;
                    chosen.Add(candidate); Search(index + 1, chosen); chosen.RemoveAt(chosen.Count - 1);
                }
                return;
            }
            var rowY = chosen.Average(item => (item.Region.Top + item.Region.Bottom) / 2);
            if (chosen.Any(item => Math.Abs((item.Region.Top + item.Region.Bottom) / 2 - rowY) > .04)) return;
            var leftToRight = chosen.OrderBy(item => (item.Region.Left + item.Region.Right) / 2).ToArray();
            var ids = leftToRight.Select(item => item.Id).ToArray();
            if (!ids.SequenceEqual(orderedIds, StringComparer.Ordinal) &&
                !ids.SequenceEqual(orderedIds.Reverse(), StringComparer.Ordinal)) return;
            for (var i = 1; i < leftToRight.Length; i++)
            {
                var previous = (leftToRight[i - 1].Region.Left + leftToRight[i - 1].Region.Right) / 2;
                var current = (leftToRight[i].Region.Left + leftToRight[i].Region.Right) / 2;
                if (current - previous is < .045 or > .082) return;
            }
            var score = chosen.Sum(item => item.Distance);
            if (best is null || score < best.Value.Score) best = (score, chosen.ToArray());
        }
        Search(0, []);
        if (best is null) return;
        foreach (var hit in best.Value.Hits)
        {
            var card = targetReferences.First(item => item.Card.Id == hit.Id).Card;
            result.RemoveAll(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                Overlap(item.Region, hit.Region) > .50);
            result.Add(new CardSighting(card, PlayerSide.Opponent, CardSightSource.Board, hit.Region, .30, 1,
                $"Clear aligned appearance in the complete ordered {source.Name} summon group ({hit.Distance:F3}, margin {hit.Margin:F3}) · legal target of a recently committed printed deck summon; independent source-relative ledger confirmation still required",
                NeedsTemporalConfirmation: true));
        }
        lock (_candidateMatcherLock) _namedSummonSequenceFound = true;
    }

    private void AddFixedSummonNeighbors(PixelFrame frame, List<CardSighting> result)
    {
        CardDefinition? source;
        CardArtReference[] targetReferences;
        lock (_candidateMatcherLock)
        {
            source = _opponentFixedSummonSource;
            targetReferences = _artReferences.Where(item => _opponentSummonCandidateIds.Contains(item.Card.Id))
                .ToArray();
        }
        if (source is null || targetReferences.Length == 0) return;
        var sourceSight = result.Where(item => item.Side == PlayerSide.Opponent &&
                item.Source == CardSightSource.Board && item.Card.Id == source.Id && item.Distance <= .30)
            .OrderBy(item => item.Distance).FirstOrDefault();
        if (sourceSight is null) return;

        var width = Math.Clamp(sourceSight.Region.Right - sourceSight.Region.Left, .052, .075);
        var height = Math.Clamp(sourceSight.Region.Bottom - sourceSight.Region.Top, width * 2.05, width * 2.42);
        var centerY = (sourceSight.Region.Top + sourceSight.Region.Bottom) / 2;
        var gap = width * .025;
        var matcher = new CardArtMatcher(targetReferences);
        foreach (var placement in new[] { "left", "right" })
        {
            var left = placement == "left" ? sourceSight.Region.Left - gap - width : sourceSight.Region.Right + gap;
            var region = new NormalizedRegion(left, centerY - height / 2, left + width, centerY + height / 2);
            if (region.Left < .215 || region.Right > .79 || region.Top < .10 || region.Bottom > .47) continue;
            var ranked = matcher.RankAligned(frame, region, 3);
            if (ranked.Count < 2 || ranked[0].Distance > .58 || ranked[1].Distance - ranked[0].Distance < .10) continue;
            var best = ranked[0];
            if (result.Any(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                item.Card.Id == best.Card.Id && Overlap(item.Region, best.Region) > .55)) continue;
            result.RemoveAll(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                item.NeedsTemporalConfirmation && Overlap(item.Region, best.Region) > .55);
            result.Add(new CardSighting(best.Card, PlayerSide.Opponent, CardSightSource.Board, best.Region,
                .24, 1,
                $"Clear aligned appearance in the printed {placement} summon slot beside visible {source.Name} ({best.Distance:F3}, margin {ranked[1].Distance - best.Distance:F3}) · legal target of a recently committed printed deck summon; independent source-relative ledger confirmation still required",
                NeedsTemporalConfirmation: true));
        }
    }

    private void AddWeakAutomaticPersistence(PixelFrame frame, List<CardSighting> result)
    {
        _boardGeneration++;
        var fresh = result.Where(sighting => sighting.Source == CardSightSource.Board &&
            sighting.NeedsTemporalConfirmation &&
            (sighting.Evidence ?? "").Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal)).ToArray();
        foreach (var pending in _pendingAutomaticAppearances.ToArray())
        {
            if (_boardGeneration - pending.Value.Generation > 2)
            {
                _pendingAutomaticAppearances.Remove(pending.Key);
                continue;
            }
            if (fresh.Any(sighting => sighting.Side == pending.Key.Side && sighting.Card.Id == pending.Key.Id)) continue;
            var persisted = BestPersistenceRegion(frame, pending.Value.Region, pending.Value.Appearance);
            var persistenceDistance = persisted.Distance;
            Trace?.Invoke($"{pending.Value.Card.Name} localized persistence distance {persistenceDistance:F3}");
            if (persistenceDistance > .58) continue;
            result.Add(new CardSighting(pending.Value.Card, pending.Key.Side, CardSightSource.Board,
                persisted.Region, .40, 1,
                $"Localized board appearance persisted in an independent scan at distance {persistenceDistance:F3} after a scale-agreeing feature candidate · bounded automatic-arrival board fallback; repetition and deck conservation still required · {pending.Key.Side.ToString().ToLowerInvariant()} candidate reference index",
                NeedsTemporalConfirmation: true));
            _pendingAutomaticAppearances.Remove(pending.Key);
        }
        foreach (var sighting in fresh)
            _pendingAutomaticAppearances[(sighting.Side, sighting.Card.Id)] = new(sighting.Card, sighting.Region,
                VisualDescriptor.Create(frame, sighting.Region), _boardGeneration);
    }

    private static (NormalizedRegion Region, double Distance) BestPersistenceRegion(PixelFrame frame,
        NormalizedRegion prior, VisualDescriptor appearance)
    {
        var priorWidth = prior.Right - prior.Left; var priorHeight = prior.Bottom - prior.Top;
        var priorX = (prior.Left + prior.Right) / 2; var priorY = (prior.Top + prior.Bottom) / 2;
        var best = (Region: prior, Distance: double.PositiveInfinity);
        foreach (var scale in new[] { .88, 1d, 1.12 })
        foreach (var dx in new[] { -.018, -.009, 0d, .009, .018 })
        foreach (var dy in new[] { -.045, -.036, -.027, -.018, -.009, 0d, .009, .018, .027, .036, .045 })
        {
            var width = priorWidth * scale; var height = priorHeight * scale;
            var region = new NormalizedRegion(priorX + dx - width / 2, priorY + dy - height / 2,
                priorX + dx + width / 2, priorY + dy + height / 2);
            if (region.Left < .20 || region.Right > .81 || region.Top < .08 || region.Bottom > .90) continue;
            var distance = VisualDescriptor.Create(frame, region).DistanceTo(appearance);
            if (distance < best.Distance) best = (region, distance);
        }
        return best;
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

    private static bool TryWeakBoardRegion(Point2d[] scene, int[] inliers, int width, int height,
        PlayerSide side, out NormalizedRegion region)
    {
        region = default;
        var points = inliers.Select(index => scene[index]).ToArray();
        if (points.Length < 6) return false;
        var left = points.Min(point => point.X) / width; var right = points.Max(point => point.X) / width;
        var top = points.Min(point => point.Y) / height; var bottom = points.Max(point => point.Y) / height;
        var spanX = right - left; var spanY = bottom - top;
        var centerX = (left + right) / 2; var centerY = (top + bottom) / 2;
        if (spanX is < .012 or > .075 || spanY is < .025 or > .19 || centerX is < .22 or > .79 ||
            (side == PlayerSide.Opponent ? centerY is < .15 or > .46 : centerY is < .47 or > .82)) return false;
        var cardWidth = Math.Clamp(Math.Max(spanX * 1.45, spanY / 2.42 * 1.25), .042, .070);
        var cardHeight = cardWidth * 2.42;
        region = new(centerX - cardWidth / 2, centerY - cardHeight / 2,
            centerX + cardWidth / 2, centerY + cardHeight / 2);
        return region.Left >= .20 && region.Right <= .81 && region.Top >= .08 && region.Bottom <= .90;
    }

    private static IEnumerable<NormalizedRegion> WeakAutomaticRegions(List<DMatch> matches, KeyPoint[] scene,
        KeyPoint[] reference, int referenceWidth, int referenceHeight, int width, int height,
        int offsetX, int offsetY, PlayerSide side)
    {
        var regions = new List<NormalizedRegion>();
        foreach (var cardWidth in new[] { .045, .0525, .060, .0675 })
        foreach (var first in matches)
        foreach (var second in matches)
        {
            if (first.QueryIdx == second.QueryIdx) continue;
            var firstScene = scene[first.QueryIdx].Pt; var secondScene = scene[second.QueryIdx].Pt;
            var firstReference = reference[first.TrainIdx].Pt; var secondReference = reference[second.TrainIdx].Pt;
            var sceneDx = Math.Abs(firstScene.X - secondScene.X) / width;
            var sceneDy = Math.Abs(firstScene.Y - secondScene.Y) / height;
            var referenceDx = Math.Abs(firstReference.X - secondReference.X) / referenceWidth;
            var referenceDy = Math.Abs(firstReference.Y - secondReference.Y) / referenceHeight;
            if (sceneDx + sceneDy < .006 || Math.Max(referenceDx, referenceDy) < .10) continue;
            var cardHeight = cardWidth * 2.42;
            NormalizedRegion Predicted(OpenCvSharp.DMatch match)
            {
                var scenePoint = scene[match.QueryIdx].Pt; var referencePoint = reference[match.TrainIdx].Pt;
                var left = (scenePoint.X + offsetX) / width - referencePoint.X / referenceWidth * cardWidth;
                var top = (scenePoint.Y + offsetY) / height - referencePoint.Y / referenceHeight * cardHeight;
                return new(left, top, left + cardWidth, top + cardHeight);
            }
            var a = Predicted(first); var b = Predicted(second);
            var centerX = (a.Left + a.Right + b.Left + b.Right) / 4;
            var centerY = (a.Top + a.Bottom + b.Top + b.Bottom) / 4;
            if (Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2) > cardWidth * .35 ||
                Math.Abs((a.Top + a.Bottom - b.Top - b.Bottom) / 2) > cardHeight * .25) continue;
            if (centerX is < .22 or > .79 ||
                (side == PlayerSide.Opponent ? centerY is < .15 or > .46 : centerY is < .47 or > .82))
                continue;
            var region = new NormalizedRegion(centerX - cardWidth / 2, centerY - cardHeight / 2,
                centerX + cardWidth / 2, centerY + cardHeight / 2);
            if (region.Left < .20 || region.Right > .81 || region.Top < .08 || region.Bottom > .90 ||
                regions.Any(existing => Math.Abs(existing.Left-region.Left)<.002 && Math.Abs(existing.Top-region.Top)<.002 &&
                    Math.Abs(existing.Right-region.Right)<.002 && Math.Abs(existing.Bottom-region.Bottom)<.002)) continue;
            regions.Add(region);
        }
        return regions;
    }

    private static IEnumerable<(int Query, NormalizedRegion Region)> WeakAutomaticSingleAnchorRegions(
        List<DMatch> matches, KeyPoint[] scene, KeyPoint[] reference, int referenceWidth, int referenceHeight,
        int width, int height, int offsetX, int offsetY, PlayerSide side)
    {
        foreach (var match in matches.GroupBy(item => item.QueryIdx).Select(group => group.First()))
        foreach (var cardWidth in new[] { .045, .0525, .060, .0675 })
        {
            var cardHeight = cardWidth * 2.42;
            var scenePoint = scene[match.QueryIdx].Pt; var referencePoint = reference[match.TrainIdx].Pt;
            var left = (scenePoint.X + offsetX) / width - referencePoint.X / referenceWidth * cardWidth;
            var top = (scenePoint.Y + offsetY) / height - referencePoint.Y / referenceHeight * cardHeight;
            var region = new NormalizedRegion(left, top, left + cardWidth, top + cardHeight);
            var centerX = (region.Left + region.Right) / 2; var centerY = (region.Top + region.Bottom) / 2;
            if (centerX is < .22 or > .79 ||
                (side == PlayerSide.Opponent ? centerY is < .15 or > .46 : centerY is < .47 or > .82) ||
                region.Left < .20 || region.Right > .81 || region.Top < .08 || region.Bottom > .90) continue;
            yield return (match.QueryIdx, region);
        }
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

    /// <summary>
    /// SIFT localizes a physical card; the colour/layout descriptor verifies which
    /// candidate owns that rectangle. Only a clear aligned winner may replace the
    /// feature label, and replacement remains temporally guarded downstream.
    /// </summary>
    private static (CardDefinition Card, double Distance, string Evidence)? VerifyBoardIdentity(PixelFrame frame,
        NormalizedRegion region, CardDefinition proposed, IReadOnlyList<Reference> references)
    {
        var art = new CardArtMatcher(references.Select(reference =>
            new CardArtReference(reference.Card, reference.Appearance)));
        var direct = art.Rank(frame, region, 3);
        if (direct.Count == 0) return null;
        if (direct[0].Card.Id == proposed.Id && direct[0].Distance <= .88)
            return (proposed, direct[0].Distance, $" · appearance-verified identity {direct[0].Distance:F3}");
        // A projected quadrilateral can be vertically offset by border animation.
        // Pay for aligned comparison only when the inexpensive descriptor disagrees.
        var aligned = art.RankAligned(frame, region, 3);
        if (aligned.Count == 0) return null;
        if (aligned[0].Card.Id == proposed.Id && aligned[0].Distance <= .80)
            return (proposed, aligned[0].Distance, $" · aligned appearance-verified identity {aligned[0].Distance:F3}");
        var margin = aligned.Count > 1 ? aligned[1].Distance - aligned[0].Distance : double.PositiveInfinity;
        if (aligned[0].Distance <= .62 && margin >= .08)
            return (aligned[0].Card, aligned[0].Distance,
                $" · SIFT-localized region relabeled by clear aligned appearance ({proposed.Name}→{aligned[0].Card.Name}, {aligned[0].Distance:F3}, margin {margin:F3})");
        return null;
    }

    private static Rect? CropRectangle(NormalizedRegion region, int frameWidth, int frameHeight,
        int cropX, int cropY, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor((region.Left * frameWidth - cropX) * 2), 0, width);
        var top = Math.Clamp((int)Math.Floor((region.Top * frameHeight - cropY) * 2), 0, height);
        var right = Math.Clamp((int)Math.Ceiling((region.Right * frameWidth - cropX) * 2), 0, width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Bottom * frameHeight - cropY) * 2), 0, height);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : null;
    }

    public void Dispose()
    {
        lock (_candidateMatcherLock)
        {
            _knownPlayerMatcher?.Dispose();
            _likelyOpponentMatcher?.Dispose();
            _namedSummonMatcher?.Dispose();
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
    private sealed record PendingAutomaticAppearance(CardDefinition Card, NormalizedRegion Region,
        VisualDescriptor Appearance, int Generation);
}
