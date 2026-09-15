using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class AutomaticSummonCandidateCoverageRegressionCase : IRecordingValidationCase
{
    public string Id => "automatic-summon-candidate-coverage";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var service = catalog.Single(card => card.Id == "203224");
        var mageAssassin = catalog.Single(card => card.Id == "202908");
        var tacticalDecision = catalog.Single(card => card.Name == "Tactical Decision");
        var morvran = catalog.Single(card => card.Name == "Morvran Voorhis");
        if (!CompanionCardRules.IsInherentDeckArrival(service) ||
            !FactionCompatibility.IsPlayableBy(service, "Northern Realms"))
            throw new InvalidOperationException("Redanian Secret Service is no longer eligible for faction-bounded automatic-summon coverage.");
        if (!CompanionCardRules.HasExplicitInherentDeckArrival(mageAssassin) ||
            !FactionCompatibility.IsPlayableBy(mageAssassin, "Nilfgaard") ||
            !LeaderSpawnCatalog.NamesSpawnedUnit(tacticalDecision, morvran))
            throw new InvalidOperationException("Tactical Decision's Morvran route no longer keeps Mage Assassin eligible for faction-bounded automatic-summon coverage.");

        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards([service.Id, "202414", "122318", "202551", "122304", "122216",
            "203184", "122314", "203109", "202472", "122209", "203025", "202481", "203185", "202918",
            "203014", "203038", "202401", "122103"]);
        var at = DateTimeOffset.UnixEpoch;
        var visible = new GwentVisualObservation(GwentViewKind.Board, false, 13, 10, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 9 };
        CardSighting Detect(string file, double seconds)
        {
            var frame = new PreparedVisionFrame(definition.Load(file), at.AddSeconds(seconds), visible, []);
            var detected = pipeline.RecognizePrepared(frame, includeBoard: true);
            return detected.Sightings.Where(item => item.Side == PlayerSide.Opponent &&
                item.Source == CardSightSource.Board && item.Card.Id == service.Id)
                .OrderBy(item => item.Distance).FirstOrDefault() ??
                throw new InvalidOperationException("The retained settled Redanian Secret Service board art was absent from its bounded candidate index.");
        }
        var sighting = Detect("evidence-03.png", 5);

        var ledger = new MatchVisionLedger();
        var before = visible with { OpponentDeckCount = 10 };
        ledger.Observe(at, before, [], boardWasScanned: false);
        ledger.Observe(at.AddSeconds(2), visible, [], boardWasScanned: false);
        if (ledger.Observe(at.AddSeconds(5), visible, [sighting], boardWasScanned: true).Count != 0)
            throw new InvalidOperationException("A two-anchor automatic-summon fallback committed without an independent repeated board frame.");
        return Task.CompletedTask;
    }
}
