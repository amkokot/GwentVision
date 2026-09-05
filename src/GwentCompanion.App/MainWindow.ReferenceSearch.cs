using System.Windows.Controls;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _updatingReferenceFilters;
    private bool _referenceFiltersManual;
    private void RefreshReferenceFilters(IEnumerable<DeckDefinition> decks)
    {
        if (ReferenceFactionFilter is null || ReferenceLeaderFilter is null || _updatingReferenceFilters) return;
        _updatingReferenceFilters = true;
        try
        {
            var source = decks.ToArray();
            var original = _opponentKnowledge.StartingLeader;
            var catalogLeaders = GwentOneCardCatalog.StartingLeaders(_candidateCatalog ?? []).ToArray();
            var observedFaction = catalogLeaders.FirstOrDefault(card => card.Name == original)?.Faction
                ?? (_opponentTracker.HasStableFaction ? _opponentTracker.Faction : null);
            SetFilterChoices(ReferenceFactionFilter, new[] { "All factions", "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate" });
            if (!_referenceFiltersManual) ReferenceFactionFilter.SelectedItem = observedFaction is not null && ReferenceFactionFilter.Items.Contains(observedFaction) ? observedFaction : "All factions";
            var faction = ReferenceFactionFilter.SelectedIndex <= 0 ? null : ReferenceFactionFilter.SelectedItem as string;
            var leaders = source.Where(deck => faction is null || deck.Faction == faction).Select(deck => deck.Leader)
                .Concat(catalogLeaders.Where(card => faction is null || card.Faction == faction).Select(card => card.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().OrderBy(name => name).Prepend("All leaders").ToArray();
            SetFilterChoices(ReferenceLeaderFilter, leaders);
            if (!_referenceFiltersManual) ReferenceLeaderFilter.SelectedItem = original is not null && leaders.Contains(original) ? original : "All leaders";
            ReferenceObservedButton.ToolTip = _referenceFiltersManual ? "Follow observed opponent again" : "Following observed opponent";
        }
        finally { _updatingReferenceFilters = false; }
    }
    private static void SetFilterChoices(ComboBox box, string[] choices)
    {
        if (box.Items.Cast<string>().SequenceEqual(choices)) return;
        var previous = box.SelectedItem as string;
        box.ItemsSource = choices;
        box.SelectedItem = previous is not null && choices.Contains(previous) ? previous : choices[0];
    }
    private void ReferenceFaction_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingReferenceFilters) return;
        _referenceFiltersManual = true;
        _updatingReferenceFilters = true;
        ReferenceLeaderFilter.SelectedIndex = 0;
        _updatingReferenceFilters = false;
        UpdateReferenceCandidates();
    }
    private void ReferenceLeader_OnChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updatingReferenceFilters) { _referenceFiltersManual = true; UpdateReferenceCandidates(); } }
    private void ReferenceObserved_OnClick(object sender, RoutedEventArgs e)
    { _referenceFiltersManual = false; UpdateReferenceCandidates(); }
}
