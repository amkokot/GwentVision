namespace GwentCompanion.Core.Data;

public static class SuppliedDeckWorkbooks
{
    public static string[] Find(string gameRoot) => new[]
        { Path.Combine(gameRoot, "notes"), Path.Combine(gameRoot, "GwentCompanion", "notes") }
        .Where(Directory.Exists).SelectMany(directory => Directory.EnumerateFiles(directory, "*.xlsx"))
        .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
        .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    public static DeckIndexEntry[] Read(string gameRoot) => Find(gameRoot)
        .SelectMany(path => DeckLinkFileReader.Read(path, assumeCurrentPatch: false)).ToArray();
}
