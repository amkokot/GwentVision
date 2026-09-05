using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class LatestFixesTests
{
    private static void Check(bool okay,string message) { if(!okay) throw new InvalidOperationException(message); }
    private static PixelFrame Load(string root,string session,string stamp)
    {
        using var file=File.OpenRead(Directory.GetFiles(Path.Combine(root,"GwentCompanion/sessions",session),"frame-*-"+stamp+".jpg").Single());
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
    }
    private static PixelFrame LoadProject(string project,string session,string stamp)
    {
        using var file=File.OpenRead(Directory.GetFiles(Path.Combine(project,"sessions",session),"frame-*-"+stamp+".jpg").Single());
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
    }
    public static async Task Run(string root)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var at=DateTimeOffset.UtcNow;
        var opening=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null) {OpponentHandCount=0,OpponentDeckCount=25,UserScore=0,OpponentScore=0};
        OpponentKnowledge Deal(int before,int after)
        {
            var k=new OpponentKnowledge(); k.ObserveScreen(opening with {OpponentDeckCount=before},at);
            k.ObserveScreen(opening with {ScreenHeader="ROUND 1",OpponentHandCount=null,OpponentDeckCount=null},at.AddSeconds(2));
            k.ObserveScreen(opening with {ScreenHeader="ROUND 1",OpponentHandCount=null,OpponentDeckCount=null},at.AddSeconds(3));
            k.ObserveScreen(opening with {OpponentHandCount=10,OpponentDeckCount=after},at.AddSeconds(10));
            return k;
        }
        var artifact=catalog.Single(c=>c.Id=="203080");
        var observed=new ObservedCard(artifact,CardProvenance.ProbableStartingDeck,.95,at);
        var exact=Deal(25,15);
        Check(exact.StartingSize==25 && exact.Assess([observed]).Renfri.State==ConstraintState.RuledOut,"Opening artifact failed Renfri exclusion");
        Check(Deal(26,16).Assess([observed]).Renfri.State==ConstraintState.Possible,"26-card Renfri exception lost");
        Check(Deal(26,15).StartingSize is null,"Setup thinning ignored by size inference");
        Check(exact.Assess([observed with {Provenance=CardProvenance.Created}]).Renfri.State!=ConstraintState.RuledOut,"Generated artifact excluded Renfri");
        var late=new OpponentKnowledge(); late.SetRound(3); late.ObserveScreen(opening,at);
        Check(late.StartingSize is null,"Late pile inferred starting size");
        using var ocr=new ScreenStateRecognizer();
        var full=Load(root,"20260828-175744","175831112");
        var empty=Load(root,"20260828-175744","175748211");
        var top=await OpponentHudRecognizer.ReadHandCandidateAsync(full,new(.925,0,.995,.065),ocr);
        var bottom=await OpponentHudRecognizer.ReadHandCandidateAsync(full,new(.925,.94,.995,1),ocr);
        var zero=await OpponentHudRecognizer.ReadHandCandidateAsync(empty,new(.925,0,.995,.065),ocr);
        Console.WriteLine($"Hand pixels: full opponent={top}, player={bottom}; pre-deal opponent={zero}");
        Check(top==10 && bottom==10 && zero==0,"Red full-hand versus genuine empty hand OCR");
        var rows=File.ReadLines(Path.Combine(root,"GwentCompanion/sessions/20260828-175744/vision-observations.jsonl"))
            .Select(s=>JsonSerializer.Deserialize<CardVisionResult>(s,GameStateJournal.Json)!).ToArray();
        var knowledge=new OpponentKnowledge(); var tracker=new LiveDeckTracker(PlayerSide.Opponent); var origins=new PlayProvenanceResolver();
        DateTimeOffset? renfriExcluded=null;
        foreach(var row in rows)
        {
            var screen=row.Screen;
            if(row.SampledAt.ToString("HHmmssfff")=="175831510")
            {
                var counterFrame=Load(root,"20260828-175744","175831510");
                screen=screen with {
                    OpponentHandCount=await OpponentHudRecognizer.ReadHandCandidateAsync(counterFrame,new(.925,0,.995,.065),ocr),
                    UserHandCount=await OpponentHudRecognizer.ReadHandCandidateAsync(counterFrame,new(.925,.94,.995,1),ocr)};
            }
            knowledge.ObserveScreen(screen,row.SampledAt);
            foreach(var e in row.Events)
            {
                var origin=origins.Observe(e); knowledge.Observe(e,"Monsters",origin.Provenance);
                if(e.Sighting.Side==PlayerSide.Opponent)
                    tracker.ConsiderDirectPlay(e.Sighting.Card,1-e.Sighting.Distance,e.ObservedAt,e.Description,origin.Provenance);
            }
            if(knowledge.Assess(tracker.DeckBuildingObservations).Renfri.State==ConstraintState.RuledOut) renfriExcluded??=row.SampledAt;
        }
        Console.WriteLine($"Recorded opening: size={knowledge.StartingSize}; Renfri excluded={renfriExcluded}; {knowledge.StartingSizeEvidence}");
        Check(knowledge.StartingSize==25 && renfriExcluded?.TimeOfDay < new TimeSpan(17,59,0),"Recorded opening artifact not reflected promptly");
        var decks=DeckLibrary.Load(Path.Combine(root,"GwentCompanion/cache/deck-library.json")).Decks;
        var rider=new ObservedCard(catalog.Single(c=>c.Id=="132310"),CardProvenance.ProbableStartingDeck,.95,at);
        var hint=SingletonPairHint.Build(decks,[rider],StartingDeckRules.EvaluateObservedDeck([rider]));
        Console.WriteLine(hint); Check(hint is not null,"Cached Rider pair should make singleton unlikely");
        Check(rider.ObservedCopies==1,"Pair hypothesis fabricated a sighting");
        Check(SingletonPairHint.Build(decks,[rider with {Provenance=CardProvenance.Created}],StartingDeckRules.EvaluateObservedDeck([])) is null,"Created Rider incorrectly marks Shupe unlikely");
        var edits=new DeckProjectionEdits(); edits.Exclude(rider.Card,2);
        Check(SingletonPairHint.Build(decks,[rider],StartingDeckRules.EvaluateObservedDeck([rider]),edits) is null,"Explicit second-copy rejection ignored");
        var refs=VisionReferenceLibrary.Load(catalog,Path.Combine(root,"GwentCompanion/cache"));
        Check(!refs.Any(r=>r.Card.Id=="202792" && r.Path.Contains("observed-art")),"Poisoned Vypper image loaded");
        var freshRows=Path.Combine(root,"GwentCompanion/diagnostics/v0.1.32-20260828-181243-pixels.jsonl");
        if(File.Exists(freshRows))
        {
            var replayLedger=new MatchVisionLedger(); var emitted=new List<VisionEvidenceEvent>();
            foreach(var line in File.ReadLines(freshRows))
            {
                var row=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
                emitted.AddRange(replayLedger.Observe(row.SampledAt,row.Screen,row.Sightings.Select(CardVisionPipeline.RequirePreviewCorroboration).ToArray(),
                    row.BoardWasScanned,row.HoveredCard,row.ArtworkWasScanned));
            }
            Check(!emitted.Any(e=>e.Sighting.Side==PlayerSide.User && e.Sighting.Source==CardSightSource.PlayPreview && e.ObservedAt.TimeOfDay<new TimeSpan(18,13,0)),
                "Dealing animation became a played Griffin");
            Check(emitted.Any(e=>e.Sighting.Side==PlayerSide.User && e.Sighting.Card.Name=="Hen Gaidth Sword" && e.ObservedAt.TimeOfDay<new TimeSpan(18,17,0)),
                "Valid short Sword preview was lost to weak-art guard");
        }
        Console.WriteLine("PASS opening conservation, Renfri size/origin guards, red-hand pixels, recorded opening, statistical singleton hint and quarantined reference.");
    }
    public static void Pixels(string root)
    {
        var cache=Path.Combine(root,"GwentCompanion/cache"); var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var refs=VisionReferenceLibrary.Load(catalog,cache);
        using var detector=new FeatureCardRecognizer(refs,Path.Combine(cache,"recognition-features"));
        var reference=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(root,"GwentCompanion/sessions/20260828-181243/game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference!;
        detector.SetKnownPlayerDeck(reference.Cards.Select(c=>c.Card.Id));
        var fallback=new CardFrameRecognizer(new CardArtMatcher(detector.ArtReferences));
        var rows=new List<object>();
        foreach(var (session,stamp,expected,wrong) in new[] {
            ("20260828-175744","180034097","Lamp Djinn","Vypper"),
            ("20260828-181243","182225702","Golyat","Vypper"),
            ("20260828-175744","180524102","Wild Hunt Rider","Vypper") })
        {
            var frame=Load(root,session,stamp); var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null);
            var features=detector.Recognize(frame,screen);
            var sights=features.Concat(fallback.Recognize(frame,screen,true,features)).ToArray();
            Console.WriteLine(stamp+": "+string.Join(", ",sights.Select(s=>s.Card.Name+" "+s.Side)));
            rows.Add(new {session,stamp,sights});
            Check(sights.Any(s=>s.Card.Name==expected),"Missing held-out identity: "+expected);
            Check(!sights.Any(s=>s.Card.Name==wrong),"False held-out identity: "+wrong);
        }
        using(var recentDetector=new FeatureCardRecognizer(refs,Path.Combine(cache,"recognition-features"),scope:VisionReferenceScope.CandidateDecks))
        {
            var gang=catalog.Single(card=>card.Id=="203089");
            recentDetector.SetLikelyOpponentCards([]);
            recentDetector.ObserveOpponentCards([gang.Id]);
            var frame=Load(root,"20260901-162121","162558544");
            var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null) { MatchHudVisible=true };
            var features=recentDetector.Recognize(frame,screen);
            var recentFallback=new CardFrameRecognizer(new CardArtMatcher(recentDetector.ArtReferences));
            var sights=features.Concat(recentFallback.Recognize(frame,screen,true,features)).Where(s=>s.Source==CardSightSource.Board&&s.Card.Id==gang.Id).ToArray();
            Console.WriteLine("162558544 recent Gang board copies: "+string.Join(", ",sights.Select(s=>$"{s.Region.Left:F3}-{s.Region.Right:F3}")));
            Check(sights.Length==2,"A just-observed self-thinning identity did not recover both clearly visible adjacent Gang bodies.");
        }
        File.WriteAllText(Path.Combine(root,"GwentCompanion/diagnostics/v0.1.31-corrected-label-pixels.json"),JsonSerializer.Serialize(rows,GameStateJournal.Json));
        Console.WriteLine("PASS fresh full-catalog pixels, no training-frame reuse.");
    }
    public static void LabelProbe(string root)
    {
        var cache=Path.Combine(root,"GwentCompanion/cache"); var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var refs=VisionReferenceLibrary.Load(catalog,cache);
        using var detector=new FeatureCardRecognizer(refs.Where(r=>r.Card.Id is "200052" or "203265"),Path.Combine(cache,"recognition-features"));
        detector.Trace=Console.WriteLine;
        foreach(var (session,stamp) in new[] {("20260828-175744","180537600"),("20260828-181243","182225702")})
        {
            var frame=Load(root,session,stamp);
            Console.WriteLine(stamp+": "+string.Join(", ",detector.Recognize(frame,new(GwentViewKind.Board,false,0,0,null)).Select(s=>s.Card.Name)));
        }
        foreach(var id in new[] {"201700","202444"})
        {
            using var sample=File.OpenRead(Directory.GetFiles(Path.Combine(cache,"observed-art",id),"*.jpg").Single());
            var descriptor=VisualDescriptor.Create(BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(sample,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]));
            var neighbors=refs.Where(r=>!r.Path.Contains("observed-art")).Select(r=> {
                using var file=File.OpenRead(r.Path);
                var image=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
                return (r.Card.Name,Distance:descriptor.DistanceTo(VisualDescriptor.Create(image)));
            }).OrderBy(p=>p.Distance).DistinctBy(p=>p.Name).Take(8);
            Console.WriteLine(id+": "+string.Join(", ",neighbors));
        }
    }
    public static void RecentGangPixels(string root)
        => RecentGangPixelsFromProject(Path.Combine(root,"GwentCompanion"));

    public static void RecentGangPixelsFromProject(string project)
    {
        var cache=Path.Combine(project,"cache"); var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var refs=VisionReferenceLibrary.Load(catalog,cache); var gang=catalog.Single(card=>card.Id=="203089");
        using var detector=new FeatureCardRecognizer(refs,Path.Combine(cache,"recognition-features"),scope:VisionReferenceScope.CandidateDecks);
        detector.ObserveOpponentCards([gang.Id]);
        var frame=LoadProject(project,"20260901-162121","162558544");
        var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null) { MatchHudVisible=true };
        var features=detector.Recognize(frame,screen);
        var artMatcher=new CardArtMatcher(detector.ArtReferences);
        var fallback=new CardFrameRecognizer(artMatcher);
        fallback.Trace=Console.WriteLine;
        var title=new CardSighting(gang,PlayerSide.Opponent,CardSightSource.PlayPreview,new(.80,.13,.90,.40),.10,1,"Exact visible preview title");
        var playedAt=DateTimeOffset.Parse("2026-09-01T16:25:55.2431513-04:00");
        fallback.ObserveEvents([new(playedAt,title,"Exact visible preview title: Renfri's Gang")]);
        fallback.Expire(playedAt.AddSeconds(3));
        var targetedClock=System.Diagnostics.Stopwatch.StartNew();
        var sights=features.Concat(fallback.Recognize(frame,screen,true,features)).Where(s=>s.Source==CardSightSource.Board&&s.Card.Id==gang.Id).ToArray(); targetedClock.Stop();
        Console.WriteLine("162558544 recent Gang board copies: "+string.Join(", ",sights.Select(s=>$"{s.Region.Left:F3}-{s.Region.Right:F3} d={s.Distance:F3}")));
        Check(sights.Length==2&&targetedClock.Elapsed<TimeSpan.FromSeconds(1),$"A just-observed self-thinning identity did not recover both clearly visible adjacent Gang bodies responsively ({targetedClock.Elapsed}).");
        var before=LoadProject(project,"20260901-162121","162554951");
        Check(!fallback.Recognize(before,screen,true,[]).Any(s=>s.Source==CardSightSource.Board&&s.Card.Id==gang.Id),
            "Recent Gang title fabricated a pair before either body resolved.");

        using var pipeline=new CardVisionPipeline(refs,catalog,Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        var finalState=Path.Combine(project,"sessions/20260901-162121/game-state-final.json");
        var playerIds=File.Exists(finalState)
            ? JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(finalState),GameStateJournal.Json)?.User.StartingDeckReference?.Cards.Select(card=>card.Card.Id) ?? []
            : [];
        pipeline.SetKnownPlayerDeck(playerIds); pipeline.SetLikelyOpponentCards([]);
        var committed=pipeline.Commit(new CardVisionResult(playedAt,screen,[title],[],false,false));
        Check(committed.Events.Any(e=>e.Sighting.Card.Id==gang.Id),"Exact Gang title did not enter the production event ledger.");
        var production=pipeline.RecognizePrepared(new(frame,playedAt.AddSeconds(3),screen,[]),true);
        Check(production.Sightings.Count(s=>s.Source==CardSightSource.Board&&s.Card.Id==gang.Id)==2,
            "The production pipeline did not carry the committed self-thinning play into its two-body board resolver.");
        var settledAgain=LoadProject(project,"20260901-162121","162559930");
        var productionAgain=pipeline.RecognizePrepared(new(settledAgain,playedAt.AddSeconds(4.7),screen,[]),true);
        Check(productionAgain.Sightings.Count(s=>s.Source==CardSightSource.Board&&s.Card.Id==gang.Id)==2,
            "The follow-up settled frame did not retain exactly two distinct Gang bodies.");
        var tracker=new LiveDeckTracker(PlayerSide.Opponent); var copies=new ThinningCopyTracker();
        var play=committed.Events.Single(e=>e.Sighting.Card.Id==gang.Id);
        tracker.ConsiderDirectPlay(gang,.99,play.ObservedAt,play.Description,CardProvenance.ProbableStartingDeck);
        copies.ObserveEvent(play,CardProvenance.ProbableStartingDeck,tracker);
        foreach(var result in new[]{production,productionAgain})
            copies.ObserveFrame(result.SampledAt,result.Screen,result.Sightings,true,[tracker],_=>false);
        Check(tracker.Observations.Single(item=>item.Card.Id==gang.Id).ObservedCopies==2,
            "Two independently settled production frames did not promote the Gang copy floor to two.");
    }
    public static void ReconcileProvisionReplay(string root)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var folder=Path.Combine(root,"GwentCompanion/sessions/20260828-165436");
        var reference=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder,"game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference!;
        using var pixels=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"GwentCompanion/diagnostics/v0.1.31-provision-pixels.json")));
        var rows=pixels.RootElement.GetProperty("pixelRows").EnumerateArray().ToDictionary(r=>r.GetProperty("stamp").GetString()!);
        var boardLedger=new MatchVisionLedger(); var tracker=new LiveDeckTracker(PlayerSide.User); var origins=new PlayProvenanceResolver();
        var copies=new ThinningCopyTracker(); var state=new GameStateTracker(); state.Reset("reconciled-pixels",reference); var ledger=new LiveValueLedger();
        foreach(var line in File.ReadLines(Path.Combine(folder,"vision-observations.jsonl")))
        {
            var result=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            if(rows.TryGetValue(result.SampledAt.ToString("HHmmssfff"),out var row))
            {
                var fresh=row.GetProperty("Cards").EnumerateArray().Select(c=>new CardSighting(catalog.First(d=>d.Name==c.GetProperty("Name").GetString()),
                    (PlayerSide)c.GetProperty("Side").GetInt32(),CardSightSource.Board,c.GetProperty("Region").Deserialize<NormalizedRegion>(),c.GetProperty("Distance").GetDouble(),1))
                    .Where(s=>s.Region.Left>=.215 && s.Region.Right<.8 && s.Region.Top>.09 && s.Region.Bottom<.9).ToArray();
                result=result with {Sightings=fresh,Screen=result.Screen with {HasCardTooltip=row.GetProperty("HasCardTooltip").GetBoolean()}};
            }
            var boardEvents=boardLedger.Observe(result.SampledAt,result.Screen,result.Sightings.Where(s=>s.Source==CardSightSource.Board).ToArray(),result.BoardWasScanned);
            result=result with {Events=result.Events.Where(e=>e.Sighting.Source!=CardSightSource.Board).Concat(boardEvents).ToArray()};
            foreach(var e in result.Events)
            {
                var origin=origins.Observe(e,reference);
                if(e.Sighting.Side==PlayerSide.User) tracker.ConsiderDirectPlay(e.Sighting.Card,1-e.Sighting.Distance,e.ObservedAt,e.Description+" "+origin.Reason,origin.Provenance);
            }
            copies.ObserveFrame(result.SampledAt,result.Screen,result.Sightings,result.BoardWasScanned,[tracker],s=>origins.HasCopyRisk(s,result.SampledAt),reference);
            ledger.Observe(GameStateVisionAdapter.Apply(state,result),catalog,reference.Cards.Select(c=>c.Card),[]);
        }
        var usage=LiveValueLedger.Provisions(tracker.Observations,reference,null,ledger.Spent(PlayerSide.User),ledger.SpentCopies(PlayerSide.User));
        Console.WriteLine(JsonSerializer.Serialize(new {usage,Cards=tracker.Observations.Select(c=>new {c.Card.Name,c.Provenance,c.ObservedCopies})},GameStateJournal.Json));
        File.WriteAllText(Path.Combine(root,"GwentCompanion/diagnostics/v0.1.31-provision-reconciled.json"),JsonSerializer.Serialize(new {Scope="Previously re-read board pixels with regenerated board events; original preview/text records retained. Before subsequent label corrections; not full streaming replay or ground truth.",usage},GameStateJournal.Json));
    }
}
