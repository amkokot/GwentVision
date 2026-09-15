using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class PostMatchScoreTests
{
    public static void Calibrate(string root)
    {
        var glyphs = new List<HudGlyph>();
        foreach (var source in new[] {
            (File:"20260901-215818/frame-008264-221205247.jpg", Labels:new[]{"56","55","84","125","75","33"}),
            (File:"20260902-100300/frame-005907-101251706.jpg", Labels:new[]{"61","73","12","39","0","0"}) })
        {
            var frame = OakEffectProbe.Load(Path.Combine(root,"GwentCompanion/sessions",source.File));
            var index=0;
            foreach (var top in new[] { .347,.514,.680 })
            foreach (var left in new[] { .405,.535 })
                glyphs.AddRange(HudDigitReader.Train(frame,new(left+.009,top,left+.048,top+.069),source.Labels[index++]));
        }
        File.WriteAllText(Path.Combine(root,"GwentCompanion/assets/vision/result-score-digits.json"),JsonSerializer.Serialize(glyphs));
        Console.WriteLine($"Calibrated {glyphs.Count} result score glyphs.");
    }
    public static async Task Run(string root)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        PostMatchRoundScore[] three = [new(1, 56, 55), new(2, 84, 125), new(3, 75, 33)];
        var reader = new PostMatchScoreRecognizer();
        Check(reader.Confirm(three, at) is null && reader.Confirm(three, at) is null &&
            reader.Confirm(three, at.AddMilliseconds(100))!.SequenceEqual(three), "Quick-skip scores need exactly two distinct captures, without a 250-ms delay.");
        reader.Reset(); reader.Confirm(three, at);
        Check(reader.Confirm([new(1, 56, 55), new(2, 4, 125), new(3, 75, 33)], at.AddMilliseconds(100)) is null,
            "Conflicting quick readings were confirmed.");
        reader.Reset(); reader.Confirm(three, at); reader.Confirm(null, at.AddMilliseconds(400));
        Check(reader.Confirm(three, at.AddMilliseconds(800)) is null, "Missing read did not break score consensus.");
        reader.Reset(); PostMatchRoundScore[] two = [new(1, 61, 73), new(2, 12, 39), new(3, 0, 0)];
        reader.Confirm(two, at);
        Check(reader.Confirm(two, at.AddMilliseconds(400)) is { Length: 2 }, "Unused 0–0 round was counted as played.");
        reader.Reset(); PostMatchRoundScore[] tied = [new(1, 10, 10), new(2, 20, 21), new(3, 0, 0)];
        reader.Confirm(tied, at);
        Check(reader.Confirm(tied, at.AddMilliseconds(400)) is { Length: 2 }, "Tied round did not count toward both players' round wins.");
        reader.Reset(); PostMatchRoundScore[] earlyForfeit = [new(1, 10, 20), new(2, 0, 0), new(3, 0, 0)];
        reader.Confirm(earlyForfeit, at);
        Check(reader.Confirm(earlyForfeit, at.AddMilliseconds(400)) is null, "Early forfeit invented a played second round.");

        // Two distinct held-out images simulate a quick NEXT click: only two
        // captures 100 ms apart are available, followed immediately by progression.
        using (var quickOcr = new ScreenStateRecognizer())
        {
            reader.Reset();
            var first = OakEffectProbe.Load(Path.Combine(root, "GwentCompanion/sessions/20260901-215818/frame-008258-221204663.jpg"));
            var second = OakEffectProbe.Load(Path.Combine(root, "GwentCompanion/sessions/20260901-215818/frame-008260-221204857.jpg"));
            var firstScreen = await quickOcr.AnalyzeAsync(first);
            var secondScreen = await quickOcr.AnalyzeAsync(second);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Check((await reader.ReadAsync(first, firstScreen, at, quickOcr)).PostMatchRoundScores is { } quick && quick.SequenceEqual(three),
                "One fully glyph-verified result frame failed to confirm all round scores.");
            var calls = quickOcr.OcrCalls;
            Check((await reader.ReadAsync(second, secondScreen, at.AddMilliseconds(100), quickOcr)).PostMatchRoundScores is not null &&
                quickOcr.OcrCalls == calls, "Confirmed scores were unnecessarily read again.");
            Console.WriteLine($"PASS quick result: one fully glyph-verified frame; {timer.ElapsedMilliseconds} ms reader work including first-use initialization; confirmed table reused without OCR.");
        }

        var audit = new List<object>();
        foreach (var fixture in new[] {
            (Session: "20260901-215818", Pattern: "frame-*22120*.jpg", Expected: three),
            (Session: "20260902-100300", Pattern: "frame-*10125*.jpg", Expected: two[..2]) })
        {
            using var ocr = new ScreenStateRecognizer(); reader.Reset();
            PostMatchRoundScore[]? confirmed = null;
            // Calibration images are excluded: confirm on other retained frames.
            foreach (var file in Directory.GetFiles(Path.Combine(root, "GwentCompanion/sessions", fixture.Session), fixture.Pattern)
                .Where(f => !f.EndsWith("frame-008264-221205247.jpg") && !f.EndsWith("frame-005907-101251706.jpg")).Order())
            {
                var stamp = Path.GetFileNameWithoutExtension(file).Split('-')[2];
                var time = DateTime.ParseExact(stamp, "HHmmssfff", null);
                var sampled = at.Date.Add(time.TimeOfDay);
                var frame = OakEffectProbe.Load(file);
                var screen = await ocr.AnalyzeAsync(frame);
                var result = await reader.ReadAsync(frame, screen, new DateTimeOffset(sampled, TimeSpan.Zero), ocr);
                if (result.PostMatchRoundScores is { } scores)
                { Check(scores.SequenceEqual(fixture.Expected), "Incorrect final table: " + file); confirmed = scores; }
                audit.Add(new { fixture.Session, File = Path.GetFileName(file), screen.ScreenHeader, result.PostMatchRoundScores });
            }
            Directory.CreateDirectory(Path.Combine(root, "GwentCompanion/diagnostics"));
            File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/post-match-scores-audit.json"), JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
            Check(confirmed is not null, "No confirmed final score table from " + fixture.Session);
        }
        using (var ocr = new ScreenStateRecognizer())
        {
            reader.Reset();
            var rankFrame = OakEffectProbe.Load(Path.Combine(root, "GwentCompanion/sessions/20260902-100300/frame-005931-101254086.jpg"));
            var screen = await ocr.AnalyzeAsync(rankFrame);
            Check((await reader.ReadAsync(rankFrame, screen, at, ocr)).PostMatchRoundScores is null &&
                (await reader.ReadAsync(rankFrame, screen, at.AddMilliseconds(400), ocr)).PostMatchRoundScores is null,
                "MMR/XP progression panel was read as round scores.");
        }
        var tracker = new GameStateTracker(); tracker.Reset("score-test");
        var collector = new MatchAcquisition("fixture", Guid.NewGuid());
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
            MatchHudVisible: true, OpponentHandCount: 8, UserScore: 4, OpponentScore: 125);
        collector.Observe(tracker.Observe(new(at, board, [], [], false,
            Measurements: new(Round: new(2, at, 1, EvidenceKind.Visual, "fixture")))), board, [], []);
        Check(collector.Snapshot()!.Rounds.Single().UserScore == 4, "Live score fixture missing.");
        var finalScreen = board with { ScreenHeader = "VICTORY", MatchHudVisible = false, PostMatchRoundScores = three };
        collector.Observe(tracker.Observe(new(at.AddSeconds(1), finalScreen, [], [], false)), finalScreen, [], []);
        var saved = CompactMatchCodec.Decode(CompactMatchCodec.Encode(collector.Snapshot()!));
        Check(saved.Rounds.Length == 3 && saved.Rounds.All(r => r.FinalConfirmed) && saved.Rounds[1].UserScore == 84,
            "Final result table did not correct and persist the live score.");
        var mmr = finalScreen with { PostMatchRoundScores = null, PostMatchMmr = new(2400, null, true, "fixture") };
        collector.Observe(tracker.Observe(new(at.AddSeconds(2), mmr, [], [], false)), mmr, [], []);
        Check(collector.Snapshot()!.Rounds.All(r => r.FinalConfirmed), "Later rating screen erased final scores.");
        // Starting in a menu is a waiting state; its rating belongs to no match.
        var lifecycle = new MatchCaptureLifecycle();
        var menu = board with { MatchHudVisible=false, ScreenHeader="STANDARD MODE", PostMatchExitCue=true,
            PostMatchMmr=new(2500,null,true,"pre-game") };
        lifecycle.Observe(menu,at);
        Check(lifecycle.WaitingForGame && lifecycle.BestRating is null, "Pre-game menu rating leaked into a match.");
        lifecycle.Observe(board,at.AddSeconds(1));
        Check(!lifecycle.WaitingForGame && !lifecycle.NewGameStarted, "First game did not resume tracking.");
        lifecycle.Observe(finalScreen,at.AddSeconds(2));
        var provisional = new PostMatchMmr(2434,null,true,"menu",2441,Confirmed:false,ReadCount:1);
        lifecycle.Observe(menu with { PostMatchMmr=null,PostMatchMmrCandidate=provisional },at.AddSeconds(3));
        lifecycle.Observe(menu with { PostMatchMmr=null,PostMatchMmrCandidate=provisional with {RatingAfter=2435,ReadCount=0} },at.AddSeconds(4));
        Check(lifecycle.BestRating == provisional, "A weaker later read replaced the best available rating.");
        lifecycle.Observe(board with {ScreenHeader="REDRAW"},at.AddSeconds(5));
        Check(lifecycle.NewGameStarted && lifecycle.BestRating == provisional, "Next-game boundary lost the previous rating.");
        lifecycle.Observe(menu,at.AddSeconds(6));
        Check(lifecycle.BestRating == provisional, "A late queued frame changed the closed match rating.");
        lifecycle.Reset(); lifecycle.Observe(board, at); lifecycle.Observe(mmr, at.AddSeconds(1));
        lifecycle.Observe(menu with {PostMatchMmr=null,PostMatchMmrCandidate=provisional}, at.AddSeconds(2));
        Check(lifecycle.BestRating is {RatingAfter:2400,Confirmed:true}, "An unconfirmed menu read replaced a confirmed result.");
        var fallbackRecord = saved with { MmrAfter=2434,FactionMmr=true,MmrUnconfirmed=true };
        Check(CompactMatchCodec.Decode(CompactMatchCodec.Encode(fallbackRecord)).MmrUnconfirmed,
            "Unconfirmed fallback lost its quality flag in compression.");
        var entry=MatchAnalysisEntry.From("",fallbackRecord);
        Check(entry.RatingLabel.Contains("unconfirmed") && MatchAnalysis.Totals([entry]).MmrSamples==0,
            "Unconfirmed fallback was presented as a confirmed rating.");
        var gate=new PostMatchAutoStopGate();
        gate.TryRequest("boundary",board,true,true,false);
        Check(gate.TryRequest("boundary",menu with {PostMatchMmr=provisional,PostMatchCaptureEnded=true},false,true,false),
            "New game did not terminate collection with the best available read.");
        Console.WriteLine("PASS final round scores: repeated real three-round and two-round result pixels; rank-panel rejection; 4→84 correction; confirmed score codec round trip.");
    }
}
