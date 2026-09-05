using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private TextBox DeckSearchBox => LibrarySearch.Query;
    private TextBox DeckCardsFilter => LibrarySearch.CardNames;
    private TextBox OpponentDeckSearchBox => ReferenceSearch.Query;
    private ComboBox ReferenceFactionFilter => ReferenceSearch.Faction;
    private ComboBox ReferenceLeaderFilter => ReferenceSearch.Leader;
    private Button ReferenceObservedButton => ReferenceSearch.Reset;
    private bool _settingSearchOptions;
    private void ConfigureDeckSearch()
    {
        LibrarySearch.Configure("Library", "DeckSearchBox", false);
        ReferenceSearch.Configure("Reference", "OpponentDeckSearchBox", true);
    }
    private void RefreshDeckSearchOptions()
    {
        if (_settingSearchOptions) return;
        _settingSearchOptions = true;
        try
        {
            var leaders = _cachedDecks.Select(d => (d.Leader, d.Faction))
                .Concat(GwentOneCardCatalog.StartingLeaders(_candidateCatalog ?? []).Select(c => (c.Name, c.Faction))).ToArray();
            var patches = _cachedDecks.SelectMany(d => d.Patches ?? []).Concat(_deckIndexEntries.SelectMany(d => d.Patches ?? []))
                .Select(p => p.Label).ToArray();
            LibrarySearch.SetOptions(leaders, patches); ReferenceSearch.SetOptions(leaders, patches);
        }
        finally { _settingSearchOptions = false; }
    }
    private void LibrarySearch_OnChanged(object? sender, EventArgs e) { if (!_settingSearchOptions) ApplyDeckFilter(); }
    private void LibrarySearch_OnReset(object? sender, EventArgs e) => LibrarySearch.ClearAll();
    private void ReferenceSearch_OnChanged(object? sender, EventArgs e)
    {
        if (_updatingReferenceFilters || _settingSearchOptions) return;
        if (ReferenceSearch.ChangedField is "Faction" or "Leader") _referenceFiltersManual = true;
        UpdateReferenceCandidates();
    }
    private void ReferenceSearch_OnReset(object? sender, EventArgs e) { _referenceFiltersManual = false; UpdateReferenceCandidates(); }

    private Window? _hypothesisWindow;
    private DeckCardList? _hypothesisCards;
    private TextBlock? _hypothesisHeading;
    private void ExpandHypothesis_OnClick(object sender, RoutedEventArgs e)
    {
        if (_hypothesisWindow is not null) { _hypothesisWindow.Activate(); return; }
        var window = new Window { Owner = this, Title = "Opponent deck · live hypothesis", Width = 470,
            Height = Math.Min(920, SystemParameters.WorkArea.Height - 20), MinHeight = 400, MinWidth = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = false };
        var panel = new DockPanel { Margin = new Thickness(10) };
        _hypothesisHeading = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_hypothesisHeading, Dock.Top); panel.Children.Add(_hypothesisHeading);
        _hypothesisCards = new DeckCardList { RowHeight = 29, ShowDetails = false };
        _hypothesisCards.RowActivated += OpponentSlot_OnActivated; panel.Children.Add(_hypothesisCards);
        window.Content = panel; _hypothesisWindow = window;
        System.Windows.Automation.AutomationProperties.SetAutomationId(window, "OpponentHypothesisWindow");
        window.Closed += (_, _) => { _hypothesisWindow = null; _hypothesisCards = null; _hypothesisHeading = null; };
        RefreshHypothesisWindow(); window.Show();
    }
    private void RefreshHypothesisWindow()
    {
        if (_hypothesisCards is null || _hypothesisHeading is null) return;
        _hypothesisCards.Rows = OpponentDeckCards.Rows;
        _hypothesisHeading.Text = OpponentDeckSummary.Text + "\n" + OpponentProvisionText.Text + "\n" +
            (_confirmedOpponentDeck is null ? "Edit candidates in the main Deck tab; click unseen rows here to remove." : OpponentPinSummary.Text);
    }
}
