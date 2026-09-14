using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class RoundBoundSourceExpiryRegressionCase : IRecordingValidationCase
{
    public string Id => "round-bound-source-expiry";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var portal = catalog.Single(card => card.Name == "Portal");
        var miner = catalog.Single(card => card.Name == "Miner");
        using var screenReader = new ScreenStateRecognizer();
        var titleReader = new PreviewTitleRecognizer(catalog);
        var roundScreen = await screenReader.AnalyzeAsync(definition.Load("evidence-01.png"));
        if (!string.Equals(roundScreen.ScreenHeader, "ROUND 2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The retained round boundary no longer reads as ROUND 2.");
        var minerPixels = definition.Load("evidence-02.png");
        var minerScreen = await screenReader.AnalyzeAsync(minerPixels);
        var minerTitle = (await titleReader.RecognizeAsync(minerPixels, minerScreen, screenReader))
            .SingleOrDefault(item => item.Card.Id == miner.Id && item.Side == PlayerSide.Opponent)
            ?? throw new InvalidOperationException("The retained post-Runestone Miner title is no longer readable.");

        var at = DateTimeOffset.UnixEpoch;
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
            OpponentHandCount: 5, MatchHudVisible: true);
        var ledger = new MatchVisionLedger();
        ledger.Observe(at, board,
            [new(portal, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.815, .136, .915, .399),
                .1, 1, "Exact visible preview title: Portal; card-frame boundary also present")], false);
        ledger.Observe(at.AddSeconds(10), roundScreen, [], false);
        ledger.Observe(at.AddSeconds(20), board, [], false);
        var minerEvents = ledger.Observe(at.AddSeconds(22), board,
            [minerTitle with { Source = CardSightSource.PlayPreview, NeedsTemporalConfirmation = false }], false);
        if (!minerEvents.Any(item => item.Sighting.Card.Id == miner.Id && item.Sighting.Source == CardSightSource.PlayPreview) ||
            minerEvents.Any(item => item.Sighting.Card.Id == miner.Id && item.Sighting.Source == CardSightSource.Board))
            throw new InvalidOperationException("The prior-round Portal route still claimed the later Miner as a deck summon.");
    }
}
