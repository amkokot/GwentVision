namespace GwentCompanion.Core.Data;

public sealed record RemovedPinnedView(string OriginalPath, byte[]? Contents, DateTime LastWriteUtc);

/// <summary>Direct PNG children only. Deleted files leave no hidden disk copies; one bounded undo lives in the UI.</summary>
public sealed class PinnedViewStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public const int UndoByteLimit = 16 * 1024 * 1024;
    public IReadOnlyList<string> List() => Directory.Exists(DirectoryPath)
        ? Directory.GetFiles(DirectoryPath, "*.png", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    public RemovedPinnedView Remove(string path)
    {
        var source = Validate(path, DirectoryPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Pinned view no longer exists.", source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        byte[]? contents;
        using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            contents = file.Length <= UndoByteLimit ? new byte[(int)file.Length] : null;
            if (contents is not null) file.ReadExactly(contents);
            File.Delete(source);
        }
        return new(source, contents, timestamp);
    }
    public string Restore(RemovedPinnedView removed)
    {
        var target = Validate(removed.OriginalPath, DirectoryPath);
        if (removed.Contents is null) throw new InvalidOperationException("This image exceeded the in-memory Undo limit.");
        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) file.Write(removed.Contents);
        File.SetLastWriteTimeUtc(target, removed.LastWriteUtc);
        return target;
    }
    public string Save(Action<Stream> write)
    {
        Directory.CreateDirectory(DirectoryPath);
        var index = List().Select(path => Path.GetFileNameWithoutExtension(path)).Where(name => name.StartsWith("pinned_", StringComparison.OrdinalIgnoreCase))
            .Select(name => long.TryParse(name.AsSpan(7), out var value) && value > 0 ? value : 0).DefaultIfEmpty().Max();
        while (index < long.MaxValue)
        {
            var path = Validate(Path.Combine(DirectoryPath, $"pinned_{++index}.png"), DirectoryPath);
            FileStream file;
            try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            try { using (file) write(file); return path; }
            catch { file.Dispose(); File.Delete(path); throw; } // Only the new incomplete capture is removed.
        }
        throw new IOException("Pinned screenshot numbering exhausted.");
    }
    private static string Validate(string path, string parent)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only a pinned PNG in its exact folder is allowed.");
        if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint) ||
            File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Pinned paths must not be links.");
        return full;
    }
}
