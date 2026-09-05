using System.IO;
using System.Net.Http;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;
using GwentCompanion.Core.Vision;

internal static class CardCacheAudit
{
    public static async Task RunAsync(string root, bool fillMissing, bool comparePublic)
    {
        var project = Path.Combine(root, "GwentCompanion"); var cache = Path.Combine(project, "cache");
        var source = Path.Combine(cache, "gwent-one-cards.json");
        var catalog = GwentOneCardCatalog.Load(source);
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var version = document.RootElement.GetProperty("request").GetProperty("REQUEST").GetProperty("version").GetString();
        string Portrait(CardDefinition card) => Path.Combine(cache, card.Kind == CardKind.Leader ? "leader-portraits" : "portraits", card.Id + ".jpg");
        bool Valid(string file)
        {
            if (!File.Exists(file)) return false;
            try { using var decoded = Cv2.ImRead(file); return !decoded.Empty() && decoded.Width >= 32 && decoded.Height >= 32; }
            catch { return false; }
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string? publicVersion = null; string[] publicMissing = [], publicExtra = [];
        if (comparePublic)
        {
            var json = await http.GetStringAsync("https://api.gwent.one/?key=data&language=en&version=latest");
            using var latest = JsonDocument.Parse(json);
            publicVersion = latest.RootElement.GetProperty("request").GetProperty("REQUEST").GetProperty("version").GetString();
            var latestIds = latest.RootElement.GetProperty("response").EnumerateObject()
                .Select(item => item.Value.GetProperty("id").GetProperty("card").GetInt32().ToString()).ToHashSet();
            var ids = catalog.Select(card => card.Id).ToHashSet();
            publicMissing = latestIds.Except(ids).Order().ToArray(); publicExtra = ids.Except(latestIds).Order().ToArray();
            // Keep the active catalogue unchanged: this audit is not a silent balance-data migration.
            await File.WriteAllTextAsync(Path.Combine(project, "diagnostics/v0.1.20-public-catalog-audit.json"), json);
        }
        var beforeMissing = catalog.Where(card => !Valid(Portrait(card))).Select(card => card.Id).ToArray();
        var downloads = new System.Collections.Concurrent.ConcurrentBag<object>();
        if (fillMissing)
        {
            using var concurrency = new SemaphoreSlim(4);
            await Task.WhenAll(catalog.Where(card => beforeMissing.Contains(card.Id)).Select(async card =>
            {
                await concurrency.WaitAsync();
                try
                {
                    var path = Portrait(card);
                    if (File.Exists(path)) { downloads.Add(new { card.Id, Status = "Existing unreadable file retained for review" }); return; }
                    if (card.ArtUri?.Host != "gwent.one") { downloads.Add(new { card.Id, Status = "No approved public art URL" }); return; }
                    var bytes = await http.GetByteArrayAsync(card.ArtUri);
                    using var decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
                    if (decoded.Empty() || decoded.Width < 32 || decoded.Height < 32) throw new InvalidDataException("Invalid public image response");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) await output.WriteAsync(bytes);
                    downloads.Add(new { card.Id, Status = "Added missing public artwork" });
                }
                catch (Exception exception) { downloads.Add(new { card.Id, Status = exception.Message }); }
                finally { concurrency.Release(); }
            }));
        }
        var references = VisionReferenceLibrary.Load(catalog, cache);
        var families = new CardAppearanceFamilies(catalog);
        var unreadable = references.Where(item => !Valid(item.Path)).Select(item => new { item.Card.Id, item.Card.Name, item.Path }).ToArray();
        var rows = catalog.Select(card => new { card.Id, card.Name, card.Kind, card.CanBeInStartingDeck,
            HasPortrait = Valid(Portrait(card)), References = references.Count(item => item.Card.Id == families.Normalize(card).Id),
            PremiumFrames = references.Count(item => item.Card.Id == families.Normalize(card).Id && item.Path.Contains("premium-frames")),
            ObservedAppearances = references.Count(item => item.Card.Id == families.Normalize(card).Id && item.Path.Contains("observed-art")),
            ModelImplemented = PlayRules.Compile(card) is { Unmodeled: null, UnmodeledDeploy: null } }).ToArray();
        var missing = rows.Where(row => !row.HasPortrait).ToArray();
        var ambiguity = catalog.Where(card => card.ArtUri is not null).GroupBy(card => card.ArtUri!.AbsoluteUri).Where(group => group.Count() > 1)
            .Select(group => new { Art = group.Key, Cards = group.Select(card => new { card.Id, card.Name, card.Kind }) }).ToArray();
        var report = new { LocalVersion = version, PublicVersion = publicVersion, TotalDefinitions = catalog.Count,
            StartingCards = catalog.Count(card => card.CanBeInStartingDeck), Leaders = catalog.Count(card => card.Kind == CardKind.Leader),
            NonStartingBoardCards = catalog.Count(card => !card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact),
            MissingBefore = beforeMissing, MissingAfter = missing, PublicIdsMissingLocally = publicMissing, LocalIdsAbsentPublicly = publicExtra,
            ReferenceImages = references.Count, UnreadableReferences = unreadable, SharedArtGroups = ambiguity,
            Downloads = downloads.ToArray(), Cards = rows,
            Note = "All definition/asset availability, not full recognition or rule-engine coverage. Leader portraits are cached separately and not fed into the ordinary board matcher. Shared artwork requires text/temporal context. Public comparison does not replace the active catalogue." };
        var outputPath = Path.Combine(project, "diagnostics/v0.1.20-card-cache-audit.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, GameStateJournal.Json));
        Console.WriteLine($"Full cache audit: {catalog.Count} definitions ({version}); public {publicVersion ?? "not checked"}; {missing.Length} missing portraits; {unreadable.Length} unreadable references; {publicMissing.Length} public identities missing locally.");
        Console.WriteLine($"{report.NonStartingBoardCards} non-starting units/specials/artifacts; {references.Count} reference images; {ambiguity.Length} shared-art groups. Report: {outputPath}");
        if (rows.Any(row => row.Kind != CardKind.Leader && (row.References == 0 || !row.HasPortrait)) || unreadable.Length > 0 || publicMissing.Length > 0)
            throw new InvalidOperationException("Card cache coverage has gaps; inspect the audit.");
        if (fillMissing && missing.Length > 0) throw new InvalidOperationException("Some missing public portraits could not be filled; inspect the audit.");
    }
}
