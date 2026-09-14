using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class OpponentNamedSpawnOutputRegressionCase : IRecordingValidationCase
{
    public string Id => "opponent-named-spawn-output";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var references = VisionReferenceLibrary.Load(catalog, cache);
        var savolla = catalog.Single(card => card.Name == "Savolla");
        var frightener = catalog.Single(card => card.Id == "202563");
        var at = DateTimeOffset.UnixEpoch;
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetLikelyOpponentCards([]);
        var preview = new CardSighting(savolla, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), .08, 1, "Exact visible preview title");
        var committed = pipeline.Commit(new(at, board, [preview], [], false, false));
        if (!committed.Events.Any(evidence => evidence.Sighting.Card.Id == savolla.Id))
            throw new InvalidOperationException("Savolla source play did not enter the chronological ledger.");

        var first = pipeline.RecognizePrepared(new(definition.Load("evidence-02.png"), at.AddSeconds(4.5), board, []), true);
        var second = pipeline.RecognizePrepared(new(definition.Load("evidence-03.png"), at.AddSeconds(6.5), board, []), true);
        if (!first.Sightings.Any(sighting => sighting.Side == PlayerSide.Opponent && sighting.Source == CardSightSource.Board && sighting.Card.Id == frightener.Id) ||
            !second.Sightings.Any(sighting => sighting.Side == PlayerSide.Opponent && sighting.Source == CardSightSource.Board && sighting.Card.Id == frightener.Id))
            throw new InvalidOperationException("Savolla's explicitly named Frightener output was absent from the opponent candidate-scoped board matcher.");
        return Task.CompletedTask;
    }
}
