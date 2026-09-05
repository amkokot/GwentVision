using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GwentCompanion.Platform.Windows.Native;
using GwentCompanion.Platform.Windows.Windows;

namespace GwentCompanion.Platform.Windows.Capture;

public sealed class Win32FrameCapture : IDisposable
{
    // Avoid repeatedly composing layered windows (CAPTUREBLT), which can disturb
    // cursor composition. Capture visible pixels; never hide/show the cursor.
    public const int RasterOperation = 0x00CC0020; // SRCCOPY only.
    private readonly object _gate = new();
    private nint _memoryDc, _bitmap, _previous;
    private int _width, _height;
    private bool _disposed;

    public BitmapSource Capture(GwentWindowSnapshot window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.IsMinimized) throw new InvalidOperationException("GWENT is minimized and cannot be captured.");
        var bounds = window.ClientBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("GWENT has invalid window dimensions.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var screenDc = NativeMethods.GetDC(nint.Zero);
            if (screenDc == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not open the desktop for capture.");
            try
            {
                if (_bitmap == nint.Zero || _width != bounds.Width || _height != bounds.Height)
                {
                    ReleaseSurface();
                    _memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                    _bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
                    if (_memoryDc == nint.Zero || _bitmap == nint.Zero)
                    {
                        ReleaseSurface();
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not allocate a capture surface.");
                    }
                    _previous = NativeMethods.SelectObject(_memoryDc, _bitmap);
                    if (_previous == nint.Zero || _previous == new nint(-1))
                    {
                        _previous = nint.Zero;
                        ReleaseSurface();
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not select the capture surface.");
                    }
                    _width = bounds.Width; _height = bounds.Height;
                }
                if (!NativeMethods.BitBlt(_memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.Left, bounds.Top, RasterOperation))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not copy the GWENT frame.");
                var source = Imaging.CreateBitmapSourceFromHBitmap(_bitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { NativeMethods.ReleaseDC(nint.Zero, screenDc); }
        }
    }

    private void ReleaseSurface()
    {
        if (_previous != nint.Zero && _memoryDc != nint.Zero) NativeMethods.SelectObject(_memoryDc, _previous);
        if (_bitmap != nint.Zero) NativeMethods.DeleteObject(_bitmap);
        if (_memoryDc != nint.Zero) NativeMethods.DeleteDC(_memoryDc);
        _previous = _bitmap = _memoryDc = nint.Zero;
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; ReleaseSurface(); _disposed = true; }
    }
}
