using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record StreamScanProgress(string Stage, double Fraction, int GamesFound, string Message);
public sealed record StreamScanResult(StreamSourceIdentity Source, int GamesFound, int GamesSaved,
    int ValidationFramesSaved, double DurationSeconds, string[] RecordPaths, string[] Warnings);
public sealed record StreamScanIndex(StreamGameBoundary[] Games, StreamDeckEvidence[] Decks, double DurationSeconds);

/// <summary>
/// Bounded two-pass VOD analysis. Pass one finds games and occasional deck-builder
/// pages cheaply; pass two gives only those game intervals the full shared vision
/// pipeline. No source video or decoded-frame collection is retained.
/// </summary>
public sealed class StreamArchiveScanner(
    IReadOnlyList<CardDefinition> catalog,
    IReadOnlyList<(CardDefinition Card, string Path)> references,
    string featureCacheDirectory,
    string detectorVersion)
{
    public Action<string>? EventTrace { get; set; }

    public async Task<StreamScanResult> ScanAsync(string mediaSource, StreamSourceIdentity source, string? sourceTitle,
        string? sourceChannel,
        string gameDirectory, string validationDirectory, StreamDeckEvidence? sourceDeck = null,
        IProgress<StreamScanProgress>? progress = null,
        CancellationToken cancellationToken = default, DateTimeOffset? sourcePublishedAtUtc = null,
        DateTimeOffset? scannedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSource);
        var index = await IndexAsync(mediaSource, progress, cancellationToken).ConfigureAwait(false);
        return await ScanIndexedAsync(mediaSource, source, sourceTitle, sourceChannel, gameDirectory,
            validationDirectory, index, sourceDeck, progress, cancellationToken, sourcePublishedAtUtc,
            scannedAtUtc).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyze a previously persisted compact index. This makes interrupted and
    /// targeted validation rescans proportional to the affected games instead of
    /// decoding a long source's index again. The caller still supplies the remote
    /// media source; no frames or video are persisted by the index.
    /// </summary>
    public async Task<StreamScanResult> ScanIndexedAsync(string mediaSource, StreamSourceIdentity source,
        string? sourceTitle, string? sourceChannel, string gameDirectory, string validationDirectory,
        StreamScanIndex index, StreamDeckEvidence? sourceDeck = null,
        IProgress<StreamScanProgress>? progress = null, CancellationToken cancellationToken = default,
        DateTimeOffset? sourcePublishedAtUtc = null, DateTimeOffset? scannedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSource);
        ArgumentNullException.ThrowIfNull(index);
        var warnings = new List<string>();
        var boundaries = index.Games;
        var decks = index.Decks;
        var duration = index.DurationSeconds;
        // All games isolated from one source intentionally share these timestamps.
        // Their StartSeconds/EndSeconds distinguish the games without pretending
        // that an edited upload provides exact real-world match start times.
        var scanTimestampUtc = (scannedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var publicationTimestampUtc = sourcePublishedAtUtc?.ToUniversalTime();
        if (boundaries.Length == 0)
            return new(source, 0, 0, 0, duration, [], ["No reliable GWENT game interval was found."]);

        var store = new StreamGameStore(gameDirectory);
        var paths = new List<string>();
        var retained = 0;
        for (var i = 0; i < boundaries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boundary = boundaries[i];
            var deck = decks.Where(item => item.ObservedSeconds <= boundary.StartSeconds &&
                    boundary.StartSeconds - item.ObservedSeconds <= 15 * 60)
                .OrderByDescending(item => item.ObservedSeconds).FirstOrDefault() ?? sourceDeck;
            progress?.Report(new("Analyzing", i / (double)boundaries.Length, boundaries.Length,
                $"Game {i + 1} of {boundaries.Length} · {Clock(boundary.StartSeconds)}"));
            // Match live detection's candidate-scoped index, and bound a long
            // multi-deck VOD by releasing transient opponent references after
            // each game. Cached player-deck features make reconstruction cheap.
            using var pipeline = new CardVisionPipeline(references, catalog, featureCacheDirectory,
                VisionReferenceScope.CandidateDecks, allowStreamResolution: true) { UseTextCache = true };
            pipeline.SetKnownPlayerDeck(deck?.Cards.Select(item => item.CardId) ?? []);
            var analyzed = await AnalyzeGameAsync(mediaSource, source, sourceTitle, sourceChannel, boundary, deck, pipeline,
                validationDirectory, scanTimestampUtc, publicationTimestampUtc, cancellationToken).ConfigureAwait(false);
            retained += analyzed.ValidationFrames;
            if (analyzed.Record.AnalyzedFrames < 4)
            {
                warnings.Add($"Skipped game {boundary.Index}: too little unobscured evidence.");
                continue;
            }
            paths.Add(store.Save(analyzed.Record));
        }
        StreamValidationRetention.Enforce(validationDirectory);
        progress?.Report(new("Complete", 1, boundaries.Length, $"Saved {paths.Count} compact game record(s)."));
        return new(source, boundaries.Length, paths.Count, retained, duration, paths.ToArray(), warnings.ToArray());
    }

    /// <summary>Run only the cheap first pass. Useful for validating long-form game/deck isolation.</summary>
    public async Task<StreamScanIndex> IndexAsync(string mediaSource, IProgress<StreamScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSource);
        var (boundaries, decks, duration) = await DiscoverAsync(mediaSource, progress, cancellationToken).ConfigureAwait(false);
        return new(boundaries.ToArray(), decks.ToArray(), duration);
    }

    private async Task<(List<StreamGameBoundary> Boundaries, List<StreamDeckEvidence> Decks, double Duration)> DiscoverAsync(
        string mediaSource, IProgress<StreamScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var capture = Open(mediaSource);
        var sourceFps = EffectiveFps(capture);
        var duration = capture.FrameCount > 0 ? capture.FrameCount / sourceFps : 0;
        var stride = Math.Max(1, (int)Math.Round(sourceFps)); // one inexpensive state sample per second
        var segmenter = new StreamGameSegmenter();
        var boundaries = new List<StreamGameBoundary>();
        var decks = new List<StreamDeckEvidence>();
        var visual = new GwentVisualStateDetector();
        using var textScreen = new ScreenStateRecognizer();
        using var builder = new DeckBuilderScanner(catalog);
        var terminalGate = new StreamTerminalGate();
        var newGameGate = new StreamNewGameGate();
        var deckPhase = new StreamDeckPhaseGate();
        using var source = new Mat(); using var resized = new Mat(); using var bgra = new Mat();
        long decoded = 0; double finalSeconds = 0, lastDeckAttempt = -20, lastTerminalProbe = -1,
            lastProgressAt = double.NegativeInfinity;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameNumber = decoded++;
            // Grab advances the decoder without copying/converting a Mat. Only
            // the one-per-second state sample needs a full Retrieve via Read.
            if (frameNumber % stride != 0)
            {
                if (!capture.Grab()) break;
                continue;
            }
            if (!capture.Read(source) || source.Empty()) break;
            var seconds = PositionSeconds(capture, frameNumber, sourceFps);
            finalSeconds = Math.Max(finalSeconds, seconds);
            var frame = ConvertFrame(source, resized, bgra, 1280);
            var prepared = VisionFrameNormalizer.Prepare(frame, allowStreamResolution: true);
            var disposition = StreamFrameDisposition.SkippedObscured;
            GwentVisualObservation? screen = null;
            if (prepared.Supported)
            {
                screen = visual.Analyze(prepared.Frame);
                disposition = StreamFrameQuality.Classify(prepared.Frame, screen);
            }
            // Broadcast deck walkthroughs can accidentally satisfy one HUD anchor
            // and look like a reduced game. Route their repeated deck rows to the
            // deck parser before segmentation so they neither create a fake game
            // nor contaminate the following real game interval.
            var deckListCandidate = prepared.Supported && screen?.IsCardSelectionOverlay != true &&
                (screen?.MatchHudVisible != true && StreamDeckBuilderGate.LooksLikeDeckList(prepared.Frame) ||
                 StreamDeckBuilderGate.LooksLikeDeckListDespiteApparentHud(prepared.Frame));
            // A deck builder cannot open inside an active match. Round-redraw
            // grids are row-like at low resolution and must remain game context.
            var deckListLike = !segmenter.IsActive && deckListCandidate;
            var terminal = false; var newGameStart = false; string? header = null;
            if (prepared.Supported && seconds - lastTerminalProbe >= 1 &&
                (deckListLike || deckPhase.IsActive ||
                 segmenter.IsActive && disposition != StreamFrameDisposition.Reliable))
            {
                lastTerminalProbe = seconds;
                var read = await textScreen.AnalyzeAsync(prepared.Frame).ConfigureAwait(false);
                header = read.ScreenHeader?.Trim().ToUpperInvariant();
                if (!deckPhase.IsActive && !deckListLike && segmenter.IsActive)
                {
                var terminalCandidate = header is "VICTORY" or "DEFEAT" or "DRAW" or "GAME OVER" or "STANDARD MODE"
                    ? header : read.PostMatchExitCue ? "POST MATCH EXIT" : null;
                terminal = terminalGate.Observe(seconds, terminalCandidate);
                newGameStart = newGameGate.Observe(seconds, StreamRoundHeader.IsFirst(header));
                }
            }
            var inDeckPhase = deckPhase.Observe(seconds, deckListLike,
                disposition == StreamFrameDisposition.Reliable, StreamRoundHeader.IsFirst(header));
            if (inDeckPhase)
            {
                disposition = StreamFrameDisposition.OutsideGame;
                terminalGate.Reset(); newGameGate.Reset();
            }
            else if (disposition == StreamFrameDisposition.Reliable)
            {
                terminalGate.Reset();
                newGameGate.Reset();
            }
            var completed = segmenter.Observe(seconds, disposition, terminal, newGameStart);
            if (completed is not null) { boundaries.Add(completed); deckPhase.Reset(); }

            // Deck scans are deliberately sparse and must produce a real leader
            // plus several exact card rows. Menu/overlay prose alone cannot pass.
            if (deckListLike && seconds - lastDeckAttempt >= 20)
            {
                lastDeckAttempt = seconds;
                var page = await ReadBestDeckPageAsync(builder, prepared.Frame).ConfigureAwait(false);
                if (page is not null) MergeDeckEvidence(decks, page, seconds);
            }
            if (seconds - lastProgressAt >= 120)
            {
                lastProgressAt = seconds;
                progress?.Report(new("Indexing", duration > 0 ? Math.Clamp(seconds / duration, 0, 1) : 0,
                    boundaries.Count, $"Finding games · {Clock(seconds)}"));
            }
        }
        var tail = segmenter.Finish(finalSeconds);
        if (tail is not null) boundaries.Add(tail);
        return (boundaries.Where(item => item.EndSeconds - item.StartSeconds >= 20).ToList(), decks, Math.Max(duration, finalSeconds));
    }

    private async Task<(StreamGameRecord Record, int ValidationFrames)> AnalyzeGameAsync(string mediaSource,
        StreamSourceIdentity sourceIdentity, string? sourceTitle, string? sourceChannel, StreamGameBoundary boundary, StreamDeckEvidence? deck,
        CardVisionPipeline pipeline, string validationDirectory, DateTimeOffset scannedAtUtc,
        DateTimeOffset? sourcePublishedAtUtc, CancellationToken cancellationToken)
    {
        using var capture = Open(mediaSource);
        var sourceFps = EffectiveFps(capture);
        capture.Set(VideoCaptureProperties.PosMsec, Math.Max(0, boundary.StartSeconds - 2) * 1000);
        using var source = new Mat(); using var resized = new Mat(); using var bgra = new Mat();
        var cards = new Dictionary<(PlayerSide Side, string Id), CardAggregate>();
        var playerReference = BuildPlayerReference(deck);
        var playerCopyTracker = playerReference is null ? null : new ThinningCopyTracker();
        var playerCopies = playerReference is null ? null : new LiveDeckTracker(PlayerSide.User);
        if (playerCopies is not null) playerCopies.SetFactionPrior(playerReference!.Faction, 10);
        var validation = new BoundedStreamValidationWriter(validationDirectory,
            StreamValidationPrivacy.AnonymousSourceKey(sourceIdentity.Key), boundary.Index);
        double nextProbe = boundary.StartSeconds, nextAnalysis = boundary.StartSeconds,
            fineUntil = boundary.StartSeconds, lastBoardScan = double.NegativeInfinity;
        double priorSeconds = Math.Max(0, boundary.StartSeconds - 2);
        var motion = new PreviewMotionSampler();
        var sanitizer = new StreamFrameSanitizer();
        var roundPrelude = new StreamRoundPreludeFilter();
        using var terminalReader = new ScreenStateRecognizer { UseTextCache = true };
        var terminalScores = new StreamTerminalScoreGate();
        var terminalResults = new StreamTerminalGate();
        (int UserScore, int OpponentScore)? terminalScore = null;
        string? terminalResult = null;
        double nextTerminalSample = Math.Max(boundary.StartSeconds, boundary.EndSeconds - 15);
        int reliable = 0, reduced = 0, skipped = 0;
        int? userScore = null, opponentScore = null, rank = null, mmr = null;
        string? resultHeader = null;
        while (capture.Read(source) && !source.Empty())
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = capture.PosMsec / 1000d;
            if (!double.IsFinite(seconds) || seconds <= priorSeconds) seconds = priorSeconds + 1 / sourceFps;
            priorSeconds = seconds;
            if (seconds > boundary.EndSeconds + 3) break;
            if (seconds < nextProbe) continue;
            var frame = ConvertFrame(source, resized, bgra, 1280);
            var sanitized = sanitizer.Apply(frame);
            // Terminal metadata has a fixed, tiny budget independent of adaptive
            // gameplay/card work. Run it on the already-open stream so long remote
            // scans never need to reopen an expiring HLS URL at completion.
            if (seconds >= nextTerminalSample)
            {
                nextTerminalSample = seconds + .45;
                var terminalScreen = await terminalReader.AnalyzeAsync(sanitized.Frame).ConfigureAwait(false);
                if (terminalScreen.View == GwentViewKind.Board && terminalScreen.MatchHudVisible == true)
                {
                    var terminalOpponent = await OpponentHudRecognizer.ReadScoreCandidateAsync(sanitized.Frame,
                        new(.90, .315, .995, .385), terminalReader).ConfigureAwait(false);
                    var terminalUser = await OpponentHudRecognizer.ReadScoreCandidateAsync(sanitized.Frame,
                        new(.90, .625, .995, .69), terminalReader).ConfigureAwait(false);
                    terminalScore = terminalScores.Observe(seconds, terminalUser, terminalOpponent) ?? terminalScore;
                }
                var terminalHeader = terminalScreen.ScreenHeader?.Trim().ToUpperInvariant();
                var terminalCandidate = terminalHeader is "VICTORY" or "DEFEAT" or "DRAW" ? terminalHeader : null;
                if (terminalResults.Observe(seconds, terminalCandidate)) terminalResult = terminalCandidate;
            }
            var previewMotion = motion.Measure(frame).Maximum;
            if (previewMotion >= .055) fineUntil = Math.Max(fineUntil, seconds + 3);
            var priorityWindow = seconds < fineUntil;
            if (seconds < nextAnalysis && !priorityWindow && previewMotion < .055)
            { nextProbe = seconds + .20; continue; }
            nextProbe = seconds + .20;
            nextAnalysis = seconds + .80;
            var at = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            var prepared = await pipeline.PrepareAsync(sanitized.Frame, at).ConfigureAwait(false);
            var disposition = prepared.Screen.IsCardSelectionOverlay && prepared.Screen.FrameGeometrySupported
                ? StreamFrameDisposition.Reduced : StreamFrameQuality.Classify(prepared.Frame, prepared.Screen);
            var deckListLike = !prepared.Screen.IsCardSelectionOverlay && prepared.Screen.FrameGeometrySupported &&
                (prepared.Screen.MatchHudVisible != true && StreamDeckBuilderGate.LooksLikeDeckList(prepared.Frame) ||
                 StreamDeckBuilderGate.LooksLikeDeckListDespiteApparentHud(prepared.Frame));
            if (deckListLike) disposition = StreamFrameDisposition.OutsideGame;
            var hudChanged = Changed(userScore, prepared.Screen.UserScore) ||
                Changed(opponentScore, prepared.Screen.OpponentScore);
            // Never combine independently confirmed sides from different frames.
            // During end-of-turn cascades that produced impossible hybrid totals
            // such as 87-50 between real 87-41 and 70-50 board states.
            if (prepared.Screen.UserScore is { } pairedUser && prepared.Screen.OpponentScore is { } pairedOpponent)
                (userScore, opponentScore) = (pairedUser, pairedOpponent);
            rank = prepared.Screen.PostMatchRank?.Rank ?? rank;
            mmr = prepared.Screen.PostMatchMmr?.RatingAfter ?? mmr;
            var preparedHeader = prepared.Screen.ScreenHeader?.Trim().ToUpperInvariant();
            if (preparedHeader is "VICTORY" or "DEFEAT" or "DRAW") resultHeader = preparedHeader;
            if (disposition == StreamFrameDisposition.OutsideGame)
                continue;
            if (disposition == StreamFrameDisposition.SkippedObscured)
            {
                skipped++;
                // Fully obscured layouts can contain presenter panels anywhere.
                // Retain only a bounded, detected-corner case that can be safely
                // anonymized without pretending the central board is trustworthy.
                if (sanitized.Corner != StreamMaskedCorner.None && prepared.Screen.FrameGeometrySupported &&
                    validation.ShouldRetain(seconds, difficult: true))
                    validation.Save(source, seconds, "obscured", sanitized.Corner);
                continue;
            }
            if (disposition == StreamFrameDisposition.Reliable) reliable++; else reduced++;
            // A single covered right-hand HUD anchor identifies a bounded corner
            // overlay. Its unsafe preview lane has been masked, while the central
            // board remains usable for summons and settled-card evidence.
            var includeBoard = (disposition == StreamFrameDisposition.Reliable ||
                    disposition == StreamFrameDisposition.Reduced && sanitized.Corner != StreamMaskedCorner.None) &&
                !prepared.Screen.IsCardSelectionOverlay &&
                (prepared.NeedsArtwork || prepared.Titles.Count > 0 || hudChanged || seconds - lastBoardScan >= 2.5);
            var recognized = pipeline.Commit(pipeline.RecognizePrepared(prepared, includeBoard));
            // The live pipeline already applies this conservative, repeated-frame
            // copy resolver. Reuse it offline when a complete player list is known
            // so two physical self-thinners do not collapse into one stream event.
            // Other duplicate identities remain copy-risky in this compact pass.
            playerCopyTracker?.ObserveFrame(at, recognized.Screen, recognized.Sightings,
                recognized.BoardWasScanned, [playerCopies!],
                sight => !CompanionCardRules.ThinningPairs.Contains(sight.Card.Id), playerReference);
            if (EventTrace is not null)
                foreach (var evidence in recognized.Events)
                    EventTrace($"{seconds:F3} {evidence.Sighting.Side}/{evidence.Sighting.Source} " +
                        $"{evidence.Sighting.Card.Name} sight={evidence.Sighting.Evidence} event={evidence.Description}");
            if (includeBoard) lastBoardScan = seconds;
            rank = recognized.Screen.PostMatchRank?.Rank ?? rank;
            mmr = recognized.Screen.PostMatchMmr?.RatingAfter ?? mmr;
            var header = recognized.Screen.ScreenHeader?.Trim().ToUpperInvariant();
            if (header is "VICTORY" or "DEFEAT" or "DRAW") resultHeader = header;
            foreach (var evidence in roundPrelude.Observe(at, recognized.Screen, recognized.Events))
            {
                var key = (evidence.Sighting.Side, evidence.Sighting.Card.Id);
                if (!cards.TryGetValue(key, out var aggregate))
                    cards[key] = aggregate = new(evidence.Sighting.Card, evidence.Sighting.Side, seconds);
                aggregate.Observe(seconds, evidence.Sighting.Source, evidence.ResolvedDeckCopies,
                    evidence.EstablishesDistinctDeckCopy);
            }
            var difficult = (prepared.NeedsArtwork || prepared.Titles.Count > 0) && recognized.Events.Count == 0;
            if (difficult && validation.ShouldRetain(seconds, difficult))
                validation.Save(source, seconds, "unresolved-action", sanitized.Corner);
            if (recognized.Events.Count > 0 || prepared.NeedsArtwork || prepared.Titles.Count > 0 || hudChanged)
                fineUntil = Math.Max(fineUntil, seconds + 4);
        }
        if (terminalScore is { } terminalPair)
            (userScore, opponentScore) = (terminalPair.UserScore, terminalPair.OpponentScore);
        resultHeader = terminalResult ?? resultHeader;
        foreach (var evidence in roundPrelude.Finish())
        {
            var seconds = (evidence.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            var key = (evidence.Sighting.Side, evidence.Sighting.Card.Id);
            if (!cards.TryGetValue(key, out var aggregate))
                cards[key] = aggregate = new(evidence.Sighting.Card, evidence.Sighting.Side, seconds);
            aggregate.Observe(seconds, evidence.Sighting.Source, evidence.ResolvedDeckCopies,
                evidence.EstablishesDistinctDeckCopy);
        }
        if (playerCopies is not null)
        foreach (var observed in playerCopies.Observations.Where(item => item.ObservedCopies > 1))
        {
            var key = (PlayerSide.User, observed.Card.Id);
            var seconds = (observed.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            if (!cards.TryGetValue(key, out var aggregate))
                cards[key] = aggregate = new(observed.Card, PlayerSide.User, seconds);
            aggregate.ObserveCopyFloor(seconds, observed.ObservedCopies, CardSightSource.Board);
        }
        var analyzed = reliable + reduced;
        var reliability = analyzed + skipped == 0 ? 0 : (reliable + reduced * .65) / (analyzed + skipped);
        var record = new StreamGameRecord(2, boundary.Tag(sourceIdentity), sourceIdentity.Key, sourceIdentity.CanonicalUri,
            sourceTitle, sourceChannel, boundary.Index, boundary.StartSeconds, boundary.EndSeconds, scannedAtUtc,
            reliability, analyzed, reduced, skipped,
            cards.Values.Select(item => item.ToRecord()).OrderBy(item => item.FirstSeconds).ToArray(), deck,
            userScore, opponentScore, resultHeader, rank, mmr, detectorVersion, sourcePublishedAtUtc);
        return (record, validation.Saved);
    }

    private DeckDefinition? BuildPlayerReference(StreamDeckEvidence? deck)
    {
        if (deck is not { CompleteEnoughToUse: true }) return null;
        var definitions = catalog.ToDictionary(card => card.Id, StringComparer.Ordinal);
        var entries = deck.Cards.Where(item => item.Copies > 0 && definitions.ContainsKey(item.CardId))
            .Select(item => new DeckCard(definitions[item.CardId], item.Copies)).ToArray();
        if (entries.Sum(item => item.Count) < 25) return null;
        var faction = entries.Select(item => item.Card.Faction)
            .FirstOrDefault(value => !string.Equals(value, "Neutral", StringComparison.OrdinalIgnoreCase)) ?? "Neutral";
        return new DeckDefinition("stream-player-reference", "Stream player reference", faction,
            deck.LeaderName ?? "", 0, entries);
    }

    private static async Task<DeckBuilderPage?> ReadBestDeckPageAsync(DeckBuilderScanner scanner, PixelFrame frame)
    {
        DeckBuilderPage? best = null;
        foreach (var region in new[] { DeckBuilderScanner.LeftPanel, DeckBuilderScanner.RightPanel })
        {
            var page = await scanner.ReadAsync(frame, region).ConfigureAwait(false);
            if (page.Leader is not null && page.Cards.Sum(card => card.Count) >= 5 &&
                (best is null || page.Cards.Sum(card => card.Count) > best.Cards.Sum(card => card.Count))) best = page;
        }
        return best;
    }

    private static void MergeDeckEvidence(List<StreamDeckEvidence> decks, DeckBuilderPage page, double seconds)
    {
        var prior = decks.LastOrDefault();
        var sameEpisode = prior is not null && prior.LeaderId == page.Leader!.Id && seconds - prior.ObservedSeconds <= 120;
        var cards = (sameEpisode ? prior!.Cards : [])
            .Concat(page.Cards.Select(card => new StreamDeckCard(card.Card.Id, card.Card.Name, card.Count)))
            .GroupBy(card => card.CardId, StringComparer.Ordinal).Select(group => group.MaxBy(card => card.Copies)!).ToArray();
        var evidence = new StreamDeckEvidence(page.Leader!.Id, page.Leader.Name, page.Stratagem?.Id,
            page.Stratagem?.Name, cards, seconds, cards.Sum(card => card.Copies) >= 25);
        if (sameEpisode) decks[^1] = evidence; else decks.Add(evidence);
    }

    private static VideoCapture Open(string source)
    {
        var capture = new VideoCapture(source);
        if (!capture.IsOpened()) { capture.Dispose(); throw new InvalidOperationException("The media stream could not be opened."); }
        return capture;
    }

    private static double EffectiveFps(VideoCapture capture) =>
        double.IsFinite(capture.Fps) && capture.Fps is >= 1 and <= 240 ? capture.Fps : 30;

    private static double PositionSeconds(VideoCapture capture, long frame, double fps)
    {
        var seconds = capture.PosMsec / 1000;
        return double.IsFinite(seconds) && seconds > 0 ? seconds : frame / fps;
    }

    private static PixelFrame ConvertFrame(Mat source, Mat resized, Mat bgra, int maximumWidth)
    {
        var selected = source;
        if (source.Width > maximumWidth)
        {
            Cv2.Resize(source, resized, new Size(maximumWidth, (int)Math.Round(source.Height * maximumWidth / (double)source.Width)),
                0, 0, InterpolationFlags.Area); selected = resized;
        }
        if (selected.Channels() == 4) selected.CopyTo(bgra);
        else Cv2.CvtColor(selected, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Width * bgra.Height * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new PixelFrame(bgra.Width, bgra.Height, pixels);
    }

    private static bool Changed(int? prior, int? current) => prior is not null && current is not null && prior != current;
    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString("hh\\:mm\\:ss");

    private sealed class CardAggregate
    {
        private readonly CardDefinition _card; private readonly PlayerSide _side; private readonly double _first;
        private int _events; private int _observedCopies = 1; private double _last; private readonly HashSet<string> _sources = [];
        public CardAggregate(CardDefinition card, PlayerSide side, double first)
        { _card = card; _side = side; _first = first; _last = first; }
        public void Observe(double seconds, CardSightSource source, int? resolvedCopies = null, bool distinctCopy = false)
        {
            _events++; _last = seconds; _sources.Add(source.ToString());
            var floor = Math.Clamp(resolvedCopies ?? 1, 1, _card.IsGold ? 1 : 2);
            if (distinctCopy && _observedCopies > 0) floor = Math.Min(2, Math.Max(floor, _observedCopies + 1));
            _observedCopies = Math.Max(_observedCopies, floor);
        }
        public void ObserveCopyFloor(double seconds, int copies, CardSightSource source)
        {
            _last = Math.Max(_last, seconds); _sources.Add(source.ToString());
            _observedCopies = Math.Max(_observedCopies, Math.Clamp(copies, 1, _card.IsGold ? 1 : 2));
        }
        public StreamDetectedCard ToRecord() => new(_card.Id, _card.Name, _side, _events, _first, _last,
            _sources.OrderBy(value => value, StringComparer.Ordinal).ToArray(), _observedCopies);
    }
}

public static class StreamFrameQuality
{
    public static StreamFrameDisposition Classify(PixelFrame frame, GwentVisualObservation screen)
    {
        if (!screen.FrameGeometrySupported) return StreamFrameDisposition.SkippedObscured;
        if (screen.IsCardSelectionOverlay) return StreamFrameDisposition.OutsideGame;
        if (screen.MatchHudVisible != true) return StreamFrameDisposition.OutsideGame;
        var top = WhiteRatio(frame, new(.955, .020, .981, .052));
        var bottom = WhiteRatio(frame, new(.955, .947, .981, .979));
        if (top >= .08 && bottom >= .08) return StreamFrameDisposition.Reliable;
        if (top >= .08 || bottom >= .08) return StreamFrameDisposition.Reduced;
        return StreamFrameDisposition.SkippedObscured;
    }

    internal static double WhiteRatio(PixelFrame frame, NormalizedRegion region)
    {
        var samples = 0; var hits = 0;
        for (var y = region.PixelTop(frame.Height); y < region.PixelBottom(frame.Height); y += 2)
        for (var x = region.PixelLeft(frame.Width); x < region.PixelRight(frame.Width); x += 2)
        {
            samples++; var color = frame.GetPixel(x, y);
            if (Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 170) hits++;
        }
        return samples == 0 ? 0 : hits / (double)samples;
    }
}

public enum StreamMaskedCorner { None, UpperRight, LowerRight }
public readonly record struct StreamSanitizedFrame(PixelFrame Frame, StreamMaskedCorner Corner);

/// <summary>
/// Removes only the right-side lane whose fixed hand-counter anchor is absent.
/// The copied buffer is short-lived; source video and decoded frame collections
/// are never retained. Other layouts remain governed by quality/skip rules.
/// </summary>
public sealed class StreamFrameSanitizer
{
    private static readonly NormalizedRegion TopAnchor = new(.955, .020, .981, .052);
    private static readonly NormalizedRegion BottomAnchor = new(.955, .947, .981, .979);
    private StreamMaskedCorner _candidate;
    private int _votes;

    public StreamSanitizedFrame Apply(PixelFrame frame)
    {
        var top = StreamFrameQuality.WhiteRatio(frame, TopAnchor) >= .08;
        var bottom = StreamFrameQuality.WhiteRatio(frame, BottomAnchor) >= .08;
        var observed = top == bottom ? StreamMaskedCorner.None :
            top ? StreamMaskedCorner.LowerRight : StreamMaskedCorner.UpperRight;
        if (observed == StreamMaskedCorner.None) { _candidate = StreamMaskedCorner.None; _votes = 0; }
        else if (observed == _candidate) _votes = Math.Min(3, _votes + 1);
        else { _candidate = observed; _votes = 1; }
        // A transient animation can briefly cover one counter. Require three
        // consecutive samples before treating that corner as a broadcast overlay.
        var corner = _votes >= 3 ? observed : StreamMaskedCorner.None;
        if (corner == StreamMaskedCorner.None) return new(frame, corner);
        var pixels = frame.BgraPixels.ToArray();
        var region = corner == StreamMaskedCorner.UpperRight
            ? new NormalizedRegion(.775, 0, 1, .37)
            : new NormalizedRegion(.775, .63, 1, 1);
        for (var y = region.PixelTop(frame.Height); y < region.PixelBottom(frame.Height); y++)
        for (var x = region.PixelLeft(frame.Width); x < region.PixelRight(frame.Width); x++)
        {
            var offset = (y * frame.Width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
            pixels[offset + 3] = byte.MaxValue;
        }
        return new(new PixelFrame(frame.Width, frame.Height, pixels), corner);
    }
}

/// <summary>
/// Holds compact event objects briefly so a late-readable ROUND/REDRAW banner can
/// retract enlarged cards dealt into either hand. This changes only archived
/// stream records; the shared recognizer still learns their valid card artwork.
/// </summary>
public sealed class StreamRoundPreludeFilter
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);
    private readonly List<VisionEvidenceEvent> _pending = [];

    public IReadOnlyList<VisionEvidenceEvent> Observe(DateTimeOffset at, GwentVisualObservation screen,
        IReadOnlyList<VisionEvidenceEvent> events)
    {
        _pending.AddRange(events);
        if (IsRoundPrelude(screen.ScreenHeader))
            _pending.RemoveAll(item => item.Sighting.Source == CardSightSource.PlayPreview &&
                at >= item.ObservedAt && at - item.ObservedAt <= Hold);
        return Drain(item => at >= item.ObservedAt && at - item.ObservedAt >= Hold);
    }

    public IReadOnlyList<VisionEvidenceEvent> Finish() => Drain(_ => true);

    private IReadOnlyList<VisionEvidenceEvent> Drain(Func<VisionEvidenceEvent, bool> predicate)
    {
        var ready = _pending.Where(predicate).OrderBy(item => item.ObservedAt).ToArray();
        if (ready.Length > 0) _pending.RemoveAll(ready.Contains);
        return ready;
    }

    private static bool IsRoundPrelude(string? header)
    {
        var compact = string.Concat((header ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return compact is "REDRAW" or "ROUND" or "ROUND1" or "ROUND2" or "ROUND3" or "FINALROUND";
    }
}

public static class StreamDeckBuilderGate
{
    /// <summary>Cheap repeated-row gate before any deck-builder OCR is attempted.</summary>
    public static bool LooksLikeDeckList(PixelFrame frame)
    {
        var (left, right) = RowBandCounts(frame);
        return left >= 4 || right >= 4;
    }

    public static (int Left, int Right) RowBandCounts(PixelFrame frame) =>
        (RowBands(frame, new(.045, .17, .245, .94)), RowBands(frame, new(.755, .17, .955, .94)));

    /// <summary>
    /// A presenter panel can light one hand-counter anchor on a deck page. Require
    /// a dense left deck list and a sparse right lane; real boards/redraws in the
    /// held-out broadcast have dense right artwork even when their left overlay
    /// also creates horizontal bands.
    /// </summary>
    public static bool LooksLikeDeckListDespiteApparentHud(PixelFrame frame)
    {
        var (left, right) = RowBandCounts(frame);
        return left >= 10 && right <= 6;
    }

    private static int RowBands(PixelFrame frame, NormalizedRegion region)
    {
        var bands = 24; var rows = 0;
        for (var band = 0; band < bands; band++)
        {
            var top = region.Top + (region.Bottom - region.Top) * band / bands;
            var bottom = region.Top + (region.Bottom - region.Top) * (band + 1) / bands;
            var bright = 0; var samples = 0;
            for (var y = (int)(top * frame.Height); y < (int)(bottom * frame.Height); y += 2)
            for (var x = (int)(region.Left * frame.Width); x < (int)(region.Right * frame.Width); x += 3)
            {
                var color = frame.GetPixel(x, y); samples++;
                if (color.Red >= 135 && color.Green >= 120 && color.Blue >= 90 &&
                    Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - Math.Min(color.Red, Math.Min(color.Green, color.Blue)) <= 100) bright++;
            }
            if (samples > 0 && bright / (double)samples is >= .015 and <= .28) rows++;
        }
        return rows;
    }
}

/// <summary>
/// Keep a positively identified deck walkthrough outside match segmentation
/// across hovers, animations, and presenter frames that momentarily break the
/// row fingerprint. Release only on corroborated authentic gameplay.
/// </summary>
public sealed class StreamDeckPhaseGate
{
    private bool _active, _releasedToGame;
    private int _deckVotes, _releaseVotes;
    private double _lastDeckAt = double.NegativeInfinity, _lastReleaseAt = double.NegativeInfinity;
    public bool IsActive => _active;

    public bool Observe(double seconds, bool deckFingerprint, bool reliableMatchFrame, bool firstRound)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_releasedToGame) return false;
        // Once a deck phase exists, explicit gameplay evidence outranks the
        // repeated-row texture shared by a redraw grid.
        if (_active && (reliableMatchFrame || firstRound))
        {
            _releaseVotes = seconds - _lastReleaseAt is >= .35 and <= 3 ? _releaseVotes + 1 : 1;
            _lastReleaseAt = seconds;
            if (_releaseVotes >= 2)
            {
                _active = false; _releasedToGame = true;
                _deckVotes = _releaseVotes = 0;
                _lastDeckAt = _lastReleaseAt = double.NegativeInfinity;
                return false;
            }
            return true;
        }
        if (deckFingerprint)
        {
            _deckVotes = seconds - _lastDeckAt is >= .35 and <= 3 ? _deckVotes + 1 : 1;
            _lastDeckAt = seconds;
            if (_deckVotes >= 2) _active = true;
            _releaseVotes = 0; _lastReleaseAt = double.NegativeInfinity;
            return _active;
        }
        if (!_active) { _deckVotes = 0; return false; }
        _releaseVotes = 0; _lastReleaseAt = double.NegativeInfinity;
        return true;
    }

    public void Reset()
    {
        _active = _releasedToGame = false; _deckVotes = _releaseVotes = 0;
        _lastDeckAt = _lastReleaseAt = double.NegativeInfinity;
    }
}

