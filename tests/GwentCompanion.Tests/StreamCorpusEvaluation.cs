using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class StreamCorpusEvaluation
{
    public static async Task<int> RunAsync(string gameRoot, string[] args)
    {
        var inputIndex = Array.IndexOf(args, "--stream-corpus-evaluate");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --stream-corpus-evaluate <window-root>.");
        var root = Path.GetFullPath(args[inputIndex + 1]);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 && outputIndex + 1 < args.Length
            ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(root, "automated.json");
        var selectedWindows = args.Select((value, index) => (value, index))
            .Where(item => item.value == "--window" && item.index + 1 < args.Length)
            .Select(item => args[item.index + 1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cache = Path.Combine(gameRoot, "GwentCompanion", "cache");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        using var pipeline = new CardVisionPipeline(references, cards, Path.Combine(cache, "recognition-features"),
            allowStreamResolution: true) { UseTextCache = true };
        var windows = new List<object>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal)
                     .Where(path => selectedWindows.Count == 0 || selectedWindows.Contains(Path.GetFileName(path))))
        {
            var files = Directory.GetFiles(directory, "frame-*.jpg").OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) continue;
            pipeline.Reset();
            var sanitizer = new StreamFrameSanitizer();
            var roundPrelude = new StreamRoundPreludeFilter();
            var findings = new List<object>(); var eventCards = new Dictionary<string, int>(StringComparer.Ordinal);
            var reliable = 0; var reduced = 0; var outside = 0; var obscured = 0; var deckListFrames = 0;
            foreach (var (file, index) in files.Select((file, index) => (file, index)))
            {
                using var stream = File.OpenRead(file);
                var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var pixels = BitmapFrameAdapter.ToPixelFrame(bitmap);
                var stamp = Path.GetFileNameWithoutExtension(file).Replace("frame-", "", StringComparison.Ordinal);
                var seconds = long.Parse(stamp, CultureInfo.InvariantCulture) / 1000d;
                var sanitized = sanitizer.Apply(pixels);
                var prepared = await pipeline.PrepareAsync(sanitized.Frame, DateTimeOffset.UnixEpoch.AddSeconds(seconds)).ConfigureAwait(false);
                var disposition = prepared.Screen.IsCardSelectionOverlay && prepared.Screen.FrameGeometrySupported
                    ? StreamFrameDisposition.Reduced : StreamFrameQuality.Classify(prepared.Frame, prepared.Screen);
                var deckListLike = !prepared.Screen.IsCardSelectionOverlay && prepared.Screen.FrameGeometrySupported &&
                    (prepared.Screen.MatchHudVisible != true && StreamDeckBuilderGate.LooksLikeDeckList(prepared.Frame) ||
                     StreamDeckBuilderGate.LooksLikeDeckListDespiteApparentHud(prepared.Frame));
                if (deckListLike) { deckListFrames++; disposition = StreamFrameDisposition.OutsideGame; }
                switch (disposition)
                {
                    case StreamFrameDisposition.Reliable: reliable++; break;
                    case StreamFrameDisposition.Reduced: reduced++; break;
                    case StreamFrameDisposition.OutsideGame: outside++; break;
                    default: obscured++; break;
                }
                // Every frame gets preview/title analysis; a 0.5 FPS board cadence
                // plus action-triggered scans keeps the benchmark bounded.
                var includeBoard = (disposition == StreamFrameDisposition.Reliable ||
                        disposition == StreamFrameDisposition.Reduced && sanitized.Corner != StreamMaskedCorner.None) &&
                    (index % 4 == 0 || prepared.NeedsArtwork || prepared.Titles.Count > 0);
                var result = pipeline.Commit(pipeline.RecognizePrepared(prepared, includeBoard));
                var acceptedEvents = roundPrelude.Observe(result.SampledAt, result.Screen, result.Events);
                foreach (var card in acceptedEvents.Select(item => item.Sighting.Card.Name))
                    eventCards[card] = eventCards.GetValueOrDefault(card) + 1;
                if (result.Events.Count == 0 && result.Sightings.Count == 0 && prepared.Screen.ScreenHeader is null &&
                    result.HoveredCard is null && disposition is StreamFrameDisposition.Reliable) continue;
                findings.Add(new
                {
                    Seconds = seconds, Frame = Path.GetFileName(file), Disposition = disposition.ToString(),
                    MaskedCorner = sanitized.Corner.ToString(), DeckListLike = deckListLike,
                    prepared.Screen.ScreenHeader, prepared.Screen.MatchHudVisible, prepared.Screen.IsCardSelectionOverlay,
                    prepared.Screen.UserScore, prepared.Screen.OpponentScore, prepared.Screen.UserHandCount,
                    prepared.Screen.OpponentHandCount, prepared.Screen.UserDeckCount, prepared.Screen.OpponentDeckCount,
                    Hovered = result.HoveredCard?.Name,
                    Sightings = result.Sightings.Select(item => new { item.Card.Id, item.Card.Name, Side = item.Side.ToString(),
                        Source = item.Source.ToString(), item.Distance, item.Margin, item.Evidence }).ToArray(),
                    Events = result.Events.Select(item => new { item.Sighting.Card.Id, item.Sighting.Card.Name,
                        Side = item.Sighting.Side.ToString(), Source = item.Sighting.Source.ToString(), item.Description }).ToArray(),
                    AcceptedEvents = acceptedEvents.Select(item => new { ObservedSeconds = (item.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds,
                        item.Sighting.Card.Id, item.Sighting.Card.Name, Side = item.Sighting.Side.ToString(),
                        Source = item.Sighting.Source.ToString(), item.Description }).ToArray(),
                });
            }
            foreach (var card in roundPrelude.Finish().Select(item => item.Sighting.Card.Name))
                eventCards[card] = eventCards.GetValueOrDefault(card) + 1;
            windows.Add(new { Window = Path.GetFileName(directory), Frames = files.Length, Reliable = reliable, Reduced = reduced,
                Outside = outside, Obscured = obscured, DeckListFrames = deckListFrames, EventCards = eventCards, Findings = findings });
            Console.WriteLine($"WINDOW {Path.GetFileName(directory)} frames={files.Length} reliable={reliable} reduced={reduced} outside={outside} obscured={obscured} events={string.Join(", ", eventCards.Select(item => item.Key + "×" + item.Value))}");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, GeneratedAtUtc = DateTimeOffset.UtcNow, Windows = windows,
            ElapsedSeconds = watch.Elapsed.TotalSeconds, pipeline.OcrCalls, pipeline.OcrCacheHits,
            Note = "Blind stream-window evaluation. Manual labels are stored separately and remain authoritative.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE stream-corpus windows={windows.Count} elapsed={watch.Elapsed.TotalSeconds:F1}s output={output}");
        return 0;
    }
}
