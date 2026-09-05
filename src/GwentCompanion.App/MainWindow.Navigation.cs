using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private enum UiPage { Plays, Deck, Candidates, Reference, Library, Pinned, Settings, Advanced, Audit, Memory, MyDeck, Points }
    private UiPage _page = UiPage.Deck;
    private UiPage _lastGameplayPage = UiPage.Deck;
    private UiPage _lastReferencePage = UiPage.Reference;
    private bool _changingReferenceMode;
    private bool _changingNavigation;
    private string? _selectedRule;
    private void ShowPage(UiPage page)
    {
        if (page == UiPage.Plays && !ExperimentalOverviewEnabled && !_analysisControlTest && _reviewEvidencePath is null)
            page = UiPage.Deck;
        if (page != UiPage.Library) CancelLibraryPreview();
        _page = page;
        if (page == UiPage.Library) RefreshLibraryPreview();
        var isReference = page is UiPage.Reference or UiPage.MyDeck;
        var isLive = page is UiPage.Plays or UiPage.Deck or UiPage.Candidates or UiPage.Pinned;
        if (isLive || isReference) _lastGameplayPage = page;
        foreach (var child in Pages.Children.OfType<FrameworkElement>()) child.Visibility = child.Name == page + "Page" ? Visibility.Visible : Visibility.Collapsed;
        _changingNavigation = true;
        foreach (var button in GameplayNavigation.Children.OfType<RadioButton>().Append(LibraryNavigation)) button.IsChecked = (string)button.Tag == (isReference ? UiPage.Reference : isLive ? UiPage.Deck : page).ToString();
        foreach (var button in LiveModeBar.Children.OfType<RadioButton>()) button.IsChecked = (string)button.Tag == page.ToString();
        _changingNavigation = false;
        PageBackBar.Visibility = isLive || page == UiPage.Library || isReference ? Visibility.Collapsed : Visibility.Visible;
        ReferenceModeBar.Visibility = isReference ? Visibility.Visible : Visibility.Collapsed;
        if (isReference)
        {
            _lastReferencePage = page;
            _changingReferenceMode = true;
            ReferenceDeckNavigation.IsChecked = page == UiPage.Reference;
            ReferenceMyDeckNavigation.IsChecked = page == UiPage.MyDeck;
            _changingReferenceMode = false;
        }
        AuxiliaryPageTitle.Text = page switch { UiPage.MyDeck => "My deck", UiPage.Pinned => "Snapshots", _ => page.ToString() };
        if (isReference || page == UiPage.Pinned) { RefreshPinnedCaptures(); RefreshPinnedOpponentReference(); }
        RefreshHoverBanner();
        UpdateWorkspaceLayout();
    }
    private bool ExperimentalOverviewEnabled => EnableExperimentalAnalysisChoice?.IsChecked == true;
    private void Navigate_OnClick(object sender, RoutedEventArgs e)
    {
        if (_changingNavigation) return;
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<UiPage>(name, out var page)) ShowPage(page == UiPage.Reference ? _lastReferencePage : page);
    }
    private void ReferenceMode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_changingReferenceMode && Pages is not null && sender is FrameworkElement { Tag: string name }
            && Enum.TryParse<UiPage>(name, out var page)) ShowPage(page);
    }
    private void BackToGame_OnClick(object sender, RoutedEventArgs e) => ShowPage(_lastGameplayPage);
    private void SetAnalysisButtonState(bool running)
    {
        AnalysisIcon.Data = Geometry.Parse(running ? "M 1,1 L 15,1 L 15,15 L 1,15 Z" : "M 2,1 L 14,8 L 2,15 Z");
        AnalysisIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#F2AF60" : "#8BDCAB"));
        var label = running ? "Stop analysis" : "Start analysis";
        AutomationProperties.SetName(DiagnosticButton, label);
        DiagnosticButton.ToolTip = label + " (records diagnostics)";
    }
    private void RenderDeckRuleBadges()
    {
        if (DeckConstraintBadges is null) return;
        var rules = _opponentKnowledge.Assess(_opponentTracker.DeckBuildingObservations);
        var entries = new[] { ("Devotion", rules.Devotion), ("Musicians", rules.Musicians!),
            ("Nekker", rules.GoldenNekker), ("Renfri", rules.Renfri), ("Shupe", rules.Shupe) };
        DeckConstraintBadges.Children.Clear();
        foreach (var (name, rule) in entries)
        {
            var (color, state) = rule.State switch
            {
                ConstraintState.Confirmed => ("#85D7B1", "confirmed"),
                ConstraintState.RuledOut => ("#FF9990", "ruled out"),
                ConstraintState.Likely => ("#85D7B1", "likely, not confirmed"),
                ConstraintState.Possible => ("#C2CBC5", "still possible"),
                _ => ("#C2CBC5", "unknown / review evidence")
            };
            var pairHint = name == "Shupe" && rule.State == ConstraintState.Possible ? _lastProjection?.SingletonPairHypothesis : null;
            if (pairHint is not null) { color = "#FF9990"; state = "unlikely, inferred pair"; }
            var detail = pairHint ?? name + ": " + state + ". " + rule.Reason;
            var button = new Button { Content = new TextBlock { Text = name, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), FontSize = 10 },
                Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 4, 4) };
            AutomationProperties.SetName(button, name + ": " + state); AutomationProperties.SetHelpText(button, detail);
            button.Click += (_, _) => { _selectedRule = _selectedRule == name ? null : name; DeckRuleDetailText.Text = detail; DeckRuleDetailText.Visibility = _selectedRule is null ? Visibility.Collapsed : Visibility.Visible; };
            DeckConstraintBadges.Children.Add(button);
            if (_selectedRule == name) DeckRuleDetailText.Text = detail;
        }
    }
}
