using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App.Controls;

/// <summary>Independent faction scales, actual match indices, and gaps for unavailable observations.</summary>
public sealed class MatchRatingChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(MatchRatingSeries),
        typeof(MatchRatingChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public MatchRatingSeries? Series { get => (MatchRatingSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private readonly List<(Point Position, MatchRatingPoint Value)> _hits = [];
    private static readonly Brush Muted = FactionPalette.Brush("#B5BFBC");
    public MatchRatingChart() { MouseMove += Hover; MouseLeave += (_, _) => ToolTip = null; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hits.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Series is not { } series || ActualWidth < 130) return;
        var accent = FactionPalette.Brush(FactionPalette.For(series.Faction).Accent);
        void Text(string value, double x, double y, Brush? color = null, double size = 11) => dc.DrawText(
            new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
                color ?? Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        Text(series.Faction, 0, 0, accent, 16);
        var ratings = series.Points.Where(p => p.Rating.HasValue).ToArray();
        Text($"{series.Points.Length} games · {ratings.Length} plotted ratings", 0, 26);
        if (ratings.Length == 0)
        {
            Text(series.Points.Length == 0 ? "Play this faction to start its rating history." : "No ordered faction-MMR readings yet.", 0, 100);
            if (series.UnorderedGames > 0) Text($"{series.UnorderedGames} older games have no same-day order.", 0, 123);
            return;
        }
        var low = (int)Math.Floor((ratings.Min(p => p.Rating!.Value) - 5) / 10d) * 10;
        var high = (int)Math.Ceiling((ratings.Max(p => p.Rating!.Value) + 5) / 10d) * 10;
        const double left = 48, top = 58;
        var width = ActualWidth - left - 16; var height = ActualHeight - top - 48;
        var grid = new Pen(FactionPalette.Brush("#36443F"), 1);
        foreach (var value in new[] { low, (low + high) / 2, high })
        {
            var y = top + height * (high - value) / (high - low);
            dc.DrawLine(grid, new(left, y), new(left + width, y)); Text(value.ToString(), 0, y - 7);
        }
        Point Position(MatchRatingPoint p) => new(left + width * (p.Game - 1) / Math.Max(1, series.Points.Length - 1),
            top + height * (high - p.Rating!.Value) / (high - low));
        MatchRatingPoint? previous = null;
        var stroke = new Pen(accent, 2);
        foreach (var p in series.Points)
        {
            if (p.Rating is null) { previous = null; continue; }
            var point = Position(p);
            if (previous is not null && previous.Patch == p.Patch) dc.DrawLine(stroke, Position(previous), point);
            dc.DrawEllipse(accent, null, point, 3, 3); _hits.Add((point, p)); previous = p;
        }
        Text("1", left - 3, top + height + 8);
        if (series.Points.Length > 1) Text(series.Points.Length.ToString(), left + width - 12, top + height + 8);
        Text("Games played with this faction", left + 20, ActualHeight - 17);
        System.Windows.Automation.AutomationProperties.SetName(this,
            $"{series.Faction}: {series.Points.Length} games, {ratings.Length} ratings, range {ratings.Min(p => p.Rating)} to {ratings.Max(p => p.Rating)} faction MMR.");
    }
    private void Hover(object sender, MouseEventArgs e)
    {
        if (_hits.Count == 0) { ToolTip = null; return; }
        var position = e.GetPosition(this);
        var nearest = _hits.MinBy(p => (p.Position - position).LengthSquared);
        ToolTip = nearest.Value is not null && (nearest.Position - position).Length < 20
            ? $"Game {nearest.Value.Game} · {nearest.Value.Date:dd MMM yyyy}\n{nearest.Value.Rating} faction MMR · patch {nearest.Value.Patch}" : null;
    }
}
