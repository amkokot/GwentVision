using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed class CardDataSnapshot
{
    public string Version { get; }
    public IReadOnlyList<CardDefinition> Cards { get; }
    public string Source { get; }
    internal string Json { get; }
    internal string Hash { get; }
    private CardDataSnapshot(string version, IReadOnlyList<CardDefinition> cards, string json, string source)
    {
        Version = version; Cards = cards; Json = json; Source = source;
        Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(cards.OrderBy(c => c.Id)))));
    }

    public static CardDataSnapshot Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > CardDataUpdater.MaxBytes)
            throw new InvalidDataException("Card data is too large. Choose a full gwent.one JSON catalogue.");
        using var document = JsonDocument.Parse(json);
        var request = document.RootElement.GetProperty("request");
        var parameters = request.GetProperty("REQUEST");
        if (request.GetProperty("status").GetInt32() != 200 || parameters.GetProperty("language").GetString() != "en")
            throw new InvalidDataException("Use a successful English gwent.one card-data export.");
        var version = parameters.GetProperty("version").GetString() ?? "";
        if (!System.Version.TryParse(version, out _) || version.Length > 24)
            throw new InvalidDataException("Card data has no valid patch version.");
        var response = document.RootElement.GetProperty("response");
        // Response keys are row indices, not card IDs. Identity lives in id.card.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in response.EnumerateObject())
        {
            var id = entry.Value.GetProperty("id").GetProperty("card").GetInt32();
            if (id <= 0 || !ids.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !rows.Add(entry.Name))
                throw new InvalidDataException("Card data contains duplicate or invalid IDs.");
        }
        var cards = GwentOneCardCatalog.Parse(json);
        if (cards.Count < 1000 || cards.Count > 10000 || cards.Any(c => string.IsNullOrWhiteSpace(c.Name) ||
                c.Kind == CardKind.Unknown || c.Provision is < 0 or > 100 || c.Power is < 0 or > 1000 || c.PrintedArmor is < 0 or > 1000) ||
            !cards.Any(c => c.Kind == CardKind.Stratagem))
            throw new InvalidDataException("Incomplete or invalid card data. A complete catalogue, including leaders and stratagems, is required.");
        var leaders = GwentOneCardCatalog.StartingLeaders(cards);
        foreach (var faction in new[] { "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate" })
            if (!leaders.Any(c => c.Faction == faction) || cards.Count(c => c.Faction == faction && c.CanBeInStartingDeck) < 50)
                throw new InvalidDataException("Card data is missing a faction or its leader abilities: " + faction);
        var source = document.RootElement.TryGetProperty("gwentVisionSource", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
            ? sourceElement.GetString() ?? "gwent.one" : "gwent.one";
        return new(version, cards, json, source);
    }
}

public sealed record CardDataUpdateResult(bool Changed, string Version, string? PreviousVersion, IReadOnlyList<string> Changes,
    bool ComparisonAdded = false, string Source = "gwent.one");

