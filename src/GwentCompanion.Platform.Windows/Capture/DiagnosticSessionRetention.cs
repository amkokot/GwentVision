using System.Globalization;
using System.IO;

namespace GwentCompanion.Platform.Windows.Capture;

public sealed record DiagnosticSessionRetentionResult(
    int RetainedSessions,
    int RemovedSessions,
    long RetainedBytes,
    long RemovedBytes);

/// <summary>
/// Keeps the automatically captured diagnostic evidence as a small rolling
/// history. Match records live separately and are not affected by this policy.
/// </summary>
public static class DiagnosticSessionRetention
{
    public const int DefaultMaximumSessions = 5;
    public const long DefaultMaximumBytes = 256L * 1024 * 1024;

    public static DiagnosticSessionRetentionResult Enforce(
        string sessionRoot,
        string? protectedSessionDirectory = null,
        int maximumSessions = DefaultMaximumSessions,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        var root = Path.GetFullPath(sessionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0);

        var protectedPath = string.IsNullOrWhiteSpace(protectedSessionDirectory)
            ? null
            : Path.GetFullPath(protectedSessionDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entries = Directory.EnumerateDirectories(root)
            .Select(path => new SessionEntry(
                Path.GetFullPath(path),
                SessionSortTime(path),
                MeasureDirectory(path)))
            .OrderBy(item => item.LastWriteUtc)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retainedBytes = entries.Sum(item => item.Bytes);
        var removedBytes = 0L;
        var removedSessions = 0;
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (entries.Count > maximumSessions || retainedBytes > maximumBytes)
        {
            // Always leave the newest session available for the most recent game,
            // even when one explicit training recording exceeds the rolling cap.
            var candidate = entries.Take(Math.Max(0, entries.Count - 1)).FirstOrDefault(item =>
                !blocked.Contains(item.Path) &&
                (protectedPath is null || !item.Path.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)));
            if (candidate is null) break;

            if (!IsDirectChild(root, candidate.Path))
                throw new InvalidOperationException("Diagnostic retention selected a directory outside its session root.");
            try
            {
                Directory.Delete(candidate.Path, recursive: true);
                entries.Remove(candidate);
                retainedBytes -= candidate.Bytes;
                removedBytes += candidate.Bytes;
                removedSessions++;
            }
            catch (IOException)
            {
                // An antivirus scan or another app instance may briefly hold a file.
                // Keep it and try another session on the next retention pass.
                blocked.Add(candidate.Path);
            }
            catch (UnauthorizedAccessException)
            {
                blocked.Add(candidate.Path);
            }
        }

        return new(entries.Count, removedSessions, Math.Max(0, retainedBytes), removedBytes);
    }

    private static long MeasureDirectory(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0L; }
                    catch (UnauthorizedAccessException) { return 0L; }
                });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static DateTime SessionSortTime(string directory) =>
        DateTime.TryParseExact(Path.GetFileName(directory), "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp
            : Directory.GetLastWriteTimeUtc(directory);

    private static bool IsDirectChild(string root, string path) =>
        Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(root, StringComparison.OrdinalIgnoreCase) == true;

    private sealed record SessionEntry(string Path, DateTime LastWriteUtc, long Bytes);
}
