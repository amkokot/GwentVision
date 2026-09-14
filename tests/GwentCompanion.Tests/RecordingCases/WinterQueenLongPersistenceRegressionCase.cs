using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class WinterQueenLongPersistenceRegressionCase : IRecordingValidationCase
{
    public string Id => "winter-queen-long-persistence";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var queen = catalog.Single(card => card.Id == "202608");
        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var detector = new FeatureCardRecognizer(references.Where(reference => reference.Card.Id == queen.Id),
            Path.Combine(cache, "recognition-features"));
        var matcher = new CardArtMatcher(detector.ArtReferences);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards([queen.Id]);
        var at = DateTimeOffset.UnixEpoch;
        var coveredScreen = new GwentVisualObservation(GwentViewKind.Board, true, .78, 0,
            new(.50, .2857, .6667, .4286)) { MatchHudVisible = true };
        // An unrelated player-side tooltip does not obscure the opponent body.
        var cleanScreen = new GwentVisualObservation(GwentViewKind.Board, true, .42, 0,
            new(.5833,.7857,.75,.9286)) { MatchHudVisible = true };

        CardSighting DetectQueen(string file, DateTimeOffset when, GwentVisualObservation screen, PlayerSide side) =>
            pipeline.RecognizePrepared(new(definition.Load(file), when, screen, []), true).Sightings
                .Where(sighting => sighting.Card.Id == queen.Id && sighting.Side == side &&
                    sighting.Source == CardSightSource.Board &&
                    (sighting.Evidence ?? "").Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal))
                .OrderBy(sighting => sighting.Distance).FirstOrDefault() ??
            throw new InvalidOperationException($"{file} no longer produces the guarded {side} Winter Queen board candidate.");

        var coveredRegion = new NormalizedRegion(.4827512, .1499209, .5427512, .2951209);
        var coveredIdentity = matcher.IdentityDistance(definition.Load("evidence-01.png"), coveredRegion, queen.Id);
        if (coveredIdentity > .90)
            throw new InvalidOperationException($"The retained tooltip-covered Winter Queen crop no longer resembles the card ({coveredIdentity:F3}).");
        var covered = new CardSighting(queen, PlayerSide.Opponent, CardSightSource.Board, coveredRegion, .40, 1,
            $"One scene anchor independently selected two scaled references; appearance distance {coveredIdentity:F3} · bounded automatic-arrival board fallback; repetition and deck conservation still required · opponent candidate reference index",
            NeedsTemporalConfirmation: true);
        // The retained clean frame has a direct low-distance reference-art match.
        // Continuity-only crop-to-crop persistence must not substitute for this
        // identity evidence (it caused the false King of Beggars regression).
        var clean = DetectQueen("evidence-02.png", at.AddSeconds(56.7), cleanScreen, PlayerSide.Opponent);
        var ledger = new MatchVisionLedger();
        if (ledger.Observe(at, coveredScreen, [covered], true).Count != 0)
            throw new InvalidOperationException("One tooltip-covered Winter Queen hit committed without corroboration.");
        var arrival = ledger.Observe(at.AddSeconds(56.7), cleanScreen, [clean], true);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != queen.Id ||
            !arrival[0].Description.Contains("persisted", StringComparison.Ordinal))
            throw new InvalidOperationException($"Sparse same-body Winter Queen sightings did not recover the automatic arrival without deck OCR. " +
                $"First={covered.Region} [{covered.Evidence}] second={clean.Region} [{clean.Evidence}] events={arrival.Count}.");
        if (new OpponentKnowledge().BoardOrigin(arrival[0], "Monsters")?.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Recovered Winter Queen did not enter starting-deck accounting.");

        var falseRegion = new NormalizedRegion(.4629629, .5452662, .5304629, .7086162);
        var falseIdentity = matcher.IdentityDistance(definition.Load("evidence-03.png"), falseRegion, queen.Id);
        if (falseIdentity <= .75)
            throw new InvalidOperationException($"The retained player-side negative became too similar to Winter Queen ({falseIdentity:F3}).");
        var falsePlayer = new CardSighting(queen, PlayerSide.User, CardSightSource.Board, falseRegion, .40, 1,
            $"Two geometrically agreeing anchors among five ratio-separated SIFT candidates; appearance distance {falseIdentity:F3} · bounded automatic-arrival board fallback; repetition and deck conservation still required · user candidate reference index",
            NeedsTemporalConfirmation: true);
        var negative = new MatchVisionLedger();
        negative.Observe(at, coveredScreen, [covered], true);
        if (negative.Observe(at.AddSeconds(56.7), coveredScreen, [falsePlayer], true).Count != 0)
            throw new InvalidOperationException("An opposite-side, tooltip-covered high-distance Winter Queen candidate became a persisted arrival.");

        var displaced = new MatchVisionLedger();
        displaced.Observe(at, coveredScreen, [covered], true);
        if (displaced.Observe(at.AddSeconds(56.7), cleanScreen,
            [clean with { Region = new(.62, .15, .68, .295) }], true).Count != 0)
            throw new InvalidOperationException("Spatially inconsistent automatic-arrival candidates were merged.");
        return Task.CompletedTask;
    }
}
