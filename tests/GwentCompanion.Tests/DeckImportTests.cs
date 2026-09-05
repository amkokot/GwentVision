using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class DeckImportTests
{
    private const string Hash = "1234567890abcdef1234567890abcdef";
    private static readonly Uri Url = new("https://www.playgwent.com/en/decks/" + Hash);
    private static CardDefinition Card(string id, bool gold = false) => new(id, id, "Nilfgaard", CardKind.Unit, 4, IsGold: gold,
        CardCategories: new HashSet<string> { "Soldier" }, SecondaryFactionNames: new HashSet<string> { "Syndicate" }, AbilityText: "Test text");
    private static DeckDefinition Deck(string id = Hash) => new(id, "Original", "Nilfgaard", "Enslave", 15,
        Enumerable.Range(0, 25).Select(i => new DeckCard(Card("Card " + i))).ToArray(), Url, DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "gv-import-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    public static void Library()
    {
        var library = new DeckLibrary(); var original = Deck();
        var copy = original with { Id = "abcdefabcdefabcdefabcdefabcdefab", Name = "Alternate label", Cards = original.Cards.Reverse().ToArray(),
            SourceUri = new Uri("https://www.playgwent.com/en/decks/abcdefabcdefabcdefabcdefabcdefab") };
        Check(library.Merge([original, copy]) == new LibraryMergeResult(1, 1), "Exact duplicate composition must merge despite order/name/link.");
        library.Rename(original.Id, "My named list"); library.Merge([copy with { Name = "New import label" }]);
        Check(library.Find(copy.Id)?.Deck.Name == "My named list", "Reimport must preserve custom rename and alias resolution.");
        Check(library.Decks.Length == 1 && library.Records[0].Sources.Length == 2, "Duplicates must not overweight inference; retain both source links.");
        Check(DeckLibrary.Fingerprint(original) == DeckLibrary.Fingerprint(original with { Cards = original.Cards.Select(item => item with { Card = item.Card with { Provision = 9 } }).ToArray() }), "Balance changes are not composition changes.");
        Check(DeckLibrary.Fingerprint(original) != DeckLibrary.Fingerprint(original with { Leader = "Imprisonment" }), "Leader ability is part of identity.");
        Check(DeckLibrary.Fingerprint(original) != DeckLibrary.Fingerprint(original with { Faction = "Monsters" }), "Faction is part of identity.");
        var changed = original with { Cards = original.Cards.Append(new DeckCard(Card("Card 0"))).ToArray() };
        Check(library.Merge([changed]).Added == 1, "Quantity change must create a variant without overwriting existing ID.");
        Check(library.Find(original.Id)!.Deck.CardCount == 25 && library.Decks.Any(deck => deck.CardCount == 26), "Old selection must not silently become a new composition.");
        var directory = Temp(); var path = Path.Combine(directory, "library.json");
        try
        {
            library.AddLinks(DeckLinkFileReader.FromText(Url.AbsoluteUri)); library.Save(path); library.Save(path);
            var restored = DeckLibrary.Load(path);
            Check(restored.Decks.Length == 2 && restored.ImportedLinks.Count == 1 && File.Exists(path + ".bak"), "Library and import manifests must survive restart with a backup.");
            var card = restored.Find(copy.Id)!.Deck.Cards[0].Card;
            Check(card.HasCategory("soldier") && card.SecondaryFactions.Contains("syndicate") && card.AbilityText == "Test text", "Round-trip must retain card metadata and case-insensitive sets.");
            var catalog = DeckSearchCatalog.Build(DeckLinkFileReader.FromText(Url + " " + copy.SourceUri), restored.Decks, restored);
            Check(catalog.Count == 2 && catalog.Count(item => item.Name == "My named list") == 1, "Index aliases must not produce duplicate UI rows; show renamed title.");
            Check(catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("Alternate label"))), "Merged original names should remain searchable.");
            Check(DeckSearchCatalog.ContainsCards(original, "Card 1, Card 24"), "All comma-separated card names must match.");
            Check(!DeckSearchCatalog.ContainsCards(original with { Name = "Renfri" }, "Renfri"), "Card-only search must not match a deck title.");
            Check(!DeckSearchCatalog.ContainsCards(null, "Renfri"), "Uncached index entries cannot claim card membership.");

            var editable = restored.Find(original.Id)!.Deck;
            var modified = editable with { Id = "builder-overwrite", Name = "Modified", Cards = editable.Cards.Take(24).Append(new DeckCard(Card("Replacement"))).ToArray(), SourceUri = null };
            var beforeOverwrite = restored.Records.Count;
            var overwritten = restored.Replace(editable.Id, modified);
            Check(restored.Records.Count == beforeOverwrite && restored.Find(editable.Id) is null && restored.Find(overwritten.Id)?.Deck.ContainsName("Replacement") == true,
                "Overwrite must replace one exact record and retire its old identity instead of adding a version.");
            Check(restored.Find(overwritten.Id) is { Sources.Length: 0, Export: null } && restored.ImportedLinks.Count == 0,
                "Overwrite must not attribute old import/export evidence to the modified composition.");
            try { restored.Replace(overwritten.Id, restored.Records.First(record => record.Deck.Id != overwritten.Id).Deck with { Id = "collision" }); throw new Exception("Duplicate overwrite should fail."); }
            catch (InvalidOperationException) { }
            var removed = restored.Remove(overwritten.Id);
            Check(removed.Deck.Id == overwritten.Id && restored.Find(overwritten.Id) is null && restored.Records.Count == beforeOverwrite - 1,
                "Delete must remove only the requested exact record.");
            restored.EnsureVariationGroups();
            Check(restored.VariationGroups.SelectMany(group => group.Members).Count() == restored.Records.Count,
                "Overwrite/delete left stale variation membership.");
            File.WriteAllText(path, "broken json");
            try { DeckLibrary.Load(path); throw new Exception("Corrupt library should fail visibly."); } catch (JsonException) { }
            Check(File.ReadAllText(path) == "broken json", "Do not overwrite an unreadable library.");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void Links()
    {
        Check(DeckLinkFileReader.FromText("https://playgwent.com/pl/decks/" + Hash + "\n" + Url).Count == 1, "Normalize language/host variants to one HTTPS deck URL.");
        Check(DeckLinkFileReader.CanonicalUrl(new Uri("https://evil.test/en/decks/" + Hash)) is null, "Never fetch a workbook-supplied third-party host.");
        Check(DeckLinkFileReader.CanonicalUrl(new Uri("https://www.playgwent.com@evil.test/en/decks/" + Hash)) is null, "Reject misleading URL credentials.");
        Check(DeckLinkFileReader.FromText("https://www.playgwent.com/en/decks/guides/12345").Count == 1, "Public guide links must import, with IDs distinct from deck hashes.");
        Check(DeckLinkFileReader.CanonicalUrl(new Uri("https://playgwent.com/en/decks/guides/not-a-guide")) is null, "Reject malformed guide IDs.");
        var directory = Temp();
        try
        {
            var path = Path.Combine(directory, "headerless.xlsx");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Part(string name, string value) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value); }
                Part("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheets/></workbook>");
                Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'/>");
                Part("xl/sharedStrings.xml", $"<sst><si><t>Text with {Url} inside it</t></si></sst>");
                Part("xl/worksheets/sheet1.xml", $"<worksheet><f>HYPERLINK(&quot;https://www.playgwent.com/en/decks/abcdefabcdefabcdefabcdefabcdefab&quot;,&quot;Deck&quot;)</f></worksheet>");
                Part("xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships><Relationship Target='{Url}'/><Relationship Target='https://evil.test/en/decks/{Hash}'/></Relationships>");
            }
            Check(DeckLinkFileReader.Read(path).Count == 2, "Headerless workbook, shared strings, formulas, and hyperlinks should union and dedupe without evaluating anything.");
            var located = Path.Combine(directory, "located.xlsx");
            using (var archive = ZipFile.Open(located, ZipArchiveMode.Create))
            {
                void Part(string name, string value) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value); }
                Part("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='10.10' r:id='r1'/></sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>");
                Part("xl/worksheets/sheet1.xml", $"<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='7'><c r='A7' t='inlineStr'><is><t>See {Url} and https://www.playgwent.com/en/decks/guides/12345</t></is></c><c r='B7'><f>HYPERLINK(&quot;https://www.playgwent.com/en/decks/abcdefabcdefabcdefabcdefabcdefab&quot;,&quot;Variant&quot;)</f></c></row></sheetData></worksheet>");
            }
            var locatedLinks = DeckLinkFileReader.Read(located, false);
            Check(locatedLinks.Count == 3 && locatedLinks.All(e => e.Sheet == "10.10" && e.Row == 7 && e.Patches!.Any(p => p.Label == "10.10")),
                "Every URL/formula in a headerless row must retain its real worksheet, row and historical patch.");
            var suppliedRoot = Path.Combine(directory, "game");
            Directory.CreateDirectory(Path.Combine(suppliedRoot, "notes")); Directory.CreateDirectory(Path.Combine(suppliedRoot, "GwentCompanion", "notes"));
            File.Copy(located, Path.Combine(suppliedRoot, "notes", "original.xlsx"));
            File.Copy(located, Path.Combine(suppliedRoot, "GwentCompanion", "notes", "updated.xlsx"));
            File.Copy(located, Path.Combine(suppliedRoot, "notes", "~$locked.xlsx"));
            Check(SuppliedDeckWorkbooks.Find(suppliedRoot).Length == 2 && SuppliedDeckWorkbooks.Read(suppliedRoot).Select(e => e.DeckUri).Distinct().Count() == 3,
                "Both supplied notes locations must be read; ignore Excel lock files and dedupe links at import.");
            var csv = Path.Combine(directory, "links.csv"); File.WriteAllText(csv, $"Name,Link\nExample,{Url}\nSame,{Url}");
            Check(DeckLinkFileReader.Read(csv).Count == 1, "CSV plain links should import once.");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void Draft()
    {
        var card = Card("Impera Brigade"); var gold = Card("Renfri", true);
        var draft = new DeckScanDraft();
        Check(draft.Observe([new(card)]) == 0 && draft.CardCount == 0, "One unstable OCR frame must not populate the draft.");
        draft.Observe([new(card)]); draft.Observe([new(card)]);
        Check(draft.CardCount == 1, "Repeated page must not inflate counts.");
        draft.Observe([new(card, 2)]); draft.Observe([new(card, 2)]);
        Check(draft.CardCount == 2, "Stable explicit second copy should update quantity.");
        draft.Observe([new(gold)]); draft.Observe([new(gold)]);
        Check(draft.CardCount == 3, "Scrolling unions distinct cards.");
        draft.SetCount(card, 1); draft.Observe([new(card, 2)]); draft.Observe([new(card, 2)]);
        Check(draft.Cards.First(item => item.Card.Id == card.Id).Count == 1, "Manual quantity must override continuing OCR.");
        draft.SetCount(gold, 0); draft.Observe([new(gold)]); draft.Observe([new(gold)]);
        Check(draft.CardCount == 1, "Removed OCR false positives must not reappear.");
        draft.Reset(); draft.Observe([new(card)]); draft.BreakSequence(); draft.Observe([new(card)]);
        Check(draft.CardCount == 0, "Lost foreground interrupts OCR consensus.");
        var leader = new CardDefinition("leader", "Enslave", "Nilfgaard", CardKind.Leader, 15);
        try { draft.Build("Partial", leader); throw new Exception("Partial scan should not save as complete deck."); } catch (InvalidDataException) { }
        draft.Load(Deck().Cards); Check(draft.Build("Review", leader).CardCount == 25, "Reviewed complete deck can be saved.");
        var region = new NormalizedRegion(.02, .2, .2, .23);
        var page = DeckBuilderScanner.MatchLines([new("5 IMPERA BRIGADE x2 4", region), new("12 Renfri 8", region),
            new("Play Renfri from your deck", region), new("Impera Brigad", region)], [card, gold]);
        Check(page.Count == 2 && page[0].Count == 2 && page[1].Count == 1, "OCR matches exact names/copy labels, rejecting tooltip prose and fuzzy fragments.");
    }

    public static async Task CacheAsync(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion", "cache", "decks");
        var fixture = Directory.GetFiles(cache, "*.json").First(p => !Path.GetFileName(p).StartsWith("guide-")); var raw = File.ReadAllText(fixture);
        var hash = Path.GetFileNameWithoutExtension(fixture);
        var entry = DeckLinkFileReader.FromText("https://www.playgwent.com/en/decks/" + hash).Single();
        var directory = Temp();
        try
        {
            var handler = new FakeHttp("<div data-state='" + WebUtility.HtmlEncode(raw) + "'></div>");
            using var http = new HttpClient(handler); var service = new PlayGwentDeckCacheService(http);
            File.WriteAllText(Path.Combine(directory, hash + ".json"), "corrupt");
            var result = await service.SyncAsync([entry, entry], directory, int.MaxValue);
            Check(result.Requested == 1 && result.Decks.Count == 1 && result.Decks[0].CardCount >= 25 && handler.Requests == 1, "Corrupt cache refetch and duplicate link suppression.");
            await service.SyncAsync([entry], directory, 1);
            Check(handler.Requests == 1, "Validated payload should reuse cache.");
            var invalid = entry with { DeckUri = new Uri("https://evil.test/decks/" + hash) };
            Check((await service.SyncAsync([invalid], directory, 1)).Requested == 0 && handler.Requests == 1, "Untrusted hosts must never receive a request.");
            var other = DeckLinkFileReader.FromText(Url.AbsoluteUri).Single();
            handler.Body = "<div data-state='{}'></div>";
            var bad = await service.SyncAsync([other], directory, 1);
            Check(bad.Decks.Count == 0 && bad.Expired == 1 && !File.Exists(Path.Combine(directory, Hash + ".json")), "Invalid payload is not cached as a deck.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.SyncAsync([entry], directory, 1, cancellationToken: canceled.Token); throw new Exception("Cancellation expected."); }
            catch (OperationCanceledException) { }
            Check(service.LoadCached([entry], directory, 1).Count == 1, "Cancellation retains completed payloads.");
            using var parsed = JsonDocument.Parse(raw);
            var guide = DeckLinkFileReader.FromText("https://www.playgwent.com/en/decks/guides/404499").Single();
            var guideJson = JsonSerializer.Serialize(new { guide = new { id = 404499, modified = "2026-02-18T12:00:00Z", deck = parsed.RootElement.GetProperty("deck") } });
            handler.Body = "<div data-state='" + WebUtility.HtmlEncode(guideJson) + "'></div>";
            var guided = await service.SyncAsync([guide], directory, 1);
            Check(guided.Decks.Count == 1 && guided.Decks[0].Id == hash && guided.Decks[0].SourceUri == guide.DeckUri &&
                guided.Decks[0].LastEdited?.Month == 2 && File.Exists(Path.Combine(directory, "guide-404499.json")), "Guide payload should retain actual hash and guide date, using a distinct cache key.");
            Check(service.LoadCached([guide], directory, 1).Count == 1, "Guides must load offline.");
            var guideLibrary = new DeckLibrary(); guideLibrary.Merge(guided.Decks);
            var guideSearch = DeckSearchCatalog.Build([entry, guide], guideLibrary.Decks, guideLibrary);
            Check(guideSearch.Count == 1 && guideSearch[0].Deck?.Id == hash, "Guide IDs must resolve to their cached deck in search, not add an uncached duplicate row.");
            var wrongGuide = guide with { DeckUri = new Uri("https://www.playgwent.com/en/decks/guides/404500") };
            Check((await service.SyncAsync([wrongGuide], directory, 1)).Decks.Count == 0, "Wrong guide ID must fail closed.");
            handler.Status = HttpStatusCode.NotFound;
            var dead = guide with { DeckUri = new Uri("https://www.playgwent.com/en/decks/guides/404501") };
            var deadResult = await service.SyncAsync([dead], directory, 1);
            var requests = handler.Requests;
            await service.SyncAsync([dead], directory, 1);
            Check(deadResult.Expired == 1 && deadResult.Errors.Count == 0 && handler.Requests == requests, "404 links must be marked unavailable and use bounded negative caching.");
            Check(DeckPatchMetadata.HistoricalDate([new("10.10", false, "Worksheet 10.10")], DateTimeOffset.UtcNow)?.Year == 2023,
                "An old sheet must not become current just because a balance update rewrites the payload.");
        }
        finally { Directory.Delete(directory, true); }
    }
    public static void ExpandedLibrary(string root)
    {
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var catalog = DeckSearchCatalog.Build(library.ImportedLinks, library.Decks, library);
        Check(catalog.Where(c => c.Deck is not null).Select(c => c.Deck!.Id).Distinct().Count() == library.Decks.Length,
            "Expanded import search lost a cached composition or duplicated its aliases.");
        Check(catalog.Count(c => c.Deck is not null) == library.Decks.Length, "Cached guide/direct aliases created duplicate search rows.");
        var guides = library.ImportedLinks.Where(e => e.DeckUri.AbsolutePath.Contains("/guides/")).DistinctBy(e => e.DeckUri).ToArray();
        foreach (var guide in guides)
        {
            var result = DeckSearchCatalog.Build([guide], library.Decks, library);
            Check(result.Any(c => c.SourceUri == guide.DeckUri && c.Deck is not null), "Guide is incorrectly shown as uncached: " + guide.DeckUri);
        }
        Console.WriteLine($"Expanded import search passed: {library.Decks.Length} cached records, {guides.Length} guides resolve, {catalog.Count(c => c.Deck is null)} unavailable links.");
    }
    private sealed class FakeHttp(string body) : HttpMessageHandler
    {
        public string Body = body; public int Requests; public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) }); }
    }
}
