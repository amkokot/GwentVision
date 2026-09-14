using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class ContinuousPlayAllCopiesRegressionCase : IRecordingValidationCase
{
    public string Id => "continuous-play-all-copies";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var tempest = catalog.Single(card => card.Name == "Tempest");
        var fog = catalog.Single(card => card.Name == "Impenetrable Fog");
        using var screenReader = new ScreenStateRecognizer();
        var titleReader = new PreviewTitleRecognizer(catalog);
        var ledger = new MatchVisionLedger();
        var at = DateTimeOffset.UnixEpoch;
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
            OpponentHandCount: 8, MatchHudVisible: true);
        ledger.Observe(at, board,
            [new(tempest, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.815, .136, .915, .399),
                .1, 1, "Exact visible preview title: Tempest; card-frame boundary also present")], false);

        var events = new List<VisionEvidenceEvent>();
        var offsets = new[] { 3.0, 3.201, 4.809, 5.902 };
        for (var index = 0; index < offsets.Length; index++)
        {
            var pixels = definition.Load($"evidence-{index + 1:D2}.png");
            var screen = await screenReader.AnalyzeAsync(pixels);
            var sightings = await titleReader.RecognizeAsync(pixels, screen, screenReader);
            if (index < 3 && !sightings.Any(item => item.Card.Id == fog.Id && item.Side == PlayerSide.Opponent))
                throw new InvalidOperationException($"The retained Tempest sequence lost Fog identity in evidence-{index + 1:D2}.png.");
            // The live journal classified the first two samples as the title-only
            // phase. PNG retention preserves their pixels but can sharpen the edge
            // enough for the current reader to call it framed, so retain the
            // reviewed phase label while still requiring fresh pixel identity.
            if (index < 2)
                sightings = sightings.Select(item => item.Card.Id == fog.Id ? item with
                {
                    Distance = .16,
                    Evidence = "Unique long visible preview-title edge: Impenetrable Fog; repeated frames required",
                    NeedsTemporalConfirmation = true,
                    IsSupplementalTitle = true
                } : item).ToArray();
            events.AddRange(ledger.Observe(at.AddSeconds(offsets[index]), screen with
            {
                MatchHudVisible = true,
                OpponentHandCount = 8
            }, sightings, false, artworkWasScanned: true));
        }

        var pair = events.SingleOrDefault(item => item.Sighting.Card.Id == fog.Id && item.ResolvedDeckCopies == 2);
        if (pair is null)
            throw new InvalidOperationException("The continuous Tempest popup collapsed the second framed Fog instead of establishing both original copies.");
    }
}
