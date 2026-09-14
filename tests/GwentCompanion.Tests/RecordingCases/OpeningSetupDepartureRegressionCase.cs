using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class OpeningSetupDepartureRegressionCase : IRecordingValidationCase
{
    public string Id => "opening-setup-departure";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        using var reader = new ScreenStateRecognizer();
        var first = definition.Load("evidence-01.png");
        var second = definition.Load("evidence-02.png");
        var firstDeck = await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(first, reader);
        var secondDeck = await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(second, reader);
        if (firstDeck != 14 || secondDeck != 14)
            throw new InvalidOperationException($"The retained opening draw pile must read 14 twice, not {firstDeck?.ToString() ?? "unknown"}/{secondDeck?.ToString() ?? "unknown"}.");

        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var at = DateTimeOffset.UnixEpoch;
        foreach (var (faction, expectedId) in new[] { ("Scoia'tael", "203280"), ("Skellige", "203042") })
        {
            var knowledge = new OpponentKnowledge();
            knowledge.ConfigureOpeningSetupCards(catalog);
            knowledge.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
                ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: firstDeck, OpponentScore: 0, MatchHudVisible: true), at);
            knowledge.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
                ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: secondDeck, OpponentScore: 0, MatchHudVisible: true), at.AddSeconds(1));
            var openingRules = knowledge.Assess([]);
            if (openingRules.Musicians?.State != ConstraintState.RuledOut)
                throw new InvalidOperationException("The first authenticated fully dealt zero-point opening did not rule out Musicians.");
            knowledge.ObserveOpeningCounts(faction);
            if (knowledge.OpeningStartingCards.SingleOrDefault()?.Card.Id != expectedId)
                throw new InvalidOperationException($"The retained short opening did not infer the unique legal {faction} setup departure.");
        }
        var musiciansKnowledge = new OpponentKnowledge();
        musiciansKnowledge.ConfigureOpeningSetupCards(catalog);
        musiciansKnowledge.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
            ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: firstDeck, OpponentScore: 1, MatchHudVisible: true), at);
        musiciansKnowledge.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
            ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: secondDeck, OpponentScore: 1, MatchHudVisible: true), at.AddSeconds(1));
        musiciansKnowledge.ObserveOpeningCounts("Scoia'tael");
        if (musiciansKnowledge.OpeningStartingCards.SingleOrDefault()?.Card.Id != "202200")
            throw new InvalidOperationException("The retained short opening plus one-point board did not select Musicians over Eudora.");

        var unknownScore = new OpponentKnowledge();
        unknownScore.ConfigureOpeningSetupCards(catalog);
        unknownScore.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
            ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: firstDeck, MatchHudVisible: true), at);
        unknownScore.ObserveScreen(new(GwentViewKind.Board, false, 0, 0, null,
            ScreenHeader: "ROUND 1", OpponentHandCount: 10, OpponentDeckCount: secondDeck, MatchHudVisible: true), at.AddSeconds(1));
        unknownScore.ObserveOpeningCounts("Scoia'tael");
        if (unknownScore.OpeningStartingCards.Count != 0)
            throw new InvalidOperationException("An unread opening score guessed between Eudora and Musicians.");
    }
}
