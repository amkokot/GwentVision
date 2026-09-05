using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GwentCompanion.Core.Simulation;

namespace GwentCompanion.App.Controls;

/// <summary>Small vector histogram: at most 32 bars, no images, animations or external chart runtime.</summary>
public sealed class PointHistogram : FrameworkElement
{
    public static readonly DependencyProperty DistributionProperty = DependencyProperty.Register(nameof(Distribution), typeof(PointDistribution), typeof(PointHistogram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public PointDistribution? Distribution { get => (PointDistribution?)GetValue(DistributionProperty); set => SetValue(DistributionProperty, value); }
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(int?), typeof(PointHistogram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public int? Threshold { get => (int?)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Distribution is not { Bins.Count: > 0 } distribution || ActualWidth < 50) return;
        var min = distribution.Bins.Min(bin => bin.Points); var max = distribution.Bins.Max(bin => bin.Points);
        var buckets = Math.Min(32, max - min + 1); var width = (ActualWidth - 4) / buckets; var height = Math.Max(12, ActualHeight - 22);
        var grouped = distribution.Bins.GroupBy(bin => Math.Min(buckets - 1, (bin.Points - min) * buckets / Math.Max(1, max - min + 1)));
        var peak = Math.Max(.05, grouped.Max(group => group.Sum(bin => bin.Probability)));
        var blue = new SolidColorBrush(Color.FromRgb(111, 175, 211)); var green = new SolidColorBrush(Color.FromRgb(111, 209, 164));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(64, 78, 73)), 1), new(0, height), new(ActualWidth, height));
        foreach (var group in grouped)
        {
            var barHeight = height * group.Sum(bin => bin.Probability) / peak;
            dc.DrawRoundedRectangle(Threshold is { } gap && group.All(bin => bin.Points > gap) ? green : blue, null,
                new Rect(2 + group.Key * width, height - barHeight, Math.Max(1, width - 2), barHeight), 1, 1);
        }
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        Label(min.ToString(), 1, height + 3); Label(max + " pts", Math.Max(32, ActualWidth - 40), height + 3);
        Label($"peak {peak:P0}", Math.Max(0, ActualWidth / 2 - 26), height + 3);
    }
}