/// <summary>Reject a single bad result-header OCR without delaying stable result screens.</summary>
public sealed class StreamTerminalGate
{
    private string? _candidate;
    private double _lastAt = double.NegativeInfinity;
    private int _votes;

    public bool Observe(double seconds, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) { Reset(); return false; }
        if (candidate.Equals(_candidate, StringComparison.Ordinal) && seconds - _lastAt is >= .35 and <= 3)
            _votes++;
        else
        {
            _candidate = candidate;
            _votes = 1;
        }
        _lastAt = seconds;
        return _votes >= 2;
    }

    public void Reset() { _candidate = null; _lastAt = double.NegativeInfinity; _votes = 0; }
}

/// <summary>
/// Retain only repeated, same-frame score pairs from a stream interval's bounded
/// terminal tail. Confirming the two sides independently can combine different
/// animation states into a total that never appeared on screen.
/// </summary>
public sealed class StreamTerminalScoreGate
{
    private (int UserScore, int OpponentScore)? _candidate;
    private (int UserScore, int OpponentScore)? _confirmed;
    private double _lastAt = double.NegativeInfinity;

    public (int UserScore, int OpponentScore)? Observe(double seconds, int? userScore, int? opponentScore)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (userScore is null || opponentScore is null)
        {
            // A resolving effect can cover or recolor one counter for a single
            // frame. Keep the preceding same-frame pair briefly, but never let
            // it bridge a longer absence or a different numeric observation.
            if (seconds - _lastAt > 3)
            {
                _candidate = null;
                _lastAt = double.NegativeInfinity;
            }
            return _confirmed;
        }
        var pair = (userScore.Value, opponentScore.Value);
        if (_candidate == pair && seconds - _lastAt is >= .25 and <= 3)
            _confirmed = pair;
        _candidate = pair;
        _lastAt = seconds;
        return _confirmed;
    }

    public void Reset()
    {
        _candidate = null;
        _confirmed = null;
        _lastAt = double.NegativeInfinity;
    }
}

