using System.Diagnostics;
using System.Runtime.InteropServices;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Windows;

internal static class CaptureRegression
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(nint process, int flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public static int Run(int? fixtureProcess = null)
    {
        var windows = new GwentWindowService();
        GwentWindowSnapshot? window;
        if (fixtureProcess is { } id)
        {
            using var fixture = Process.GetProcessById(id);
            if (fixture.ProcessName != "GwentCompanion.App" || !fixture.MainWindowTitle.Contains("OFFLINE REVIEW"))
                throw new InvalidOperationException("Only a verified companion OFFLINE REVIEW window can be used as a capture fixture.");
            window = windows.Snapshot(fixture.MainWindowHandle);
        }
        else window = windows.Find();
        if (window is null || window.IsMinimized || fixtureProcess is null && window.Handle != GetForegroundWindow())
        { Console.WriteLine("SKIP: GWENT must be visible in the foreground; no other app captured."); return 2; }
        if (Win32FrameCapture.RasterOperation != 0x00CC0020) throw new Exception("Layered capture must remain disabled.");
        using var process = Process.GetCurrentProcess();
        using var capture = new Win32FrameCapture();
        var first = capture.Capture(window); var firstPixels = BitmapFrameAdapter.ToPixelFrame(first).BgraPixels;
        var initialHandles = GetGuiResources(process.Handle, 0);
        for (var i = 0; i < 120; i++)
        {
            if (fixtureProcess is null && window.Handle != GetForegroundWindow()) { Console.WriteLine("SKIP: game lost foreground during stress check."); return 2; }
            var bounds = window.ClientBounds;
            // Exercise allocation/reuse/resize without resizing or sending any input to GWENT.
            var testWindow = i % 20 == 0 ? window with { ClientBounds = bounds with { Right = bounds.Right - 8 } } : window;
            var frame = capture.Capture(testWindow);
            if (frame.PixelWidth != testWindow.ClientBounds.Width || frame.PixelHeight != bounds.Height) throw new Exception("Capture size mismatch.");
        }
        var finalHandles = GetGuiResources(process.Handle, 0);
        if (!firstPixels.SequenceEqual(BitmapFrameAdapter.ToPixelFrame(first).BgraPixels)) throw new Exception("Reusing native surface changed a prior frozen frame.");
        if (finalHandles > initialHandles + 3) throw new Exception($"GDI handle leak: {initialHandles} -> {finalHandles}");
        capture.Dispose();
        try { capture.Capture(window); throw new Exception("Capture after disposal must fail."); } catch (ObjectDisposedException) { }
        Console.WriteLine($"PASS 120 passive captures, resize/reuse, frozen-frame immutability and disposal; GDI handles {initialHandles}->{finalHandles}. No files or game input.");
        return 0;
    }
}