/// <summary>Public data only. Fetch/validate off-thread, then atomically activate with a recoverable previous copy.</summary>
public sealed class CardDataUpdater(string path)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const string LatestUrl = "https://api.gwent.one/?key=data&language=en&version=latest";
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string BackupPath => Path + ".previous";
    public CardDataSnapshot? Current() => File.Exists(Path) ? CardDataSnapshot.Parse(File.ReadAllText(Path)) : null;

    public static async Task<CardDataSnapshot> DownloadAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        var latest = await DownloadVersionAsync(client, "latest", cancellationToken).ConfigureAwait(false);
        try { return await PlayGwentCardValueOverlay.ApplyAsync(client, latest, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException)
        { return latest; }
    }

    public static async Task<CardDataSnapshot> DownloadVersionAsync(HttpClient client, string version, CancellationToken cancellationToken = default)
    {
        if (version != "latest" && !Version.TryParse(version, out _)) throw new ArgumentException("Invalid catalogue version.");
        using var response = await client.GetAsync("https://api.gwent.one/?key=data&language=en&version=" + Uri.EscapeDataString(version), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("The server returned an oversized card catalogue.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (version != "latest" && result.Version != version) throw new InvalidDataException("The source returned a different patch than requested.");
        return result;
    }

    public static async Task<CardDataSnapshot> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CardDataSnapshot> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(); var block = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Card data exceeds the 10 MB safety limit.");
            buffer.Write(block, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return CardDataSnapshot.Parse(Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF'));
    }

    public CardDataUpdateResult Install(CardDataSnapshot candidate) => Activate(candidate, rollback: false);
    public CardDataUpdateResult RestorePrevious() => Activate(null, rollback: true);

    private CardDataUpdateResult Activate(CardDataSnapshot? candidate, bool rollback)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var gate = new FileStream(Path + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Validate again: callers cannot bypass checks by modifying a snapshot's collection.
        candidate = CardDataSnapshot.Parse(rollback ? File.ReadAllText(BackupPath) : candidate!.Json);
        var current = Current();
        if (!rollback && current is not null)
        {
            if (System.Version.Parse(candidate.Version) < System.Version.Parse(current.Version))
                throw new InvalidDataException($"Received older patch {candidate.Version}; kept {current.Version}. Use Restore previous only for an intentional rollback.");
            var ids = candidate.Cards.Select(c => c.Id).ToHashSet();
            if (current.Cards.Any(c => (c.CanBeInStartingDeck || c.Kind == CardKind.Leader) && !ids.Contains(c.Id)))
                throw new InvalidDataException("The update is missing existing cards or leaders. Your current data was kept.");
        }
        var changes = DescribeChanges(current, candidate);
        if (current?.Hash == candidate.Hash && current.Version == candidate.Version)
            return new(false, candidate.Version, current.Version, changes, Source: candidate.Source);
        var temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // A failed backup/archive/write leaves the active file untouched.
            if (current is not null)
            {
                var archive = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "card-data-history");
                Directory.CreateDirectory(archive);
                var historyPath = System.IO.Path.Combine(archive, current.Version + "-" + current.Hash + ".json");
                if (!File.Exists(historyPath)) File.WriteAllText(historyPath, current.Json, new UTF8Encoding(false));
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(candidate.Json); stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(temporary, Path, BackupPath);
            else File.Move(temporary, Path);
            return new(true, candidate.Version, current?.Version, changes, Source: candidate.Source);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string[] DescribeChanges(CardDataSnapshot? current, CardDataSnapshot candidate)
    {
        var old = current?.Cards.ToDictionary(c => c.Id) ?? [];
        var changes = new List<string>();
        foreach (var card in candidate.Cards.OrderBy(c => c.Name))
        {
            if (!old.TryGetValue(card.Id, out var prior)) { changes.Add(card.Name + " · added"); continue; }
            var fields = new List<string>();
            if (prior.Power != card.Power) fields.Add($"power {prior.Power} → {card.Power}");
            if (prior.Provision != card.Provision) fields.Add($"{(card.Kind == CardKind.Leader ? "leader bonus" : "provisions")} {prior.Provision} → {card.Provision}");
            if (prior.PrintedArmor != card.PrintedArmor) fields.Add($"armor {prior.PrintedArmor?.ToString() ?? "?"} → {card.PrintedArmor?.ToString() ?? "?"}");
            if (prior.Name != card.Name || prior.AbilityText != card.AbilityText || prior.Kind != card.Kind || prior.Faction != card.Faction ||
                prior.IsGold != card.IsGold || prior.CanBeInStartingDeck != card.CanBeInStartingDeck ||
                !prior.Categories.SetEquals(card.Categories) || !prior.SecondaryFactions.SetEquals(card.SecondaryFactions) || prior.ArtUri != card.ArtUri)
                fields.Add("card details updated");
            if (fields.Count > 0) changes.Add(card.Name + " · " + string.Join("; ", fields));
        }
        var currentIds = candidate.Cards.Select(c => c.Id).ToHashSet();
        changes.AddRange(old.Values.Where(c => !currentIds.Contains(c.Id)).Select(c => c.Name + " · removed"));
        return changes.ToArray();
    }
}
