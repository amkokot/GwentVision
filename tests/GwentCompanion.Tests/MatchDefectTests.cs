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

internal static class MatchDefectTests
{
    private const string Session = "20260831-102406";
    private const string LatestCarryoverSession = "20260901-181048";
    private const string PreTutorFrame = "frame-001685-102655071.jpg";
    private const string FogChoiceFrame = "frame-001691-102655667.jpg";
    private static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void DeckGlyphFixtures(string root)
    {
        var folder=Path.Combine(root,"GwentCompanion/sessions",Session);
        var glyphs=new List<HudGlyph>();
        foreach(var fixture in new (string File,NormalizedRegion Region,string Label)[]{
            (PreTutorFrame,new(.855,.815,.88,.86),"14"),
            ("frame-001745-102701075.jpg",new(.878,.893,.901,.934),"12"),
            ("frame-000326-102439183.jpg",new(.171,.779,.192,.811),"0"),
            ("frame-000326-102439183.jpg",new(.819,.784,.845,.823),"15"),
            ("frame-007603-103647171.jpg",new(.242,.054,.265,.090),"17"),
            ("frame-007603-103647171.jpg",new(.846,.827,.870,.872),"6"),
            ("frame-007603-103647171.jpg",new(.120,.794,.146,.837),"18"),
            ("frame-003534-103000265.jpg",new(.245,.100,.267,.136),"10"),
            ("frame-003534-103000265.jpg",new(.848,.874,.875,.914),"8"),
            ("frame-003534-103000265.jpg",new(.118,.844,.145,.884),"13")})
        {
            try { glyphs.AddRange(HudDigitReader.Train(Load(Path.Combine(folder,fixture.File)),fixture.Region,fixture.Label)); }
            catch(Exception e) {Console.WriteLine("TRAIN FAIL "+fixture.File+" "+fixture.Label+" "+e.Message);}
        }
        Console.WriteLine("DECK_GLYPHS="+JsonSerializer.Serialize(glyphs));
    }
    public static async Task Run(string root)
    {
        var project = Path.Combine(root, "GwentCompanion"); var cache = Path.Combine(project, "cache");
        var folder = Path.Combine(project, "sessions", Session);
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        CardDefinition Get(string id) => catalog.Single(card => card.Id == id);
        var at = DateTimeOffset.UnixEpoch;
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);
        var pick = board with { IsCardSelectionOverlay = true, ScreenHeader = "PICK A CARD TO PLAY" };
        VisionEvidenceEvent Event(CardDefinition card, double seconds, PlayerSide side = PlayerSide.User) =>
            new(at.AddSeconds(seconds), new(card, side, CardSightSource.PlayPreview, new(.81,.41,.92,.66), .08, 1), "fixture");
        DetectorUpdateTests.Confirmation(); PlayProvenanceTests.Run(root); DetectorUpdateTests.Origins(root); MatchReviewTests.Inference(root);
        var rules = new DeckPlayResolutionTracker(catalog);
        var source = Get("202203"); var fog = Get("113305");
        Check(DeckPlayResolutionTracker.NamedTargets(source, catalog).Count == 3, "Named multi-target grammar (including Oxford comma).");
        Check(DeckPlayResolutionTracker.NamedTargets(source with { Id="other-source", Name="Other source" }, catalog).Count == 3,
            "Tutor rule is keyed to a particular card identity.");
        foreach(var target in DeckPlayResolutionTracker.NamedTargets(source, catalog))
        {
            rules.Reset();
            rules.Observe(at,board with {UserDeckCount=14,UserHandCount=8},[],null);
            rules.Observe(at.AddSeconds(1),board,[Event(source,1)],null);
            Check(rules.Observe(at.AddSeconds(2),pick,[],new([target,target])).Count==0,"Choice page spent cards.");
            rules.Observe(at.AddSeconds(2.3),pick,[],new([target,target]));
            rules.Observe(at.AddSeconds(2.5),pick,[],new([],Complete:false)); // closing fade is not contrary identity
            Check(rules.Observe(at.AddSeconds(3),board with {UserDeckCount=14,UserHandCount=7},[],null).Count==0,"Unchanged deck was a resolution.");
            var resolved=rules.Observe(at.AddSeconds(4),board with {UserDeckCount=12,UserHandCount=7},[],null);
            Check(resolved.Count==1 && resolved[0].ResolvedDeckCopies==2 && resolved[0].Sighting.Card.Id==target.Id,"Multi-copy tutor resolution lost identity/copies.");
            Check(rules.Observe(at.AddSeconds(5),board with {UserDeckCount=12,UserHandCount=7},[],null).Count==0,"Resolution counted twice.");
            var tracker=new LiveDeckTracker(PlayerSide.User); tracker.SetFactionPrior("Scoia'tael");
            tracker.ConsiderDirectPlay(target,.92,at.AddSeconds(4),"fixture",CardProvenance.ProbableStartingDeck);
            new ThinningCopyTracker().ObserveEvent(resolved[0],CardProvenance.ProbableStartingDeck,tracker);
            Check(tracker.Observations.Single().ObservedCopies==2,"Resolved copies did not reach accounting tracker.");
            var reference=new DeckDefinition("fixture","fixture","Scoia'tael","",0,[new(target,2)]);
            var state=new GameStateTracker(); state.Reset("resolution",reference); var ledger=new LiveValueLedger();
            ledger.Observe(GameStateVisionAdapter.Apply(state,new(at.AddSeconds(4),board,[],resolved,false)),catalog,[],[]);
            Check(LiveValueLedger.Provisions(tracker.Observations,reference,null,ledger.Spent(PlayerSide.User),ledger.SpentCopies(PlayerSide.User)).CommittedCards==2,
                "Resolution metadata did not propagate to live provision counter.");
        }
        for(var mode=0;mode<7;mode++)
        {
            rules.Reset(); rules.Observe(at,board with {UserDeckCount=14,UserHandCount=8},[],null);
            rules.Observe(at.AddSeconds(1),board,mode==0?[]:[Event(source,1)],null);
            rules.Observe(at.AddSeconds(2),pick,[],new([fog,fog]));
            if(mode!=1) rules.Observe(at.AddSeconds(2.3),pick,[],new(mode==2?[fog,Get("113302")]:[fog,fog]));
            if(mode==3) rules.Observe(at.AddSeconds(3),board,[Event(Get("202263"),3)],null);
            if(mode==4) rules.Observe(at.AddSeconds(3),pick with {ScreenHeader="DECK"},[],null);
            var result=rules.Observe(at.AddSeconds(mode==5?50:4),board with {UserDeckCount=12,UserHandCount=mode==6?9:7},[],null);
            if(mode==0)
                Check(result.Count==2 && result[0].Sighting.Card.Id==source.Id && result[1].Sighting.Card.Id==fog.Id && result[1].ResolvedDeckCopies==2,
                    "Repeated complete named choices plus exact hand/deck conservation did not recover the uniquely implied missed source.");
            else Check(result.Count==0,$"Unsafe tutor inference accepted (negative {mode}).");
        }
        var royal=catalog.Single(card=>card.Name=="Royal Decree"); var vereena=catalog.Single(card=>card.Name=="Vereena");
        Check(DeckPlayResolutionTracker.IsGenericDeckPlaySource(royal) && DeckPlayResolutionTracker.CanSelectFromDeck(royal,vereena) &&
            !DeckPlayResolutionTracker.CanSelectFromDeck(royal,fog),"Generic tutor eligibility ignored printed target kind.");
        rules.Reset();
        rules.Observe(at,board with {UserDeckCount=8,UserHandCount=2},[],null);
        rules.Observe(at.AddSeconds(1),board,[Event(royal,1)],null);
        Check(rules.Observe(at.AddSeconds(2),pick,[],new([],false,vereena)).Count==0,"One selected-title frame resolved a tutor.");
        Check(rules.Observe(at.AddSeconds(2.3),pick,[],new([],false,vereena)).Count==0,"Selection resolved before the overlay closed.");
        var selected=rules.Observe(at.AddSeconds(3),board with {UserDeckCount=7,UserHandCount=1},[],null);
        Check(selected is [{ResolvedDeckCopies:1}] && selected[0].Sighting.Card.Id==vereena.Id,
            "Repeated exact selected title plus deck/hand conservation did not recover the mandatory Royal Decree target.");
        rules.Reset(); rules.Observe(at,board with {UserDeckCount=8,UserHandCount=2},[],null);
        rules.Observe(at.AddSeconds(1),board,[Event(royal,1)],null);
        rules.Observe(at.AddSeconds(2),pick,[],new([],false,vereena));
        rules.Observe(at.AddSeconds(2.3),pick,[],new([],false,vereena));
        Check(rules.Observe(at.AddSeconds(3),board with {UserHandCount=1},[],null).Count==0,
            "A lingering highlighted tutor option was accepted without a confirming deck decrement.");
        rules.Reset(); rules.Observe(at,board,[],null); rules.Observe(at.AddSeconds(1),board,[Event(royal,1)],null);
        rules.Observe(at.AddSeconds(2),pick,[],new([],false,vereena));
        Check(rules.Observe(at.AddSeconds(3),board,[],null).Count==0,"One-frame tutor browsing invented a selected card.");

