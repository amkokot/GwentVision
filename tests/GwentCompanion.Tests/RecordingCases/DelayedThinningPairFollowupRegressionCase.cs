using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class DelayedThinningPairFollowupRegressionCase : IRecordingValidationCase
{
    public string Id => "delayed-thinning-pair-followup";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var rider = catalog.Single(card => card.Id == "132310");
        var portrait = VisionEfficiencyTests.Load(Path.Combine(project, "cache", "portraits", rider.Id + ".jpg"));
        var matcher = new CardArtMatcher([new(rider, VisualDescriptor.Create(portrait))]);
        var recognizer = new CardFrameRecognizer(matcher);
        var at = DateTimeOffset.UnixEpoch;
        var preview = new CardSighting(rider, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), .10, 1, "Exact visible preview title");
        var play = new VisionEvidenceEvent(at, preview, "Exact visible preview title");
        recognizer.ObserveEvents([play]);

        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        var firstScreen = board with { HasCardTooltip = true, TooltipRegion = new(.70, .69, .99, .98) };
        var first = recognizer.Recognize(definition.Load("evidence-02.jpg"), firstScreen, true, [])
            .Where(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == rider.Id).ToArray();
        recognizer.Expire(at.AddSeconds(12.2));
        var second = recognizer.Recognize(definition.Load("evidence-03.jpg"), board, true, [])
            .Where(sighting => sighting.Source == CardSightSource.Board && sighting.Card.Id == rider.Id).ToArray();
        if (first.Length != 2 || second.Length != 2)
            throw new InvalidOperationException($"Late pair sequence was not retained across two settled scans ({first.Length}/{second.Length}).");

        var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        tracker.ConsiderDirectPlay(rider, .99, at, play.Description, CardProvenance.ProbableStartingDeck);
        var copies = new ThinningCopyTracker();
        copies.ObserveEvent(play, CardProvenance.ProbableStartingDeck, tracker);
        copies.ObserveFrame(at.AddSeconds(6), firstScreen, first, true, [tracker], _ => false);
        copies.ObserveFrame(at.AddSeconds(12.2), board, second, true, [tracker], _ => false);
        if (tracker.DeckBuildingObservations.Single(item => item.Card.Id == rider.Id).ObservedCopies != 2)
            throw new InvalidOperationException("Two late settled Rider frames did not promote the physical original-copy floor to two.");

        var schedule = new VisionScanSchedule();
        schedule.ObservePreview(at, [preview]);
        foreach (var secondAt in new[] { 1d, 2.2, 6d, 12.2 })
            if (!schedule.NextIncludesBoard(at.AddSeconds(secondAt)))
                throw new InvalidOperationException($"Bounded thinning follow-up was lost at +{secondAt:F1}s.");
        if (schedule.NextIncludesBoard(at.AddSeconds(13.5)))
            throw new InvalidOperationException("Thinning follow-up exceeded its fixed four-pass budget.");
        return Task.CompletedTask;
    }
}
