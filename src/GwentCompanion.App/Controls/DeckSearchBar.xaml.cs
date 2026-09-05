using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App.Controls;

public partial class DeckSearchBar : UserControl
{
    private bool _updating = true;
    private (string Name, string Faction)[] _leaders = [];
    public event EventHandler? Changed;
    public event EventHandler? ResetRequested;
    public string ChangedField { get; private set; } = "";
    public TextBox Query => QueryBox;
    public TextBox CardNames => CardNamesBox;
    public string ExcludedCards => ExcludedCardsBox.Text;
    public int SortOrder => SortBox.SelectedIndex;
    public ComboBox Faction => FactionBox;
    public ComboBox Leader => LeaderBox;
    public Button Reset => ResetButton;
    public DeckSearchBar()
    {
        InitializeComponent();
        FactionBox.ItemsSource = new[] { "All factions", "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate" };
        FactionBox.SelectedIndex = 0; LeaderBox.ItemsSource = new[] { "All leaders" }; LeaderBox.SelectedIndex = 0;
        PatchBox.ItemsSource = new[] { "All patches" }; PatchBox.SelectedIndex = 0;
        CompletenessBox.ItemsSource = new[] { "All lists", "Complete cached", "Incomplete observed", "Uncached links" }; CompletenessBox.SelectedIndex = 0;
        SortBox.ItemsSource = new[] { "Relevance / newest", "Name A–Z", "Provisions high–low", "Provisions low–high" }; SortBox.SelectedIndex = 0;
        _updating = false;
    }
    public void Configure(string prefix, string queryId, bool observedReset)
    {
        AutomationProperties.SetAutomationId(QueryBox, queryId);
        AutomationProperties.SetAutomationId(FactionBox, prefix + "FactionFilter");
        AutomationProperties.SetAutomationId(LeaderBox, prefix + "LeaderFilter");
        AutomationProperties.SetAutomationId(MoreButton, prefix + "MoreFilters");
        AutomationProperties.SetAutomationId(PatchBox, prefix + "PatchFilter");
        AutomationProperties.SetAutomationId(CardNamesBox, prefix == "Library" ? "DeckCardsFilter" : "ReferenceCardsFilter");
        AutomationProperties.SetName(ResetButton, observedReset ? "Use observed opponent" : "Reset deck search");
        ResetButton.ToolTip = observedReset ? "Use observed faction and original leader" : "Clear query and filters";
        SortBox.Visibility = SortLabel.Visibility = observedReset ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetOptions(IEnumerable<(string Name, string Faction)> leaders, IEnumerable<string> patches)
    {
        _updating = true;
        try
        {
            _leaders = leaders.Distinct().ToArray(); RefreshLeaders();
            var labels = patches.Distinct().OrderByDescending(p => Version.TryParse(p, out var v) ? v : new Version(0, 0)).Prepend("All patches").ToArray();
            SetChoices(PatchBox, labels);
        }
        finally { _updating = false; }
    }
    public DeckSearchFilters Filters => new(
        (DevotionBox.IsChecked == true ? DeckCategoryFilter.Devotion : 0) | (SingletonBox.IsChecked == true ? DeckCategoryFilter.Singleton : 0) |
        (RenfriBox.IsChecked == true ? DeckCategoryFilter.Renfri : 0) | (NekkerBox.IsChecked == true ? DeckCategoryFilter.GoldenNekker : 0) |
        (MusiciansBox.IsChecked == true ? DeckCategoryFilter.Musicians : 0),
        PatchBox.SelectedIndex > 0 ? PatchBox.SelectedItem as string : null,
        (DeckCompletenessFilter)Math.Max(0, CompletenessBox.SelectedIndex), UnknownBox.IsChecked == true);
    public void ClearAll()
    {
        _updating = true;
        QueryBox.Clear(); FactionBox.SelectedIndex = 0; RefreshLeaders(); LeaderBox.SelectedIndex = 0; ClearExtra();
        _updating = false; Notify("Reset");
    }
    private void ClearExtra()
    {
        foreach (var box in new[] { DevotionBox, SingletonBox, RenfriBox, NekkerBox, MusiciansBox, UnknownBox }) box.IsChecked = false;
        PatchBox.SelectedIndex = CompletenessBox.SelectedIndex = SortBox.SelectedIndex = 0; CardNamesBox.Clear(); ExcludedCardsBox.Clear();
    }
    private void RefreshLeaders() => SetChoices(LeaderBox, _leaders.Where(l => FactionBox.SelectedIndex <= 0 || l.Faction == FactionBox.SelectedItem as string)
        .Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n).Prepend("All leaders").ToArray());
    private static void SetChoices(ComboBox box, string[] values)
    {
        if (box.Items.Cast<string>().SequenceEqual(values)) return;
        var selected = box.SelectedItem as string; box.ItemsSource = values;
        box.SelectedItem = values.Contains(selected) ? selected : values[0];
    }
    private void Notify(string field)
    {
        if (_updating) return;
        var filters = Filters; var parts = new List<string>();
        if (filters.Categories != 0) parts.Add(filters.Categories.ToString());
        if (filters.Patch is not null) parts.Add("Patch " + filters.Patch);
        if (filters.Completeness != 0) parts.Add(CompletenessBox.SelectedItem.ToString()!);
        if (filters.IncludeUnknown) parts.Add("include unknown");
        if (!string.IsNullOrWhiteSpace(CardNamesBox.Text)) parts.Add("contains " + CardNamesBox.Text);
        if (!string.IsNullOrWhiteSpace(ExcludedCardsBox.Text)) parts.Add("without " + ExcludedCardsBox.Text);
        if (SortOrder > 0) parts.Add(SortBox.SelectedItem.ToString()!);
        ActiveText.Text = string.Join(" · ", parts); ActiveText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = parts.Count == 0 ? "Filters ▾" : $"Filters ({parts.Count}) ▾";
        ChangedField = field; Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Faction_OnChanged(object sender, SelectionChangedEventArgs e)
    { if (_updating) return; _updating = true; LeaderBox.SelectedIndex = 0; RefreshLeaders(); _updating = false; Notify("Faction"); }
    private void Leader_OnChanged(object sender, SelectionChangedEventArgs e) => Notify("Leader");
    private void Query_OnChanged(object sender, TextChangedEventArgs e) => Notify("Query");
    private void ExtraText_OnChanged(object sender, TextChangedEventArgs e) => Notify("Extra");
    private void Extra_OnChanged(object sender, SelectionChangedEventArgs e) => Notify("Extra");
    private void Filter_OnChanged(object sender, RoutedEventArgs e) => Notify("Extra");
    private void Reset_OnClick(object sender, RoutedEventArgs e) => ResetRequested?.Invoke(this, EventArgs.Empty);
    private void ClearExtra_OnClick(object sender, RoutedEventArgs e) { _updating = true; ClearExtra(); _updating = false; Notify("Extra"); }
}
