using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private void ShowObservedDecks_OnChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        ApplyDeckFilter();
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
    }

    private static string ObservedDraftPath => Path.Combine(FindDataRoot(), "cache", "deck-editor-drafts.json");

    private IEnumerable<DeckListItem> SavedObservedDraftItems()
    {
        try { return DeckEditorDraftStore.Load(ObservedDraftPath).Select(DraftListItem).ToArray(); }
        catch (Exception error) { LibraryActionStatus("Saved drafts unavailable; their cache was preserved: " + error.Message); return []; }
    }
    private DeckListItem DraftListItem(DeckEditorDraft draft)
    {
        draft = draft with { Cards = draft.Cards.Select(c => c with { Card = CurrentCard(c.Card) }).Where(c => StartingDeckRules.IsStartingCard(c.Card)).ToArray() };
        var leader = _candidateCatalog?.FirstOrDefault(c => c.Id == draft.LeaderId)?.Name;
        return new DeckListItem("Draft · " + draft.Name, $"Editable draft · {draft.Faction ?? "faction unknown"} · {draft.Cards.Sum(c => c.Count)} proposed cards · not match evidence",
            null, null, FilterFaction: draft.Faction, FilterLeader: leader, RecordedAt: draft.UpdatedAt,
            ObservedNames: draft.Cards.Select(c => c.Card.Name).ToArray(),
            PartialSlots: DeckBuilderOrder.Sort(draft.Cards).SelectMany(c => Enumerable.Range(1, c.Count).Select(copy =>
                new ProjectedDeckSlot(0, c.Card, copy, DeckSlotState.Selected, null, null, false, "Saved manual draft; not an observed card.")))
                .Select((slot, i) => slot with { Position = i + 1 }).ToArray(), DraftSourceKey: draft.SourceKey, SavedDraft: draft);
    }
    private void SaveObservedDraft(DeckEditorDraft draft)
    {
        if (BuilderBlockReason() is { } reason) throw new InvalidOperationException(reason);
        DeckEditorDraftStore.Save(ObservedDraftPath, draft);
        RefreshDeckList(); // Do not merge incomplete/proposed cards into the inference corpus or raw journal.
        DeckList.SelectedItem = DeckList.Items.Cast<DeckListItem>().FirstOrDefault(i => i.SavedDraft?.SourceKey == draft.SourceKey);
        LibraryActionStatus("Saved draft · " + draft.Name + ". Original match observations unchanged.");
    }
    private (DeckDefinition Deck, string SourceKey) ObservedEditorTemplate(DeckListItem item)
    {
        _candidateCatalog ??= GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"));
        var key = item.DraftSourceKey ?? (item.MemoryId is { } memory ? "learned:" + memory : null)
            ?? throw new InvalidOperationException("This observation has no stable draft source.");
        var saved = item.SavedDraft ?? DeckEditorDraftStore.Load(ObservedDraftPath).FirstOrDefault(d => d.SourceKey == key);
        var faction = saved is not null ? saved.Faction : item.FilterFaction;
        var leader = _candidateCatalog.FirstOrDefault(c => c.Kind == CardKind.Leader &&
            (saved is not null ? c.Id == saved.LeaderId : c.Name == item.FilterLeader) && (faction is null || c.Faction == faction));
        var stratagem = _candidateCatalog.FirstOrDefault(c => c.Kind == CardKind.Stratagem && c.Id == (saved is not null ? saved.StratagemId : item.FilterStratagemId));
        var cards = saved?.Cards ?? (item.PartialSlots ?? []).Where(s => s.Card is not null)
            .GroupBy(s => s.Card!.Id).Select(g => new DeckCard(g.First().Card!, g.Count())).ToArray();
        return (new DeckDefinition("draft:" + key, saved?.Name ?? item.Name, faction ?? "", leader?.Name ?? "", leader?.Provision ?? 0,
            DeckBuilderOrder.Sort(cards.Select(c => c with { Card = CurrentCard(c.Card) }).Where(c => StartingDeckRules.IsStartingCard(c.Card))).ToArray(), Stratagem: stratagem), key);
    }
}
