using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SewerRaidersThinningFollowupRegressionCase : IRecordingValidationCase
{
    public string Id => "sewer-raiders-thinning-followup";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var sewer = catalog.Single(card => card.Id == "202334");
        var portrait = VisionEfficiencyTests.Load(Path.Combine(project, "cache", "portraits", sewer.Id + ".jpg"));
        var artMatcher = new CardArtMatcher([new(sewer, VisualDescriptor.Create(portrait))]);
        var recognizer = new CardFrameRecognizer(artMatcher);
        var at = DateTimeOffset.UnixEpoch;
        var preview = new CardSighting(sewer, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), .08, 1, "Exact visible preview title");
        var play = new VisionEvidenceEvent(at, preview, "Exact visible preview title");
        recognizer.ObserveEvents([play]);
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 1, OpponentDeckCount = 7, OpponentCoins = 5 };

        if (recognizer.Recognize(definition.Load("evidence-01.png"), board, true, [])
            .Any(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == sewer.Id))
            throw new InvalidOperationException("The enlarged Sewer Raiders play was mistaken for its summoned copy.");
        var first = recognizer.Recognize(definition.Load("evidence-03.png"), board, true, [])
            .Where(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == sewer.Id).ToArray();
        var second = recognizer.Recognize(definition.Load("evidence-04.png"), board, true, [])
            .Where(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == sewer.Id).ToArray();
        if (first.Length != 2 || second.Length != 2)
            throw new InvalidOperationException($"Settled Sewer Raiders pair was not recovered in both clean frames ({first.Length}/{second.Length}).");

        foreach (var coins in new int?[] { 3, null })
        {
            var guarded = new CardFrameRecognizer(artMatcher);
            guarded.ObserveEvents([play]);
            var sightings = guarded.Recognize(definition.Load("evidence-04.png"),
                board with { OpponentCoins = coins }, true, []);
            if (sightings.Count(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == sewer.Id) > 1)
                throw new InvalidOperationException(
                    $"The premium-pair fallback fired without a confirmed Hoard requirement (coins: {coins?.ToString() ?? "unknown"}).");
        }

        var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        tracker.ConsiderDirectPlay(sewer, .99, at, play.Description, CardProvenance.ProbableStartingDeck);
        var copies = new ThinningCopyTracker();
        copies.ObserveEvent(play, CardProvenance.ProbableStartingDeck, tracker);
        copies.ObserveFrame(at.AddSeconds(2.9), board, first, true, [tracker], _ => false);
        copies.ObserveFrame(at.AddSeconds(3.1), board, second, true, [tracker], _ => false);
        if (tracker.DeckBuildingObservations.Single(item => item.Card.Id == sewer.Id).ObservedCopies != 2)
            throw new InvalidOperationException("Repeated physical Sewer Raiders bodies did not promote the original-copy floor to two.");

        var until = at.AddSeconds(15);
        var previewPriority = StreamingVisionProcessor<object>.ThinningFollowupPriority(at.AddMilliseconds(300), at, until);
        var settledPriority = StreamingVisionProcessor<object>.ThinningFollowupPriority(at.AddSeconds(3), at, until);
        var latePriority = StreamingVisionProcessor<object>.ThinningFollowupPriority(at.AddSeconds(10), at, until);
        if (!(settledPriority > latePriority && latePriority > 0 && settledPriority > previewPriority) ||
            StreamingVisionProcessor<object>.ThinningFollowupPriority(at.AddSeconds(16), at, until) != 0)
            throw new InvalidOperationException("Live retention priority does not favor the clean settling band within a bounded window.");
        return Task.CompletedTask;
    }
}
