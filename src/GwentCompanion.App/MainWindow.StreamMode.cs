using System.Windows;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private StreamModeWindow? _streamModeWindow;

    private void StreamMode_OnClick(object sender, RoutedEventArgs e)
    {
        if (_streamModeWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate(); return;
        }
        var window = new StreamModeWindow(FindDataRoot()) { Owner = this };
        _streamModeWindow = window;
        window.Closed += (_, _) => _streamModeWindow = null;
        window.Show();
    }
}
