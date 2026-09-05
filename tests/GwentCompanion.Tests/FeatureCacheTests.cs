using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class FeatureCacheTests
{
    public static void Run(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var catalog = BuiltInCardCatalog.Merge(DeckLibrary.Load(Path.Combine(cache, "deck-library.json")).Decks.SelectMany(d => d.Cards).Select(c => c.Card)
            .Concat(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))));
        var references = VisionReferenceLibrary.Load(catalog, cache);
        var directory = Path.Combine(cache, "recognition-features");
        var files = new[] { "frame-000001-112523653.jpg", "frame-000281-112553672.jpg", "frame-000319-112557730.jpg",
            "frame-001561-112819161.jpg", "frame-002819-113039197.jpg" };
        var frames = files.Select(name =>
        {
            using var source = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions/20260828-112523", name));
            return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        }).ToArray();
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        var clock = Stopwatch.StartNew();
        string fingerprint; string[] expected; int count;
        using (var cold = new FeatureCardRecognizer(references, directory, useCachedFeatures: false))
        {
            var coldSeconds = clock.Elapsed.TotalSeconds;
            fingerprint = cold.FeatureFingerprint(); count = cold.ReferenceCount;
            expected = frames.Select(frame => JsonSerializer.Serialize(cold.Recognize(frame, screen))).ToArray();
            Console.WriteLine($"Cold: {coldSeconds:F2}s; {cold.ComputedImages} images; {count} reference scales; fingerprint {fingerprint}");
        }
        clock.Restart();
        using (var warm = new FeatureCardRecognizer(references, directory))
        {
            Console.WriteLine($"Warm: {clock.Elapsed.TotalSeconds:F2}s; hits={warm.CachedImages}; computed={warm.ComputedImages}");
            if (warm.ComputedImages != 0 || warm.CachedImages != references.Count || warm.ReferenceCount != count || warm.FeatureFingerprint() != fingerprint)
                throw new InvalidOperationException("Warm-cache features differ from recomputed features.");
            for (var i = 0; i < frames.Length; i++)
                if (JsonSerializer.Serialize(warm.Recognize(frames[i], screen)) != expected[i]) throw new InvalidOperationException("Warm detection differs: " + files[i]);
        }
        // Corruption is confined to disposable derived data; constructor must recompute it.
        var corrupt = Directory.GetFiles(directory, "*.gz").First();
        File.WriteAllBytes(corrupt, [1, 2, 3]);
        using (var repaired = new FeatureCardRecognizer(references, directory))
            if (repaired.ComputedImages != 1 || repaired.FeatureFingerprint() != fingerprint) throw new InvalidOperationException("Corrupt feature cache lost or changed references.");
        Console.WriteLine($"PASS lossless full-library features, exact latest-frame detections, corrupt-cache repair; cache={Directory.GetFiles(directory, "*.gz").Sum(p => new FileInfo(p).Length) / 1048576.0:F1} MiB.");
    }
}
