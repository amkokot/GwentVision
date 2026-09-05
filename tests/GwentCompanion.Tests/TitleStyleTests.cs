using System.IO;
using System.Diagnostics;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;

internal static class TitleStyleTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static PixelFrame Resize(PixelFrame frame, int width, double exposure = 1)
    {
        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, (int)Math.Round(frame.Height * width / (double)frame.Width)), 0, 0, InterpolationFlags.Area);
        var bytes = new byte[resized.Width * resized.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(resized.Data, bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i++) if (i % 4 != 3) bytes[i] = (byte)Math.Clamp((int)Math.Round(bytes[i] * exposure), 0, 255);
        return new(resized.Width, resized.Height, bytes);
    }

    public static async Task Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var path = Path.Combine(root, "GwentCompanion/sessions/20260831-102406/play-events/20260831-103340773-Opponent/during.png");
        var frame = OakEffectProbe.Load(path);
        using var oldReader = new ScreenStateRecognizer(); using var reader = new ScreenStateRecognizer();
        var baseline = new PreviewTitleRecognizer(catalog) { UseTitleStyleFallback = false };
        var improved = new PreviewTitleRecognizer(catalog);
        var screen = new GwentVisualStateDetector().Analyze(frame);
        Check((await baseline.RecognizeAsync(frame, screen, oldReader)).Count == 0, "Baseline calibration frame unexpectedly recognized.");
        var hits = await improved.RecognizeAsync(frame, screen, reader);
        Check(hits is [{ Card.Name: "Fire Scorpion", Side: PlayerSide.Opponent, NeedsTemporalConfirmation: true, IsSupplementalTitle: true }],
            "Adaptive title did not recover Fire Scorpion conservatively.");
        Check(improved.HasUnresolvedHeader, "Supplemental title disabled artwork scheduling.");
        Check(reader.OcrCalls <= oldReader.OcrCalls + 4, "Fallback exceeded two OCR calls per preview lane.");
        var count = reader.OcrCalls;
        Check((await improved.RecognizeAsync(frame, screen with { IsCardSelectionOverlay = true }, reader)).Count == 0 && reader.OcrCalls == count,
            "Choice overlay ran title fallback.");
        var crop = new NormalizedRegion(.665,.255,.82,.30);
        var lines = await reader.ReadTitleLinesAsync(frame, crop);
        var exact = lines.Single(line => line.Text == "FIRE SCORPION");
        var calls = reader.OcrCalls;
        Check((await reader.ReadTitleLinesAsync(frame, crop)).SequenceEqual(lines) && reader.OcrCalls == calls,
            "Style cache failed to preserve exact text/coordinates.");
        Check(TitleTextStyle.CoversBrightText(frame, exact.Region, crop), "Complete title failed coverage.");
        Check(!TitleTextStyle.CoversBrightText(frame, exact.Region with { Left = .736 }, crop), "Dropped title prefix was accepted.");
        Check(!TitleTextStyle.IsBrightTitleLine(frame, new(.675,.351,.796,.376)), "Parchment ability text qualified as a title.");
        foreach (var cards in new[] { catalog.Where(card => card.Name != "Fire Scorpion").ToArray(),
            catalog.Append(catalog.Single(card => card.Name == "Fire Scorpion") with { Id = "ambiguous-title" }).ToArray() })
            Check((await new PreviewTitleRecognizer(cards).RecognizeAsync(frame, screen, reader)).Count == 0,
                "Missing/ambiguous catalog identity was guessed from a nearest name or category.");

        var flat = new PixelFrame(160,100,Enumerable.Repeat((byte)220,160*100*4).ToArray());
        Check(TitleTextStyle.TryBuild(flat,new(.1,.1,.4,.3),4) is null, "Flat/parchment colour triggered style OCR.");
        Check(TitleTextStyle.TryBuild(frame,new(0,0,1,1),4) is null, "Style transform allowed unbounded full-frame work.");
        var transformed = TitleTextStyle.TryBuild(frame,crop,4)!;
        Check(transformed.Length == (crop.PixelRight(frame.Width)-crop.PixelLeft(frame.Width))*4*
            (crop.PixelBottom(frame.Height)-crop.PixelTop(frame.Height))*4*4, "Style dimensions changed.");
        Check(transformed.Where((_,i)=>i%4==3).All(value=>value==255) && transformed.Where((_,i)=>i%4!=3).Any(value=>value is > 0 and < 255),
            "Style destroyed opacity or antialiased letter edges.");
        var original = frame.BgraPixels.ToArray();
        _ = TitleTextStyle.TryBuild(frame,crop,4);
        Check(frame.BgraPixels.SequenceEqual(original), "Style preprocessing mutated shared artwork pixels.");

        var board = screen with { MatchHudVisible=true }; var at = DateTimeOffset.UnixEpoch;
        var ledger = new MatchVisionLedger();
        Check(ledger.Observe(at,board,hits).Count==0, "One styled title became a play.");
        Check(ledger.Observe(at.AddMilliseconds(250),board,hits).Count==1, "Repeated exact styled title did not confirm.");
        Check(ledger.Observe(at.AddMilliseconds(500),board,hits).Count==0, "Styled title duplicated a play.");
        ledger.Reset(); ledger.Observe(at,board,hits);
        ledger.Observe(at.AddMilliseconds(100),board with { IsCardSelectionOverlay=true },[]);
        Check(ledger.Observe(at.AddMilliseconds(250),board,hits).Count==0, "Overlay retained a styled confirmation vote.");
        var styled=hits.Single(); var art=styled with { Distance=.1,NeedsTemporalConfirmation=false,IsSupplementalTitle=false,Evidence="art" };
        Check(CardVisionPipeline.MergePreviewEvidence([art],[styled]).SequenceEqual(new[]{art}), "Styled fallback delayed existing matching art.");
        var otherArt=art with {Card=catalog.Single(card=>card.Name=="Cat Witcher")};
        Check(CardVisionPipeline.MergePreviewEvidence([otherArt],[styled]).SequenceEqual(new[]{otherArt}), "Styled fallback replaced existing conflicting strong art.");
        Check(CardVisionPipeline.MergePreviewEvidence([otherArt],[styled with {IsSupplementalTitle=false}]).Single().Card==styled.Card,
            "Legacy title-over-art policy changed.");
        Check(CardVisionPipeline.MergePreviewEvidence([],hits).SequenceEqual(hits), "Artwork miss erased supplemental title.");

        var stress=0; var recovered=0;
        foreach (var (file,name) in new[] {
            ("20260831-102406/play-events/20260831-103340773-Opponent/during.png","Fire Scorpion"),
            ("20260827-081435/frame-000752-081550234.jpg","Barbegazi"),
            ("20260827-101339/frame-001338-101552792.jpg","Cat Witcher"),
            ("20260827-101339/frame-004715-102130490.jpg","Mysteries of Loc Feainn"),
            ("20260827-081435/frame-005771-082412135.jpg","Arachas Queen"),
            ("20260827-184848/frame-000590-184947189.jpg","Miner") })
        {
            var source=OakEffectProbe.Load(Path.Combine(root,"GwentCompanion/sessions",file));
            foreach(var width in new[]{960,1280,1920}) foreach(var exposure in new[]{.85,1,1.10})
            {
                var pixels=Resize(source,width,exposure); var visual=new GwentVisualStateDetector().Analyze(pixels);
                var before=await baseline.RecognizeAsync(pixels,visual,oldReader);
                var after=await improved.RecognizeAsync(pixels,visual,reader);
                Check(before.All(after.Contains),"Existing title changed under resolution/exposure variation.");
                Check(after.Except(before).All(hit=>hit.Card.Name==name&&hit.Side==PlayerSide.Opponent&&hit.NeedsTemporalConfirmation),
                    "New incorrect identity under resolution/exposure variation: "+file);
                recovered+=after.Except(before).Count(); stress++;
            }
        }
        Console.WriteLine($"PASS title style: real missed title, exact/ambiguous/absent names, crop coverage, shared-pixel immutability, bounded cache/calls, overlay/temporal/artwork safety; {stress} resolution/exposure variants, {recovered} extra correct sightings.");
    }

    public static async Task Audit(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        using var oldReader = new ScreenStateRecognizer(); using var newReader = new ScreenStateRecognizer(); using var stateReader = new ScreenStateRecognizer();
        var baseline = new PreviewTitleRecognizer(catalog) { UseTitleStyleFallback = false };
        var improved = new PreviewTitleRecognizer(catalog);
        var files = new List<string>(); var extra = new List<object>();
        foreach (var session in new[] { "20260831-102406", "20260827-081435", "20260827-101339", "20260827-184848" })
        {
            var folder = Path.Combine(root, "GwentCompanion/sessions", session);
            if (Directory.Exists(Path.Combine(folder, "play-events")))
                files.AddRange(Directory.GetFiles(Path.Combine(folder, "play-events"), "during.png", SearchOption.AllDirectories));
            // Deterministic general-board/hover/overlay controls, not chosen by recognized card.
            files.AddRange(Directory.GetFiles(folder, "frame-*.jpg").Order(StringComparer.Ordinal).Where((_, i) => i % 160 == 0));
        }
        var clock = Stopwatch.StartNew(); var index = 0; var unchanged = 0;
        double oldMilliseconds=0, newMilliseconds=0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var frame = OakEffectProbe.Load(file); var screen = await stateReader.AnalyzeAsync(frame);
            var timer=Stopwatch.StartNew();
            var before = await baseline.RecognizeAsync(frame, screen, oldReader);
            oldMilliseconds+=timer.Elapsed.TotalMilliseconds; timer.Restart();
            var after = await improved.RecognizeAsync(frame, screen, newReader);
            newMilliseconds+=timer.Elapsed.TotalMilliseconds;
            foreach (var sight in before)
                if (!after.Contains(sight)) throw new InvalidOperationException("Existing title changed: " + file);
            var added = after.Except(before).ToArray(); unchanged += before.Count;
            if (added.Length > 0)
            {
                Console.WriteLine("ADDED " + file + " " + string.Join('|', added.Select(hit => hit.Side + ":" + hit.Card.Name)));
                extra.Add(new { File = file, Sightings = added.Select(hit => new { hit.Side, hit.Card.Id, hit.Card.Name, hit.NeedsTemporalConfirmation }) });
            }
            if (++index % 40 == 0) Console.WriteLine($"Audited {index} frames in {clock.Elapsed.TotalSeconds:F1}s");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Frames = index, ExistingPreserved = unchanged, Added = extra,
            Seconds = clock.Elapsed.TotalSeconds, BaselineOcrCalls = oldReader.OcrCalls, UpdatedOcrCalls = newReader.OcrCalls,
            BaselineMilliseconds=oldMilliseconds, UpdatedMilliseconds=newMilliseconds }));
    }

    public static async Task Probe(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer(); var titles = new PreviewTitleRecognizer(catalog) { UseTitleStyleFallback = false };
        var improved = new PreviewTitleRecognizer(catalog);
        foreach (var file in new[] {
            "20260831-102406/play-events/20260831-103340773-Opponent/during.png",
            "20260827-184848/frame-000590-184947189.jpg",
            "20260827-101339/frame-001338-101552792.jpg",
            "20260827-081435/frame-000752-081550234.jpg",
            "20260827-101339/frame-004715-102130490.jpg",
            "20260827-081435/frame-005771-082412135.jpg" })
        {
            var frame = OakEffectProbe.Load(Path.Combine(root, "GwentCompanion/sessions", file));
            var screen = await reader.AnalyzeAsync(frame);
            Console.WriteLine("FRAME " + file);
            Console.WriteLine("EXISTING " + string.Join('|', (await titles.RecognizeAsync(frame, screen, reader)).Select(hit => hit.Card.Name)));
            Console.WriteLine("IMPROVED " + string.Join('|', (await improved.RecognizeAsync(frame, screen, reader)).Select(hit => hit.Card.Name + ":" + hit.NeedsTemporalConfirmation)));
            foreach (var region in new[] { new NormalizedRegion(.645,.137,.81,.355), new(.665,.255,.82,.30), new(.645,.137,.81,.19) })
                Console.WriteLine("STYLE " + region + " " + string.Join('|', (await reader.ReadTitleLinesAsync(frame, region)).Select(line => line.Text + " " + line.Region)));
        }
    }
}
