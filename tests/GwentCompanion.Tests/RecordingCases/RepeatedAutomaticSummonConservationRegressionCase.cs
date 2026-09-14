using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class RepeatedAutomaticSummonConservationRegressionCase : IRecordingValidationCase
{
    public string Id => "repeated-automatic-summon-conservation";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var service = catalog.Single(card => card.Id == "203224");
        if (!CompanionCardRules.IsInherentDeckArrival(service) ||
            !FactionCompatibility.IsPlayableBy(service, "Northern Realms"))
            throw new InvalidOperationException("Redanian Secret Service is no longer eligible for faction-bounded automatic-summon coverage.");

        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards([service.Id, "202414", "122318", "202551", "122304", "122216",
            "203184", "122314", "203109", "202472", "122209", "203025", "202481", "203185", "202918",
            "203014", "203038", "202401", "122103"]);

        var at = DateTimeOffset.UnixEpoch;
        var settled = new GwentVisualObservation(GwentViewKind.Board, false, 13, 10, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 9 };
        CardSighting? Detect(string file, double seconds)
        {
            var frame = new PreparedVisionFrame(definition.Load(file), at.AddSeconds(seconds), settled, []);
            var sighting = pipeline.RecognizePrepared(frame, includeBoard: true).Sightings
                .Where(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                    item.Card.Id == service.Id)
                .OrderBy(item => item.Distance).FirstOrDefault();
            return sighting;
        }

        var repeated = new[] { Detect("evidence-01.png", 3), Detect("evidence-02.png", 5),
            Detect("evidence-03.png", 7) }.OfType<CardSighting>().Where(sighting =>
            sighting.NeedsTemporalConfirmation &&
            (sighting.Evidence ?? "").Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal)).Take(2).ToArray();
        if (repeated.Length != 2)
            throw new InvalidOperationException($"Only {repeated.Length} retained settled frames produced the guarded Redanian Secret Service board candidate.");
        var first = repeated[0]; var second = repeated[1];
        pipeline.Reset();
        if (Detect("evidence-01.png", 10) is null)
            throw new InvalidOperationException("The automatic-arrival candidate could not be armed for the negative visual control.");
        if (Detect("evidence-03.png", 12) is not null)
            throw new InvalidOperationException("Localized persistence hallucinated Redanian Secret Service in the retained pre-arrival board frame.");
        var before = settled with { OpponentDeckCount = 10 };

        var ledger = new MatchVisionLedger();
        ledger.Observe(at, before, [], boardWasScanned: false);
        ledger.Observe(at.AddSeconds(2), settled, [], boardWasScanned: false);
        if (ledger.Observe(at.AddSeconds(3), settled, [first], boardWasScanned: true).Count != 0)
            throw new InvalidOperationException("One weak automatic-summon frame committed without independent visual repetition.");
        var arrival = ledger.Observe(at.AddSeconds(5), settled, [second], boardWasScanned: true);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != service.Id ||
            arrival[0].Sighting.Source != CardSightSource.Board)
            throw new InvalidOperationException("Repeated Redanian Secret Service board evidence plus a same-hand 10→9 deck departure did not commit exactly one arrival. " +
                $"First={first.Evidence} Second={second.Evidence}");
        var origin = new OpponentKnowledge().BoardOrigin(arrival[0], "Northern Realms");
        if (origin?.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("The conserved automatic arrival did not enter starting-deck accounting.");

        var noDrop = new MatchVisionLedger();
        noDrop.Observe(at, before, [], boardWasScanned: false);
        noDrop.Observe(at.AddSeconds(3), before, [first], boardWasScanned: true);
        if (noDrop.Observe(at.AddSeconds(5), before, [second], boardWasScanned: true).Count != 0)
            throw new InvalidOperationException("Repeated artwork manufactured an automatic summon without a deck departure.");

        var oneFrame = new MatchVisionLedger();
        oneFrame.Observe(at, before, [], boardWasScanned: false);
        oneFrame.Observe(at.AddSeconds(2), settled, [], boardWasScanned: false);
        if (oneFrame.Observe(at.AddSeconds(5), settled, [first], boardWasScanned: true).Count != 0)
            throw new InvalidOperationException("A deck departure plus one weak frame manufactured an automatic summon.");

        var simultaneousCost = new MatchVisionLedger();
        simultaneousCost.Observe(at, before, [], boardWasScanned: false);
        simultaneousCost.Observe(at.AddSeconds(2), settled with { OpponentHandCount = 7 }, [], boardWasScanned: false);
        simultaneousCost.Observe(at.AddSeconds(3), settled with { OpponentHandCount = 7 }, [first], boardWasScanned: true);
        if (simultaneousCost.Observe(at.AddSeconds(5), settled with { OpponentHandCount = 7 }, [second], boardWasScanned: true).Count != 0)
            throw new InvalidOperationException("A simultaneous hand/deck decrement was mistaken for an unplayed automatic arrival.");
        return Task.CompletedTask;
    }
}
