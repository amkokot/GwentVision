using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record CardBalanceChange(string CardId, string Name, CardDefinition? Before, CardDefinition After)
{
    public int PowerDelta => Before is null ? 0 : After.Power - Before.Power;
    public int ProvisionDelta => Before is null ? 0 : After.Provision - Before.Provision;
    public bool DetailsChanged => Before is not null && (Before.Name != After.Name || Before.AbilityText != After.AbilityText || Before.Kind != After.Kind || Before.Faction != After.Faction ||
        Before.IsGold != After.IsGold || Before.CanBeInStartingDeck != After.CanBeInStartingDeck || !Before.Categories.SetEquals(After.Categories) || !Before.SecondaryFactions.SetEquals(After.SecondaryFactions));
    // Directional stats, not an evaluation of overall strength. Disloyal/fixed-power cards are ambiguous.
    private bool PowerComparable => After.Kind == CardKind.Unit && !(After.AbilityText ?? "").Contains("Disloyal", StringComparison.OrdinalIgnoreCase) &&
        !(After.AbilityText ?? "").Contains("power is always", StringComparison.OrdinalIgnoreCase);
    private int Benefit => After.Kind == CardKind.Leader ? ProvisionDelta : -ProvisionDelta;
    private bool Comparable => Before is not null && !DetailsChanged && Before.PrintedArmor == After.PrintedArmor && (PowerDelta == 0 || PowerComparable);
    public bool StatBuff => Comparable && (Benefit > 0 || PowerDelta > 0) && Benefit >= 0 && PowerDelta >= 0;
    public bool StatNerf => Comparable && (Benefit < 0 || PowerDelta < 0) && Benefit <= 0 && PowerDelta <= 0;
    public string Summary => Before is null ? Name + " · new" : Name + " · " + string.Join("; ", new[]
    {
        PowerDelta != 0 ? $"power {Before.Power} → {After.Power}" : null,
        ProvisionDelta != 0 ? $"{(After.Kind == CardKind.Leader ? "leader bonus" : "provisions")} {Before.Provision} → {After.Provision}" : null,
        Before.PrintedArmor != After.PrintedArmor ? $"armor {Before.PrintedArmor?.ToString() ?? "?"} → {After.PrintedArmor?.ToString() ?? "?"}" : null,
        DetailsChanged ? "card details changed" : null,
    }.Where(s => s is not null));
}

public sealed record CardBalanceChanges(string? FromVersion, string? ToVersion, IReadOnlyList<CardBalanceChange> Cards)
{
    public static CardBalanceChanges Empty { get; } = new(null, null, []);
    public bool Available => FromVersion is not null && ToVersion is not null;
    public static CardBalanceChanges Compare(CardDataSnapshot before, CardDataSnapshot after)
    {
        var old = before.Cards.ToDictionary(c => c.Id);
        return new(before.Version, after.Version, after.Cards.Select(c => new CardBalanceChange(c.Id, c.Name, old.GetValueOrDefault(c.Id), c))
            .Where(c => c.Before is null || c.PowerDelta != 0 || c.ProvisionDelta != 0 || c.Before.PrintedArmor != c.After.PrintedArmor || c.DetailsChanged)
            .OrderBy(c => c.Name).ToArray());
    }
    public static CardBalanceChanges Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var current = new CardDataUpdater(path).Current(); if (current is null) return Empty;
        var candidates = new List<CardDataSnapshot>();
        foreach (var candidatePath in new[] { path + ".previous", path + ".baseline" })
        {
            if (!File.Exists(candidatePath)) continue;
            try
            {
                var candidate = CardDataSnapshot.Parse(File.ReadAllText(candidatePath));
                if (Version.Parse(candidate.Version) <= Version.Parse(current.Version) && (candidate.Version != current.Version || candidate.Hash != current.Hash)) candidates.Add(candidate);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or IOException) { }
        }
        var prior = candidates.OrderByDescending(c => Version.Parse(c.Version)).FirstOrDefault();
        return prior is null ? Empty : Compare(prior, current);
    }

    /// <summary>Bootstrap comparison when the app already had the latest data before updates were introduced.</summary>
    public static async Task<bool> EnsureBaselineAsync(string path, HttpClient client, CancellationToken token = default)
    {
        if (Load(path).Available) return false;
        var current = new CardDataUpdater(path).Current(); if (current is null) return false;
        using var response = await client.GetAsync("https://gwent.one/en/cards/changelog/", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int historyLimit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > historyLimit) throw new InvalidDataException("Patch history exceeds its safety limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > historyLimit) throw new InvalidDataException("Patch history exceeds its safety limit.");
            buffer.Write(block, 0, count);
        }
        var history = Encoding.UTF8.GetString(buffer.ToArray());
        var versions = Regex.Matches(history, @"\bv(\d+\.\d+\.\d+)\b").Select(m => m.Groups[1].Value).Distinct()
            .Select(Version.Parse).Where(v => v < Version.Parse(current.Version)).OrderDescending().ToArray();
        if (versions.Length == 0) return false;
        var prior = await CardDataUpdater.DownloadVersionAsync(client, versions[0].ToString(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Do not attach a comparison to a catalogue replaced by another updater while fetching.
        if (new CardDataUpdater(path).Current()?.Hash != current.Hash) return false;
        var target = path + ".baseline"; var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temporary, prior.Json, token).ConfigureAwait(false); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
}
