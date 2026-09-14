using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SaskiaRecurringDepartureRegressionCase : IRecordingValidationCase
{
    public string Id => "saskia-recurring-departure";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var saskia = catalog.Single(card => card.Id == "203090");
        var dryad = catalog.Single(card => card.Id == "202275");
        var forest = catalog.Single(card => card.Id == "202272");
        var pool = CompanionCardRules.RecurringSummonPool(saskia, catalog);
        if (!pool.Contains(dryad.Id) || !pool.Contains(forest.Id))
            throw new InvalidOperationException("Saskia's generalized target pool lost a retained legal bronze target.");

        using var ocr = new ScreenStateRecognizer();
        var firstBefore = definition.Load("evidence-01.png");
        var firstAfter = definition.Load("evidence-02.png");
        var secondAfter = definition.Load("evidence-03.png");
        var secondBefore = definition.Load("evidence-04.png");
        var handRegion = new NormalizedRegion(.925, 0, .995, .065);
        if (await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(firstBefore, ocr) != 14 ||
            await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(firstAfter, ocr) != 13 ||
            await OpponentHudRecognizer.ReadHandCandidateAsync(firstBefore, handRegion, ocr) != 6 ||
            await OpponentHudRecognizer.ReadHandCandidateAsync(firstAfter, handRegion, ocr) != 6)
            throw new InvalidOperationException("The first retained Saskia activation no longer reads as hand 6 with deck 14→13.");
        if (await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(secondBefore, ocr) != 11 ||
            await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(secondAfter, ocr) != 10 ||
            await OpponentHudRecognizer.ReadHandCandidateAsync(secondBefore, handRegion, ocr) != 3 ||
            await OpponentHudRecognizer.ReadHandCandidateAsync(secondAfter, handRegion, ocr) != 3)
            throw new InvalidOperationException("The second retained Saskia activation no longer reads as hand 3 with deck 11→10.");

        using var detector = new FeatureCardRecognizer(VisionReferenceLibrary.Load(catalog, cache),
            Path.Combine(cache, "recognition-features"), scope: VisionReferenceScope.CandidateDecks);
        detector.ObserveOpponentSummonCandidates(pool);
        detector.ObserveOpponentCards([saskia.Id]);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 6, OpponentDeckCount = 13 };
        var dryadSighting = detector.Recognize(firstAfter, screen, true)
            .Where(sighting => sighting.Side == PlayerSide.Opponent && sighting.Card.Id == dryad.Id &&
                (sighting.Evidence ?? "").Contains("legal target of a recently committed printed deck summon", StringComparison.Ordinal))
            .OrderBy(sighting => sighting.Distance).FirstOrDefault() ??
            throw new InvalidOperationException("The first retained activation no longer identifies its far-right Dryad Matron candidate.");
        if ((dryadSighting.Region.Left + dryadSighting.Region.Right) / 2 < .53)
            throw new InvalidOperationException("Dryad Matron was not localized in the first retained far-right arrival lane.");
        var forestSighting = detector.Recognize(secondAfter,
                screen with { OpponentHandCount = 3, OpponentDeckCount = 10 }, true)
            .Where(sighting => sighting.Side == PlayerSide.Opponent && sighting.Card.Id == forest.Id &&
                (sighting.Evidence ?? "").Contains("legal target of a recently committed printed deck summon", StringComparison.Ordinal))
            .OrderBy(sighting => sighting.Distance).FirstOrDefault() ??
            throw new InvalidOperationException("The second retained activation no longer identifies its far-right Forest Whisperer candidate.");
        if ((forestSighting.Region.Left + forestSighting.Region.Right) / 2 < .60)
            throw new InvalidOperationException("Forest Whisperer was not localized in the retained far-right arrival lane.");

        var at = DateTimeOffset.UnixEpoch;
        var source = new CardSighting(saskia, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.815, .136, .915, .399), .08, 1, "Exact visible preview title");
        var rightBlocker = new CardSighting(saskia, PlayerSide.Opponent, CardSightSource.Board,
            dryadSighting.Region with { Left = dryadSighting.Region.Left + .08, Right = dryadSighting.Region.Right + .08 },
            .08, 1, "Independent card geometry to the right");
        var rightmostGuard = new MatchVisionLedger();
        rightmostGuard.Observe(at, screen with { OpponentDeckCount = 14 }, [source], false);
        rightmostGuard.Observe(at.AddSeconds(1), screen with { OpponentDeckCount = 14 }, [], false);
        rightmostGuard.Observe(at.AddSeconds(2), screen with { OpponentDeckCount = 13 }, [], false);
        if (rightmostGuard.Observe(at.AddSeconds(3), screen, [dryadSighting, rightBlocker], true)
            .Any(item => item.Sighting.Card.Id == dryad.Id))
            throw new InvalidOperationException("A non-rightmost Saskia candidate consumed the conserved deck departure.");
        var deployLedger = new MatchVisionLedger();
        deployLedger.Observe(at.AddSeconds(-10), screen with { OpponentHandCount = 0, OpponentDeckCount = 25 }, [], false);
        deployLedger.Observe(at, screen with { OpponentHandCount = null, OpponentDeckCount = null }, [source], false);
        deployLedger.Observe(at.AddSeconds(5), screen with { OpponentHandCount = 8, OpponentDeckCount = 14 }, [], false);
        var deployArrival = deployLedger.Observe(at.AddSeconds(34), screen with { OpponentDeckCount = 14 },
            [dryadSighting with { Distance = .34, Margin = 1 }], true);
        if (deployArrival.Count != 1 || deployArrival[0].Sighting.Card.Id != dryad.Id ||
            deployArrival[0].ResolvedDeckCopies != 1)
            throw new InvalidOperationException("A strong rightmost Saskia target did not consume its bounded Deploy summon credit.");
        var ledger = new MatchVisionLedger();
        ledger.Observe(at, screen with { OpponentDeckCount = 14 }, [source], false);
        ledger.Observe(at.AddSeconds(101), screen with { OpponentDeckCount = 14 }, [], false);
        ledger.Observe(at.AddSeconds(103), screen with { OpponentDeckCount = 13 }, [], false);
        var firstArrival = ledger.Observe(at.AddSeconds(104), screen, [dryadSighting], true);
        if (firstArrival.Count != 1 || firstArrival[0].Sighting.Card.Id != dryad.Id ||
            firstArrival[0].ResolvedDeckCopies != 1)
            throw new InvalidOperationException("The first conserved Saskia departure did not resolve exactly one Dryad Matron.");

        var secondScreen = screen with { OpponentHandCount = 3, OpponentDeckCount = 11 };
        ledger.Observe(at.AddSeconds(225), secondScreen, [], false);
        if (ledger.Observe(at.AddSeconds(238), secondScreen, [forestSighting], true).Count != 0 ||
            ledger.Observe(at.AddSeconds(238.3), secondScreen, [forestSighting], true).Count != 0)
            throw new InvalidOperationException("Forest Whisperer borrowed the stale first-activation deck departure.");
        var secondArrival = ledger.Observe(at.AddSeconds(240), secondScreen with { OpponentDeckCount = 10 }, [], false);
        if (secondArrival.Count != 1 || secondArrival[0].Sighting.Card.Id != forest.Id ||
            secondArrival[0].ResolvedDeckCopies != 1)
            throw new InvalidOperationException("A delayed HUD decrement did not resolve the immediately preceding Forest Whisperer sighting.");
    }
}
