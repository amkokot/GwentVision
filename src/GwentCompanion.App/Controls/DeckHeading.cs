using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App.Controls;

/// <summary>Shared, compact deck-result identity; cached ability art only, never a network request.</summary>
public sealed class DeckHeading : UserControl
{
    public static readonly DependencyProperty TitleProperty = Field(nameof(Title));
    public static readonly DependencyProperty SubtitleProperty = Field(nameof(Subtitle));
    public static readonly DependencyProperty FactionProperty = Field(nameof(Faction));
    public static readonly DependencyProperty LeaderProperty = Field(nameof(Leader));
    private static DependencyProperty Field(string name) => DependencyProperty.Register(name, typeof(string), typeof(DeckHeading), new PropertyMetadata(null, Changed));
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Faction { get => (string?)GetValue(FactionProperty); set => SetValue(FactionProperty, value); }
    public string? Leader { get => (string?)GetValue(LeaderProperty); set => SetValue(LeaderProperty, value); }
    private readonly Border _surface = new() { Padding = new Thickness(7, 5, 7, 5), BorderThickness = new Thickness(3, 0, 0, 0) };
    private readonly Image _icon = new() { Width = 35, Height = 42, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _title = new() { Foreground = FactionPalette.Brush("#F1F3F5"), FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { Foreground = FactionPalette.Brush("#CAD0D7"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private static readonly Dictionary<string, ImageSource?> Images = new();
    private static readonly Lazy<(string? Root, Dictionary<string, string> Leaders)> ArtIndex = new(() =>
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var cache = Path.Combine(directory.FullName, "GwentCompanion", "cache");
                if (!File.Exists(Path.Combine(cache, "gwent-one-cards.json"))) continue;
                try { return (cache, GwentOneCardCatalog.StartingLeaders(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json")))
                    .ToDictionary(c => c.Faction + "|" + c.Name, c => c.Id)); }
                catch { return (null, new()); }
            }
        return (null, new());
    });
    public DeckHeading()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(_title); text.Children.Add(_subtitle);
        Grid.SetColumn(text, 1); grid.Children.Add(_icon); grid.Children.Add(text); _surface.Child = grid; Content = _surface;
        SizeChanged += (_, _) => UpdateIconVisibility(); Refresh();
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((DeckHeading)target).Refresh();
    private void Refresh()
    {
        var palette = FactionPalette.For(Faction); _surface.Background = FactionPalette.Brush(palette.Surface); _surface.BorderBrush = FactionPalette.Brush(palette.Accent);
        _title.Text = Title ?? ""; _subtitle.Text = Subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Source = LeaderArt(Faction, Leader); UpdateIconVisibility();
    }
    private void UpdateIconVisibility() => _icon.Visibility = ActualWidth >= 245 && _icon.Source is not null ? Visibility.Visible : Visibility.Collapsed;
    internal static ImageSource? LeaderArt(string? faction, string? leader)
    {
        if (string.IsNullOrWhiteSpace(leader) || string.IsNullOrWhiteSpace(faction)) return null;
        var key = faction + "|" + leader;
        if (Images.TryGetValue(key, out var image)) return image;
        image = null;
        if (ArtIndex.Value.Root is { } root && ArtIndex.Value.Leaders.TryGetValue(key, out var id))
        {
            var path = Path.Combine(root, "leader-portraits", id + ".jpg");
            try { if (File.Exists(path)) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 70; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); image = bitmap; } }
            catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException) { }
        }
        Images[key] = image; return image;
    }
}