        var latest=Path.Combine(project,"sessions/20260901-124026");
        if(Directory.Exists(latest))
        {
            var latestReferences=VisionReferenceLibrary.Load(catalog,cache);
            using var pipeline=new CardVisionPipeline(latestReferences,catalog,Path.Combine(cache,"recognition-features"));
            foreach(var fixture in new[]{("frame-000974-124204053.jpg","Fleder"),("frame-001166-124223263.jpg","Feast of Blood")})
            {
                var prepared=await pipeline.PrepareAsync(Load(Path.Combine(latest,fixture.Item1)),at.AddMinutes(1));
                Check(prepared.HoveredCard?.Name==fixture.Item2 && prepared.HoverInPlayerHand,
                    $"Readable selected hand title did not survive coarse tooltip merging: {fixture.Item2}.");
                pipeline.Reset();
            }
            using var choiceOcr=new ScreenStateRecognizer();
            var latestChoiceReader=new DeckPlayChoiceRecognizer(latestReferences,catalog);
            foreach(var name in new[]{"frame-007288-125236060.jpg","frame-007291-125236337.jpg"})
            {
                var pixels=Load(Path.Combine(latest,name)); var choiceScreen=await choiceOcr.AnalyzeAsync(pixels);
                var read=await latestChoiceReader.ReadAsync(pixels,choiceScreen,choiceOcr);
                Check(read?.HighlightedCard?.Id==vereena.Id,"Vereena's selected deck-choice title was not recovered from GWENT font pixels.");
            }
        }
        var origin=new PlayProvenanceResolver();
        var runemage=catalog.Single(card=>card.Name=="Runemage"); var runestone=catalog.Single(card=>card.Name=="Morana Runestone");
        origin.Observe(Event(runemage,0,PlayerSide.Opponent));
        Check(origin.Observe(Event(runestone,3,PlayerSide.Opponent)).Provenance==CardProvenance.Unknown,
            "Runemage's selected Runestone was charged as an original deck card.");
        origin.Reset();
        foreach(var pair in new[]{("203210","201579"),("201583","162307"),("162315","202988"),("203045","203270")})
        {
            // Use a compatible catalog target for sources whose printed identities vary by patch.
            var target=pair.Item1=="201583"?catalog.First(c=>!c.IsGold&&c.CanBeInStartingDeck&&c.Faction=="Nilfgaard"&&c.Kind==CardKind.Unit):
                pair.Item1=="203045"?catalog.First(c=>c.Faction=="Scoia'tael"&&c.Kind==CardKind.Special):Get(pair.Item2);
            origin.Reset(); origin.Observe(Event(Get(pair.Item1),0,PlayerSide.Opponent));
            origin.Observe(Event(Get("202677"),1)); // other-side evidence must not consume the window
            Check(origin.Observe(Event(target,3,PlayerSide.Opponent)).Provenance==CardProvenance.Unknown,"Acquired/generated target charged as original: "+pair.Item1);
            Check(origin.Observe(Event(target,40,PlayerSide.Opponent)).Provenance==CardProvenance.Unknown,"Later preview erased copy uncertainty.");
        }
        origin.Reset(); origin.Observe(Event(Get("202263"),0));
        Check(origin.Observe(Event(Get("203047"),3)).Provenance==CardProvenance.ProbableStartingDeck,"Ordinary tutor treated as Create.");
        var sameReference=new DeckDefinition("same","same","Scoia'tael","",0,[new(Get("203047"))]);
        var uncertain=new ObservedCard(Get("203047"),CardProvenance.Unknown,.9,at,"created");
        Check(LiveValueLedger.Provisions([uncertain],sameReference,null,new HashSet<string>{"203047"},new Dictionary<string,int>{{"203047",1}}).SpentFloor==0,
            "Known-deck fallback overrode explicit uncertain provenance.");
        Check(new ProvisionUsage(167,165,null,"",25,0).RemainingReadout.Contains("conflicts"),"Impossible budget claimed all slots accounted.");

