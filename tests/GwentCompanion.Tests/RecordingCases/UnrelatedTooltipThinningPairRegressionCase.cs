using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class UnrelatedTooltipThinningPairRegressionCase : IRecordingValidationCase
{
    public string Id => "unrelated-tooltip-thinning-pair";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var rider = catalog.Single(card => card.Id == "132310");
        var portrait = VisionEfficiencyTests.Load(Path.Combine(project, "cache", "portraits", rider.Id + ".jpg"));
        var matcher = new CardArtMatcher([new(rider, VisualDescriptor.Create(portrait))]);
        var at = DateTimeOffset.UnixEpoch;
        var play = new CardSighting(rider, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), .10, 1, "Exact visible preview title");
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };

        var before = new CardFrameRecognizer(matcher);
        if (before.Recognize(definition.Load("evidence-01.jpg"), board, true, []).Any(sighting => sighting.Card.Id == rider.Id))
            throw new InvalidOperationException("Rider pair was fabricated before the initiating play.");

        var recognizer = new CardFrameRecognizer(matcher);
        recognizer.ObserveEvents([new(at, play, "Exact visible preview title")]);
        var previewScreen = board with { HasCardTooltip = true, TooltipRegion = new(.67, .21, .83, .36) };
        if (recognizer.Recognize(definition.Load("evidence-02.jpg"), previewScreen, true, []).Any(sighting =>
                sighting.Source == CardSightSource.Board && sighting.Card.Id == rider.Id))
            throw new InvalidOperationException("The enlarged Rider preview was mistaken for a settled pair.");

        var unrelatedPlayerTooltip = board with { HasCardTooltip = true, TooltipRegion = new(.70, .69, .99, .98) };
        var pair = recognizer.Recognize(definition.Load("evidence-03.jpg"), unrelatedPlayerTooltip, true, [])
            .Where(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == rider.Id).ToArray();
        if (pair.Length != 2)
            throw new InvalidOperationException($"Unrelated player tooltip suppressed the two settled Rider bodies ({pair.Length} found).");

        var covered = board with { HasCardTooltip = true, TooltipRegion = new(.43, .08, .82, .46) };
        if (recognizer.Recognize(definition.Load("evidence-03.jpg"), covered, true, []).Any(sighting =>
                sighting.Source == CardSightSource.Board && sighting.Card.Id == rider.Id))
            throw new InvalidOperationException("A tooltip covering the opponent row did not suppress overlapped pair candidates.");
        return Task.CompletedTask;
    }
}
