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

// Reuses the production artwork pipeline and regenerates events; old artwork/events
// must never leak into the new measurements. Saved HUD/text are explicitly identified.
internal static class CurrentPixelReplay
{
    public static async Task Run(string root, string[] args)
    {
        string Option(string key, string fallback) { var i=Array.IndexOf(args,key); return i<0?fallback:args[i+1]; }
        var session=Option("--session","20260828-181243");
        var cache=Path.Combine(root,"GwentCompanion/cache");
        var folder=Path.Combine(root,"GwentCompanion/sessions",session);
        var output=Path.Combine(root,"GwentCompanion/diagnostics",Option("--output","v0.1.32-"+session+"-pixels.jsonl"));
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var saved=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder,"game-state-final.json")),GameStateJournal.Json)!;
        var reference=saved.User.StartingDeckReference;
        var records=File.ReadLines(Path.Combine(folder,"vision-observations.jsonl")).Select(s=>JsonSerializer.Deserialize<CardVisionResult>(s,GameStateJournal.Json)!).ToArray();
        static int StampMilliseconds(string stamp) =>
            int.Parse(stamp[..2])*3_600_000+int.Parse(stamp[2..4])*60_000+int.Parse(stamp[4..6])*1_000+int.Parse(stamp[6..]);
        var files=Directory.GetFiles(folder,"frame-*.jpg").Select(path=>(Path:path,
            Milliseconds:StampMilliseconds(Path.GetFileNameWithoutExtension(path).Split('-')[2]))).OrderBy(item=>item.Milliseconds).ToArray();
        string? NearestPath(DateTimeOffset at)
        {
            var milliseconds=(int)at.TimeOfDay.TotalMilliseconds;
            var nearest=files.MinBy(item=>Math.Abs(item.Milliseconds-milliseconds));
            return Math.Abs(nearest.Milliseconds-milliseconds)<=180 ? nearest.Path : null;
        }
        var trainingFrames=Directory.GetFiles(Path.Combine(cache,"observed-art"),"*.json",SearchOption.AllDirectories)
            .Select(p=>JsonDocument.Parse(File.ReadAllText(p))).Select(doc=> { using(doc) return doc.RootElement.TryGetProperty("Source",out var source)?source.GetString():null; })
            .Where(s=>s?.StartsWith(session+"/",StringComparison.Ordinal)==true || s?.StartsWith(session+"\\",StringComparison.Ordinal)==true)
            .Select(s=>Path.GetFileName(s!.Replace('/',Path.DirectorySeparatorChar))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reschedule=args.Contains("--reschedule"); var schedule=new VisionScanSchedule();
        int? userScore=null,otherScore=null,handCount=null,opponentHandCount=null;
        DateTimeOffset? thinningPriorityUntil=null;
        Console.WriteLine($"{session}: {records.Length} records; {records.Count(r=>r.ArtworkWasScanned)} artwork passes; {records.Count(r=>r.BoardWasScanned)} board passes. Loading production recognizer.");
        var opponentFaction=saved.Opponent.Faction?.Value;
        var library=DeckLibrary.Load(Path.Combine(cache,"deck-library.json")).Decks;
        var factionDecks=library.Where(deck=>opponentFaction is not null &&
            deck.Faction.Equals(opponentFaction,StringComparison.OrdinalIgnoreCase)).ToArray();
        var likelyOpponentIds=factionDecks.SelectMany(deck=>deck.Cards.Select(card=>(card.Card.Id,
                Weight:card.Count*Math.Max(1,deck.Occurrences?.Count??1))))
            .GroupBy(item=>item.Id,StringComparer.Ordinal).OrderByDescending(group=>group.Sum(item=>item.Weight))
            .ThenBy(group=>group.Key,StringComparer.Ordinal).Take(70).Select(group=>group.Key).ToArray();
        var allReferences=VisionReferenceLibrary.Load(catalog,cache);
        Console.WriteLine($"Live-sized audit scope: known player deck + {likelyOpponentIds.Length} prevalent {opponentFaction??"unknown-faction"} candidates; {allReferences.Count} images remain lazy.");
        using var pipeline=new CardVisionPipeline(allReferences,catalog,Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck(reference?.Cards.Select(c=>c.Card.Id)??[]);
        pipeline.SetLikelyOpponentCards(likelyOpponentIds);
        using var ocr=new ScreenStateRecognizer(); var titles=new PreviewTitleRecognizer(catalog);
        using var writer=new StreamWriter(output,false);
        var processed=0; var artworks=0; var timer=System.Diagnostics.Stopwatch.StartNew();
        foreach(var old in records)
        {
            var screen=old.Screen;
            var hudChanged=screen.UserHandCount is {} h && handCount is {} lastH && h<lastH ||
                screen.UserScore is {} u && userScore is {} lastU && u!=lastU ||
                screen.OpponentScore is {} o && otherScore is {} lastO && o!=lastO;
            var opponentPlayed=screen.OpponentHandCount is {} enemy && opponentHandCount is {} lastEnemy && enemy<lastEnemy;
            userScore=screen.UserScore??userScore; otherScore=screen.OpponentScore??otherScore; handCount=screen.UserHandCount??handCount;
            opponentHandCount=screen.OpponentHandCount??opponentHandCount;
            var retainedTitles=old.Sightings.Where(item=>item.Source==CardSightSource.PlayPreview &&
                (item.Evidence?.Contains("visible preview title",StringComparison.OrdinalIgnoreCase)??false)).ToArray();
            if (retainedTitles.Any(item=>CompanionCardRules.ThinningPairs.Contains(item.Card.Id)))
                thinningPriorityUntil=old.SampledAt.AddSeconds(7);
            schedule.ObservePreview(old.SampledAt,retainedTitles);
            var thinningPriority=thinningPriorityUntil is { } until && old.SampledAt<=until;
            CardVisionResult fresh;
            var path=NearestPath(old.SampledAt);
            if((old.ArtworkWasScanned || reschedule && (hudChanged || opponentPlayed || thinningPriority)) && path is not null &&
                !trainingFrames.Contains(Path.GetFileName(path)))
            {
                using var stream=File.OpenRead(path);
                var frame=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
                var names=await titles.RecognizeAsync(frame,old.Screen,ocr);
                schedule.ObservePreview(old.SampledAt,names);
                var refresh=schedule.NextIncludesBoard(old.SampledAt,userScore,otherScore,handCount,
                    old.HoverInPlayerHand && old.HoveredCard is not null,opponentHandCount);
                fresh=pipeline.RecognizePrepared(new PreparedVisionFrame(frame,old.SampledAt,old.Screen,names,titles.HasUnresolvedHeader,
                    old.Description,old.GraveyardInspection,old.DevotionCue,old.HoveredCard,old.RuntimeValue,old.HoverInPlayerHand)
                    { PointerInPlayerHand=old.PointerInPlayerHand },old.BoardWasScanned || reschedule && refresh);
                schedule.ObserveArtwork(fresh.Sightings,fresh.BoardWasScanned);
                artworks++;
            }
            else
            {
                // These passes originally ran only OCR. Do not retain any historical art guess.
                fresh=old with {BoardWasScanned=false,ArtworkWasScanned=false,Sightings=old.Sightings.Where(s=>s.Source==CardSightSource.PlayPreview &&
                    (s.Evidence?.Contains("visible preview title",StringComparison.OrdinalIgnoreCase)??false)).ToArray(),Events=[],CardMeasurements=null};
            }
            fresh=pipeline.Commit(fresh);
            writer.WriteLine(JsonSerializer.Serialize(fresh,GameStateJournal.Json));
            if(++processed%100==0) { writer.Flush(); Console.WriteLine($"{processed}/{records.Length}, art={artworks}, elapsed={timer.Elapsed.TotalSeconds:F0}s"); }
        }
        Console.WriteLine($"Saved {output}. Scope: scheduled artwork re-read (excluding {trainingFrames.Count} exact training frames); production event reconciliation; saved HUD/hover/inspection and text-only ticks retained. Extra HUD-priority candidate frames={reschedule}; not a live queue performance or complete OCR replay.");
    }
}
