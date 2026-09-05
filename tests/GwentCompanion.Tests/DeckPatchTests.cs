using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

internal static class DeckPatchTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var at = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        Check(DeckPatchMetadata.Current(at).Label == "14.8" && DeckPatchMetadata.Current(at.AddMonths(1)).Label == "14.9", "Monthly patch rollover.");
        Check(DeckPatchMetadata.Current(DateTimeOffset.Parse("2027-01-01T00:00:00Z")).Label == "15.1", "Annual patch rollover.");
        Check(DeckPatchMetadata.FromSheet("11.10.2").Single() is { Label: "11.10.2", Inferred: false }, "Keep explicit historical subpatches.");
        Check(DeckPatchMetadata.FromSheet("JANFEB 2026").Select(p => p.Label).SequenceEqual(new[] { "14.1", "14.2" }), "Multi-month sheet remains ambiguous.");
        Check(DeckPatchMetadata.FromSheet("JANFEB 2026", at.AddMonths(-7)).Single().Label == "14.1", "Row date can resolve multi-month tab.");
        Check(DeckPatchMetadata.FromSheet("All Decks").Length == 0, "Do not pretend undated historical rows are current.");
        var notes = Path.Combine(root, "notes");
        var reader = new WorkbookDeckIndexReader();
        var shin = reader.Read(Path.Combine(notes, "Shinmiri2's Gwent Decks - twitch.tv_shinmiri2.xlsx"));
        Check(shin.Entries.Any(entry => entry.Sheet == "14.8"), "Saved newest worksheet found.");
        Check(shin.Entries.Where(entry => entry.Sheet == "14.8").All(entry => entry.Patches!.Single() is { Label: "14.8", Inferred: false }), "Shinmiri worksheet patches extracted.");
        var kerp = reader.Read(Path.Combine(notes, "Kerp's Library.xlsx"));
        Check(kerp.Entries.Count > 0 && kerp.Entries.All(entry => entry.Patches!.Single() is { Label: "14.8", Inferred: true }), "User correction overrides Kerp's misleading 10.10 tab.");
        var qcento = reader.Read(Path.Combine(notes, "Qcento's Decklists.xlsx"));
        Check(qcento.Entries.Any(entry => entry.Patches?.Any(p => p.Label == "13.12") == true), "Qcento month tabs converted.");
        var generic = DeckLinkFileReader.FromText("https://www.playgwent.com/en/decks/1234567890abcdef1234567890abcdef").Single();
        Check(generic.Patches!.Single().Label == DeckPatchMetadata.Current(DateTimeOffset.Now).Label, "General uploader defaults current.");
        var deck = new DeckDefinition("patch-test", "Test", "Neutral", "", 0, [new(new("test", "Test", "Neutral", CardKind.Unit, 4))], Patches: shin.Entries.First(entry => entry.Sheet == "14.8").Patches);
        var library = new DeckLibrary(); library.Merge([deck, deck with { Patches = [new("14.7", false, "Earlier worksheet")] }]);
        Check(library.Decks.Single().Patches!.Select(p => p.Label).Distinct().Count() == 2, "Merging duplicate decks retains patch history without increasing deck count.");
        var directory = Path.Combine(Path.GetTempPath(), "gv-patch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "library.json"); library.Save(path);
            var restored = DeckLibrary.Load(path);
            Check(restored.Decks.Single().Patches!.Count == 2, "Patch history round-trip.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"Patch audit: Shinmiri {shin.Entries.Count} rows; Kerp {kerp.Entries.Count} rows (override); Qcento {qcento.Entries.Count} rows.");
    }
}
