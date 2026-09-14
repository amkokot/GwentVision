using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SirScratchTitleRegressionCase : IRecordingValidationCase
{
    public string Id => "sir-scratch-title";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer();
        var titles = new PreviewTitleRecognizer(catalog);
        var ledger = new MatchVisionLedger();
        var events = new List<VisionEvidenceEvent>();
        var at = DateTimeOffset.UnixEpoch;
        foreach (var (file, seconds) in new[] { ("evidence-01.png", 0d), ("evidence-02.png", .2) })
        {
            var frame = definition.Load(file);
            var screen = await reader.AnalyzeAsync(frame);
            var sightings = await titles.RecognizeAsync(frame, screen, reader);
            if (!sightings.Any(item => item.Card.Id == "203081" && item.Side == PlayerSide.Opponent))
                throw new InvalidOperationException("The clear Sir Scratch-a-Lot opponent title was missed.");
            events.AddRange(ledger.Observe(at.AddSeconds(seconds), screen, sightings,
                boardWasScanned: false, artworkWasScanned: true));
        }
        if (events.Count(item => item.Sighting.Card.Id == "203081") != 1)
            throw new InvalidOperationException("Two Sir Scratch-a-Lot title frames did not produce exactly one play episode.");
    }
}
