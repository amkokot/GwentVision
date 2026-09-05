using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class MultiAccentPreviewTitleRegressionCase : IRecordingValidationCase
{
    public string Id => "multi-accent-preview-title";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var expected = catalog.Single(card => card.Id == "203045");
        using var reader = new ScreenStateRecognizer();
        var titles = new PreviewTitleRecognizer(catalog);
        var ledger = new MatchVisionLedger();
        var events = new List<VisionEvidenceEvent>();

        for (var index = 0; index < definition.Files.Length; index++)
        {
            var frame = definition.Load(definition.Files[index].File);
            var screen = await reader.AnalyzeAsync(frame);
            var sightings = await titles.RecognizeAsync(frame, screen, reader);
            var previews = sightings.Where(item => item.Side == PlayerSide.Opponent &&
                item.Source == CardSightSource.PlayPreview).ToArray();
            if (previews.Length != 1 || previews[0].Card.Id != expected.Id)
                throw new InvalidOperationException($"Evidence {index + 1} did not identify {expected.Name} exactly.");
            events.AddRange(ledger.Observe(DateTimeOffset.UnixEpoch.AddMilliseconds(index * 250), screen, sightings, false));
        }

        if (events.Count(item => item.Sighting.Card.Id == expected.Id &&
                item.Sighting.Source == CardSightSource.PlayPreview) != 1)
            throw new InvalidOperationException($"Repeated {expected.Name} frames did not resolve to one play episode.");
    }
}
