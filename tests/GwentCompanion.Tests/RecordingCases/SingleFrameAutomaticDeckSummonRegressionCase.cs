using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SingleFrameAutomaticDeckSummonRegressionCase : IRecordingValidationCase
{
    public string Id => "single-frame-automatic-deck-summon";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var queen = catalog.Single(card => card.Id == "202608");
        var references = VisionReferenceLibrary.Load(catalog, cache).Where(reference => reference.Card.Id == queen.Id).ToArray();
        using var detector = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"));
        var visibleScreen = new GwentVisualObservation(GwentViewKind.Board, true, .8, 0,
            new(.42, .57, .58, .93)) { MatchHudVisible = true };
        var queenRegion = new NormalizedRegion(.5036867, .1375039, .5530566, .2352492);
        var identity = new CardArtMatcher(detector.ArtReferences)
            .IdentityDistance(definition.Load("evidence-03.jpg"), queenRegion, queen.Id);
        if (identity > .58)
            throw new InvalidOperationException($"The retained difficult board crop no longer resembles Winter Queen ({identity:F3}).");
        var sighting = new CardSighting(queen, PlayerSide.Opponent, CardSightSource.Board, queenRegion,
            .38, 1, $"Reviewed six-feature board hit; retained crop identity distance {identity:F3}", NeedsTemporalConfirmation: true);

        var at = DateTimeOffset.UnixEpoch;
        var counts = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 6, OpponentDeckCount = 14 };
        var ledger = new MatchVisionLedger();
        ledger.Observe(at, counts, [], boardWasScanned: false);
        ledger.Observe(at.AddSeconds(4), counts with { OpponentDeckCount = 13 }, [], boardWasScanned: false);
        var arrival = ledger.Observe(at.AddSeconds(33), visibleScreen, [sighting], boardWasScanned: true);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != queen.Id ||
            arrival[0].Sighting.Source != CardSightSource.Board)
            throw new InvalidOperationException("Same-hand 14→13 deck departure did not corroborate the one-frame Winter Queen arrival.");
        var origin = new OpponentKnowledge().BoardOrigin(arrival[0], "Monsters");
        if (origin?.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Confirmed Winter Queen arrival did not enter starting-deck accounting.");

        var noDrop = new MatchVisionLedger();
        noDrop.Observe(at, counts, [], boardWasScanned: false);
        if (noDrop.Observe(at.AddSeconds(30), visibleScreen, [sighting], true).Count != 0)
            throw new InvalidOperationException("One artwork frame manufactured an automatic summon without a deck departure.");
        var handPlay = new MatchVisionLedger();
        handPlay.Observe(at, counts, [], boardWasScanned: false);
        handPlay.Observe(at.AddSeconds(4), counts with { OpponentHandCount = 5, OpponentDeckCount = 13 }, [], false);
        if (handPlay.Observe(at.AddSeconds(30), visibleScreen, [sighting], true).Count != 0)
            throw new InvalidOperationException("A simultaneous hand/deck decrement was mistaken for an unplayed automatic arrival.");
        var stale = new MatchVisionLedger();
        stale.Observe(at, counts, [], boardWasScanned: false);
        stale.Observe(at.AddSeconds(4), counts with { OpponentDeckCount = 13 }, [], false);
        if (stale.Observe(at.AddSeconds(50), visibleScreen, [sighting], true).Count != 0)
            throw new InvalidOperationException("A stale deck departure corroborated an unrelated later board sighting.");
        return Task.CompletedTask;
    }
}