        foreach(var id in new[]{"203047","202677"})
        {
            var values=new LiveValueLedger(); var state=new GameStateTracker().Current with {SessionId="buff-"+id,At=at,Phase=GamePhase.Playing};
            var play=new GameStateEvent("buff-event",at,"PlayPreview",PlayerSide.User,id,null,"fixture");
            values.Observe(new(state,state,[play],true),catalog,[],[]);
            values.Observe(new(state,state,[play],true),catalog,[],[]);
            Check(values.Pending is [{Kind:PendingValueKind.HandBoost,Minimum:0,Maximum:2}],"Conditional hand boost missing or duplicated.");
            Check(values.CarryoverReadout(PlayerSide.User)=="Carryover 2 (max)","Conditional carryover did not use its cautious maximum.");
            var next=state with {At=at.AddSeconds(1),Round=new(2,at.AddSeconds(1),1,EvidenceKind.Visual,"fixture")};
            values.Observe(new(state,next,[],true),catalog,[],[]);
            Check(values.Pending.Count==1,"Round change incorrectly erased hand boost.");
            var empty=next with {At=at.AddSeconds(2),User=next.User with {HandCount=new(0,at.AddSeconds(2),1,EvidenceKind.Visual,"fixture")}};
            values.Observe(new(next,empty,[],true),catalog,[],[]);
            Check(values.Pending.Count==0,"Empty hand retained banked hand boost.");
        }
        {
            var values=new LiveValueLedger();var state=new GameStateTracker().Current with
                {SessionId="stacked-hand-buffs",At=at,Phase=GamePhase.Playing,
                 User=new(PlayerSide.User,HandCount:new(5,at,1,EvidenceKind.Visual,"fixture"))};
            values.Observe(new(state,state,
                [new("harvest",at,"PlayPreview",PlayerSide.User,"203047",null,"fixture")],true),catalog,[],[]);
            var after=state with {At=at.AddSeconds(1),User=state.User with
                {HandCount=new(5,at.AddSeconds(1),1,EvidenceKind.Visual,"fixture")}};
            values.Observe(new(state,after,
                [new("circle",at.AddSeconds(1),"PlayPreview",PlayerSide.User,"202677",null,"fixture")],true),catalog,[],[]);
            Check(values.Pending.Count==2 && values.Pending.Sum(item=>item.Maximum)==4 &&
                values.CarryoverReadout(PlayerSide.User)=="Carryover 4 (max)",
                "Harvest and Circle did not accumulate as two independent conditional +2 hand boosts.");
        }
        foreach(var held in new[]{Get("201579"),Get("203047")}) // neutral unit / non-neutral special
        {
            var values=new LiveValueLedger();var state=new GameStateTracker().Current with {SessionId="ineligible",At=at,Phase=GamePhase.Playing,
                User=new(PlayerSide.User,HandCount:new(1,at,1,EvidenceKind.Visual,"fixture")),
                Cards=[new("held",held,new(new(PlayerSide.User,CardZone.Hand,null,null),at,1,EvidenceKind.Visual,"fixture"),at,at,CardPresence.Visible)]};
            values.Observe(new(state,state,[new("circle",at,"PlayPreview",PlayerSide.User,"202677",null,"fixture")],true),catalog,[],[]);
            Check(values.Pending.Count==0,"Circle of Life accepted an ineligible hand target.");
        }

