using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GwentCompanion.App;

/// <summary>Monitor rectangles and placement are in physical desktop pixels, including negative origins.
/// SetWindowPos lets per-monitor WPF DPI handling apply without mixing one monitor's DIPs with another's.</summary>
internal static class DesktopDisplays
{
    internal sealed record Display(string Id, Rect Bounds, Rect WorkArea, bool Primary);
    internal static IReadOnlyList<Display> All()
    {
        var displays = new List<Display>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect rect, nint state) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info)) displays.Add(new(info.Device, info.Monitor.ToRect(), info.Work.ToRect(), (info.Flags & 1) != 0));
            return true;
        }, 0);
        return displays.OrderByDescending(d => d.Primary).ThenBy(d => d.Bounds.Left).ToArray();
    }
    internal static Rect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).EnsureHandle(), out var bounds)) throw new InvalidOperationException("Could not read the window bounds.");
        return bounds.ToRect();
    }
    internal static Display Nearest(Rect bounds, IReadOnlyList<Display> displays) => displays
        .OrderByDescending(d => { var intersection = Rect.Intersect(bounds, d.Bounds); return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height; })
        .ThenBy(d => Math.Pow(d.Bounds.Left + d.Bounds.Width / 2 - bounds.Left - bounds.Width / 2, 2) + Math.Pow(d.Bounds.Top + d.Bounds.Height / 2 - bounds.Top - bounds.Height / 2, 2)).First();

    internal static Rect Fit(Rect desired, Rect work)
    {
        var width = Math.Min(desired.Width, work.Width); var height = Math.Min(desired.Height, work.Height);
        return new Rect(Math.Clamp(desired.Left, work.Left, work.Right - width), Math.Clamp(desired.Top, work.Top, work.Bottom - height), width, height);
    }
    internal static void Place(Window window, Rect bounds)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).EnsureHandle(), 0, (int)bounds.X, (int)bounds.Y,
            (int)bounds.Width, (int)bounds.Height, 0x0014)) // NOZORDER | NOACTIVATE: do not change the game's focus or stacking policy.
            throw new InvalidOperationException("Windows could not move the workspace.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint state);
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
#pragma warning restore SYSLIB1054
}
