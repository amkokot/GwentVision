using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class OakEffectProbe
{
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "GwentCompanion", "sessions", "20260827-184848");
        var card = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json")).Single(c => c.Id == "202680");
        var files = Directory.GetFiles(folder, "frame-*.jpg").Where(f =>
            string.CompareOrdinal(Path.GetFileName(f), "frame-001520") >= 0 && string.CompareOrdinal(Path.GetFileName(f), "frame-001650") < 0).Order().ToArray();
        var frames = files.Select(f => (Frame: Load(f), At: new DateTimeOffset(DateTime.ParseExact("20260827" + Path.GetFileNameWithoutExtension(f).Split('-')[2],
            "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture), TimeSpan.FromHours(-4)))).ToList();
        // The compact corpus retains the initializing preview and settled board.
        // Repeat the last immutable settled frame after the production debounce;
        // a retained neighbor may be only 300 ms later and therefore cannot supply
        // the recognizer's independent second vote by itself.
        if(frames.Count>=2) frames.Add((frames[^1].Frame,frames[^1].At.AddMilliseconds(500)));
        CardSighting Title(PlayerSide side) => new(card, side, CardSightSource.PlayPreview, new(.815, .136, .915, .399), .1, 1);
        var clock = Stopwatch.StartNew();
        DevotionVisualCue? Replay(string mode)
        {
            using var detector = new OakcrittersEffectRecognizer();
            DevotionVisualCue? cue = null;
            for (var i = 0; i < frames.Count; i++)
            {
                var (frame, at) = frames[i];
                if (mode == "no-bleed")
                {
                    // Counterfactual ablation, not a real negative match: keep copies,
                    // erase only friendly-board red status pixels.
                    var pixels = (byte[])frame.BgraPixels.Clone();
                    for (var y = (int)(frame.Height * .48); y < frame.Height * .88; y++)
                    for (var x = (int)(frame.Width * .24); x < frame.Width * .79; x++)
                    { var k = (y * frame.Width + x) * 4; if (pixels[k + 2] > pixels[k + 1] * 1.8 && pixels[k + 2] > pixels[k] * 1.6) pixels[k + 2] = pixels[k + 1]; }
                    frame = new(frame.Width, frame.Height, pixels);
                }
                if (mode == "unchanged") frame = frames[0].Frame;
                var titles = i == 0 && mode != "no-title" ? new[] { Title(mode == "user" ? PlayerSide.User : PlayerSide.Opponent) } : [];
                cue ??= detector.Observe(frame, new(GwentViewKind.Board, false, 0, 0, null, IsCardSelectionOverlay: mode == "overlay"),
                    titles, mode == "expired" && i > 0 ? at.AddSeconds(20) : at);
            }
            return cue;
        }
        var positive = Replay("positive");
        if (positive is null) throw new InvalidOperationException("Recorded Oakcritters effect was not recovered.");
        foreach (var mode in new[] { "no-bleed", "unchanged", "no-title", "user", "overlay", "expired" })
            if (Replay(mode) is not null) throw new InvalidOperationException("Oakcritters false cue: " + mode);
        var report = new { Session = "20260827-184848", Frames = frames.Count, Cue = positive,
            NegativeControls = new[] { "copy only (red-status ablation)", "unchanged opening board", "no title", "user title", "overlay", "expired window" },
            ElapsedMilliseconds = clock.ElapsedMilliseconds,
            Scope = "Verified preview initializes runtime-only art template; different subsequent saved frames test effect. Not population-level accuracy." };
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.13-oak-effect.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Oakcritters cue at {positive.At:HH:mm:ss.fff}; {frames.Count} samples from {files.Length} retained frames + six negative controls.");
    }
    public static async Task IntegratedAsync(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var card = catalog.Single(c => c.Id == "202680");
        // One public art reference keeps setup cheap; this test exercises the actual
        // live text/effect path and result propagation, not full-catalog artwork recall.
        using var pipeline = new CardVisionPipeline([(card, Path.Combine(root, "GwentCompanion/cache/portraits/202680.jpg"))], catalog);
        var folder = Path.Combine(root, "GwentCompanion/sessions/20260827-184848");
        var files = Directory.GetFiles(folder, "frame-*.jpg").Where(f =>
            string.CompareOrdinal(Path.GetFileName(f), "frame-001520") >= 0 && string.CompareOrdinal(Path.GetFileName(f), "frame-001620") < 0).Order().ToArray();
        var sequence=files.Select(file=>(File:file,At:new DateTimeOffset(DateTime.ParseExact("20260827" + Path.GetFileNameWithoutExtension(file).Split('-')[2],
            "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),TimeSpan.FromHours(-4)))).ToList();
        if(sequence.Count>=2) sequence.Add((sequence[^1].File,sequence[^1].At.AddMilliseconds(500)));
        DevotionVisualCue? cue = null;
        foreach (var sample in sequence)
        {
            var prepared = await pipeline.PrepareAsync(Load(sample.File),sample.At);
            var result = pipeline.Commit(prepared.TextResult);
            if (result.DevotionCue is not null)
            {
                cue = result.DevotionCue;
                if (pipeline.RecognizePrepared(prepared, false).DevotionCue != cue) throw new InvalidOperationException("Artwork branch discarded effect cue.");
            }
        }
        if (cue is null) throw new InvalidOperationException("Live OCR/effect path failed to deliver recorded Oakcritters cue.");
        Console.WriteLine($"Integrated live text/effect path recovered Devotion at {cue.At:HH:mm:ss.fff}.");
    }
    internal static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
    }
}