public static class StreamRoundHeader
{
    public static bool IsFirst(string? header)
    {
        var compact = string.Concat((header ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return compact is "ROUND1" or "ROUNDI" or "ROIIND1" or "ROIINDI" or "ROIJND1" or "ROIJNDI";
    }
}

/// <summary>Corroborate a ROUND 1 cue so later-round OCR noise cannot split a match.</summary>
public sealed class StreamNewGameGate
{
    private double _lastAt = double.NegativeInfinity;
    private int _votes;

    public bool Observe(double seconds, bool firstRound)
    {
        if (!firstRound) { Reset(); return false; }
        _votes = seconds - _lastAt is >= .35 and <= 3 ? _votes + 1 : 1;
        _lastAt = seconds;
        return _votes >= 2;
    }

    public void Reset() { _lastAt = double.NegativeInfinity; _votes = 0; }
}

internal sealed class BoundedStreamValidationWriter(string root, string source, int game)
{
    private const int MaximumFrames = 8;
    private const int MaximumSourceFrames = 24;
    private double _lastAt = double.NegativeInfinity;
    private readonly string _sourceRoot = Path.Combine(root, source);
    private readonly int _existingSourceFrames = Directory.Exists(Path.Combine(root, source))
        ? Directory.GetFiles(Path.Combine(root, source), "*.jpg", SearchOption.AllDirectories).Length : 0;
    public int Saved { get; private set; }
    public bool ShouldRetain(double seconds, bool difficult) => difficult && Saved < MaximumFrames &&
        _existingSourceFrames + Saved < MaximumSourceFrames && seconds - _lastAt >= 4;
    public void Save(Mat frame, double seconds, string reason, StreamMaskedCorner corner)
    {
        if (Saved >= MaximumFrames) return;
        var directory = Path.Combine(_sourceRoot, $"game-{game:000}"); Directory.CreateDirectory(directory);
        using var output = new Mat();
        if (frame.Width > 720) Cv2.Resize(frame, output, new Size(720, (int)Math.Round(frame.Height * 720d / frame.Width)),
            0, 0, InterpolationFlags.Area); else frame.CopyTo(output);
        StreamValidationPrivacy.Apply(output, corner);
        var safeReason = new string(reason.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-').ToArray());
        Cv2.ImWrite(Path.Combine(directory, $"{(long)Math.Round(seconds * 1000):000000000}-{safeReason}.jpg"), output,
            [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
        _lastAt = seconds; Saved++;
    }
}

/// <summary>
/// Irreversible privacy transform for automatically retained stream evidence.
/// The left identity/display lane is always removed; a detected right overlay is
/// removed as well. OpenCV re-encoding writes no source EXIF or container metadata.
/// </summary>
public static class StreamValidationPrivacy
{
    public static string AnonymousSourceKey(string sourceKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return "source-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static void Apply(Mat frame, StreamMaskedCorner corner)
    {
        if (frame.Empty()) return;
        var dark = new Scalar(16, 16, 16);
        Cv2.Rectangle(frame, new Rect(0, 0, Math.Max(1, (int)Math.Ceiling(frame.Width * .24)), frame.Height), dark, -1);
        if (corner == StreamMaskedCorner.UpperRight)
            Cv2.Rectangle(frame, new Rect((int)(frame.Width * .77), 0,
                frame.Width - (int)(frame.Width * .77), (int)Math.Ceiling(frame.Height * .40)), dark, -1);
        else if (corner == StreamMaskedCorner.LowerRight)
            Cv2.Rectangle(frame, new Rect((int)(frame.Width * .77), (int)(frame.Height * .60),
                frame.Width - (int)(frame.Width * .77), frame.Height - (int)(frame.Height * .60)), dark, -1);
    }
}

internal static class StreamValidationRetention
{
    private const long MaximumBytes = 64L * 1024 * 1024;
    public static void Enforce(string root)
    {
        if (!Directory.Exists(root)) return;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories).Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var bytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (bytes <= MaximumBytes) break;
            if (!file.FullName.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Stream validation target escaped its cache root.");
            bytes -= file.Length; file.Delete();
        }
        // Remove only now-empty leaf folders; source/game evidence containing a
        // retained difficult frame is never disturbed.
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
