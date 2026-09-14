using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class PortalFixedPlacementSummonsRegressionCase : IRecordingValidationCase
{
    public string Id => "portal-fixed-placement-summons";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var portal = catalog.Single(card => card.Name == "Portal");
        var peasant = catalog.Single(card => card.Name == "Peasant Militia");
        var brigade = catalog.Single(card => card.Name == "Vrihedd Brigade");
        var pool = CompanionCardRules.RecurringSummonPool(portal, catalog, "Scoia'tael");
        if (!pool.Contains(peasant.Id) || !pool.Contains(brigade.Id) ||
            pool.Any(id => catalog.Single(card => card.Id == id) is not { Kind: CardKind.Unit, Provision: 4 }))
            throw new InvalidOperationException("Portal's printed four-provision summon pool is not bounded to legal unit targets.");

        using var screenReader = new ScreenStateRecognizer();
        var titleReader = new PreviewTitleRecognizer(catalog);
        var tooltipPixels = definition.Load("evidence-03.png");
        var tooltipScreen = await screenReader.AnalyzeAsync(tooltipPixels);
        var timerTitle = (await titleReader.RecognizeAsync(tooltipPixels, tooltipScreen, screenReader))
            .SingleOrDefault(item => item.Card.Id == brigade.Id && item.Side == PlayerSide.Opponent)
            ?? throw new InvalidOperationException("The retained Portal Timer popup no longer identifies Vrihedd Brigade.");

        var settledPixels = definition.Load("evidence-04.png");
        var settledScreen = await screenReader.AnalyzeAsync(settledPixels);
        if (settledScreen is not { View: GwentViewKind.Board, MatchHudVisible: true })
            throw new InvalidOperationException("The retained settled Portal row no longer reads as an authenticated match board.");
        // Preserve the reviewed board match together with the pixels. Its weak
        // seven-feature identity depends on the live candidate index accumulated
        // before this frame; the regression below owns the causal/placement rule,
        // while the full recording replay owns the accumulated matcher state.
        var peasantBoard = new CardSighting(peasant, PlayerSide.Opponent, CardSightSource.Board,
            new(.401, .26, .463, .42), .36, .875,
            "7 spatially consistent SIFT features; reviewed Portal summon candidate");

        var at = DateTimeOffset.UnixEpoch;
        var screen = settledScreen with { MatchHudVisible = true, OpponentHandCount = 9 };
        var ledger = new MatchVisionLedger();
        ledger.Observe(at, screen,
            [new(portal, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.815, .136, .915, .399), .1, 1,
                "Exact visible preview title: Portal; card-frame boundary also present")], false);
        var deploy = ledger.Observe(at.AddSeconds(6), screen,
            [peasantBoard, new(portal, PlayerSide.Opponent, CardSightSource.Board, new(.467, .26, .533, .42), .08, 1)], true);
        if (!deploy.Any(item => item.Sighting.Card.Id == peasant.Id && item.ResolvedDeckCopies == 1))
            throw new InvalidOperationException("Portal's retained left-side Deploy arrival was not resolved as a deck summon.");

        ledger.Observe(at.AddSeconds(20), screen with { OpponentHandCount = 8 }, [], false);
        var timer = ledger.Observe(at.AddSeconds(25), screen with { OpponentHandCount = 8 },
            [timerTitle with { Source = CardSightSource.PlayPreview, NeedsTemporalConfirmation = false }], false);
        if (!timer.Any(item => item.Sighting.Card.Id == brigade.Id && item.Sighting.Source == CardSightSource.Board &&
            item.ResolvedDeckCopies == 1))
            throw new InvalidOperationException("Portal's retained Timer popup was charged as a hand play instead of a deck summon.");
    }
}
