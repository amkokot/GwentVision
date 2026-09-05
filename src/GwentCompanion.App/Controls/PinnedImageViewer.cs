using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App.Controls;

/// <summary>Reuses one decoded bitmap; magnification is a clipped draw, with no capture, crop allocation or popup window.</summary>
public sealed class PinnedImageViewer : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(PinnedImageViewer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public static readonly DependencyProperty ZoomEnabledProperty = DependencyProperty.Register(nameof(ZoomEnabled), typeof(bool), typeof(PinnedImageViewer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public bool ZoomEnabled { get => (bool)GetValue(ZoomEnabledProperty); set => SetValue(ZoomEnabledProperty, value); }
    private Point? _pointer;
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    private static readonly Pen Outline = new(new SolidColorBrush(Color.FromRgb(229, 199, 126)), 1.5);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var viewer = (PinnedImageViewer)d; viewer._pointer = null; viewer.Cursor = viewer.ZoomEnabled ? Cursors.Cross : Cursors.Arrow; }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (ZoomEnabled) { _pointer = e.GetPosition(this); InvalidateVisual(); } }
    protected override void OnMouseLeave(MouseEventArgs e)
    { base.OnMouseLeave(e); _pointer = null; InvalidateVisual(); }
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    { base.OnRenderSizeChanged(info); _pointer = null; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // Hit-test letterboxed area as well as image.
        if (Source is not { } source) return;
        var fit = PinnedZoomLayout.Fit(ActualWidth, ActualHeight, source.PixelWidth, source.PixelHeight);
        if (fit.Width <= 0) return;
        static Rect RectOf(ZoomRect value) => new(value.X, value.Y, value.Width, value.Height);
        dc.DrawImage(source, RectOf(fit));
        if (!ZoomEnabled || _pointer is not { } point || PinnedZoomLayout.At(ActualWidth, ActualHeight, source.PixelWidth, source.PixelHeight, point.X, point.Y) is not { } zoom) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 229, 199, 126)), Outline, RectOf(zoom.Lens));
        var panel = RectOf(zoom.Panel); dc.DrawRectangle(Brushes.Black, null, panel);
        dc.PushClip(new RectangleGeometry(panel));
        var scale = fit.Width / source.PixelWidth * PinnedZoomLayout.Magnification;
        dc.DrawImage(source, new Rect(panel.X - zoom.SourcePixels.X * scale, panel.Y - zoom.SourcePixels.Y * scale,
            source.PixelWidth * scale, source.PixelHeight * scale));
        dc.Pop(); dc.DrawRectangle(null, Outline, panel);
    }
}
