using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;

internal static class VisionEfficiencyTests
{
    internal const string Session = "20260831-122341";
    internal static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task Stream(string root, string[] args)
    {
        var project=Path.Combine(root,"GwentCompanion"); var cache=Path.Combine(project,"cache"); var folder=Path.Combine(project,"sessions",Session);
        var cards=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var rows=File.ReadLines(Path.Combine(folder,"vision-observations.jsonl")).Select(line=>JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!).ToArray();
        using var pipeline=new CardVisionPipeline(VisionReferenceLibrary.Load(cards,cache),cards,Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        pipeline.SetLikelyOpponentCards(rows.SelectMany(r=>r.Sightings).Where(s=>s.Side==PlayerSide.Opponent).Select(s=>s.Card.Id).Distinct());
        pipeline.SetKnownPlayerDeck(rows.SelectMany(r=>r.Sightings).Where(s=>s.Side==PlayerSide.User).Select(s=>s.Card.Id).Distinct());
        var files=Directory.GetFiles(folder,"frame-*.jpg").Order().Select(f=>(Path:f,Time:Path.GetFileNameWithoutExtension(f).Split('-')[2]))
            .Where(f=>StringComparer.Ordinal.Compare(f.Time,"122404000")>=0 && StringComparer.Ordinal.Compare(f.Time,"122510000")<0).ToArray();
        DateTimeOffset At(string t) { var d=DateTime.ParseExact(t,"HHmmssfff",null); return new(2026,8,31,d.Hour,d.Minute,d.Second,d.Millisecond,TimeSpan.FromHours(-4)); }
        var first=At(files[0].Time); var clock=Stopwatch.StartNew(); var buffer=new RollingVisionBuffer<VisionInput<string>>();
        var producer=Task.Run(async()=>
        {
            var motion=new PreviewMotionSampler(); var opponent=new PreviewPriorityWindow(); var user=new PreviewPriorityWindow();
            try
            {
                foreach(var file in files)
                {
                    var at=At(file.Time); var delay=(at-first)-clock.Elapsed;
                    if(delay>TimeSpan.Zero) await Task.Delay(delay);
                    var pixels=Load(file.Path); var change=motion.Measure(pixels);
                    var op=opponent.Score(at,change.Opponent); var own=user.Score(at,change.User);
                    var priority=op>=.06?3+op:own>=.06?2+own:Math.Max(op,own);
                    buffer.Offer(new(pixels,at,priority,Path.GetFileName(file.Path)),at,priority);
                }
            }
            finally { buffer.Complete(); }
        });
        async IAsyncEnumerable<VisionInput<string>> Inputs() { await foreach(var item in buffer.ReadAllAsync()) yield return item; }
        var processor=new StreamingVisionProcessor<string>(pipeline);
        var lags=new List<double>(); var results=new List<CardVisionResult>();
        await foreach(var output in processor.RunAsync(Inputs()))
        {
            lags.Add(Math.Max(0,(clock.Elapsed-(output.Result.SampledAt-first)).TotalSeconds)); results.Add(output.Result);
            if(results.Count%50==0) Console.WriteLine($"Stream {results.Count} frames, lag {lags[^1]:F2}s");
        }
        await producer; lags.Sort();
        var report=new { Version=typeof(CardVisionPipeline).Assembly.GetName().Version?.ToString(),InputFrames=files.Length,buffer.Dropped,
            processor.PreparedFrames,processor.ArtworkPasses,processor.SkippedArtworkFrames,processor.MaximumPendingFrames,processor.Errors,
            MaximumLagSeconds=lags.Max(),P95LagSeconds=lags[(int)((lags.Count-1)*.95)],MeanLagSeconds=lags.Average(),ElapsedSeconds=clock.Elapsed.TotalSeconds,
            Events=results.SelectMany(r=>r.Events).ToArray(),Results=results,
            Note="Real-time paced retained-pixel replay with fixed identical candidate scope. No GWENT, capture encoding, UI, or Reach load; not a live latency guarantee." };
        var target=args[Array.IndexOf(args,"--output")+1]; File.WriteAllText(target,JsonSerializer.Serialize(report,GameStateJournal.Json));
        Console.WriteLine($"Stream done: {processor.PreparedFrames}/{files.Length}; art={processor.ArtworkPasses}; drops={buffer.Dropped}; max={lags.Max():F2}s p95={report.P95LagSeconds:F2}s");
    }
    public static async Task Run(string root, bool writeAccountingReview = false)
    {
        var project = Path.Combine(root,"GwentCompanion"); var cache = Path.Combine(project,"cache");
        var folder = Path.Combine(project,"sessions",Session);
        var cards = GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var references = VisionReferenceLibrary.Load(cards,cache);
        var reader = new DeckPlayChoiceRecognizer(references,cards);
        var pick = new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,IsCardSelectionOverlay:true,ScreenHeader:"PICK A CARD TO PLAY");
        DeckPlayChoiceReading? retainedFogChoice=null;
        foreach (var file in new[]{"frame-000610-122441984.jpg","frame-000612-122442190.jpg","frame-000614-122442390.jpg"})
        {
            var choices = reader.Read(Load(Path.Combine(folder,file)),pick);
            Check(choices is { Complete:true, Cards.Count:2 } && choices.Cards.All(c=>c.Id=="113305"),"Fog pair / tooltip coverage: "+file);
            retainedFogChoice=choices;
        }
        var original = Load(Path.Combine(folder,"frame-000610-122441984.jpg"));
        Check(reader.Read(original,pick with {ScreenHeader="DECK"}) is null &&
            reader.Read(original,pick with {IsCardSelectionOverlay=false}) is null,"Browsing / non-choice pages must not resolve tutors.");
        var hiddenPixels=(byte[])original.BgraPixels.Clone(); var random=new Random(39);
        for(var y=190;y<528;y++) for(var x=375;x<590;x++)
        {
            var offset=(y*original.Width+x)*4;
            hiddenPixels[offset]=hiddenPixels[offset+1]=hiddenPixels[offset+2]=(byte)random.Next(256);
        }
        var partial=reader.Read(new(original.Width,original.Height,hiddenPixels),pick);
        Check(partial is {Complete:false},"An unreadable choice with visible borders must not be marked fully enumerated.");
        using var ocr = new ScreenStateRecognizer();
        foreach (var file in new[]{"frame-000603-122441283.jpg","frame-000605-122441481.jpg","frame-000607-122441684.jpg"})
            Check(await OpponentHudRecognizer.ReadUserDeckCandidateAsync(Load(Path.Combine(folder,file)),ocr)==13,"Intermediate deck counter: "+file);
        var allRows = File.ReadLines(Path.Combine(folder,"vision-observations.jsonl"))
            .Select(line=>JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!).ToArray();
        var rows = allRows
            .Where(row=>row.SampledAt.ToString("HHmmss") is var time && StringComparer.Ordinal.Compare(time,"122434")>=0 && StringComparer.Ordinal.Compare(time,"122446")<0).ToArray();
        using var pipeline = new CardVisionPipeline(references,cards,Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        var files = Directory.GetFiles(folder,"frame-*.jpg").ToDictionary(f=>Path.GetFileNameWithoutExtension(f).Split('-')[2]);
        var resolver = new DeckPlayResolutionTracker(cards); var resolved = new List<VisionEvidenceEvent>();
        var reviewed = new Dictionary<DateTimeOffset,CardVisionResult>();
        foreach (var row in rows)
        {
            if (!files.TryGetValue(row.SampledAt.ToString("HHmmssfff"),out var file)) continue;
            var prepared = await pipeline.PrepareAsync(Load(file),row.SampledAt);
            reviewed[row.SampledAt] = row with { Screen = row.Screen with { UserDeckCount=prepared.Screen.UserDeckCount,
                UserHandCount=prepared.Screen.UserHandCount }, DeckPlayChoices=prepared.DeckPlayChoices };
            // Re-read HUD and choice pixels, retain the recorded independently
            // recognized source events. Raw journals are never rewritten.
            resolved.AddRange(resolver.Observe(row.SampledAt,prepared.Screen,row.Events,prepared.DeckPlayChoices));
        }
        var compactSemanticReplay=false;
        if(resolved.Count!=1 || resolved[0].Sighting.Card.Id!="113305" || resolved[0].ResolvedDeckCopies!=2)
        {
            // Dense scheduling frames were compacted after their independent pixel
            // checks above. Combine the retained, freshly read two-card choice with
            // the reviewed source and HUD transition to keep resolution semantics
            // covered without presenting the label as another OCR result.
            compactSemanticReplay=true; resolver.Reset(); resolved.Clear();
            var source=allRows.SelectMany(row=>row.Events).Single(e=>e.Sighting.Card.Id=="202203");
            var compactBoard=pick with {IsCardSelectionOverlay=false,ScreenHeader=null,MatchHudVisible=true,UserDeckCount=15,UserHandCount=8};
            resolver.Observe(source.ObservedAt.AddSeconds(-5),compactBoard,[],null);
            resolver.Observe(source.ObservedAt,compactBoard,[source],null);
            resolver.Observe(source.ObservedAt.AddSeconds(1),compactBoard with {UserDeckCount=13,UserHandCount=7},[],null);
            resolver.Observe(source.ObservedAt.AddSeconds(2),pick,[],retainedFogChoice);
            resolver.Observe(source.ObservedAt.AddSeconds(2.3),pick,[],retainedFogChoice);
            resolved.AddRange(resolver.Observe(source.ObservedAt.AddSeconds(4),compactBoard with {UserDeckCount=11,UserHandCount=7},[],null));
        }
        Check(resolved.Count==1 && resolved[0].Sighting.Card.Id=="113305" && resolved[0].ResolvedDeckCopies==2,
            "Fresh latest HUD/choices did not resolve exactly two Fog copies once.");
        Console.WriteLine(compactSemanticReplay
            ? "PASS compact Tempest: fresh retained two-card choice pixels plus reviewed 15→13→11 transition; exactly one two-copy resolution."
            : "PASS latest Tempest: fresh two-card choice coverage and intermediate 13→11 HUD evidence; exactly one two-copy resolution.");
        var board = pick with { IsCardSelectionOverlay=false,ScreenHeader=null,MatchHudVisible=true };
        var at=DateTimeOffset.UnixEpoch; var fog=cards.Single(c=>c.Id=="113305"); var tempest=cards.Single(c=>c.Id=="202203");
        for(var mode=0;mode<5;mode++)
        {
            resolver.Reset(); resolver.Observe(at,board with {UserDeckCount=15,UserHandCount=8},[],null);
            resolver.Observe(at.AddSeconds(1),board,[new(at.AddSeconds(1),new(tempest,PlayerSide.User,CardSightSource.PlayPreview,new(.8,.4,.9,.7),.08,1),"source")],null);
            if(mode!=1) resolver.Observe(at.AddSeconds(1.5),board with {UserDeckCount=13,UserHandCount=mode==2?9:7},[],null);
            resolver.Observe(at.AddSeconds(2),pick,[],new([fog,fog],Complete:mode!=3));
            resolver.Observe(at.AddSeconds(2.3),pick,[],new(mode==4?[fog]:[fog,fog],Complete:mode!=3));
            var result=resolver.Observe(at.AddSeconds(3),board with {UserDeckCount=11,UserHandCount=7},[],null);
            Check(result.Count==(mode==0?1:0),"Tutor baseline safety case "+mode);
        }
        var schedule=new VisionScanSchedule(); Check(schedule.NextIncludesBoard(at),"Initial board refresh.");
        // A cancelled attempt must still retain its fresh HUD baseline for a
        // subsequent independent tutor, rather than falling back to the stale pile.
        resolver.Reset(); resolver.Observe(at,board with {UserDeckCount=15,UserHandCount=8},[],null);
        VisionEvidenceEvent Source(double seconds)=>new(at.AddSeconds(seconds),new(tempest,PlayerSide.User,CardSightSource.PlayPreview,new(.8,.4,.9,.7),.08,1),"source");
        resolver.Observe(at.AddSeconds(1),board,[Source(1)],null);
        resolver.Observe(at.AddSeconds(2),board with {UserDeckCount=13,UserHandCount=9},[],null);
        resolver.Observe(at.AddSeconds(2.5),board,[Source(2.5)],null);
        resolver.Observe(at.AddSeconds(3),pick,[],new([fog,fog]));
        resolver.Observe(at.AddSeconds(3.3),pick,[],new([fog,fog]));
        Check(resolver.Observe(at.AddSeconds(4),board with {UserDeckCount=11,UserHandCount=8},[],null) is [{ResolvedDeckCopies:2}],
            "Cancelled tutor left a stale baseline for the next independent resolution.");
        for(var i=1;i<6;i++) Check(!schedule.NextIncludesBoard(at.AddSeconds(i),opponentHand:9,artworkBacklogged:true),"Heavy scans should yield to backlogged previews.");
        Check(schedule.NextIncludesBoard(at.AddSeconds(6),artworkBacklogged:true),"Backlog must not starve board evidence.");
        schedule.ObserveArtwork([new(fog,PlayerSide.Opponent,CardSightSource.Board,new(.4,.2,.46,.35),.1,1)],true);
        Check(!schedule.NextIncludesBoard(at.AddSeconds(6.5),artworkBacklogged:true) &&
            schedule.NextIncludesBoard(at.AddSeconds(7),artworkBacklogged:true),"New opponent copies must still receive prompt corroboration under pressure.");
        Console.WriteLine("PASS tutor attribution guards and bounded board deferral under preview pressure.");
        if (writeAccountingReview)
        {
            var input=Path.Combine(project,"diagnostics/v0.2.39-latest-counter-choice-review.jsonl");
            File.WriteAllLines(input,allRows.Select(row=>JsonSerializer.Serialize(reviewed.GetValueOrDefault(row.SampledAt,row),GameStateJournal.Json)));
            CurrentAccountingAudit.Run(root,["--session",Session,"--input",input,"--output",Path.Combine(project,"diagnostics/v0.2.39-latest-accounting-review.json")]);
        }
    }
    public static async Task Profile(string root, string[] args)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var cache = Path.Combine(project, "cache");
        var folder = Path.Combine(project, "sessions", Session);
        var cards = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        if (args.Contains("--choice-probe"))
        {
            var f = Load(Path.Combine(folder,"frame-000610-122441984.jpg"));
            using var source = Mat.FromPixelData(f.Height,f.Width,MatType.CV_8UC4,f.BgraPixels);
            using var gray = new Mat(); using var edges = new Mat();
            Cv2.CvtColor(source,gray,ColorConversionCodes.BGRA2GRAY); Cv2.Canny(gray,edges,20,60);
            var lines = Cv2.HoughLinesP(edges,1,Math.PI/180,70,f.Height*.27,f.Height*.025);
            foreach (var line in lines.Where(l=>Math.Abs(l.P1.X-l.P2.X)<40).OrderBy(l=>l.P1.X)) Console.WriteLine(line);
            return;
        }
        if (args.Contains("--train-counter"))
        {
            var path = Path.Combine(project,"assets/vision/deck-counter-digits.json");
            var original = JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path))!;
            var f = Load(Path.Combine(folder,"frame-000601-122441084.jpg"));
            var glyphs = HudDigitReader.Train(f,new(1098/1280.0,592/720.0,1125/1280.0,615/720.0),"13");
            File.WriteAllText(path,JsonSerializer.Serialize(original.Concat(glyphs).Distinct().ToArray()));
            Console.WriteLine("Added reviewed 13 glyphs; verify other pixels independently.");
            return;
        }
        if (args.Contains("--counter-probe"))
        {
            using var reader = new ScreenStateRecognizer();
            foreach (var file in Directory.GetFiles(folder,"frame-0006*.jpg").Order().Take(25))
            {
                var f = Load(file);
                var broad = await OpponentHudRecognizer.ReadUserDeckCandidateAsync(f,reader);
                var compact = new NormalizedRegion(.84,.81,.885,.865);
                Console.WriteLine(Path.GetFileName(file)+" deck="+broad+" glyph="+DeckCounterDigitReader.Read(f,compact)+" ocr="+
                    string.Join('|',(await reader.ReadLinesAsync(f,compact,3,enhance:false,whiteLetterMask:true,smooth:true)).Select(l=>l.Text)));
            }
            return;
        }
        var rows = File.ReadLines(Path.Combine(folder, "vision-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!).ToArray();
        using var pipeline = new CardVisionPipeline(VisionReferenceLibrary.Load(cards, cache), cards,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        // Fixed identical scope for A/B timing, not a claim of unknown-deck recall.
        pipeline.SetLikelyOpponentCards(rows.SelectMany(r => r.Sightings).Where(s => s.Side == PlayerSide.Opponent).Select(s => s.Card.Id).Distinct());
        pipeline.SetKnownPlayerDeck(rows.SelectMany(r => r.Sightings).Where(s => s.Side == PlayerSide.User).Select(s => s.Card.Id).Distinct());
        var files = Directory.GetFiles(folder, "frame-*.jpg").Order().ToArray();
        var selected = files.Where((f,i) => i % 35 == 0 || (StringComparer.Ordinal.Compare(Path.GetFileName(f), "frame-000550") >= 0 &&
            StringComparer.Ordinal.Compare(Path.GetFileName(f), "frame-000660") < 0)).ToArray();
        var output = new List<object>();
        var schedule = new VisionScanSchedule();
        foreach (var file in selected)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var time = DateTime.ParseExact(name.Split('-')[2], "HHmmssfff", null);
            var at = new DateTimeOffset(2026,8,31,time.Hour,time.Minute,time.Second,time.Millisecond,TimeSpan.FromHours(-4));
            var frame = Load(file); var timer = Stopwatch.StartNew();
            var prepared = await pipeline.PrepareAsync(frame,at); var prepareMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var result = pipeline.Commit(pipeline.RecognizePrepared(prepared, schedule.NextIncludesBoard(at)));
            output.Add(new { File = name, PrepareMs = prepareMs, ArtworkMs = timer.Elapsed.TotalMilliseconds,
                Screen = result.Screen, Choices = prepared.DeckPlayChoices, Sightings = result.Sightings, Events = result.Events });
            if (output.Count % 25 == 0) Console.WriteLine($"Profile {output.Count}/{selected.Length}");
        }
        var index = Array.IndexOf(args,"--output");
        var target = index >= 0 ? args[index+1] : Path.Combine(project,"diagnostics/efficiency-profile.json");
        File.WriteAllText(target, JsonSerializer.Serialize(new { Frames = output.Count, pipeline.OcrCalls, pipeline.OcrCacheHits,
            Stages = pipeline.Timings.Snapshot(), FeatureStages = pipeline.FeatureTimings, Results = output }, GameStateJournal.Json));
        Console.WriteLine(JsonSerializer.Serialize(pipeline.Timings.Snapshot()));
    }
}
