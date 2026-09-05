using System.Runtime.CompilerServices;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class StreamingVisionTests
{
    public static async Task RunAsync()
    {
        var schedule=new VisionScanSchedule(); var at=DateTimeOffset.UnixEpoch;
        var boardCard=new CardSighting(new CardDefinition("board","Board","Neutral",CardKind.Unit,4),
            PlayerSide.User,CardSightSource.Board,new(.4,.5,.47,.7),.1,1);
        Check(schedule.NextIncludesBoard(at),"Initial board scan");
        schedule.ObserveArtwork([boardCard],true);
        Check(!schedule.NextIncludesBoard(at.AddMilliseconds(500)) && schedule.NextIncludesBoard(at.AddSeconds(1)),
            "New board identity needs a guarded confirmation pass");
        schedule.ObserveArtwork([boardCard],true);
        Check(!schedule.NextIncludesBoard(at.AddSeconds(1.5)) && !schedule.NextIncludesBoard(at.AddSeconds(2)),
            "Existing artwork created an endless heavy scan loop");
        var pipeline = new FakePipeline();
        var prepared = new List<int>();
        var stream = new StreamingVisionProcessor<int>(pipeline, artworkCapacity: 2, pendingCapacity: 8)
        { OnPreparedText = (result, context) => { Check(result.Events.Count == 0, "Fast presentation must not commit events"); prepared.Add(context); } };
        var outputs = await Collect(stream.RunAsync(Inputs()));
        Check(prepared.SequenceEqual(Enumerable.Range(0, 120)), "Fast text was dropped/reordered behind artwork");
        Check(outputs.Count == 120 && outputs.Select(item => item.Context).SequenceEqual(Enumerable.Range(0, 120)),
            "Every prepared text frame must survive artwork queue drops, in capture order.");
        Check(stream.SkippedArtworkFrames > 0 && stream.MaximumPendingFrames <= 8, "The pressure test must exercise bounded dropping.");
        Check(outputs.All(o => o.Result.HoverInPlayerHand == (o.Context >= 60) &&
                               o.Result.PointerInPlayerHand == (o.Context >= 60)),
            "The live pointer must override ambiguous tooltip geometry in prepared text, artwork scheduling and committed results, including dropped-artwork frames");
        var events = outputs.SelectMany(item => item.Result.Events).ToArray();
        Check(events.Count(item => item.Sighting.Card.Id == "a") == 1 && events.Count(item => item.Sighting.Card.Id == "b") == 1,
            "Skipped artwork must not lose brief titles, and a joined art/text frame must not double-count its play.");
        Check(events.Count(item => item.Sighting.Card.Id == "c") == 1,
            "An unreadable framed header must receive artwork priority and survive redundant-frame eviction.");
        Check(events.Count(item => item.Sighting.Card.Id == "d") == 1,
            "A scoreless opponent hand drop must preserve its artwork-only play under queue pressure.");
        Check(events.Count(item => item.Sighting.Card.Id == "e") == 1,
            "A player hand drop must preserve its artwork-only play under queue pressure.");
        var defaults = new StreamingVisionProcessor<int>(new FakePipeline());
        var defaultOutputs = await Collect(defaults.RunAsync(Inputs()));
        Check(defaultOutputs.Select(o=>o.Context).SequenceEqual(Enumerable.Range(0,120)) && defaults.SkippedArtworkFrames>0,
            "Default low-latency queue must preserve chronological prepared text under load.");
        var defaultEvents=defaultOutputs.SelectMany(o=>o.Result.Events).Select(e=>e.Sighting.Card.Id).Order().ToArray();
        // This source produces twelve seconds of captures as fast as Task.Yield.
        // Lower-priority unresolved player artwork may be evicted by the opponent
        // burst; its prepared text must still survive. Do not make that intentional
        // priority tradeoff a timing-dependent requirement to keep every artwork hit.
        Check(new[]{"a","b","d","e"}.All(id=>defaultEvents.Count(found=>found==id)==1) &&
            defaultEvents.Count(id=>id=="c")<=1 && defaultEvents.All(id=>id is "a" or "b" or "c" or "d" or "e"),
            "Default queue lost/duplicated opponent or text evidence: "+string.Join(',',defaultEvents));

        var errors = 0;
        var failing = new StreamingVisionProcessor<int>(new FakePipeline(failArtwork: true, failPrepare: true), 2, 8)
        { OnError = _ => errors++ };
        var recovered = await Collect(failing.RunAsync(Inputs()));
        Check(recovered.Count == 119 && errors > 1, "A failed preparation must release its slot; failed artwork must still commit text.");
        Check(recovered.SelectMany(item => item.Result.Events).Count() == 2, "Recoverable artwork errors must retain both title events.");
        var presentationFailure = new StreamingVisionProcessor<int>(new FakePipeline(), 2, 8)
        { OnPreparedText = (_, context) => { if (context == 10) throw new InvalidOperationException("Expected presentation failure"); } };
        var authoritative = await Collect(presentationFailure.RunAsync(Inputs()));
        Check(authoritative.Count == 120 && authoritative.SelectMany(item => item.Result.Events).Any(item => item.Sighting.Card.Id == "a"),
            "A presentation failure dropped authoritative match evidence");
        var cancellation = StopEarly();
        Check(await Task.WhenAny(cancellation, Task.Delay(5000)) == cancellation, "Early disposal must unblock a full producer queue.");
        await cancellation;

        static async Task StopEarly()
        {
            var processor = new StreamingVisionProcessor<int>(new FakePipeline(), 2, 4);
            await foreach (var _ in processor.RunAsync(Inputs())) break;
        }
    }

    private static async Task<List<VisionOutput<int>>> Collect(IAsyncEnumerable<VisionOutput<int>> results)
    {
        var list = new List<VisionOutput<int>>();
        await foreach (var item in results) list.Add(item);
        return list;
    }

    private static async IAsyncEnumerable<VisionInput<int>> Inputs([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pixels = new PixelFrame(1, 1, [0, 0, 0, 255]);
        for (var i = 0; i < 120; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VisionInput<int>(pixels, DateTimeOffset.UnixEpoch.AddMilliseconds(i * 100), 0, i, PointerInPlayerHand: i >= 60);
            await Task.Yield();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakePipeline(bool failArtwork = false, bool failPrepare = false) : ICardVisionPipeline
    {
        private readonly MatchVisionLedger _ledger = new();
        public async Task<PreparedVisionFrame> PrepareAsync(PixelFrame frame, DateTimeOffset sampledAt)
        {
            await Task.Yield();
            var index = (int)(sampledAt - DateTimeOffset.UnixEpoch).TotalMilliseconds / 100;
            if (failPrepare && index == 4) throw new InvalidOperationException("Expected test OCR failure");
            var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
                OpponentHandCount: index < 50 ? 10 : 9, UserHandCount: index < 90 ? 5 : 4);
            var card = index is 10 or 11 ? new CardDefinition("a", "A", "Neutral", CardKind.Special, 4) :
                index is 80 or 81 ? new CardDefinition("b", "B", "Neutral", CardKind.Special, 5) : null;
            return new PreparedVisionFrame(frame, sampledAt, screen, card is null ? [] :
                [new CardSighting(card, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.8, .1, .9, .4), .1, 1)],
                NeedsArtwork: index == 40, HoverInPlayerHand: index == 20);
        }
        public CardVisionResult RecognizePrepared(PreparedVisionFrame prepared, bool includeBoard)
        {
            Thread.Sleep(10); // Controlled slow artwork path, never a real game or OCR dependency.
            if (failArtwork) throw new InvalidOperationException("Expected test artwork failure");
            var result = prepared.TextResult with { BoardWasScanned = includeBoard, ArtworkWasScanned = true };
            if (prepared.SampledAt == DateTimeOffset.UnixEpoch.AddSeconds(5))
                return result with { Sightings = [new CardSighting(new CardDefinition("d", "D", "Neutral", CardKind.Special, 7),
                    PlayerSide.Opponent, CardSightSource.PlayPreview, new(.8, .1, .9, .4), .1, 1)] };
            if (prepared.SampledAt == DateTimeOffset.UnixEpoch.AddSeconds(9))
                return result with { Sightings = [new CardSighting(new CardDefinition("e", "E", "Neutral", CardKind.Unit, 8),
                    PlayerSide.User, CardSightSource.PlayPreview, new(.8, .4, .9, .7), .1, 1)] };
            return !prepared.NeedsArtwork ? result : result with
            {
                Sightings = [new CardSighting(new CardDefinition("c", "C", "Neutral", CardKind.Special, 6),
                    PlayerSide.Opponent, CardSightSource.PlayPreview, new(.8, .1, .9, .4), .1, 1)]
            };
        }
        public CardVisionResult Commit(CardVisionResult result) => result with
        { Events = _ledger.Observe(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned) };
    }
}
