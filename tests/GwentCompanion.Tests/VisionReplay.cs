using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class VisionReplay
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions JournalJson = new() { Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(string[] args, string root, string notes)
    {
        if (args.Contains("--export-review"))
        {
            var reviewArgument = args[Array.IndexOf(args, "--vision-replay") + 1];
            var session = Path.IsPathRooted(reviewArgument) ? reviewArgument : Path.Combine(root, "GwentCompanion", "sessions", reviewArgument);
            var reviewOutputIndex = Array.IndexOf(args, "--output");
            var target = reviewOutputIndex < 0 ? Path.Combine(root, "GwentCompanion", "diagnostics", Path.GetFileName(session) + "-review")
                : Path.GetFullPath(args[reviewOutputIndex + 1]);
            var writer = new ReviewKeyFrameWriter(target);
            var sampler = new PreviewMotionSampler();
            var count = 0;
            foreach (var file in LoadFrames(session, false))
            {
                using var stream = File.OpenRead(file.Path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = decoder.Frames[0];
                writer.Observe(bitmap, file.At, sampler.Measure(BitmapFrameAdapter.ToPixelFrame(bitmap)));
                count++;
            }
            writer.Finish();
            Console.WriteLine($"REVIEW input={count} clips={writer.SavedClips} skipped={writer.SkippedClips} bytes={writer.SavedBytes} output={target}");
            return 0;
        }
        var entries = Directory.GetFiles(notes, "*.xlsx").SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries).ToArray();
        var cache = Path.Combine(root, "GwentCompanion", "cache");
        var decks = new PlayGwentDeckCacheService().LoadCached(entries, Path.Combine(cache, "decks"), 500);
        var cards = BuiltInCardCatalog.Merge(decks.SelectMany(deck => deck.Cards).Select(item => item.Card)
            .Concat(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))));
        var references = VisionReferenceLibrary.Load(cards, cache);
        var excludedTrainingIndex = Array.IndexOf(args, "--exclude-training");
        if (excludedTrainingIndex >= 0)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(args[excludedTrainingIndex + 1]));
            var excluded = manifest.RootElement.EnumerateArray().Select(row => Path.GetFullPath(Path.Combine(cache,
                "observed-art", row.GetProperty("CardId").GetString()!, Path.GetFileName(row.GetProperty("Source").GetString()!))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            references = references.Where(reference => !excluded.Contains(Path.GetFullPath(reference.Path))).ToArray();
            Console.WriteLine("A/B baseline: excluded only the specified new training appearances; full remaining catalog retained.");
        }
        var referenceIndex = Array.IndexOf(args, "--reference-ids");
        if (referenceIndex >= 0)
        {
            var ids = args[referenceIndex + 1].Split(',').ToHashSet();
            references = references.Where(item => ids.Contains(item.Card.Id)).ToArray();
            Console.WriteLine("DEBUG: restricted references; this is not a full-library accuracy evaluation.");
        }
        var rankIndex = Array.IndexOf(args, "--rank-region");
        var ocrIndex = Array.IndexOf(args, "--ocr-region");
        if (ocrIndex >= 0)
        {
            var framePath = Path.Combine(root, "GwentCompanion", "sessions", args[Array.IndexOf(args, "--vision-frame") + 1]);
            var values = args[ocrIndex + 1].Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            using var reader = new ScreenStateRecognizer();
            Console.WriteLine(JsonSerializer.Serialize(await reader.ReadLinesAsync(LoadFrame(framePath),
                new NormalizedRegion(values[0], values[1], values[2], values[3])), Json));
            return 0;
        }
        if (rankIndex >= 0)
        {
            var fileIndex = Array.IndexOf(args, "--vision-frame");
            var framePath = Path.Combine(root, "GwentCompanion", "sessions", args[fileIndex + 1]);
            var values = args[rankIndex + 1].Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var matcher = new CardArtMatcher(references.Select(item => new CardArtReference(item.Card, VisualDescriptor.Create(LoadFrame(item.Path)))));
            foreach (var match in matcher.RankAligned(LoadFrame(framePath), new NormalizedRegion(values[0], values[1], values[2], values[3]), 5))
                Console.WriteLine($"{match.Card.Name} {match.Distance:F3} {match.Region}");
            return 0;
        }
        var stateOnly = args.Contains("--state-only");
        var titlesOnly = args.Contains("--preview-titles-only");
        Console.WriteLine($"Preparing {references.Count} artwork references…");
        Console.WriteLine($"Recognizer assembly: {typeof(FeatureCardRecognizer).Assembly.GetName().Version}");
        using var pipeline = stateOnly || titlesOnly ? null : new CardVisionPipeline(references, cards,
            args.Contains("--no-feature-cache") ? null : Path.Combine(cache, "recognition-features"));
        if (pipeline is not null && args.Contains("--trace-matches")) pipeline.Trace = Console.WriteLine;
        using var screen = stateOnly || titlesOnly ? new ScreenStateRecognizer() : null;
        var titleReader = new PreviewTitleRecognizer(cards);
        if (args.Contains("--trace-matches")) titleReader.Trace = Console.WriteLine;
        var titleLedger = new MatchVisionLedger();
        var regression = args.Contains("--vision-regression");
        var argumentIndex = Array.FindIndex(args, value => value is "--vision-replay" or "--vision-frame");
        var argument = regression ? "20260826-225210" : args[argumentIndex + 1];
        var pathRoot = Path.IsPathRooted(argument) ? argument : Path.Combine(root, "GwentCompanion", "sessions", argument);
        var files = LoadFrames(pathRoot, args.Contains("--include-event-frames")).ToArray();
        VisionFixture[] fixtures = [];
        if (regression)
        {
            fixtures = JsonSerializer.Deserialize<VisionFixture[]>(File.ReadAllText(Path.Combine(root, "GwentCompanion", "tests", "vision-fixtures.json")), Json)!;
            using var training = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "GwentCompanion", "tests", "board-recovery-training.json")));
            var trainingFrames = training.RootElement.EnumerateArray().Select(row => row.GetProperty("Source").GetString()!.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fixtures.Any(fixture => trainingFrames.Contains(fixture.File.Replace('\\', '/'))))
                throw new InvalidDataException("Board recovery training frames must not be used for evaluation.");
            if (fixtures.Any(fixture => fixture.Expected.Any(expected => expected.MinimumCopies < 1)))
                throw new InvalidDataException("Expected copy counts must be positive.");
            files = fixtures.Select((fixture, index) => new ReplayFrame(Path.Combine(root, "GwentCompanion", "sessions", fixture.File), DateTimeOffset.UnixEpoch.AddSeconds(index * 10))).ToArray();
        }
        var subsetIndex = Array.IndexOf(args, "--frames");
        if (subsetIndex >= 0)
        {
            var prefixes = args[subsetIndex + 1].Split(',');
            var selected = files.Select((frame, index) => (frame, index)).Where(item => prefixes.Any(prefix => Path.GetFileName(item.frame.Path).StartsWith("frame-" + prefix + "-", StringComparison.Ordinal))).ToArray();
            if (regression) fixtures = selected.Select(item => fixtures[item.index]).ToArray();
            files = selected.Select(item => item.frame).ToArray();
            Console.WriteLine("Diagnostic subset selected; this is not a complete regression run.");
        }
        var outputIndex = Array.IndexOf(args, "--output");
        var strideIndex = Array.IndexOf(args, "--stride");
        if (strideIndex >= 0)
        {
            if (regression) throw new ArgumentException("Regression fixtures cannot be subsampled.");
            var stride = int.Parse(args[strideIndex + 1], CultureInfo.InvariantCulture);
            if (stride < 1) throw new ArgumentOutOfRangeException(nameof(stride));
            files = files.Where((_, index) => index % stride == 0).ToArray();
        }
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(root, "GwentCompanion", "diagnostics",
            (regression ? "vision-regression" : stateOnly ? "state-replay-" + Path.GetFileName(pathRoot) : "vision-replay-" + Path.GetFileNameWithoutExtension(pathRoot)) + ".json");
        var appendIndex = Array.IndexOf(args, "--append-session");
        var appendStreamingIndex = Array.IndexOf(args, "--append-streaming");
        if (appendStreamingIndex >= 0 && (!regression || pipeline is null || appendIndex >= 0))
            throw new ArgumentException("Append streaming requires full-pipeline regression and no other appended session.");
        if (appendIndex >= 0)
        {
            if (!regression) throw new ArgumentException("Append a session after --vision-regression.");
            var appendStrideIndex = Array.IndexOf(args, "--append-stride");
            var appendStride = appendStrideIndex < 0 ? 1 : int.Parse(args[appendStrideIndex + 1], CultureInfo.InvariantCulture);
            if (appendStride < 1) throw new ArgumentOutOfRangeException(nameof(appendStride));
            var appendPath = Path.Combine(root, "GwentCompanion", "sessions", args[appendIndex + 1]);
            files = files.Concat(LoadFrames(appendPath, false).Where((_, index) => index % appendStride == 0)).ToArray();
        }
        var report = new List<ReplayResult>();
        if (args.Contains("--streaming"))
        {
            if (pipeline is null || regression || args.Contains("--stride") || args.Contains("--frames"))
                throw new ArgumentException("Streaming replay requires a complete session and the full pipeline.");
            return await RunStreamingAsync(args, root, files, pipeline, output);
        }
        var distinct = new HashSet<string>();
        var failures = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var schedule = new VisionScanSchedule();
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            var isFixture = regression && index < fixtures.Length;
            if (isFixture || (regression && index == fixtures.Length)) { pipeline?.Reset(); titleLedger.Reset(); }
            var pixels = LoadFrame(file.Path);
            var result = pipeline is null ? new CardVisionResult(file.At, await screen!.AnalyzeAsync(pixels), [], [], false)
                : await pipeline.AnalyzeAsync(pixels, file.At, includeBoard: !args.Contains("--preview-only") &&
                    (isFixture || !args.Contains("--live-cadence") || schedule.NextIncludesBoard(file.At)));
            if (titlesOnly)
            {
                var sightings = await titleReader.RecognizeAsync(pixels, result.Screen, screen!);
                result = result with { Sightings = sightings, Events = titleLedger.Observe(file.At, result.Screen, sightings, false) };
            }
            var relative = Path.GetRelativePath(Path.Combine(root, "GwentCompanion", "sessions"), file.Path);
            report.Add(new ReplayResult(relative, result));
            if (index % 100 == 99) Console.WriteLine($"PROGRESS {index + 1}/{files.Length} elapsed={watch.Elapsed.TotalSeconds:F1}s");
            if (files.Length == 1 || (stateOnly && result.Screen.IsCardSelectionOverlay)) Console.WriteLine(relative + " " + JsonSerializer.Serialize(result.Screen));
            foreach (var sighting in result.Sightings)
            {
                var key = $"{sighting.Side}:{sighting.Card.Id}:{sighting.Source}";
                if (distinct.Add(key) || files.Length == 1)
                    Console.WriteLine($"{Path.GetFileName(file.Path)} {sighting.Side} {sighting.Source}: {sighting.Card.Name} [{sighting.Evidence}]");
            }
            foreach (var evidence in result.Events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
                Console.WriteLine($"PLAY {file.At:HH:mm:ss.fff} {evidence.Sighting.Side}: {evidence.Sighting.Card.Name}");
            if (isFixture)
            {
                var fixture = fixtures[index];
                var missing = fixture.Expected.Where(expected => result.Sightings.Count(actual => actual.Card.Id == expected.Id && actual.Side == expected.Side && actual.Source == expected.Source) < expected.MinimumCopies).ToArray();
                var extraPreview = result.Sightings.Where(actual => actual.Source == CardSightSource.PlayPreview &&
                    !fixture.Expected.Any(expected => expected.Source == actual.Source && expected.Id == actual.Card.Id && expected.Side == actual.Side)).ToArray();
                var forbidden = result.Sightings.Where(actual => fixture.Forbidden?.Any(expected =>
                    expected.Id == actual.Card.Id && expected.Side == actual.Side && (expected.Source is null || expected.Source == actual.Source)) == true).ToArray();
                var passed = missing.Length == 0 && extraPreview.Length == 0 &&
                             forbidden.Length == 0 &&
                             (!fixture.ExpectNoSightings || result.Sightings.Count == 0) &&
                             (!fixture.ExpectOverlay || result.Screen.IsCardSelectionOverlay);
                if (!passed) failures++;
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {fixture.Note}: missing={string.Join(',', missing.Select(item => item.Id))}; unexpected previews={string.Join(',', extraPreview.Select(item => item.Card.Name))}; forbidden={string.Join(',', forbidden.Select(item => item.Card.Name))}; overlay={result.Screen.IsCardSelectionOverlay}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json));
        var events = report.SelectMany(item => item.Result.Events).ToArray();
        Console.WriteLine($"DONE frames={files.Length} distinct-sightings={distinct.Count} preview-episodes={events.Count(item => item.Sighting.Source == CardSightSource.PlayPreview)} assertion-failures={(regression ? failures.ToString() : "unlabelled replay, not an accuracy score")} elapsed={watch.Elapsed.TotalSeconds:F1}s report={output}");
        if (appendStreamingIndex >= 0)
        {
            pipeline!.Reset();
            var session = Path.Combine(root, "GwentCompanion", "sessions", args[appendStreamingIndex + 1]);
            var streamOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-streaming.json");
            var streamExit = await RunStreamingAsync(args, root, LoadFrames(session, false).ToArray(), pipeline, streamOutput);
            if (streamExit != 0) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<ReplayFrame> LoadFrames(string path, bool includeEvents)
    {
        if (File.Exists(path)) return [new ReplayFrame(path, DateTimeOffset.UnixEpoch)];
        var date = Path.GetFileName(path)[..8];
        var result = Directory.GetFiles(path, "*.jpg").Select(file => new ReplayFrame(file,
            new DateTimeOffset(DateTime.ParseExact(date + Path.GetFileNameWithoutExtension(file).Split('-')[2], "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)))).ToList();
        var eventRoot = Path.Combine(path, "play-events");
        if (includeEvents && Directory.Exists(eventRoot))
        {
            foreach (var eventFile in Directory.GetFiles(eventRoot, "event.json", SearchOption.AllDirectories))
            {
                var record = JsonSerializer.Deserialize<RecordedPlayEvent>(File.ReadAllText(eventFile), Json)!;
                var duration = Regex.Match(record.DetectionNotes, @"over (\d+) ms");
                var milliseconds = duration.Success ? int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                var directory = Path.GetDirectoryName(eventFile)!;
                if (record.BeforeImage is not null) result.Add(new ReplayFrame(Path.Combine(directory, record.BeforeImage), record.DetectedAt.AddMilliseconds(-100)));
                result.Add(new ReplayFrame(Path.Combine(directory, record.DuringImage), record.DetectedAt.AddMilliseconds(milliseconds)));
                if (record.AfterImage is not null) result.Add(new ReplayFrame(Path.Combine(directory, record.AfterImage), record.DetectedAt.AddMilliseconds(milliseconds + 200)));
            }
        }
        return result.OrderBy(item => item.At);
    }

    private static PixelFrame LoadFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
    }

    private static async Task<int> RunStreamingAsync(string[] args, string root, ReplayFrame[] files,
        CardVisionPipeline pipeline, string output)
    {
        var speedIndex = Array.IndexOf(args, "--playback-speed");
        var speed = speedIndex < 0 ? 1 : double.Parse(args[speedIndex + 1], CultureInfo.InvariantCulture);
        if (!double.IsFinite(speed) || speed <= 0 || speed > 10) throw new ArgumentOutOfRangeException(nameof(speed));
        var report = new List<ReplayResult>();
        var processor = new StreamingVisionProcessor<ReplayFrame>(pipeline) { OnError = exception => Console.WriteLine("ERROR " + exception.Message) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var journal = new StreamWriter(Path.ChangeExtension(output, ".jsonl"));
        await foreach (var item in processor.RunAsync(Inputs()))
        {
            var row = new ReplayResult(Path.GetRelativePath(Path.Combine(root, "GwentCompanion", "sessions"), item.Context.Path), item.Result);
            report.Add(row);
            await journal.WriteLineAsync(JsonSerializer.Serialize(row, JournalJson)).ConfigureAwait(false);
            foreach (var evidence in item.Result.Events)
                Console.WriteLine($"EVENT {item.Result.SampledAt:HH:mm:ss.fff} {evidence.Sighting.Side} {evidence.Sighting.Source}: {evidence.Sighting.Card.Name}");
            if (report.Count % 250 == 0)
            {
                await journal.FlushAsync().ConfigureAwait(false);
                Console.WriteLine($"PROGRESS {report.Count}/{files.Length} text; art={processor.ArtworkPasses}; skipped-art={processor.SkippedArtworkFrames}; pending-peak={processor.MaximumPendingFrames}; elapsed={watch.Elapsed.TotalSeconds:F1}s");
            }
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json));
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".summary.json"), JsonSerializer.Serialize(new
        {
            InputFrames = files.Length, OutputFrames = report.Count, PlaybackSpeed = speed,
            processor.PreparedFrames, processor.ArtworkPasses, processor.SkippedArtworkFrames,
            processor.MaximumPendingFrames, processor.Errors, ElapsedSeconds = watch.Elapsed.TotalSeconds,
            pipeline.CachedReferenceImages, pipeline.ComputedReferenceImages, pipeline.OcrCalls, pipeline.OcrCacheHits, pipeline.CachedTextBytes,
            Note = "Shared bounded streaming processor. Accelerated playback is a pressure test, not a measurement of gameplay at normal speed. Input decoding waits for the producer; capture-input drops are not simulated."
        }, Json));
        Console.WriteLine($"DONE streaming frames={report.Count} art={processor.ArtworkPasses} errors={processor.Errors} output={output}");
        return processor.Errors == 0 && report.Count == files.Length ? 0 : 1;

        async IAsyncEnumerable<VisionInput<ReplayFrame>> Inputs([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var motion = new PreviewMotionSampler();
            var priority = new PreviewPriorityWindow();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var due = (file.At - files[0].At).TotalMilliseconds / speed;
                var delay = due - watch.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                var frame = LoadFrame(file.Path);
                var change = motion.Measure(frame);
                yield return new VisionInput<ReplayFrame>(frame, file.At, priority.Score(file.At, Math.Max(change.User, change.Opponent)), file);
            }
        }
    }

    private sealed record ReplayFrame(string Path, DateTimeOffset At);
    private sealed record ReplayResult(string Frame, CardVisionResult Result);
    private sealed record VisionFixture(string File, string Note, ExpectedSighting[] Expected, bool ExpectNoSightings = false, bool ExpectOverlay = false,
        ExpectedSighting[]? Forbidden = null);
    private sealed record ExpectedSighting(string Id, PlayerSide Side, CardSightSource? Source, int MinimumCopies = 1);
}
