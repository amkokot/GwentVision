using System.Runtime.InteropServices;

namespace GwentCompanion.Platform.Windows.Windows;

/// <summary>Read-only pointer/window geometry. Does not hook, move the pointer or send game input.</summary>
public static class PointerObservation
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public static bool InPlayerHand(GwentWindowSnapshot? window)
    {
        if (window is null || window.IsMinimized || GetForegroundWindow() != window.Handle || !GetCursorPos(out var point)) return false;
        var bounds = window.ClientBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var x = (point.X - bounds.Left) / (double)bounds.Width; var y = (point.Y - bounds.Top) / (double)bounds.Height;
        return x is >= .18 and <= .87 && y is >= .84 and <= .995;
    }
}