        var replay=new MatchVisionLedger();var recovered=new List<VisionEvidenceEvent>();
        foreach(var line in File.ReadLines(Path.Combine(folder,"vision-observations.jsonl")))
        {
            var row=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            recovered.AddRange(replay.Observe(row.SampledAt,row.Screen,row.Sightings.Select(CardVisionPipeline.RequirePreviewCorroboration).ToArray(),
                row.BoardWasScanned,row.HoveredCard,row.ArtworkWasScanned,row.HoverInPlayerHand));
        }
        var harvest=recovered.Where(e=>e.Sighting.Side==PlayerSide.User&&e.Sighting.Card.Id=="203047").ToArray();
        Check(harvest.Length==1,"Recorded Bountiful Harvest must survive gaps without duplicating across its nested choice menus.");
        var latestFolder=Path.Combine(root,"GwentCompanion/sessions",LatestCarryoverSession);
        var latestSaved=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(latestFolder,"game-state-final.json")),GameStateJournal.Json)!;
        var latestReference=latestSaved.User.StartingDeckReference!;
        var latestReplay=new MatchVisionLedger();var latestRecovered=new List<VisionEvidenceEvent>();
        var latestState=new GameStateTracker();latestState.Reset(LatestCarryoverSession,latestReference);
        var latestValues=new LiveValueLedger();string? circleCarryover=null,harvestCarryover=null;
        foreach(var line in File.ReadLines(Path.Combine(latestFolder,"vision-observations.jsonl")))
        {
            var row=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            var sightings=row.Sightings.Select(CardVisionPipeline.RequirePreviewCorroboration).ToArray();
            var frameEvents=latestReplay.Observe(row.SampledAt,row.Screen,sightings,row.BoardWasScanned,
                row.HoveredCard,row.ArtworkWasScanned,row.HoverInPlayerHand);
            latestRecovered.AddRange(frameEvents);
            var update=GameStateVisionAdapter.Apply(latestState,row with {Sightings=sightings,Events=frameEvents});
            latestValues.Observe(update,catalog,latestReference.Cards.Select(item=>item.Card),[]);
            if(frameEvents.Any(item=>item.Sighting.Card.Id=="202677")) circleCarryover=latestValues.CarryoverReadout(PlayerSide.User);
            if(frameEvents.Any(item=>item.Sighting.Card.Id=="203047")) harvestCarryover=latestValues.CarryoverReadout(PlayerSide.User);
        }
        var circle=latestRecovered.Where(e=>e.Sighting.Side==PlayerSide.User&&e.Sighting.Card.Id=="202677").ToArray();
        Check(circle.Length==1,"Recorded Sorceress choice must recover exactly one Circle of Life when its repeated title remains selected as the PLAY menu closes.");
        Check(circleCarryover=="Carryover 2 (max)" && harvestCarryover=="Carryover 4 (max)",
            $"Recorded hand-boost carryover sequence was wrong: Circle={circleCarryover}; Harvest={harvestCarryover}.");
        Console.WriteLine("Recorded Harvest episodes: "+string.Join(", ",harvest.Select(e=>e.ObservedAt.ToString("HH:mm:ss.fff"))));
        Console.WriteLine("Recorded nested Circle of Life: "+string.Join(", ",circle.Select(e=>e.ObservedAt.ToString("HH:mm:ss.fff"))));
        using var ocr=new ScreenStateRecognizer();var titles=new PreviewTitleRecognizer(catalog);
        var scorpion=Load(Path.Combine(folder,"play-events/20260831-103340773-Opponent/during.png"));
        Check(!(await titles.RecognizeAsync(scorpion,new GwentVisualStateDetector().Analyze(scorpion),ocr)).Any(hit=>hit.Card.Name=="Siege"),"Category fragment became Siege.");
        var choiceReader=new DeckPlayChoiceRecognizer(VisionReferenceLibrary.Load(catalog,cache),catalog);
        var choice=choiceReader.Read(Load(Path.Combine(folder,FogChoiceFrame)),pick);
        Check(choice?.Cards.Count==2&&choice.Cards.All(card=>card.Id==fog.Id),"Pixel choice reader missed the Fog pair.");
        Check(choiceReader.Read(scorpion,board) is null,"Ordinary board treated as tutor choices.");
        Check(await OpponentHudRecognizer.ReadUserDeckCandidateAsync(Load(Path.Combine(folder,PreTutorFrame)),ocr)==14,
            "Compact pre-tutor deck count was not read as 14.");
        Check(await OpponentHudRecognizer.ReadUserDeckCandidateAsync(Load(Path.Combine(folder,"frame-001745-102701075.jpg")),ocr)==12,
            "Post-tutor deck count was not read as 12.");
        var ended=Load(Path.Combine(folder,"frame-010567-104143756.jpg"));
        await ocr.AnalyzeAsync(ended);var endScreen=await ocr.AnalyzeAsync(ended);
        Check(endScreen.ScreenHeader=="GAME OVER","Centered forfeit modal missed.");
        var endState=new GameStateTracker();var endResult=new CardVisionResult(at,endScreen,[],[],false);
        Check(GameStateVisionAdapter.Apply(endState,endResult).After.Phase==GamePhase.Ended,"Forfeit modal failed to end state.");
        Console.WriteLine("PASS general match defects: temporal gaps, named tutors, provenance, conditional boosts, title/category and forfeit.");
    }
    public static void Artwork(string root)
    {
        var cache=Path.Combine(root,"GwentCompanion/cache");var folder=Path.Combine(root,"GwentCompanion/sessions",Session);
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var reference=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder,"game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference!;
        using var detector=new FeatureCardRecognizer(VisionReferenceLibrary.Load(catalog,cache),Path.Combine(cache,"recognition-features"),scope:VisionReferenceScope.CandidateDecks);
        detector.SetKnownPlayerDeck(reference.Cards.Select(c=>c.Card.Id));
        var board=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,MatchHudVisible:true);
        foreach(var file in new[]{"frame-002943-102901174.jpg","frame-008013-103728166.jpg"})
            Check(detector.Recognize(Load(Path.Combine(folder,file)),board).Any(s=>s.Card.Id=="202988"&&s.Side==PlayerSide.User&&s.Source==CardSightSource.Board),
                "Independent animation references did not recover Elven Seer: "+file);
        var early=Load(Path.Combine(folder,"frame-000326-102439183.jpg"));
        Check(!detector.Recognize(early,board).Any(s=>s.Card.Id=="202988"),"Unplayed reference card leaked into early board.");
        var preview=Load(Path.Combine(folder,"frame-001945-102721073.jpg"));
        Check(detector.Recognize(preview,board,includeBoard:false).Any(s=>s.Card.Id=="203047"&&s.Source==CardSightSource.PlayPreview),
            "Candidate-deck reference index was disabled on preview-only artwork passes.");
        Console.WriteLine("PASS animation coverage and candidate-index preview-only scan (unchanged geometry/appearance thresholds).");
    }
    public static async Task TutorPixels(string root)
    {
        var cache=Path.Combine(root,"GwentCompanion/cache");var folder=Path.Combine(root,"GwentCompanion/sessions",Session);
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var files=Directory.GetFiles(folder,"frame-*.jpg").ToDictionary(path=>Path.GetFileNameWithoutExtension(path).Split('-')[2]);
        using var pipeline=new CardVisionPipeline(VisionReferenceLibrary.Load(catalog,cache),catalog,
            Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        var events=new List<VisionEvidenceEvent>();
        var diagnostic=new DeckPlayResolutionTracker(catalog){Trace=Console.WriteLine};
        foreach(var line in File.ReadLines(Path.Combine(folder,"vision-observations.jsonl")))
        {
            var old=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            var stamp=old.SampledAt.ToString("HHmmssfff");
            if(string.CompareOrdinal(stamp,"102620000")<0 || string.CompareOrdinal(stamp,"102710000")>0) continue;
            if(!files.TryGetValue(stamp,out var path))
            {
                // Raw video retention does not include every already-processed text
                // tick. Keep its original hover/pointer/source evidence, not a hole.
                var retained=pipeline.Commit(old with { Events=[] });
                diagnostic.Observe(retained.SampledAt,retained.Screen,retained.Events,retained.DeckPlayChoices);
                events.AddRange(retained.Events);
                continue;
            }
            var prepared=await pipeline.PrepareAsync(Load(path),old.SampledAt);
            var result=pipeline.Commit(prepared.TextResult with {Sightings=old.Sightings,BoardWasScanned=old.BoardWasScanned,ArtworkWasScanned=old.ArtworkWasScanned,
                HoveredCard=old.HoveredCard,HoverInPlayerHand=old.HoverInPlayerHand});
            events.AddRange(result.Events);
            diagnostic.Observe(result.SampledAt,result.Screen,result.Events,result.DeckPlayChoices);
            if(result.Screen.UserDeckCount is not null || result.DeckPlayChoices?.Cards.Count>0 || result.Events.Count>0)
                Console.WriteLine($"TUTOR {stamp}: deck={result.Screen.UserDeckCount} hand={result.Screen.UserHandCount} header={result.Screen.ScreenHeader} choice={result.DeckPlayChoices?.Cards.Count} events={string.Join(',',result.Events.Select(e=>e.Sighting.Card.Name+":"+e.ResolvedDeckCopies))}");
        }
        Check(events.Count(e=>e.Sighting.Card.Id=="113305"&&e.ResolvedDeckCopies==2)==1,
            "Fresh HUD/choice pixels did not resolve the recorded Fog pair (retained original source sightings).");
        Console.WriteLine("PASS recorded Tempest resolution: fresh HUD/choice pixels; original source sightings retained.");
    }
    public static async Task Probe(string root)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var cache = Path.Combine(project, "cache");
        var folder = Path.Combine(project, "sessions", Session);
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var state = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder, "game-state-final.json")), GameStateJournal.Json)!;
        using var recognizer = new FeatureCardRecognizer(VisionReferenceLibrary.Load(catalog, cache), Path.Combine(cache, "recognition-features"), scope: VisionReferenceScope.CandidateDecks);
        recognizer.SetKnownPlayerDeck(state.User.StartingDeckReference!.Cards.Select(item => item.Card.Id));
        using var ocr = new ScreenStateRecognizer();
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);
        foreach(var file in Directory.GetFiles(folder,"frame-*10265*.jpg").Order().Where((_,i)=>i%5==0))
            Console.WriteLine("DECK SAMPLE " + Path.GetFileName(file) + " " + await OpponentHudRecognizer.ReadUserDeckCandidateAsync(Load(file),ocr));
        var choiceReader = new DeckPlayChoiceRecognizer(VisionReferenceLibrary.Load(catalog, cache), catalog);
        var preDeck=Load(Path.Combine(folder,PreTutorFrame));
        foreach(var scale in new[]{1,2,4}) foreach(var white in new[]{false,true})
            Console.WriteLine("COUNTER RETRY " + scale + " " + white + " " + string.Join('|',(await ocr.ReadLinesAsync(preDeck,new(.845,.807,.90,.881),scale,enhance:false,whiteLetterMask:white)).Select(line=>line.Text)));
        HudDigitReader.Trace=Console.WriteLine;
        Console.WriteLine("COUNTER GLYPH " + HudDigitReader.ReadDefault(preDeck,new(.855,.815,.88,.86)));
        foreach(var file in new[]{"board-power-digits.json","coin-digits.json"})
            Console.WriteLine("COUNTER OTHER " + file + " " + new HudDigitReader(JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"vision-assets",file)))!).Read(preDeck,new(.855,.815,.88,.86)));
        HudDigitReader.Trace=null;
        Console.WriteLine("TARGET RULE " + catalog.Single(card => card.Id == "202203").AbilityText + " => " + DeckPlayResolutionTracker.NamedTargets(catalog.Single(card => card.Id == "202203"), catalog).Count);
        foreach (var name in new[] { PreTutorFrame, "frame-001745-102701075.jpg" })
        {
            Console.WriteLine("USER DECK " + name + " " + await OpponentHudRecognizer.ReadUserDeckCandidateAsync(Load(Path.Combine(folder, name)), ocr));
            foreach(var enhance in new[]{false,true}) Console.WriteLine("DECK OCR " + enhance + " " + string.Join('|', (await ocr.ReadLinesAsync(Load(Path.Combine(folder, name)),new(.81,.80,.925,.955), 2, enhance:enhance)).Select(line => line.Text)));
            foreach (var region in new[] { new NormalizedRegion(.845,.807,.89,.876), new NormalizedRegion(.855,.815,.88,.86), new NormalizedRegion(.87,.885,.91,.935) })
                Console.WriteLine("DECK TIGHT " + region + " => " + string.Join('|', (await ocr.ReadLinesAsync(Load(Path.Combine(folder, name)), region, 3, enhance:false, whiteLetterMask:true, smooth:true)).Select(line=>line.Text)) + " glyph " + HudDigitReader.ReadDefault(Load(Path.Combine(folder, name)), region));
        }
        var choiceFrame = Load(Path.Combine(folder,FogChoiceFrame));
        Console.WriteLine("DECK CHOICES " + JsonSerializer.Serialize(choiceReader.Read(choiceFrame,
            board with { IsCardSelectionOverlay = true, ScreenHeader = "PICK A CARD TO PLAY" })));
        foreach (var file in new[] { "frame-002943-102901174.jpg", "frame-007603-103647171.jpg", "frame-008013-103728166.jpg" })
        {
            Console.WriteLine("FRAME " + file);
            recognizer.Trace = Console.WriteLine;
            var hits = recognizer.Recognize(Load(Path.Combine(folder, file)), board);
            Console.WriteLine(JsonSerializer.Serialize(hits.Select(hit => new { hit.Card.Name, hit.Source, hit.Distance, hit.Region })));
            var area = file.StartsWith("frame-002") ? new NormalizedRegion(.645, .66, .716, .839) :
                file.StartsWith("frame-007") ? new NormalizedRegion(.511, .452, .59, .615) : new NormalizedRegion(.409, .487, .488, .66);
            Console.WriteLine("BOARD COLOUR " + JsonSerializer.Serialize(new CardArtMatcher(recognizer.ArtReferences).RankAligned(Load(Path.Combine(folder, file)), area, 4)
                .Select(hit => new { hit.Card.Name, hit.Distance, hit.Region })));
        }
        var titles = new PreviewTitleRecognizer(catalog) { Trace = Console.WriteLine };
        var scorpion = Load(Path.Combine(folder, "play-events/20260831-103340773-Opponent/during.png"));
        Console.WriteLine("TITLE " + JsonSerializer.Serialize(await titles.RecognizeAsync(scorpion, new GwentVisualStateDetector().Analyze(scorpion), ocr)));
        var ended = Load(Path.Combine(folder, "frame-010567-104143756.jpg"));
        Console.WriteLine("MODAL OCR " + await ocr.ReadAsync(ended, new(.35, .39, .66, .54)));
        Console.WriteLine("MODAL RAW " + string.Join('|', (await ocr.ReadLinesAsync(ended, new(.35, .39, .66, .54), enhance: false)).Select(item => item.Text)));
        await ocr.AnalyzeAsync(ended);
        Console.WriteLine("ENDED " + JsonSerializer.Serialize(await ocr.AnalyzeAsync(ended)));
        var weather = catalog.Where(card => card.Name is "Impenetrable Fog" or "Biting Frost" or "Torrential Rain").ToArray();
        var matcher = new CardArtMatcher(weather.Select(card => new CardArtReference(card, VisualDescriptor.Create(Load(Path.Combine(cache, "portraits", card.Id + ".jpg"))))));
        foreach (var file in new[] { FogChoiceFrame, "frame-001886-102715188.jpg" })
        foreach (var region in new[] { new NormalizedRegion(.281, .245, .469, .747), new NormalizedRegion(.527, .245, .715, .747) })
            Console.WriteLine("CHOICE " + file + " " + JsonSerializer.Serialize(matcher.RankAligned(Load(Path.Combine(folder, file)), region).Select(hit => new { hit.Card.Name, hit.Distance, hit.Region })));
    }
}
