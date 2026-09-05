using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Platform.Windows.Vision;

/// <summary>Offline, opt-in migration of existing records, never an importer of orphan payloads.</summary>
internal static class DeckMetadataBackfill
{
    private sealed record Evidence(DeckDefinition Deck, string Source);
    private sealed record Update(string Id, string Name, string Leader, string? Stratagem,
        string Status, string[] Sources);

    public static async Task<int> RunAsync(string gameRoot, bool apply)
    {
        var root = Path.Combine(gameRoot, "GwentCompanion");
        var path = Path.Combine(root, "cache", "deck-library.json");
        if (apply) RequireClosedCompanion();
        var original = File.ReadAllBytes(path);
        var library = DeckLibrary.Load(path);
        var before = library.Records.ToArray();
        var evidence = new Dictionary<string, List<Evidence>>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        var parser = new PlayGwentDeckPageParser();
        foreach (var record in before)
        {
            evidence[record.Deck.Id] = [];
            foreach (var source in record.Sources.Append(record.Deck.SourceUri?.AbsoluteUri ?? "").Distinct())
            {
                if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || DeckLinkFileReader.CanonicalUrl(uri) is null) continue;
                var hash = uri.Segments.Last().Trim('/');
                if (!hash.All(char.IsAsciiLetterOrDigit)) continue;
                var payload = Path.Combine(root, "cache", "decks", hash + ".json");
                if (!File.Exists(payload)) { issues.Add($"Missing payload for {record.Deck.Id}: {source}"); continue; }
                try
                {
                    var deck = parser.ParseStateJson(File.ReadAllText(payload), uri);
                    if (deck.Id != hash) throw new InvalidDataException("Payload identity does not match source URL.");
                    evidence[record.Deck.Id].Add(new(deck, Path.GetRelativePath(root, payload)));
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
                { issues.Add($"{source}: {exception.Message}"); }
            }
        }

        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "cache", "gwent-one-cards.json"));
        using var scanner = new DeckBuilderScanner(catalog);
        var scanRoot = Path.Combine(root, "deck-scans");
        foreach (var confirmedPath in Directory.Exists(scanRoot)
                     ? Directory.GetFiles(scanRoot, "confirmed-deck.json", SearchOption.AllDirectories) : [])
        {
            using var confirmed = JsonDocument.Parse(File.ReadAllText(confirmedPath));
            var data = confirmed.RootElement;
            var record = library.Find(data.GetProperty("Id").GetString());
            if (record is null || record.Deck.Stratagem is not null) continue;
            var cards = data.GetProperty("Cards").EnumerateArray().Select(item => new DeckCard(
                catalog.Single(card => card.Id == item.GetProperty("Id").GetString()), item.GetProperty("Count").GetInt32())).ToArray();
            var reference = record.Deck with { Cards = cards, Faction = data.GetProperty("Faction").GetString()!,
                Leader = data.GetProperty("Leader").GetString()!, Stratagem = null };
            if (DeckLibrary.Fingerprint(reference) != DeckLibrary.Fingerprint(record.Deck with { Stratagem = null }))
            { issues.Add($"Skipped changed scan composition: {confirmedPath}"); continue; }

            // Independent saved images must agree; never derive header identity from deck/card prevalence.
            var draft = new DeckScanDraft();
            var samples = new List<object>();
            foreach (var imagePath in Directory.GetFiles(Path.GetDirectoryName(confirmedPath)!, "page-*.jpg").Order())
            {
                var page = await scanner.ReadAsync(DeckBuilderReplay.Restore(imagePath), DeckBuilderScanner.LeftPanel);
                draft.ObserveHeader(page.Leader, page.Stratagem);
                samples.Add(new { Page = Path.GetFileName(imagePath), Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name });
                if (draft.Leader is not null && draft.Stratagem is not null) break;
            }
            if (draft.Leader is null || draft.Stratagem is null)
            { issues.Add($"No agreed printed header: {confirmedPath}"); continue; }
            var metadata = reference with { Leader = draft.Leader.Name, Stratagem = draft.Stratagem };
            evidence[record.Deck.Id].Add(new(metadata, Path.GetRelativePath(root, confirmedPath) + " + printed header: " + JsonSerializer.Serialize(samples)));
        }

        var changes = new List<Update>();
        foreach (var record in before)
        {
            var observations = evidence[record.Deck.Id];
            var status = Enrich(library, record.Deck.Id, observations.Select(item => item.Deck).ToArray());
            var current = library.Find(record.Deck.Id)!.Deck;
            changes.Add(new(current.Id, current.Name, current.Leader, current.Stratagem?.Name,
                status, observations.Select(item => item.Source).ToArray()));
        }
        VerifyPreserved(before, library);
        var missing = library.Decks.Where(deck => string.IsNullOrWhiteSpace(deck.Leader) || deck.Stratagem is null)
            .Select(deck => deck.Id).ToArray();
        string? backup = null;
        if (apply)
        {
            RequireClosedCompanion();
            if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
                throw new IOException("Library changed during migration; no update written.");
            backup = Path.Combine(root, "cache", $"deck-library.pre-v0.1.6-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(path, backup, false);
            library.Save(path);
            var reloaded = DeckLibrary.Load(path);
            VerifyPreserved(before, reloaded);
            if (reloaded.Decks.Count(deck => deck.Stratagem is not null) != library.Decks.Count(deck => deck.Stratagem is not null))
                throw new InvalidDataException("Written stratagems did not survive a round-trip.");
        }
        var report = new { Applied = apply, Before = new { Decks = before.Length,
                Leaders = before.Count(item => !string.IsNullOrWhiteSpace(item.Deck.Leader)),
                Stratagems = before.Count(item => item.Deck.Stratagem is not null) },
            After = new { Decks = library.Records.Count, Leaders = library.Decks.Count(deck => !string.IsNullOrWhiteSpace(deck.Leader)),
                Stratagems = library.Decks.Count(deck => deck.Stratagem is not null) }, Missing = missing,
            Issues = issues, Backup = backup, Records = changes };
        var reportPath = Path.Combine(root, "diagnostics", $"v0.1.6-cache-backfill{(apply ? "" : "-preview")}.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.Applied, report.Before, report.After, report.Missing, report.Issues, report.Backup, Report = reportPath }));
        return missing.Length == 0 && changes.All(item => !item.Status.StartsWith("Conflict")) ? 0 : 2;
    }

    private static string Enrich(DeckLibrary library, string id, IReadOnlyList<DeckDefinition> evidence)
    {
        var record = library.Find(id) ?? throw new KeyNotFoundException(id);
        var fingerprint = DeckLibrary.Fingerprint(record.Deck with { Stratagem = null });
        if (evidence.Any(deck => DeckLibrary.Fingerprint(deck with { Stratagem = null }) != fingerprint))
            return "Conflict: source composition or leader differs; preserved existing record";
        var known = evidence.Select(deck => deck.Stratagem).Append(record.Deck.Stratagem)
            .Where(card => card is not null).Cast<CardDefinition>().ToArray();
        if (known.Any(card => card.Kind != CardKind.Stratagem) || known.Select(card => card.Id).Distinct().Count() > 1)
            return "Conflict: sources disagree on stratagem; preserved existing record";
        if (record.Deck.Stratagem is not null) return "Already complete";
        if (known.Length == 0) return "Unknown: no verified stratagem evidence";
        var result = library.Merge([record.Deck with { Stratagem = known[0],
            Name = record.OriginalNames.FirstOrDefault() ?? record.Deck.Name }]);
        if (result.Added != 0 || library.Find(id)!.Deck.Stratagem?.Id != known[0].Id)
            throw new InvalidDataException("Metadata enrichment changed deck identity; no library should be saved.");
        return "Enriched";
    }

    private static void VerifyPreserved(IReadOnlyList<LibraryDeck> before, DeckLibrary after)
    {
        if (before.Count != after.Records.Count) throw new InvalidDataException("Metadata update changed deck count.");
        foreach (var old in before)
        {
            var current = after.Find(old.Deck.Id) ?? throw new InvalidDataException("Lost saved deck identity.");
            if (current.Deck.Name != old.Deck.Name || current.Deck.Id != old.Deck.Id || current.CustomName != old.CustomName ||
                !current.Aliases.SequenceEqual(old.Aliases) || !current.Sources.SequenceEqual(old.Sources) ||
                !current.OriginalNames.SequenceEqual(old.OriginalNames) ||
                DeckLibrary.Fingerprint(current.Deck with { Stratagem = null }) != DeckLibrary.Fingerprint(old.Deck with { Stratagem = null }))
                throw new InvalidDataException("Metadata update changed composition, leader, name or provenance.");
        }
    }

    private static void RequireClosedCompanion()
    {
        var processes = Process.GetProcessesByName("GwentCompanion.App");
        try { if (processes.Length != 0) throw new InvalidOperationException("Close Gwent Vision before applying the metadata backfill."); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static void Regression(string gameRoot)
    {
        var parser = new PlayGwentDeckPageParser();
        var payload = Path.Combine(gameRoot, "GwentCompanion", "cache", "decks", "a9067ad7e117854b2f2d21db4071d9a5.json");
        var deck = parser.ParseStateJson(File.ReadAllText(payload), new Uri("https://www.playgwent.com/en/decks/a9067ad7e117854b2f2d21db4071d9a5"));
        if (deck.Stratagem is null) throw new InvalidDataException("Fixture must include a public stratagem.");
        DeckLibrary Library()
        {
            var value = new DeckLibrary(); value.Merge([deck with { Stratagem = null }]);
            value.Rename(deck.Id, "Keep my custom name"); return value;
        }
        var library = Library(); var before = library.Records.ToArray();
        if (Enrich(library, deck.Id, [deck, deck]) != "Enriched") throw new InvalidOperationException("Agreement did not enrich.");
        VerifyPreserved(before, library);
        if (Enrich(library, deck.Id, [deck]) != "Already complete") throw new InvalidOperationException("Migration is not idempotent.");
        foreach (var changed in new[] { deck with { Leader = "Different leader" },
                     deck with { Cards = deck.Cards.Skip(1).ToArray() },
                     deck with { Stratagem = deck.Stratagem with { Id = "different" } } })
        {
            library = Library();
            if (!Enrich(library, deck.Id, [deck, changed]).StartsWith("Conflict") || library.Decks[0].Stratagem is not null)
                throw new InvalidOperationException("Conflicting metadata changed a cached record.");
        }
        library = Library();
        if (!Enrich(library, deck.Id, []).StartsWith("Unknown") || library.Decks[0].Stratagem is not null)
            throw new InvalidOperationException("Missing metadata must not be guessed.");
    }
}
