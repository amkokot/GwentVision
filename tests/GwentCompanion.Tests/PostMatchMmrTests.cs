using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class PostMatchMmrTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static VisibleTextLine Line(string text, double x = .3, double y = .4) => new(text, new(x, y, x + .14, y + .025));
    public static async Task RunAsync(string root)
    {
        PostMatchMmr? Parse(params VisibleTextLine[] lines) => PostMatchMmrRecognizer.Parse("VICTORY", lines);
        var full = Parse(Line("FACTION MMR 2442 (+7)"));
        Check(full is { RatingAfter: 2442, Change: 7, IsFactionRating: true, RatingBefore: 2435, MatchContext: 2435 }, "Complete faction rating/change parse failed.");
        Check(Parse(Line("fMMR 2401 −8")) is { RatingBefore: 2409, Change: -8 }, "Loss sign or before-rating arithmetic failed.");
        Check(Parse(Line("Faction MMR"), Line("2442", y: .435), Line("+7", y: .47)) is { RatingBefore: 2435 }, "Nearby numeric lines failed.");
        Check(Parse(Line("MMR +7")) is { RatingAfter: null, Change: 7, MatchContext: null }, "Delta-only MMR invented a rating/scope.");
        Check(Parse(Line("MMR 9750 +7")) is { MatchContext: null }, "Unqualified aggregate MMR was treated as faction MMR.");
        Check(Parse(Line("Faction MMR 2442")) is { Change: null, RatingBefore: null, MatchContext: 2442 }, "Unread change was invented as zero.");
        Check(Parse(Line("Faction MMR 2442 +0")) is { Change: 0, RatingBefore: 2442 }, "Explicit zero change lost.");
        foreach (var text in new[] { "RANK 0", "XP 2442 +7", "PEAK MMR 2442 +7", "TOTAL MMR 9750 +7", "FACTION MMR 24O2 +7", "FACTION MMR 2442 +999", "FACTION MMR 2442 +7 XP" })
            Check(Parse(Line(text)) is null, "Unqualified/corrupt number accepted: " + text);
        Check(PostMatchMmrRecognizer.Parse("DECK", [Line("FACTION MMR 2442 +7")]) is null, "Non-result screen accepted.");
        Check(Parse(Line("FACTION MMR 2442 +7", x: .76)) is null, "Opponent-side rating became ours.");
        Check(Parse(Line("FACTION MMR"), Line("2442", y: .8)) is null, "Distant unrelated number joined rating label.");
        Check(Parse(Line("FACTION MMR 2442 +7"), Line("FACTION MMR 2470 +5", y: .7)) is null, "Two conflicting rating panels were resolved arbitrarily.");
        Check(Parse(Line("FACTION MMR"), Line("2442", y: .435), Line("2470", y: .47)) is null, "Conflicting nearby values accepted.");
        var reader = new PostMatchMmrRecognizer();
        var ranked = new[] { new VisibleTextLine("2431", new(.483,.272,.521,.309)), new VisibleTextLine("2405", new(.49,.315,.517,.339)) };
        Check(PostMatchMmrRecognizer.ParseRankedPanel("VICTORY", "RANKED", ranked) is { RatingAfter:2405, SeasonPeak:2431, RatingBefore:null, Change:null, MatchContext:2405, IsFactionRating:true }, "Ranked current/season peak diamond");
        Check(PostMatchMmrRecognizer.ParseRankedPanel("VICTORY", "XP", ranked) is null &&
            PostMatchMmrRecognizer.ParseRankedPanel("DECK", "RANKED", ranked) is null &&
            PostMatchMmrRecognizer.ParseRankedPanel("VICTORY", "RANKED", [ranked[0] with { Text="2400" },ranked[1]]) is null &&
            PostMatchMmrRecognizer.ParseRankedPanel("VICTORY", "RANKED", [ranked[0], ranked[1] with { Text="240S" }]) is null,
            "Wrong context or repaired ranked numerals accepted");
        var rankLine = new VisibleTextLine("3", new(.493,.378,.509,.415));
        var rank = PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "RANKED", [rankLine]);
        Check(rank is { Rank:3, IsProRank:false }, "Standard-ladder rank shield did not parse.");
        Check(PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "RANKED", [rankLine with {Text="0"}]) is {IsProRank:true},
            "Rank 0 did not retain Pro Rank semantics.");
        foreach (var invalid in new[] {
            PostMatchMmrRecognizer.ParseRankPanel("DECK", "RANKED", [rankLine]),
            PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "XP", [rankLine]),
            PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "RANKED", [rankLine with {Text="31"}]),
            PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "RANKED", [rankLine with {Region=new(.493,.50,.509,.53)}]),
            PostMatchMmrRecognizer.ParseRankPanel("VICTORY", "RANKED", [rankLine,rankLine with {Text="4"}]) })
            Check(invalid is null, "Wrong context/progress/conflicting number was accepted as ladder rank.");
        Check(reader.Confirm(full, At) is null && reader.Confirm(full, At) is null && reader.Confirm(full, At.AddSeconds(1)) is null &&
            reader.Confirm(full, At.AddSeconds(2)) == full, "Rating not confirmed from three distinct spaced frames.");
        reader.Reset(); reader.Confirm(full, At); reader.Confirm(full, At.AddSeconds(1)); reader.Confirm(null, At.AddSeconds(2));
        Check(reader.Confirm(full, At.AddSeconds(3)) is null, "An unread frame did not break consensus.");
        Check(reader.Confirm(full, At.AddSeconds(9)) is null, "Stale consensus carried across long gap.");
        reader.Reset();
        Check(reader.ConfirmRank(rank, At) is null && reader.ConfirmRank(rank, At.AddSeconds(1)) == rank,
            "Rank was not confirmed from two distinct spaced, result-layout-gated frames.");
        reader.Reset(); var unreadRank=new PostMatchRank(null,"Ranked ladder result screen");
        Check(reader.ConfirmRank(unreadRank,At) is null && reader.ConfirmRank(unreadRank,At.AddSeconds(1)) is {Rank:null},
            "Confirmed ranked-screen layout depended on reading its shield number.");
        var cards = new[] { new ObservedCard(new CardDefinition("test", "Test", "Monsters", CardKind.Unit, 4), CardProvenance.ConfirmedStartingDeck, 1, At) };
        var encounter = new LearnedOpponentEncounter("mmr-fixture", At, "Monsters", null, null, null, null, 25, cards, [],
            OpponentProvisionCalculator.Calculate(cards, 165), MatchMmr: full!.MatchContext, PostMatchMmr: full);
        var memory = new OpponentDeckMemoryStore(); var saved = memory.Record(encounter, "Test");
        var updated = memory.Record(encounter with { MatchMmr = null, PostMatchMmr = null }, "Test");
        Check(updated.EncounterCount == 1 && updated.Encounters.Single().PostMatchMmr == full, "Re-saving a match lost its observed change or duplicated encounter.");
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/mmr-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "memory.json"); memory.Save(path);
        Check(OpponentDeckMemoryStore.Load(path).Records.Single().Encounters.Single().PostMatchMmr == full, "Post-match rating did not round-trip.");

        var rankMemory = new OpponentDeckMemoryStore();
        rankMemory.Record(encounter with { SessionId="rank-fixture", MatchMmr=null, PostMatchMmr=null, PostMatchRank=rank }, "Rank test");
        var rankPath=Path.Combine(folder,"rank-memory.json"); rankMemory.Save(rankPath);
        Check(OpponentDeckMemoryStore.Load(rankPath).Records.Single().Encounters.Single().PostMatchRank == rank,
            "Post-match rank did not round-trip independently of MMR.");

        // Real standard-rank result pixels: rank 3 is above RANKED; the animated 0→70 below it is progress.
        var rankFolder=Path.Combine(root,"GwentCompanion/sessions/20260901-112058");
        var rankFiles=Directory.GetFiles(rankFolder,"frame-*.jpg").Order(StringComparer.Ordinal).TakeLast(15)
            .Select(Path.GetFileName).Cast<string>().ToArray();
        using var rankOcr=new ScreenStateRecognizer(); var rankReader=new PostMatchMmrRecognizer(); var rankRows=new List<object>();
        PostMatchRank? confirmedRank=null;
        foreach(var name in rankFiles)
        {
            var file=Path.Combine(rankFolder,name); var stamp=Path.GetFileNameWithoutExtension(file).Split('-')[2];
            var time=DateTime.ParseExact(stamp,"HHmmssfff",null);
            var at=new DateTimeOffset(2026,9,1,time.Hour,time.Minute,time.Second,time.Millisecond,TimeSpan.FromHours(-4));
            using var stream=File.OpenRead(file);
            var pixels=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
            var observed=await rankReader.ReadAsync(pixels,await rankOcr.AnalyzeAsync(pixels),at,rankOcr);
            confirmedRank=observed.PostMatchRank??confirmedRank;
            var label=await rankOcr.ReadAsync(pixels,new(.43,.44,.58,.50));
            var rawRank=await rankOcr.ReadLinesAsync(pixels,new(.46,.34,.54,.44),scale:3,enhance:false,whiteLetterMask:true,smooth:true);
            var glyph=HudDigitReader.ReadRank(pixels,new(.485,.37,.52,.435));
            rankRows.Add(new {File=name, observed.ScreenHeader, Label=label, RankNumbers=rawRank, Glyph=glyph, observed.PostMatchRank});
        }
        File.WriteAllText(Path.Combine(root,"GwentCompanion/diagnostics/v0.2.53-rank-audit.json"),
            JsonSerializer.Serialize(new {Session="20260901-112058",ExpectedRank=3,Frames=rankRows},new JsonSerializerOptions{WriteIndented=true}));
        Check(confirmedRank is {Rank:3}, "Newest standard-rank result pixels did not confirm rank 3.");

        // A later independent rank-3 match confirms the same screen and exercises
        // the live auto-stop boundary: the recording ends immediately after the
        // third spaced vote, so the preceding result frames must remain available.
        var latestRankFolder=Path.Combine(root,"GwentCompanion/sessions/20260901-152643");
        var latestRankFiles=Directory.GetFiles(latestRankFolder,"frame-*.jpg").Order(StringComparer.Ordinal).TakeLast(40).ToArray();
        using var latestRankOcr=new ScreenStateRecognizer(); var latestRankReader=new PostMatchMmrRecognizer();
        PostMatchRank? latestConfirmedRank=null;
        foreach(var file in latestRankFiles)
        {
            var stamp=Path.GetFileNameWithoutExtension(file).Split('-')[2]; var time=DateTime.ParseExact(stamp,"HHmmssfff",null);
            var sampledAt=new DateTimeOffset(2026,9,1,time.Hour,time.Minute,time.Second,time.Millisecond,TimeSpan.FromHours(-4));
            using var stream=File.OpenRead(file);
            var pixels=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
            var observed=await latestRankReader.ReadAsync(pixels,await latestRankOcr.AnalyzeAsync(pixels),sampledAt,latestRankOcr);
            latestConfirmedRank=observed.PostMatchRank??latestConfirmedRank;
        }
        Check(latestConfirmedRank is {Rank:3},"Latest independent rank-3 result pixels did not confirm before auto-stop.");

        // This rank-2 panel exists for only ~1.4 seconds between the defeat
        // banner and the transition. The previous three-vote rule could never
        // complete despite two clean, independently retained reads.
        var quickRankFolder=Path.Combine(root,"GwentCompanion/sessions/20260901-231132");
        var quickRankFiles=new[]{"frame-009327-232716509.jpg","frame-009334-232717218.jpg"};
        using var quickRankOcr=new ScreenStateRecognizer(); var quickRankReader=new PostMatchMmrRecognizer();
        PostMatchRank? quickRank=null;
        foreach(var name in quickRankFiles)
        {
            var file=Path.Combine(quickRankFolder,name); var stamp=Path.GetFileNameWithoutExtension(file).Split('-')[2];
            var time=DateTime.ParseExact(stamp,"HHmmssfff",null);
            var sampledAt=new DateTimeOffset(2026,9,1,time.Hour,time.Minute,time.Second,time.Millisecond,TimeSpan.FromHours(-4));
            using var stream=File.OpenRead(file);
            var pixels=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
            var baseScreen=await quickRankOcr.AnalyzeAsync(pixels);
            var observed=await quickRankReader.ReadAsync(pixels,baseScreen,sampledAt,quickRankOcr);
            quickRank=observed.PostMatchRank??quickRank;
        }
        // Exact rank OCR is intentionally optional: screen confirmation must stop
        // recording even when the ornate shield digit cannot be read safely.
        Check(quickRank is not null,"Short rank-2 result animation did not confirm before its transition.");

        // The newest recording stops on the victory score screen, before rating progression.
        // Negative pixel validation only: no claim of successful positive OCR from this match.
        var files = Directory.GetFiles(Path.Combine(root, "GwentCompanion/sessions/20260827-184848"), "frame-*.jpg")
            .OrderBy(f => f, StringComparer.Ordinal).TakeLast(4).ToArray();
        using var ocr = new ScreenStateRecognizer(); var rows = new List<object>(); var resultHeaders = 0;
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
            var screen = await ocr.AnalyzeAsync(frame);
            if (PostMatchMmrRecognizer.IsResultHeader(screen.ScreenHeader)) resultHeaders++;
            var lines = await ocr.ReadLinesAsync(frame, new(.02, .12, .75, .94), scale: 2);
            var parsed = PostMatchMmrRecognizer.Parse(screen.ScreenHeader, lines);
            Check(parsed is null, "Victory round-score numbers were misread as MMR.");
            rows.Add(new { File = Path.GetFileName(file), screen.ScreenHeader, Parsed = parsed, Text = lines.Select(l => l.Text) });
        }
        Check(resultHeaders > 0, "Negative MMR audit skipped every frame because no result heading was recognized.");
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.21-mmr-audit.json"), JsonSerializer.Serialize(new {
            Scope = "Parser/consensus/persistence fixtures pass; four newest real-match ending frames are negative pixel checks. No positive MMR panel in this recording; live positive OCR remains unvalidated.", Frames = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("  Post-match MMR and standard-rank parsing, temporal confirmation, persistence, real rank-2/rank-3 pixels passed; victory-score negatives remain rejected.");
    }
}
