using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SeparatedThinningPairRegressionCase : IRecordingValidationCase
{
    public string Id => "separated-thinning-pair";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var rider = catalog.Single(card => card.Id == "132310");
        var portrait = VisionEfficiencyTests.Load(Path.Combine(project, "cache", "portraits", rider.Id + ".jpg"));
        var recognizer = new CardFrameRecognizer(new CardArtMatcher([new(rider, VisualDescriptor.Create(portrait))]));
        var at = DateTimeOffset.UnixEpoch;
        var preview = new CardSighting(rider, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), .10, 1, "Exact visible preview title");
        var play = new VisionEvidenceEvent(at, preview, "Exact visible preview title");
        recognizer.ObserveEvents([play]);
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null) { MatchHudVisible = true };

        CardSighting[] Pair(PixelFrame frame, IReadOnlyList<CardSighting>? accepted = null) => recognizer.Recognize(frame, board, true, accepted ?? [])
            .Where(item => item.Source == CardSightSource.Board && item.Side == PlayerSide.Opponent && item.Card.Id == rider.Id)
            .OrderBy(item => item.Region.Left).ToArray();
        var firstFrame = definition.Load("evidence-01.png");
        var first = Pair(firstFrame);
        var second = Pair(definition.Load("evidence-02.png"));
        if (first.Length != 2 || second.Length != 2 || first[1].Region.Left - first[0].Region.Left < .10)
            throw new InvalidOperationException($"The Rider copies separated by one existing row card were not retained ({first.Length}/{second.Length}).");

        // Derive a wider-gap layout from the anonymized real frame: move the
        // played copy left and cover its former pixels with the intervening card.
        // This prevents the regression from silently reintroducing any fixed
        // adjacency/gap limit when a row contains several cards.
        var width = first[0].Region.Right - first[0].Region.Left;
        var middleCenter = (first[0].Region.Right + first[1].Region.Left) / 2;
        var filler = first[0].Region with { Left = middleCenter - width / 2, Right = middleCenter + width / 2 };
        var farLeft = first[0].Region with { Left = .245, Right = .245 + width };
        var spreadFrame = MoveRegion(firstFrame, first[0].Region, filler, farLeft);
        var spread = Pair(spreadFrame);
        if (spread.Length != 2 || spread[1].Region.Left - spread[0].Region.Left < .25)
            throw new InvalidOperationException($"A far-right Rider summon with several intervening card widths was not retained ({spread.Length}).");

        // Far-right placement is the spatial safeguard replacing adjacency: a
        // known same-row body beyond the matching copy must reject this pairing.
        var farRight = spread[1].Region;
        var blockerRegion = farRight with { Left = farRight.Right + .005, Right = farRight.Right + .005 + width };
        var blocker = new CardSighting(catalog.First(card => card.Id != rider.Id), PlayerSide.Opponent,
            CardSightSource.Board, blockerRegion, .08, 1);
        if (Pair(spreadFrame, [blocker]).Length == 2)
            throw new InvalidOperationException("A matching copy that was not the far-right known row body was accepted as the summon.");

        var deck = new LiveDeckTracker(PlayerSide.Opponent);
        deck.ConsiderDirectPlay(rider, .99, at, play.Description, CardProvenance.ProbableStartingDeck);
        var copies = new ThinningCopyTracker();
        copies.ObserveEvent(play, CardProvenance.ProbableStartingDeck, deck);
        copies.ObserveFrame(at.AddSeconds(4), board, first, true, [deck], _ => false);
        copies.ObserveFrame(at.AddSeconds(7), board, second, true, [deck], _ => false);
        if (deck.DeckBuildingObservations.Single(item => item.Card.Id == rider.Id).ObservedCopies != 2)
            throw new InvalidOperationException("Repeated separated Rider bodies did not promote the original-copy floor to two.");
        return Task.CompletedTask;
    }

    private static PixelFrame MoveRegion(PixelFrame frame, NormalizedRegion source, NormalizedRegion filler, NormalizedRegion destination)
    {
        var pixels = (byte[])frame.BgraPixels.Clone();
        Copy(frame.BgraPixels, pixels, frame.Width, frame.Height, filler, source);
        Copy(frame.BgraPixels, pixels, frame.Width, frame.Height, source, destination);
        return new PixelFrame(frame.Width, frame.Height, pixels);
    }

    private static void Copy(byte[] sourcePixels, byte[] destinationPixels, int width, int height,
        NormalizedRegion source, NormalizedRegion destination)
    {
        var sourceLeft = source.PixelLeft(width); var sourceTop = source.PixelTop(height);
        var sourceRight = source.PixelRight(width); var sourceBottom = source.PixelBottom(height);
        var destinationLeft = destination.PixelLeft(width); var destinationTop = destination.PixelTop(height);
        var destinationRight = destination.PixelRight(width); var destinationBottom = destination.PixelBottom(height);
        for (var y = destinationTop; y < destinationBottom; y++)
        for (var x = destinationLeft; x < destinationRight; x++)
        {
            var sourceX = sourceLeft + (x - destinationLeft) * (sourceRight - sourceLeft) / Math.Max(1, destinationRight - destinationLeft);
            var sourceY = sourceTop + (y - destinationTop) * (sourceBottom - sourceTop) / Math.Max(1, destinationBottom - destinationTop);
            Buffer.BlockCopy(sourcePixels, (sourceY * width + sourceX) * 4,
                destinationPixels, (y * width + x) * 4, 4);
        }
    }
}
