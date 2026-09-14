using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class SaskiaRecurringDeckSummonRegressionCase : IRecordingValidationCase
{
    public string Id => "saskia-recurring-deck-summon";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var saskia = catalog.Single(card => card.Id == "203090");
        var berserker = catalog.Single(card => card.Id == "202476");
        var skirmisher = catalog.Single(card => card.Id == "142305");
        var pool = CompanionCardRules.RecurringSummonPool(saskia, catalog);
        if (!pool.Contains(berserker.Id) || !pool.Contains(skirmisher.Id) || pool.Count > 80 ||
            pool.Any(id => !StartingDeckRules.IsLegalStartingCard(catalog.Single(card => card.Id == id), "Scoia'tael")))
            throw new InvalidOperationException("Saskia's bounded visual pool lost a legal bronze target or admitted an illegal starting card.");

        var references = VisionReferenceLibrary.Load(catalog.Where(card => pool.Contains(card.Id)), cache);
        using var recognizer = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
            scope: VisionReferenceScope.CandidateDecks);
        recognizer.ObserveOpponentSummonCandidates(pool);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 9, OpponentDeckCount = 14 };
        var candidates = recognizer.Recognize(definition.Load("evidence-03.png"), screen, true);
        var sighting = candidates.SingleOrDefault(item => item.Side == PlayerSide.Opponent &&
            item.Source == CardSightSource.Board && item.Card.Id == berserker.Id)
            ?? throw new InvalidOperationException("The retained Saskia board did not recover Dwarf Berserker from its strong source-scoped geometry.");
        if (candidates.Any(item => item.Card.Id == skirmisher.Id))
            throw new InvalidOperationException("The retained 4-power/3-armor Dwarf Berserker was mislabeled as Dwarven Skirmisher.");

        var at = DateTimeOffset.UnixEpoch;
        var ledger = new MatchVisionLedger();
        var preview = new CardSighting(saskia, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.815, .136, .915, .399), .1, 1, "Exact visible preview title");
        var source = ledger.Observe(at, screen with { OpponentHandCount = 10, OpponentDeckCount = 15 }, [preview], false).Single();
        ledger.Observe(at.AddSeconds(1), screen with { OpponentDeckCount = 15 }, [], false);
        ledger.Observe(at.AddSeconds(5), screen, [], false);
        var arrival = ledger.Observe(at.AddSeconds(9), screen, [sighting], true).SingleOrDefault(item => item.Sighting.Card.Id == berserker.Id)
            ?? throw new InvalidOperationException("Strong source-scoped Berserker geometry did not join Saskia to the conserved deck departure.");
        var knowledge = new OpponentKnowledge(); knowledge.Observe(source, "Scoia'tael", CardProvenance.ProbableStartingDeck);
        if (knowledge.BoardOrigin(arrival, "Scoia'tael")?.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Saskia's bounded summon credit did not classify the recovered Berserker as probable starting-deck evidence.");
        return Task.CompletedTask;
    }
}
