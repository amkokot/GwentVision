using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GwentCompanion.Platform.Windows.Native;

namespace GwentCompanion.Platform.Windows.Windows;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

public sealed record GwentWindowSnapshot(
    nint Handle,
    int ProcessId,
    string Title,
    PixelRect Bounds,
    PixelRect ClientBounds,
    PixelRect MonitorBounds,
    PixelRect MonitorWorkArea,
    bool IsMinimized,
    bool IsFullscreen)
{
    public string DisplayMode => IsMinimized ? "Minimized" : IsFullscreen ? "Fullscreen / borderless" : "Windowed";
}

public sealed class GwentWindowService
{
    private const int FullscreenTolerance = 3;

    public GwentWindowSnapshot? Find()
    {
        var candidates = new List<WindowCandidate>();
        var knownProcessIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !LooksLikeGwentProcess(process))
                {
                    continue;
                }

                knownProcessIds.Add(process.Id);
                process.Refresh();
                AddCandidate(candidates, process.MainWindowHandle, process.Id, process.MainWindowTitle, 100);
            }
        }

        NativeMethods.EnumWindows((handle, parameter) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || handle == nint.Zero)
            {
                return true;
            }

            _ = parameter;
            _ = NativeMethods.GetWindowThreadProcessId(handle, out var rawProcessId);
            var processId = unchecked((int)rawProcessId);
            if (processId == Environment.ProcessId)
            {
                return true;
            }

            var title = ReadWindowText(handle);
            var className = ReadClassName(handle);
            var isKnownProcess = knownProcessIds.Contains(processId);
            var hasGwentTitle = title.Contains("GWENT", StringComparison.OrdinalIgnoreCase) &&
                                !title.Contains("Companion", StringComparison.OrdinalIgnoreCase);
            var isUnityWindow = className.Contains("UnityWndClass", StringComparison.OrdinalIgnoreCase);
            if (!isKnownProcess && !hasGwentTitle)
            {
                return true;
            }

            var score = (isKnownProcess ? 100 : 0) + (hasGwentTitle ? 50 : 0) + (isUnityWindow ? 10 : 0);
            AddCandidate(candidates, handle, processId, title, score);
            return true;
        }, nint.Zero);

        var best = candidates
            .DistinctBy(candidate => candidate.Handle)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Area)
            .FirstOrDefault();

        return best is null ? null : Snapshot(best.Handle, best.ProcessId, best.Title);
    }

    public GwentWindowSnapshot Snapshot(nint handle)
    {
        return Snapshot(handle, 0, "GWENT");
    }

    public string BuildDiscoveryReport()
    {
        var report = new StringBuilder();
        report.AppendLine($"Captured: {DateTimeOffset.Now:O}");
        report.AppendLine($"Companion PID/session: {Environment.ProcessId}/{Process.GetCurrentProcess().SessionId}");
        report.AppendLine("Visible top-level windows (unrelated titles are redacted):");

        NativeMethods.EnumWindows((handle, parameter) =>
        {
            _ = parameter;
            if (!NativeMethods.IsWindowVisible(handle) ||
                !NativeMethods.GetWindowRect(handle, out var rect) ||
                rect.Right <= rect.Left ||
                rect.Bottom <= rect.Top)
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(handle, out var rawProcessId);
            var processId = unchecked((int)rawProcessId);
            var processName = ReadProcessName(processId);
            var className = ReadClassName(handle);
            var title = ReadWindowText(handle);
            var relevantTitle = title.Contains("GWENT", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Witcher", StringComparison.OrdinalIgnoreCase);
            var displayedTitle = relevantTitle ? title : "<redacted>";
            report.AppendLine(
                $"  PID {processId}; process={processName}; class={className}; " +
                $"rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; title={displayedTitle}");
            return true;
        }, nint.Zero);

        report.AppendLine("Relevant running processes:");
        foreach (var process in Process.GetProcesses().OrderBy(item => item.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Contains("gwent", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Contains("witcher", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Contains("galaxy", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Contains("redlauncher", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                    {
                        report.AppendLine(
                            $"  PID {process.Id}; session={process.SessionId}; process={process.ProcessName}; " +
                            $"mainWindow=0x{process.MainWindowHandle:X}");
                    }
                }
                catch (Exception exception)
                {
                    report.AppendLine($"  PID {process.Id}; inspection failed: {exception.GetType().Name}");
                }
            }
        }

        return report.ToString();
    }

    public void ArrangeWindowForSidebar(GwentWindowSnapshot window, int sidebarWidth, int gap = 8)
    {
        if (window.IsFullscreen)
        {
            throw new InvalidOperationException(
                "GWENT is fullscreen. Change it to Windowed or Borderless Windowed in the game settings before arranging it.");
        }

        var work = window.MonitorWorkArea;
        var frameWidth = Math.Max(0, window.Bounds.Width - window.ClientBounds.Width);
        var frameHeight = Math.Max(0, window.Bounds.Height - window.ClientBounds.Height);
        var availableOuterWidth = Math.Max(800, work.Width - sidebarWidth - gap);
        var clientWidth = Math.Max(640, availableOuterWidth - frameWidth);
        var clientHeight = (int)Math.Round(clientWidth * 9d / 16d);
        if (clientHeight + frameHeight > work.Height)
        {
            clientHeight = Math.Max(360, work.Height - frameHeight);
            clientWidth = (int)Math.Round(clientHeight * 16d / 9d);
        }

        var width = clientWidth + frameWidth;
        var height = clientHeight + frameHeight;

        var left = work.Left;
        var top = work.Top + Math.Max(0, (work.Height - height) / 2);
        if (!NativeMethods.SetWindowPos(
                window.Handle,
                nint.Zero,
                left,
                top,
                width,
                height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not resize the GWENT window.");
        }
    }

    private static GwentWindowSnapshot Snapshot(nint handle, int processId, string title)
    {
        var bounds = GetExtendedBounds(handle);
        var clientBounds = GetClientBounds(handle);
        var monitorHandle = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitorHandle == nint.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not inspect the GWENT monitor.");
        }

        var monitor = ToPixelRect(monitorInfo.Monitor);
        var work = ToPixelRect(monitorInfo.Work);
        var fullscreen = Math.Abs(bounds.Left - monitor.Left) <= FullscreenTolerance &&
                         Math.Abs(bounds.Top - monitor.Top) <= FullscreenTolerance &&
                         Math.Abs(bounds.Right - monitor.Right) <= FullscreenTolerance &&
                         Math.Abs(bounds.Bottom - monitor.Bottom) <= FullscreenTolerance;

        return new GwentWindowSnapshot(
            handle,
            processId,
            title,
            bounds,
            clientBounds,
            monitor,
            work,
            NativeMethods.IsIconic(handle),
            fullscreen);
    }

    private static PixelRect GetExtendedBounds(nint window)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaExtendedFrameBounds,
                out var extended,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return ToPixelRect(extended);
        }

        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the GWENT window bounds.");
        }

        return ToPixelRect(rect);
    }

    private static PixelRect GetClientBounds(nint window)
    {
        if (!NativeMethods.GetClientRect(window, out var clientRect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the GWENT client bounds.");
        }

        var origin = new NativePoint { X = clientRect.Left, Y = clientRect.Top };
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not map the GWENT client area to the screen.");
        }

        return new PixelRect(
            origin.X,
            origin.Y,
            origin.X + clientRect.Right - clientRect.Left,
            origin.Y + clientRect.Bottom - clientRect.Top);
    }

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static bool LooksLikeGwentProcess(Process process)
    {
        try
        {
            return process.ProcessName.Equals("Gwent", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddCandidate(
        ICollection<WindowCandidate> candidates,
        nint handle,
        int processId,
        string title,
        int score)
    {
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle) || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return;
        }

        var area = Math.Max(0L, (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top));
        if (area == 0)
        {
            return;
        }

        candidates.Add(new WindowCandidate(handle, processId, title, score, area));
    }

    private static string ReadWindowText(nint handle)
    {
        var text = new StringBuilder(512);
        _ = NativeMethods.GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private static string ReadClassName(nint handle)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassName(handle, className, className.Capacity);
        return className.ToString();
    }

    private static string ReadProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private sealed record WindowCandidate(nint Handle, int ProcessId, string Title, int Score, long Area);
}

internal static class ProcessExtensions
{
    internal static DateTime StartTimeSafe(this Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
