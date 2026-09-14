using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class FalseCiriGraveyardTacticReplayRegressionCase : IRecordingValidationCase
{
    public string Id => "false-ciri-graveyard-tactic-replay";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var falseCiri = catalog.Single(card => card.Name == "False Ciri");
        var buhurt = catalog.Single(card => card.Name == "Buhurt");
        using var screenReader = new ScreenStateRecognizer();
        var titles = new PreviewTitleRecognizer(catalog);

        var sourceFrame = definition.Load("evidence-01.png");
        var sourceScreen = await screenReader.AnalyzeAsync(sourceFrame);
        var sourceSight = (await titles.RecognizeAsync(sourceFrame, sourceScreen, screenReader))
            .FirstOrDefault(item => item.Card.Id == falseCiri.Id && item.Side == PlayerSide.Opponent)
            ?? throw new InvalidOperationException("The retained False Ciri preview no longer resolves exactly.");
        var targetFrame = definition.Load("evidence-03.png");
        var targetScreen = await screenReader.AnalyzeAsync(targetFrame);
        var targetSight = (await titles.RecognizeAsync(targetFrame, targetScreen, screenReader))
            .FirstOrDefault(item => item.Card.Id == buhurt.Id && item.Side == PlayerSide.Opponent)
            ?? throw new InvalidOperationException("The retained replayed Buhurt preview no longer resolves exactly.");

        var at = DateTimeOffset.UnixEpoch;
        var origins = new PlayProvenanceResolver();
        origins.Observe(new VisionEvidenceEvent(at, sourceSight, "Retained exact False Ciri preview"));
        var replay = origins.Observe(new VisionEvidenceEvent(at.AddSeconds(4), targetSight, "Retained exact Buhurt preview"));
        if (replay.Provenance != CardProvenance.Replayed || !replay.Reason.Contains(falseCiri.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("False Ciri's retained Buhurt was counted as another starting-deck copy instead of a graveyard replay.");
    }
}
