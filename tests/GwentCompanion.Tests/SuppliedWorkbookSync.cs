using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;

internal static class SuppliedWorkbookSync
{
    public static async Task<int> RunAsync(string root, bool apply)
    {
        var files = SuppliedDeckWorkbooks.Find(root);
        var entries = SuppliedDeckWorkbooks.Read(root);
        var links = entries.Select(e => e.DeckUri).Distinct().ToArray();
        var sources = files.Select(path => new { Path = path, Entries = DeckLinkFileReader.Read(path, false).Count }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { Sources = sources, UniqueLinks = links.Length,
            Guides = links.Count(u => u.AbsolutePath.Contains("/guides/")), Apply = apply }));
        if (!apply) return 0;
        var cache = Path.Combine(root, "GwentCompanion", "cache");
        var libraryPath = Path.Combine(cache, "deck-library.json");
        var before = DeckLibrary.Load(libraryPath).Records.Count;
        var backup = libraryPath + ".before-workbook-sync-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".bak";
        if (File.Exists(libraryPath)) File.Copy(libraryPath, backup, overwrite: false);
        var progress = new Progress<DeckCacheProgress>(p =>
        {
            if (p.Completed % 50 == 0 || p.Completed == p.Requested)
                Console.WriteLine($"DECKS {p.Completed}/{p.Requested}: {p.Loaded} valid, {p.Expired} unavailable, {p.Failed} failed");
        });
        var result = await new PlayGwentDeckCacheService().SyncAsync(entries, Path.Combine(cache, "decks"), int.MaxValue, progress);
        // Re-read before merging so custom names/edits made during a long sync survive.
        var library = DeckLibrary.Load(libraryPath);
        library.AddLinks(entries); var merged = library.Merge(result.Decks); library.Save(libraryPath);
        var successful = result.Decks.Select(d => d.SourceUri).ToHashSet();
        var report = new { Sources = sources, result.Requested, result.Downloaded, result.LoadedFromCache,
            result.Expired, result.Errors, Before = before, After = library.Records.Count, Merge = merged, Backup = backup,
            Unavailable = links.Where(link => !successful.Contains(link)).Select(u => u.AbsoluteUri),
            SelfWound = library.Decks.Where(d => d.CountOf("202282") > 0 || d.CountOf("202277") > 0)
                .Select(d => new { d.Id, d.Name, d.SourceUri, d.Leader, d.LastEdited, d.SourceUpdatedAt, d.Patches,
                    Cards = d.Cards.Select(c => new { c.Card.Id, c.Card.Name, c.Count }) }) };
        var output = Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.22-workbook-sync.json");
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE: {before} -> {library.Records.Count} library records; {result.Downloaded} downloaded; {result.Expired} unavailable; {result.Errors.Count} failed. Report: {output}");
        foreach (var error in result.Errors.Take(10)) Console.WriteLine(error);
        return result.Errors.Count == 0 ? 0 : 1;
    }
}
