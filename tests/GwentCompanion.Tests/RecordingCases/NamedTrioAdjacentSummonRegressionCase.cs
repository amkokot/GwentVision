using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class NamedTrioAdjacentSummonRegressionCase : IRecordingValidationCase
{
    public string Id => "named-trio-adjacent-summon";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var lambert = catalog.Single(card => card.Name == "Lambert");
        var eskel = catalog.Single(card => card.Name == "Eskel");
        var vesemir = catalog.Single(card => card.Name == "Vesemir");
        var targets = CompanionCardRules.NamedDeckSummonTargets(lambert, catalog);
        if (!targets.SequenceEqual([eskel.Id, vesemir.Id]))
            throw new InvalidOperationException("Lambert's printed ordered deck-summon targets were not parsed exactly.");

        var ids = targets.Prepend(lambert.Id).ToHashSet(StringComparer.Ordinal);
        var references = VisionReferenceLibrary.Load(catalog.Where(card => ids.Contains(card.Id)), cache);
        using var recognizer = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
            scope: VisionReferenceScope.CandidateDecks);
        recognizer.ObserveOpponentSummonSource(lambert, [], targets);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 1, OpponentDeckCount = 6 };
        var sightings = recognizer.Recognize(definition.Load("evidence-01.png"), screen, true);
        var missingTargets = new[] { eskel, vesemir }.Where(card => !sightings.Any(item =>
            item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board && item.Card.Id == card.Id)).ToArray();
        if (missingTargets.Length > 0)
            throw new InvalidOperationException("The retained settled trio did not recover the source-scoped target(s): " +
                string.Join(", ", missingTargets.Select(card => card.Name)) + ". Seen: " +
                string.Join(", ", sightings.Select(item => item.Card.Name)));

        var at = DateTimeOffset.UnixEpoch;
        var ledger = new MatchVisionLedger();
        var preview = new CardSighting(lambert, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.815, .136, .915, .399), .08, 1,
            "Exact visible preview title: Lambert; card-frame boundary also present");
        ledger.Observe(at, screen with { OpponentDeckCount = 8, OpponentHandCount = 2 }, [preview], false);
        var arrivals = ledger.Observe(at.AddSeconds(6), screen, sightings, true);
        if (arrivals.Count(item => targets.Contains(item.Sighting.Card.Id) && item.ResolvedDeckCopies == 1 &&
                item.EstablishesDistinctDeckCopy) != 2)
            throw new InvalidOperationException("Lambert's complete retained adjacent sequence did not establish both deck summons.");
        return Task.CompletedTask;
    }
}
