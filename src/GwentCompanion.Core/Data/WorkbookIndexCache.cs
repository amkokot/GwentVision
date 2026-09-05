using System.Text.Json;

namespace GwentCompanion.Core.Data;

/// <summary>Derived workbook index. Spreadsheet XML is reread only when a source file changes.</summary>
public static class WorkbookIndexCache
{
    private const int Version = 1;

    public static DeckIndexEntry[] LoadOrRead(string gameRoot, string cachePath)
    {
        var inputs = Inputs(gameRoot);
        try
        {
            if (File.Exists(cachePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(cachePath)) is { Version: Version } state &&
                state.Inputs.SequenceEqual(inputs)) return state.Entries;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { /* Disposable derived data: rebuild below. */ }

        var entries = SuppliedDeckWorkbooks.Read(gameRoot);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State(Version, inputs, entries)));
            File.Move(temporary, cachePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Index remains usable in memory. */ }
        finally
        {
            if (temporary is not null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return entries;
    }

    private static SourceStamp[] Inputs(string gameRoot) => SuppliedDeckWorkbooks.Find(gameRoot)
        .SelectMany(path => new[] { path, Path.Combine(Path.GetDirectoryName(path)!, "deck-patch-overrides.json") })
        .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => { var file = new FileInfo(path); return new SourceStamp(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks); })
        .ToArray();

    private sealed record SourceStamp(string Path, long Length, long LastWriteUtcTicks);
    private sealed record State(int Version, SourceStamp[] Inputs, DeckIndexEntry[] Entries);
}
