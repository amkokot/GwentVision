using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private RemovedPinnedView? _removedPin;
    private PinnedViewStore PinnedStore => new(ResolvePinnedCaptureDirectory());
    private string SavePinned(BitmapSource source) => PinnedStore.Save(stream =>
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream);
    });
    private void Camera_OnClick(object sender, RoutedEventArgs e)
    {
        if (_reviewEvidencePath is not null || !RequireGameWindow(out var window)) return;
        try
        {
            // Always capture NOW. Do not pin a stale analysis preview.
            var source = _frameCapture.Capture(window);
            _lastPreview = source; PreviewImage.Source = source; PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PinCurrentButton.IsEnabled = true;
            var path = SavePinned(source); RefreshPinnedCaptures(path);
            FooterStatusText.Text = "View pinned · open the pin icon to inspect or remove it.";
        }
        catch (Exception exception) { FooterStatusText.Text = "Capture not pinned: " + exception.Message; }
    }
    private void RemovePinned_OnClick(object sender, RoutedEventArgs e)
    { if (PinnedCaptureList.SelectedItem is PinnedCaptureItem item) RemovePin(item.Path); }
    private void RemovePinnedRow_OnClick(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string path }) RemovePin(path); }
    private void PinnedCaptureList_OnKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Delete) { RemovePinned_OnClick(sender, e); e.Handled = true; } }
    private void RemovePin(string path)
    {
        if (_reviewEvidencePath is not null && !HasIsolatedPinnedReview) { FooterStatusText.Text = "Pinned views are read-only in offline review."; return; }
        try
        {
            _removedPin = PinnedStore.Remove(path); RefreshPinnedCaptures(); UndoPinnedButton.IsEnabled = _removedPin.Contents is not null;
            FooterStatusText.Text = _removedPin.Contents is not null ? "PNG deleted from disk · Undo available until the next removal or app exit." :
                "PNG permanently deleted · image exceeded the 16 MiB Undo limit.";
        }
        catch (Exception exception) { FooterStatusText.Text = "Could not remove view: " + exception.Message; }
    }
    private void UndoPinned_OnClick(object sender, RoutedEventArgs e)
    {
        if (_removedPin is null) return;
        try
        {
            var restored = PinnedStore.Restore(_removedPin); _removedPin = null;
            RefreshPinnedCaptures(restored); UndoPinnedButton.IsEnabled = false;
            FooterStatusText.Text = "Pinned view restored.";
        }
        catch (Exception exception) { FooterStatusText.Text = "Could not restore view: " + exception.Message; }
    }
    private static string? IsolatedPinnedReviewDirectory()
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--review-pinned-root");
        if (Array.IndexOf(args, "--review-evidence") < 0 || index < 0 || index + 1 >= args.Length) return null;
        var requested = Path.GetFullPath(args[index + 1]);
        var allowed = Path.GetFullPath(Path.Combine(FindDataRoot(), "diagnostics", "ui-pinned-fixture"));
        if (!requested.Equals(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Pinned review writes require the isolated diagnostics/ui-pinned-fixture directory.");
        return requested;
    }
    private static bool HasIsolatedPinnedReview => IsolatedPinnedReviewDirectory() is not null;
    private void PinnedZoom_OnChanged(object sender, RoutedEventArgs e)
    {
        if (PinnedCaptureImage is null) return;
        PinnedCaptureImage.ZoomEnabled = PinnedZoomButton.IsChecked == true;
        PinnedZoomStatus.Text = PinnedCaptureImage.ZoomEnabled ? "Zoom on · hover over the image · 3×" : "Zoom off";
    }
}
