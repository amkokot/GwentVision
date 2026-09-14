using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class TempestGapSeparatedCopiesRegressionCase : IRecordingValidationCase
{
    public string Id => "tempest-gap-separated-copies";

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
            OpponentHandCount: 6, MatchHudVisible: true);
        ledger.Observe(at, board,
            [new(tempest, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.815, .136, .915, .399),
                .1, 1, "Exact visible preview title: Tempest; card-frame boundary also present")], false);

        var events = new List<VisionEvidenceEvent>();
        var offsets = new[] { 3.0, 3.601, 3.792, 3.990, 4.190, 4.391 };
        for (var index = 0; index < offsets.Length; index++)
        {
            var pixels = definition.Load($"evidence-{index + 1:D2}.png");
            var screen = await screenReader.AnalyzeAsync(pixels);
            var sightings = await titleReader.RecognizeAsync(pixels, screen, screenReader);
            var fogSightings = sightings.Where(item => item.Card.Id == fog.Id && item.Side == PlayerSide.Opponent).ToArray();
            if (index is 0 or >= 4 && fogSightings.Length == 0)
                throw new InvalidOperationException($"The retained Fog title disappeared from evidence-{index + 1:D2}.png.");
            if (index is >= 1 and <= 3 && fogSightings.Length != 0)
                throw new InvalidOperationException($"The retained blank interval still reads as Fog in evidence-{index + 1:D2}.png.");
            if (index >= 4)
                sightings = sightings.Select(item => item.Card.Id == fog.Id ? item with
                {
                    Distance = .16,
                    Evidence = "Unique long visible preview-title edge: Impenetrable Fog; repeated frames required",
                    NeedsTemporalConfirmation = true
                } : item).ToArray();
            events.AddRange(ledger.Observe(at.AddSeconds(offsets[index]), screen with
            {
                MatchHudVisible = true,
                OpponentHandCount = 6
            }, sightings, false, artworkWasScanned: false));
        }
        if (events.Count(item => item.Sighting.Card.Id == fog.Id && item.ResolvedDeckCopies == 2) != 1)
            throw new InvalidOperationException("The separated identical Fog animations did not establish exactly two original copies once.");
    }
}
