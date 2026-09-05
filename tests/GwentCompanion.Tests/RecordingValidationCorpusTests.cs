using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class RecordingValidationCorpusTests
{
    private sealed record Manifest(int SchemaVersion, int FileCount, long Bytes, ManifestFile[] Files, Anonymization? Anonymization = null);
    private sealed record ManifestFile(string File, long Bytes, string Sha256, string[] Tags);
    private sealed record Anonymization(string Status, string Version, string[] ProtectedRegions);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task Run(string project)
    {
        var manifestPath=Path.Combine(project,"tests","recording-validation","manifest.json");
        var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),GameStateJournal.Json)
            ?? throw new InvalidDataException("Recording validation manifest is empty.");
        Check(manifest.SchemaVersion is 1 or 2,"Unknown recording validation manifest schema.");
        if (manifest.SchemaVersion==2)
            Check(manifest.Anonymization is { Status: "anonymized", Version.Length: > 0, ProtectedRegions.Length: > 0 },
                "Public recording corpus does not carry a valid anonymization declaration.");
        Check(manifest.Files.Length==manifest.FileCount && manifest.Files.Select(item=>item.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()==manifest.FileCount,
            "Recording validation manifest count or uniqueness is invalid.");
        Check(manifest.FileCount>=1000 && manifest.Bytes<2L*1024*1024*1024,"Recording corpus is unexpectedly sparse or no longer compact.");
        foreach(var required in new[]
        {
            "sessions/20260901-162121/frame-000815-162243239.jpg",
            "sessions/20260901-162121/frame-002729-162554951.jpg",
            "sessions/20260901-162121/frame-002764-162558544.jpg",
            "sessions/20260901-162121/frame-002778-162559930.jpg",
            "sessions/20260901-181048/frame-001390-181307431.jpg",
            "sessions/20260901-181048/frame-001395-181307937.jpg",
            "sessions/20260901-181048/frame-002708-181519246.jpg",
            "sessions/20260901-181048/frame-007826-182351339.jpg",
            "sessions/20260901-181048/frame-007835-182352232.jpg",
            "sessions/20260901-190253/play-events/20260901-190529760-Opponent/during.png",
            "sessions/20260901-190253/play-events/20260901-190607733-Opponent/during.png",
            "sessions/20260901-190253/play-events/20260901-190719150-Opponent/during.png",
            "sessions/20260901-190253/play-events/20260901-191141450-Opponent/during.png",
            "sessions/20260901-190253/play-events/20260901-191549653-Opponent/during.png",
            "sessions/20260901-191628/play-events/20260901-191753215-Opponent/during.png",
            "sessions/20260901-191628/play-events/20260901-191815118-Opponent/during.png",
            "sessions/20260901-191628/frame-001081-191816932.jpg",
            "sessions/20260901-191628/frame-001646-191913433.jpg",
            "sessions/20260901-204341/frame-001751-204636654.jpg",
            "sessions/20260901-204341/frame-001843-204645865.jpg",
            "sessions/20260901-204341/frame-001854-204646968.jpg",
            "sessions/20260901-204341/frame-004439-205106059.jpg",
            "sessions/20260901-204341/play-events/20260901-204858164-Opponent/during.png",
            "sessions/20260901-204341/play-events/20260901-205223177-Opponent/during.png",
            "sessions/20260901-204341/play-events/20260901-205657072-Opponent/during.png",
            "sessions/20260901-215818/play-events/20260901-220227877-User/during.png",
            "sessions/20260901-215818/play-events/20260901-220235663-User/during.png",
            "sessions/20260901-215818/play-events/20260901-220236077-User/during.png",
            "sessions/20260901-215818/play-events/20260901-220726957-Opponent/during.png"
        }) Check(manifest.Files.Any(item=>item.File.Equals(required,StringComparison.OrdinalIgnoreCase)),
            "Latest reviewed validation pixel was not retained: "+required);
        foreach(var entry in manifest.Files)
        {
            var path=Path.GetFullPath(Path.Combine(project,entry.File.Replace('/',Path.DirectorySeparatorChar)));
            var sessions=Path.GetFullPath(Path.Combine(project,"sessions"))+Path.DirectorySeparatorChar;
            Check(path.StartsWith(sessions,StringComparison.OrdinalIgnoreCase),"Manifest entry escapes sessions: "+entry.File);
            var info=new FileInfo(path);
            Check(info.Exists && info.Length==entry.Bytes,"Missing or size-changed recording validation file: "+entry.File);
            using var stream=info.OpenRead();
            Check(Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase),
                "Hash mismatch in recording validation file: "+entry.File);
        }

        var cards=GwentOneCardCatalog.Load(Path.Combine(project,"cache","gwent-one-cards.json"));
        using var screenReader=new ScreenStateRecognizer();
        var titles=new PreviewTitleRecognizer(cards);
        var ledger=new MatchVisionLedger();
        var simlasEvents=new List<VisionEvidenceEvent>();
        foreach(var (file,at) in new[]
        {
            ("frame-006123-125039542.jpg",new DateTimeOffset(2026,9,1,12,50,39,542,TimeSpan.FromHours(-4))),
            ("frame-006125-125039738.jpg",new DateTimeOffset(2026,9,1,12,50,39,738,TimeSpan.FromHours(-4)))
        })
        {
            var pixels=VisionEfficiencyTests.Load(Path.Combine(project,"sessions","20260901-124026",file));
            var screen=await screenReader.AnalyzeAsync(pixels);
            var sightings=await titles.RecognizeAsync(pixels,screen,screenReader);
            Check(sightings.Any(item=>item.Card.Id=="202985" && item.Side==PlayerSide.Opponent),"Simlas title was not recovered from "+file);
            simlasEvents.AddRange(ledger.Observe(at,screen,sightings,boardWasScanned:false,artworkWasScanned:true));
        }
        Check(simlasEvents.Count==1 && simlasEvents[0].Sighting.Card.Id=="202985" && simlasEvents[0].Sighting.Side==PlayerSide.Opponent,
            "The retained Simlas sequence must produce exactly one opponent play episode.");

        async Task ExactTitle(string session,string file,string id)
        {
            var pixels=VisionEfficiencyTests.Load(Path.Combine(project,"sessions",session,file));
            var screen=await screenReader.AnalyzeAsync(pixels);
            var sightings=await titles.RecognizeAsync(pixels,screen,screenReader);
            Check(sightings.Any(item=>item.Card.Id==id),$"Expected exact title {id} was missed in {session}/{file}.");
        }
        await ExactTitle("20260827-081435","frame-004089-082123937.jpg","202216");
        await ExactTitle("20260901-124026","frame-006274-125054648.jpg","203220");
        await ExactTitle("20260901-152643","play-events/20260901-153012049-Opponent/during.png","162305");
        await ExactTitle("20260901-152643","play-events/20260901-153514529-Opponent/during.png","203242");
        await ExactTitle("20260901-152643","play-events/20260901-153524147-Opponent/during.png","202547");
        await ExactTitle("20260901-152643","frame-005226-153526826.jpg","162305");

        var falseTooltipPixels=VisionEfficiencyTests.Load(Path.Combine(project,"sessions","20260901-204341","frame-001700-204631565.jpg"));
        var falseTooltipScreen=await screenReader.AnalyzeAsync(falseTooltipPixels);
        Check(!falseTooltipScreen.HasCardTooltip,
            $"The beige battlefield re-expanded into a tooltip without a local paper/title panel: {falseTooltipScreen.TooltipRegion} ({falseTooltipScreen.TooltipConfidence:F3}).");
        Check(!(await titles.RecognizeAsync(falseTooltipPixels,falseTooltipScreen,screenReader)).Any(item=>item.Card.Id=="202381"),
            "The impossible tooltip again enabled a false Cleaver's Muscle preview.");

        var references=VisionReferenceLibrary.Load(cards,Path.Combine(project,"cache"));
        var choiceReader=new DeckPlayChoiceRecognizer(references,cards);
        var choiceScreen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,IsCardSelectionOverlay:true,ScreenHeader:"PICK A CARD TO PLAY");
        var freshChoice=choiceReader.Read(VisionEfficiencyTests.Load(Path.Combine(project,"sessions","20260901-204341","frame-001854-204646968.jpg")),choiceScreen);
        Check(freshChoice is {Complete:true,Cards.Count:2} && freshChoice.Cards.All(card=>card.Id=="113305"),
            "The retained current Tempest page did not read as exactly two Fog copies.");
        var missedSourceResolver=new DeckPlayResolutionTracker(cards);
        var beforeChoice=choiceScreen with {IsCardSelectionOverlay=false,ScreenHeader=null,MatchHudVisible=true,UserDeckCount=13,UserHandCount=7};
        missedSourceResolver.Observe(DateTimeOffset.UnixEpoch,beforeChoice,[],null);
        missedSourceResolver.Observe(DateTimeOffset.UnixEpoch.AddSeconds(1),choiceScreen,[],freshChoice);
        missedSourceResolver.Observe(DateTimeOffset.UnixEpoch.AddSeconds(1.3),choiceScreen,[],freshChoice);
        var recoveredTempest=missedSourceResolver.Observe(DateTimeOffset.UnixEpoch.AddSeconds(2),beforeChoice with {UserDeckCount=11,UserHandCount=6},[],null);
        Check(recoveredTempest.Count==2 && recoveredTempest[0].Sighting.Card.Id=="202203" &&
              recoveredTempest[1].Sighting.Card.Id=="113305" && recoveredTempest[1].ResolvedDeckCopies==2,
            "Choice/counter conservation did not recover the missed Tempest source and its two Fog copies exactly once.");

        async Task ConfirmedHover(string session,string id,params string[] files)
        {
            var hover=new HoverCardRecognizer(cards);
            CardDefinition? confirmed=null;
            foreach(var file in files)
            {
                var pixels=VisionEfficiencyTests.Load(Path.Combine(project,"sessions",session,file));
                var screen=await screenReader.AnalyzeAsync(pixels);
                var stamp=Path.GetFileNameWithoutExtension(file).Split('-')[2];
                var at=new DateTimeOffset(2026,9,1,int.Parse(stamp[..2]),int.Parse(stamp[2..4]),int.Parse(stamp[4..6]),
                    int.Parse(stamp[6..]),TimeSpan.FromHours(-4));
                confirmed=(await hover.ReadAsync(pixels,screen,at,screenReader)).Card??confirmed;
            }
            Check(confirmed?.Id==id,$"Expected confirmed hover {id} was missed in retained {session} sequence.");
        }
        await ConfirmedHover("20260901-181048","202677","frame-001390-181307431.jpg","frame-001393-181307732.jpg","frame-001395-181307937.jpg");
        await ConfirmedHover("20260901-181048","202809","frame-007826-182351339.jpg","frame-007828-182351538.jpg","frame-007830-182351737.jpg","frame-007835-182352232.jpg");

        async Task<(VisionEvidenceEvent Event,GwentVisualObservation Screen)> PixelEvent(string relative,string id,DateTimeOffset at)
        {
            var pixels=VisionEfficiencyTests.Load(Path.Combine(project,"sessions","20260901-152643",relative));
            var screen=await screenReader.AnalyzeAsync(pixels);
            var sight=(await titles.RecognizeAsync(pixels,screen,screenReader)).Single(item=>item.Card.Id==id &&
                item.Side==PlayerSide.Opponent && item.Source==CardSightSource.PlayPreview);
            return (new(at,sight,"Retained reviewed play-preview pixels"),screen);
        }
        var battlePixels=await PixelEvent("play-events/20260901-153514529-Opponent/during.png","203242",new(2026,9,1,15,35,14,529,TimeSpan.FromHours(-4)));
        var hunterPixels=await PixelEvent("play-events/20260901-153524147-Opponent/during.png","202547",new(2026,9,1,15,35,24,147,TimeSpan.FromHours(-4)));
        var arbalestPixels=await PixelEvent("frame-005226-153526826.jpg","162305",new(2026,9,1,15,35,26,728,TimeSpan.FromHours(-4)));
        var handEvidence=new HandCommitTracker();
        Check(handEvidence.Observe(battlePixels.Event.ObservedAt,battlePixels.Screen,[battlePixels.Event]).Count==0,
            "Battle Stations became one of its own bounded hand-play children.");
        var refillHunter=handEvidence.Observe(hunterPixels.Event.ObservedAt,hunterPixels.Screen,[hunterPixels.Event]);
        var refillArbalest=handEvidence.Observe(arbalestPixels.Event.ObservedAt,arbalestPixels.Screen,[arbalestPixels.Event]);
        Check(refillHunter.Single().Sighting.Card.Id=="202547" && refillArbalest.Single().Sighting.Card.Id=="162305",
            "Retained Battle Stations pixels did not recover the Hunter and Arbalest as two bounded hand plays.");

        var backup=cards.Single(card=>card.Id=="203220");
        var simlas=cards.Single(card=>card.Id=="202985");
        var reviewedOpponent=new LiveDeckTracker(PlayerSide.Opponent);
        reviewedOpponent.ConsiderDirectPlay(backup,.99,DateTimeOffset.UnixEpoch,"Two reviewed pre-Alissa Backup Plan episodes",CardProvenance.ProbableStartingDeck);
        reviewedOpponent.SetObservedCopyLowerBound(backup.Id,2,"reviewed original episodes later replayed by Simlas");
        reviewedOpponent.ConsiderDirectPlay(simlas,.99,DateTimeOffset.UnixEpoch.AddSeconds(1),"reviewed Simlas episode",CardProvenance.ProbableStartingDeck);
        reviewedOpponent.SetObservedCopyLowerBound(simlas.Id,2,"legal-copy clamp negative");
        Check(reviewedOpponent.Observations.Single(item=>item.Card.Id==backup.Id).ObservedCopies==2 &&
              reviewedOpponent.Observations.Single(item=>item.Card.Id==simlas.Id).ObservedCopies==1,
            "Reviewed Simlas sequence must retain two original Backup Plans but only one gold Simlas.");

        var kaer=VisionEfficiencyTests.Load(Path.Combine(project,"sessions","20260827-081435","frame-005268-082321831.jpg"));
        var kaerScreen=await screenReader.AnalyzeAsync(kaer);
        var kaerCard=cards.Single(card=>card.Id=="202823");
        var kaerSightings=CardVisionPipeline.SuppressBoardTooltipPreviews(
            (await titles.RecognizeAsync(kaer,kaerScreen,screenReader)).Concat([
                new CardSighting(kaerCard,PlayerSide.User,CardSightSource.Board,new(.562,.406,.641,.614),.38,1)
            ]).Select(CardVisionPipeline.RequirePreviewCorroboration).ToArray(),kaerScreen,kaerCard);
        var oneFrame=new MatchVisionLedger().Observe(DateTimeOffset.UnixEpoch,kaerScreen,kaerSightings,boardWasScanned:false,artworkWasScanned:true);
        Check(oneFrame.All(item=>item.Sighting.Card.Name!="Kaer Seren"),"A selected board location became a play from one ambiguous frame.");

        LatestFixesTests.RecentGangPixelsFromProject(project);
        var addOnCases = await RecordingValidationCases.VerifyAndRunAsync(project, manifest.Files.Select(item => item.File));

        Console.WriteLine($"PASS compact recording corpus: {manifest.FileCount} hash-verified media files" +
            (manifest.SchemaVersion==2 ? " with declared player-identity masking" : " (private source corpus)") +
            $"; {addOnCases.Cases} independently discovered add-on cases ({addOnCases.Files} files); creation/theft/spawn chains, Saskia/Miner, Circle/Cat, Gang/Phooca, Simlas/Backup Plan, Battle Stations, Pellar and Kaer Seren.");
    }
}
