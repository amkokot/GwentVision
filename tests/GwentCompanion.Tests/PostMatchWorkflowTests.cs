using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class PostMatchWorkflowTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Run(string root)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var at=DateTimeOffset.UnixEpoch.AddDays(1);
        var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,ScreenHeader:"DEFEAT",
            PostMatchMmr:new(2378,null,true,"verified ranked panel",2440));
        var gate=new PostMatchAutoStopGate();
        Check(!gate.TryRequest("one",screen,false,true,false),"Disabled setting stopped analysis.");
        Check(!gate.TryRequest("one",screen,true,false,false),"Idle analysis stopped.");
        Check(!gate.TryRequest("one",screen,true,true,true),"Offline/closing/in-transition analysis stopped.");
        Check(!gate.TryRequest("one",screen with {ScreenHeader="DECK"},true,true,false),"Non-result rating stopped analysis.");
        Check(!gate.TryRequest("one",screen with {PostMatchMmr=null},true,true,false),"Unread/unconfirmed MMR stopped analysis.");
        Check(!gate.TryRequest("one",screen with {PostMatchMmr=new(null,7,true,"delta")},true,true,false),"Delta-only MMR stopped analysis.");
        Check(!gate.TryRequest("one",screen with {PostMatchMmr=new(9750,null,false,"total")},true,true,false),"Unknown rating scope stopped analysis.");
        Check(gate.TryRequest("one",screen,true,true,false)&&!gate.TryRequest("one",screen,true,true,false),"Stop did not trigger once per session.");
        Check(gate.TryRequest("two",screen,true,true,false),"Next match could not auto-stop.");
        gate.Reset(); Check(gate.TryRequest("two",screen,true,true,false),"Explicit reset did not clear old session.");
        gate.Reset(); var rankScreen=screen with {PostMatchMmr=null,PostMatchRank=new(3,"verified rank shield")};
        Check(gate.TryRequest("rank",rankScreen,true,true,false)&&!gate.TryRequest("rank",rankScreen,true,true,false),
            "Confirmed standard rank did not trigger exactly one stop.");
        gate.Reset(); var unreadRankScreen=rankScreen with {PostMatchRank=new(null,"confirmed RANKED result")};
        Check(gate.TryRequest("rank-unread",unreadRankScreen,true,true,false),
            "A confirmed ranked-results layout did not stop when its shield number was unreadable.");

        // Fresh OCR on distinct retained result frames, not the cached MMR journal.
        var folder=Path.Combine(root,"GwentCompanion/sessions/20260831-122341");
        using var ocr=new ScreenStateRecognizer(); var mmr=new PostMatchMmrRecognizer(); gate.Reset(); var stops=0;
        foreach(var file in Directory.GetFiles(folder,"frame-*12342*.jpg").Order(StringComparer.Ordinal)
            .GroupBy(file=>Path.GetFileNameWithoutExtension(file).Split('-')[2][..6]).Select(group=>group.First()))
        {
            var stamp=Path.GetFileNameWithoutExtension(file).Split('-')[2];
            var time=at.AddHours(int.Parse(stamp[..2])).AddMinutes(int.Parse(stamp[2..4])).AddSeconds(int.Parse(stamp[4..6])).AddMilliseconds(int.Parse(stamp[6..]));
            var pixels=OakEffectProbe.Load(file);
            var reading=await mmr.ReadAsync(pixels,await ocr.AnalyzeAsync(pixels),time,ocr);
            if(reading.PostMatchMmr is { } value) Check(value is {RatingAfter:2378,SeasonPeak:2440,Change:null},"Latest MMR current/peak misread.");
            if(gate.TryRequest("latest",reading,true,true,false)) stops++;
        }
        Check(stops==1,"Latest result pixels did not produce exactly one confirmed MMR stop.");

        var alba=catalog.Single(c=>c.Name=="Alba Armored Cavalry"); var knight=catalog.Single(c=>c.Name=="Nilfgaardian Knight");
        var known=new[]{new ObservedCard(alba,CardProvenance.ProbableStartingDeck,1,at,"raw",1)};
        var encounter=new LearnedOpponentEncounter("raw-match",at,"Nilfgaard","Tactical Decision",16,null,25,25,known,[],OpponentProvisionCalculator.Calculate(known,166));
        var memory=new OpponentDeckMemoryStore(); var record=memory.Capture(encounter,"Review fixture",null,null,"fixture");
        record=memory.Suggest(record.Id,[new(alba,2),new(knight)]);
        var template=OpponentReviewDraft.Template(record,catalog);
        Check(template.CardCount==3&&template.CountOf(alba.Id)==2&&OpponentReviewDraft.ObservedCopies(record,alba.Id)==1,
            "Opening shared builder lost proposed duplicate copies or promoted them into observations.");
        var leader=GwentOneCardCatalog.StartingLeaders(catalog).First(c=>c.Faction=="Nilfgaard"&&c.Name!="Tactical Decision");
        var draft=new DeckEditorDraft(OpponentReviewDraft.SourceKey(record),"Reviewed name","Nilfgaard",leader.Id,null,template.Cards,at.AddSeconds(1));
        var raw=JsonSerializer.Serialize(record.Encounters);
        var saved=memory.Review(record.Id,draft.Name,OpponentReviewDraft.Cards(record,draft),true,OpponentReviewDraft.Header(draft,catalog));
        Check(saved.Leader==leader.Name&&saved.Name==draft.Name&&saved.LeaderBonus==leader.Provision&&saved.EncounterCount==1&&!saved.Complete,
            "Review header/name failed or partial review became complete/another encounter.");
        Check(JsonSerializer.Serialize(saved.Encounters)==raw&&OpponentReviewDraft.ObservedCopies(saved,alba.Id)==1,
            "Review overwrote the original leader, raw cards or copy evidence.");
        saved=memory.Capture(encounter with {PostMatchMmr=screen.PostMatchMmr},"ignored",null,null,"late rating");
        Check(saved.Leader==leader.Name&&saved.Name==draft.Name&&saved.EncounterCount==1,"Late MMR overwrote reviewed header/name or duplicated match.");
        var output=Path.Combine(root,"GwentCompanion/diagnostics/post-match-workflow-tests",Guid.NewGuid().ToString("N"),"memory.json");
        memory.Save(output);
        var loaded=OpponentDeckMemoryStore.Load(output).Records.Single();
        Check(loaded.Leader==leader.Name&&loaded.Encounters.Single().StartingLeader=="Tactical Decision","Reviewed header did not round-trip separately from raw.");
        var unknown=memory.Review(record.Id,draft.Name,OpponentReviewDraft.Cards(record,draft),true,new(null,null,null,null));
        Check(unknown.Leader is null&&unknown.Faction is null&&unknown.Budget.Capacity is null,"Cleared review header fell back to old raw metadata.");
        Check(JsonSerializer.Deserialize<LearnedOpponentDeck>(JsonSerializer.Serialize(record with {ReviewedHeader=null},GameStateJournal.Json),GameStateJournal.Json)!.Leader=="Tactical Decision",
            "Old memory records without reviewed headers lost compatibility.");
        Console.WriteLine("PASS post-match workflow: stable latest MMR pixels, once-only/offline/disabled gates, full-builder draft copies, reviewed headers, raw evidence and late-result preservation.");
    }
}
