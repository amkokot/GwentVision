using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace GwentCompanion.App.Controls;

public partial class DeckCardList : UserControl
{
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(DeckCardList), new PropertyMetadata(38d));
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public static readonly DependencyProperty ShowDetailsProperty = DependencyProperty.Register(nameof(ShowDetails), typeof(bool), typeof(DeckCardList), new PropertyMetadata(true));
    public bool ShowDetails { get => (bool)GetValue(ShowDetailsProperty); set => SetValue(ShowDetailsProperty, value); }
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(IEnumerable),
        typeof(DeckCardList), new PropertyMetadata(null));
    public IEnumerable? Rows { get => (IEnumerable?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public DeckCardList() => InitializeComponent();
    public event EventHandler<object>? RowActivated;
    public event EventHandler<object>? RowInspected;
    public void HighlightRows(IEnumerable<object> rows)
    {
        CardRows.SelectionMode = SelectionMode.Multiple;
        CardRows.SelectedItems.Clear();
        foreach (var row in rows) CardRows.SelectedItems.Add(row);
    }
    private void Row_OnInspected(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: { } row }) RowInspected?.Invoke(this, row);
    }
    private void Row_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: { } row }) RowActivated?.Invoke(this, row);
    }
}
