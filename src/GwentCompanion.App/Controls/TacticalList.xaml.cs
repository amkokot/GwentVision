using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App.Controls;

public sealed record TacticalAction(TacticalWatchRow Row, string Action);
public sealed record TacticalItem(TacticalWatchRow Row)
{
    public string Name => Row.Rule.Card.Name;
    public string Kind => Row.Rule.Summon ? "SUMMON" : "BONUS";
    public string Status => Row.Label;
    public string Promotion => Row.Promotion;
    public Brush Color => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Row.State switch
    {
        TacticalState.Missed or TacticalState.NotMet => "#FF8585", TacticalState.Ready or TacticalState.Seen => "#85D7B1",
        TacticalState.Pending => "#E5C77E", TacticalState.Unavailable => "#87909B", _ => "#CCD4DD"
    }));
}

public partial class TacticalList : UserControl
{
    private IReadOnlyList<TacticalWatchRow> _rows = [];
    private bool _changing;
    private bool _detailOnly;
    private readonly TacticalDismissals _dismissed = new();
    public event EventHandler<TacticalAction>? ActionRequested;
    public TacticalList() { InitializeComponent(); }
    public void ClearSearch() => Search.Clear();
    public bool IsDismissed(TacticalWatchRow row) => _dismissed.Contains(row);
    public void Dismiss(TacticalWatchRow row) { _dismissed.Hide(row); Filter(); }
    public void RestoreDismissed() { _dismissed.Restore(_rows); Filter(); }
    public void ResetDismissed() { _dismissed.Reset(); Filter(); }
    public void UseDetailOnly()
    {
        if (_detailOnly) return;
        _detailOnly = true;
        ListHeader.Visibility = SearchPanel.Visibility = Cards.Visibility = Visibility.Collapsed;
        Layout.RowDefinitions[2].MinHeight = 0;
        Layout.RowDefinitions[2].Height = new GridLength(0);
        Layout.Margin = new Thickness(3, 0, 3, 0);
        DetailPanel.Margin = new Thickness(0);
    }
    private void Dismiss_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TacticalItem item }) { _dismissed.Hide(item.Row); Filter(); }
        e.Handled = true;
    }
    private void RestoreHidden_OnClick(object sender, RoutedEventArgs e) { _dismissed.Restore(_rows); Filter(); }
    public void SetMixedRows(IReadOnlyList<TacticalWatchRow> rows)
    {
        Legend.Text = "Green: ready/resolved · red: inactive or reviewed miss. Unknown is not absent.";
        var sorted = rows.OrderByDescending(row => row.State == TacticalState.Missed || row.State == TacticalState.Ready)
            .ThenByDescending(row => row.Promoted).ThenBy(row => row.Rule.Card.Name).ThenByDescending(row => row.Rule.Summon).ToArray();
        if (_rows.SequenceEqual(sorted)) return;
        _rows = sorted; Filter();
    }
    public void SetRows(IReadOnlyList<TacticalWatchRow> rows, bool summons)
    {
        Legend.Visibility = Visibility.Collapsed;
        KindFilter.Visibility = Visibility.Collapsed;
        KindFilter.SelectedIndex = 0;
        CategoryColumn.Width = new GridLength(0);
        Legend.Text = summons ? "GREEN = ready/resolved · RED = inactive or reviewed absence · AMBER = trigger seen. Unknown never means absent."
            : "GREEN = this bonus condition is met, not proof of a card in hand or a fully legal play. Unsupported conditions stay unknown.";
        var sorted = rows.OrderByDescending(row => row.State == TacticalState.Missed || row.State == TacticalState.Ready)
            .ThenByDescending(row => row.Promoted).ThenBy(row => row.Rule.Card.Name).ToArray();
        if (_rows.SequenceEqual(sorted)) return;
        _rows = sorted; Filter();
    }
    public void SelectCard(string id)
    {
        KindFilter.SelectedIndex = 0;
        Search.Clear();
        Cards.SelectedItem = Cards.Items.OfType<TacticalItem>().FirstOrDefault(item => item.Row.Rule.Card.Id == id);
        if (Cards.SelectedItem is not null) Cards.ScrollIntoView(Cards.SelectedItem);
    }
    private void Search_OnChanged(object sender, TextChangedEventArgs e) { if (Cards is not null) Filter(); }
    private void KindFilter_OnChanged(object sender, SelectionChangedEventArgs e) { if (Cards is not null) Filter(); }
    private void Layout_OnSizeChanged(object sender, SizeChangedEventArgs e) { if (DetailPanel is not null) DetailPanel.MaxHeight = _detailOnly ? 260 : Math.Max(90, Math.Min(260, ActualHeight * .48)); }
    private void Filter()
    {
        var selected = (Cards.SelectedItem as TacticalItem)?.Row.Rule;
        var query = Search.Text.Trim();
        _changing = true;
        Cards.ItemsSource = _rows.Where(row => !_dismissed.Contains(row))
            .Where(row => KindFilter.SelectedIndex == 0 || row.Rule.Summon == (KindFilter.SelectedIndex == 1))
            .Where(row => (row.Rule.Card.Name + " " + row.RequirementText()).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(row => new TacticalItem(row)).ToArray();
        Cards.SelectedItem = Cards.Items.OfType<TacticalItem>().FirstOrDefault(item => item.Row.Rule.Key == selected?.Key);
        var hidden = _rows.Count(row => _dismissed.Contains(row));
        RestoreHidden.Content = $"Restore hidden ({hidden})";
        RestoreHidden.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        _changing = false; RenderDetail();
    }
    private void Cards_OnChanged(object sender, SelectionChangedEventArgs e) { if (!_changing) RenderDetail(); }
    private void RenderDetail()
    {
        Actions.Children.Clear();
        if (Cards.SelectedItem is not TacticalItem item) { DetailPanel.Visibility = Visibility.Collapsed; Detail.Text = "Select a card for its trigger and evidence."; ActionHelp.Text = ""; return; }
        DetailPanel.Visibility = Visibility.Visible;
        var row = item.Row;
        Detail.Text = row.Rule.Card.Name + " · " + row.Label + "\n" + (row.Promotion.Length > 0 ? row.Promotion + "\n" : "") + row.Detail;
        void Add(string text, string action)
        {
            var button = new Button { Content = text, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 4, 4), FontSize = 10 };
            button.Click += (_, _) => ActionRequested?.Invoke(this, new(row, action)); Actions.Children.Add(button);
        }
        if (row.Rule.Summon) { Add("Prioritize", "keep"); Add("Clear priority", "unkeep"); }
        if (row.Rule.Condition != TacticalCondition.Unknown || row.Rule.LocalCondition)
        { Add("Condition met", "met"); Add("Not met now", "notmet"); Add("Clear condition", "clearcondition"); }
        if (row.Rule.Summon)
        {
            if (!row.Rule.Random) Add("Review no arrival…", "missed");
            Add("Arrival seen", "arrived"); Add("Not expected", "unavailable"); Add("Clear review", "clearreview");
        }
        if (_detailOnly) { Add("Hide " + row.Rule.Card.Name, "hide"); Add("Close details", "close"); }
        ActionHelp.Text = "Condition checks expire after 30 seconds or a new play. A met condition does not prove the card is in hand or its full play is legal." +
            (row.Rule.Summon ? " Arrival reviews do not add original deck cards." : "");
    }
}
internal static class TacticalRowText
{
    internal static string RequirementText(this TacticalWatchRow row) => row.Rule.Requirement;
}
