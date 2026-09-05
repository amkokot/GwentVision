using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class LeaderAbilityRecognizerTests
{
    public static void Probe(string root, string[] args)
    {
        string Option(string key, string fallback) { var index=Array.IndexOf(args,key); return index<0?fallback:args[index+1]; }
        var sessionId=Option("--session","20260901-112058");
        var project=Path.Combine(root,"GwentCompanion");
        var catalog=GwentOneCardCatalog.Load(Path.Combine(project,"cache","gwent-one-cards.json"));
        using var recognizer=new LeaderAbilityRecognizer(catalog,Path.Combine(project,"assets","vision","leaders"))
            {Trace=Console.WriteLine};
        LeaderAbilityReading? reading=null;
        var paths=Directory.GetFiles(Path.Combine(project,"sessions",sessionId),"frame-*.jpg").Order().Take(600).ToArray();
        using (var stream=File.OpenRead(paths[0]))
        {
            var bitmap=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var region=GwentLayout.OpponentLeaderAbilityWide;
            var crop=new CroppedBitmap(bitmap,new((int)(region.Left*bitmap.PixelWidth),(int)(region.Top*bitmap.PixelHeight),
                (int)((region.Right-region.Left)*bitmap.PixelWidth),(int)((region.Bottom-region.Top)*bitmap.PixelHeight)));
            var enlarged=new TransformedBitmap(crop,new ScaleTransform(8,8));
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(enlarged));
            using var output=File.Create(Path.Combine(project,"diagnostics","current-leader-crop.png")); encoder.Save(output);
        }
        foreach(var path in paths)
            reading=recognizer.Observe(Load(path),Board,File.GetLastWriteTimeUtc(path))??reading;
        Console.WriteLine($"CURRENT LEADER {sessionId}: {reading?.Card.Name??"unread"}; confidence={reading?.Confidence:F3}; frames={reading?.AgreeingFrames}");
    }

    public static void Run(string root)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var assets = Path.Combine(project, "assets", "vision", "leaders");
        var recordings = new (string Session, string? Start, int Take, string Leader)[]
        {
            ("20260901-112058", null, 200, "Hidden Cache"),
            ("20260901-090553", null, 80, "Guerilla Tactics"),
            ("20260829-211713", null, 80, "Fruits of Ysgith"),
            ("20260829-211223", null, 80, "Patricidal Fury"),
            ("20260829-191618", null, 80, "Enslave"),
            ("20260829-180333", null, 80, "Ursine Ritual"),
            ("20260829-144438", "frame-001110-144629062.jpg", 24, "Nature's Gift"),
            ("20260829-115219", null, 24, "Inspired Zeal"),
            ("20260828-175744", null, 40, "Force of Nature"),
        };
        foreach (var recording in recordings)
        {
            using var recognizer = new LeaderAbilityRecognizer(catalog, assets) { Trace = Console.WriteLine };
            var session = Path.Combine(project, "sessions", recording.Session);
            var ordered = Directory.GetFiles(session, "frame-*.jpg").Order().ToArray();
            var paths = (recording.Start is null ? ordered : ordered.Where(path =>
                    string.CompareOrdinal(Path.GetFileName(path), recording.Start) >= 0))
                .Take(recording.Take).ToArray();
            LeaderAbilityReading? reading = null;
            foreach (var path in paths)
            {
                reading = recognizer.Observe(Load(path), Board, File.GetLastWriteTimeUtc(path));
                if (reading is not null) break;
            }
            if (reading?.Card.Name != recording.Leader)
                throw new InvalidOperationException($"Recorded session {recording.Session} should repeatedly read {recording.Leader}; got {reading?.Card.Name ?? "nothing"}.");
        }

        using (var recognizer = new LeaderAbilityRecognizer(catalog, assets))
        {
            var absent = Path.Combine(project, "sessions", "20260829-211713", "frame-001066-211900247.jpg");
            for (var i = 0; i < 7; i++)
                if (recognizer.Observe(Load(absent), Board, File.GetLastWriteTimeUtc(absent).AddMilliseconds(i * 200)) is not null)
                    throw new InvalidOperationException("An obscured leader badge produced a false identity.");
        }

        var battle = catalog.Single(card => card.Name == "Battle Trance");
        var harmony = catalog.Single(card => card.Name == "Call of Harmony");
        var knowledge = new OpponentKnowledge();
        if (!knowledge.ObserveVisibleLeader(battle, .91, true) || knowledge.StartingLeader != battle.Name || knowledge.CurrentLeaderId != battle.Id)
            throw new InvalidOperationException("Visible opening leader did not populate starting/current knowledge.");
        knowledge.ObserveVisibleLeader(harmony, .94, false);
        if (knowledge.StartingLeader != battle.Name || knowledge.CurrentLeader != harmony.Name)
            throw new InvalidOperationException("A replacement leader overwrote original deck metadata.");
        Console.WriteLine("PASS persistent leader icon recognition and separate starting/current leader knowledge.");
    }

    private static readonly GwentVisualObservation Board =
        new(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);

    private static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
    }
}
