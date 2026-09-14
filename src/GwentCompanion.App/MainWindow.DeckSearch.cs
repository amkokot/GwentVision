using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private TextBox DeckSearchBox => LibrarySearch.Query;
    private TextBox DeckCardsFilter => LibrarySearch.CardNames;
    private bool _settingSearchOptions;
    private bool _deckSearchOptionsDirty = true;
    private void ConfigureDeckSearch()
    {
        LibrarySearch.Configure("Library", "DeckSearchBox", false);
    }
    private void RefreshDeckSearchOptions()
    {
        if (_settingSearchOptions || !_deckSearchOptionsDirty) return;
        _settingSearchOptions = true;
        try
        {
            var leaders = _cachedDecks.Select(d => (d.Leader, d.Faction))
                .Concat(GwentOneCardCatalog.StartingLeaders(_candidateCatalog ?? []).Select(c => (c.Name, c.Faction))).ToArray();
            var patches = _cachedDecks.SelectMany(d => d.Patches ?? []).Concat(_deckIndexEntries.SelectMany(d => d.Patches ?? []))
                .Select(p => p.Label).ToArray();
            LibrarySearch.SetOptions(leaders, patches);
            _deckSearchOptionsDirty = false;
        }
        finally { _settingSearchOptions = false; }
    }
    private void LibrarySearch_OnChanged(object? sender, EventArgs e) { if (!_settingSearchOptions) ApplyDeckFilter(); }
    private void LibrarySearch_OnReset(object? sender, EventArgs e) => LibrarySearch.ClearAll();
    private Window? _opponentCardsWindow;
    private DeckCardList? _expandedOpponentCards;
    private TextBlock? _expandedOpponentCardsHeading;
    private void ExpandOpponentCards_OnClick(object sender, RoutedEventArgs e)
    {
        if (_opponentCardsWindow is not null) { _opponentCardsWindow.Activate(); return; }
        var window = new Window { Owner = this, Title = "Opponent cards", Width = 470,
            Height = Math.Min(920, SystemParameters.WorkArea.Height - 20), MinHeight = 400, MinWidth = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = false };
        var panel = new DockPanel { Margin = new Thickness(10) };
        _expandedOpponentCardsHeading = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_expandedOpponentCardsHeading, Dock.Top); panel.Children.Add(_expandedOpponentCardsHeading);
        _expandedOpponentCards = new DeckCardList { RowHeight = 29, ShowDetails = false };
        _expandedOpponentCards.RowActivated += OpponentSlot_OnActivated; panel.Children.Add(_expandedOpponentCards);
        window.Content = panel; _opponentCardsWindow = window;
        System.Windows.Automation.AutomationProperties.SetAutomationId(window, "OpponentCardsWindow");
        window.Closed += (_, _) => { _opponentCardsWindow = null; _expandedOpponentCards = null; _expandedOpponentCardsHeading = null; };
        RefreshOpponentCardsWindow(); window.Show();
    }
    private void RefreshOpponentCardsWindow()
    {
        if (_expandedOpponentCards is null || _expandedOpponentCardsHeading is null) return;
        _expandedOpponentCards.Rows = OpponentDeckCards.Rows;
        _expandedOpponentCardsHeading.Text = OpponentDeckSummary.Text + "\n" + OpponentProvisionText.Text + "\n" +
            (_useOpponentModel ? "Edit candidates in the main opponent-card view." : "Observed starting-deck evidence only.");
    }
}
