using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Editable proposal for the shared builder, kept separate from raw encounter evidence.</summary>
public static class OpponentReviewDraft
{
    public static string SourceKey(LearnedOpponentDeck record) => "learned:" + record.Id;
    public static DeckDefinition Template(LearnedOpponentDeck record, IReadOnlyList<CardDefinition> catalog)
    {
        var current = new CurrentCardValues(catalog);
        var leader = GwentOneCardCatalog.StartingLeaders(catalog).FirstOrDefault(c => c.Name == record.Leader && c.Faction == record.Faction);
        var stratagem = catalog.FirstOrDefault(c => c.Kind == CardKind.Stratagem && c.Id == record.StratagemId);
        return new("draft:" + SourceKey(record), record.Name, record.Faction ?? "", leader?.Name ?? "", leader?.Provision ?? 0,
            DeckBuilderOrder.Sort(record.DraftCards.Where(c => StartingDeckRules.IsStartingCard(c.Card))
                .Select(c => new DeckCard(current.Card(c.Card), c.ObservedCopies))).ToArray(), Stratagem: stratagem);
    }
    public static int ObservedCopies(LearnedOpponentDeck record, string cardId) => record.Encounters.SelectMany(e => e.Cards)
        .Where(c => c.Card.Id == cardId && StartingDeckRules.CountsAgainstStartingDeck(c.Provenance))
        .Select(c => c.ObservedCopies).DefaultIfEmpty(0).Max();
    public static IReadOnlyList<ObservedCard> Cards(LearnedOpponentDeck record, DeckEditorDraft draft)
    {
        if (draft.SourceKey != SourceKey(record)) throw new InvalidOperationException("Opponent review belongs to another source.");
        return draft.Cards.Select(c => new ObservedCard(c.Card, CardProvenance.ProbableStartingDeck, 1, draft.UpdatedAt,
            "User-reviewed starting-list proposal; raw observed copies: " + ObservedCopies(record, c.Card.Id) + ". Not an automatic detection.", c.Count)).ToArray();
    }
    public static ReviewedDeckHeader Header(DeckEditorDraft draft, IReadOnlyList<CardDefinition> catalog)
    {
        var leader = GwentOneCardCatalog.StartingLeaders(catalog).FirstOrDefault(c => c.Id == draft.LeaderId);
        var stratagem = catalog.FirstOrDefault(c => c.Id == draft.StratagemId && c.Kind == CardKind.Stratagem);
        if (draft.LeaderId is not null && (leader is null || leader.Faction != draft.Faction) ||
            draft.StratagemId is not null && (stratagem is null || draft.Faction is { } faction && !FactionCompatibility.IsPlayableBy(stratagem, faction)))
            throw new InvalidOperationException("Choose a valid faction, leader and stratagem combination.");
        return new(draft.Faction, leader?.Name, leader?.Provision, stratagem?.Id);
    }
}
