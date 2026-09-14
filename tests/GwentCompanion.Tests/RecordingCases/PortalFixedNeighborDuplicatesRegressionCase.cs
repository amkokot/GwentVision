using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class PortalFixedNeighborDuplicatesRegressionCase : IRecordingValidationCase
{
    public string Id => "portal-fixed-neighbor-duplicates";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var portal = catalog.Single(card => card.Name == "Portal");
        var swordmaster = catalog.Single(card => card.Name == "Elven Swordmaster");
        var peasant = catalog.Single(card => card.Name == "Peasant Militia");
        var pool = CompanionCardRules.RecurringSummonPool(portal, catalog, "Scoia'tael");
        var referenceIds = pool.Append(portal.Id).ToHashSet(StringComparer.Ordinal);
        var references = VisionReferenceLibrary.Load(catalog.Where(card => referenceIds.Contains(card.Id)), cache);
        using var recognizer = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
            scope: VisionReferenceScope.CandidateDecks);
        recognizer.ObserveOpponentSummonSource(portal, pool);
        recognizer.ObserveOpponentCards([portal.Id]);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 14 };
        var deploySightings = recognizer.Recognize(definition.Load("evidence-01.png"), screen, true);
        if (deploySightings.Count(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                item.Card.Id == swordmaster.Id) < 2)
            throw new InvalidOperationException("The retained Portal Deploy row did not preserve both physical Elven Swordmasters.");
        var timerSightings = recognizer.Recognize(definition.Load("evidence-02.png"), screen, true);
        if (!timerSightings.Any(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                item.Card.Id == peasant.Id && (item.Evidence ?? "").Contains("printed right summon slot", StringComparison.Ordinal)))
            throw new InvalidOperationException("The retained Portal Timer row did not recover Peasant Militia from the clear right-slot appearance.");

        var at = DateTimeOffset.UnixEpoch;
        var ledger = new MatchVisionLedger();
        CardSighting Preview(CardDefinition card) => new(card, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.815, .136, .915, .399), .1, 1, $"Exact visible preview title: {card.Name}; card-frame boundary also present");
        ledger.Observe(at, screen, [Preview(swordmaster)], false);
        ledger.Observe(at.AddSeconds(3), screen, [Preview(portal)], false);
        var deploy = ledger.Observe(at.AddSeconds(8), screen, deploySightings, true);
        if (!deploy.Any(item => item.Sighting.Card.Id == swordmaster.Id && item.ResolvedDeckCopies == 1 &&
                item.EstablishesDistinctDeckCopy))
            throw new InvalidOperationException("Portal's left slot was lost to global same-identity deduplication.");
        var timer = ledger.Observe(at.AddSeconds(25), screen, timerSightings, true);
        if (!timer.Any(item => item.Sighting.Card.Id == peasant.Id && item.ResolvedDeckCopies == 1))
            throw new InvalidOperationException("Portal's right Timer slot did not establish Peasant Militia's deck origin.");
        return Task.CompletedTask;
    }
}
