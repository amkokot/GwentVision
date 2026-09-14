namespace GwentCompanion.Core.Data;

public enum MatchOutcome { Unknown, Win, Loss, Draw }

/// <summary>Small analysis index; full decks and action arrays are loaded only when a match is expanded.</summary>
public sealed record MatchAnalysisEntry(string Path, Guid InstallationId, Guid MatchId, DateOnly Date,
    string Patch, bool PatchInferred, long Revision, bool CaptureStopped, MatchOutcome Outcome,
    string UserFaction, string UserLeader, string OpponentFaction, string OpponentLeader,
    int? FactionMmr, int? MmrChange, int ObservedOpponentIdentities, int HypothesizedOpponentCopies,
    int PlayPreviews, bool SequenceTruncated, int? Rank = null, int? UnqualifiedMmr = null, DateTimeOffset? StartedAtUtc = null,
    bool MmrUnconfirmed = false)
{
    public static MatchAnalysisEntry From(string path, CompactMatch match) => new(path, match.InstallationId,
        match.MatchId, match.GameDateUtc, Clean(match.Patch, "Unknown patch"), match.PatchInferred,
        match.Revision, match.CaptureStopped, ParseOutcome(match.Result), Clean(match.User.Faction),
        Clean(match.User.Leader, "Unknown leader"), Clean(match.Opponent.Faction),
        Clean(match.Opponent.Leader, "Unknown leader"), match.FactionMmr ? match.MmrAfter : null,
        match.FactionMmr ? match.MmrChange : null,
        match.Opponent.Observations.Select(c => c.CardId).Distinct(StringComparer.Ordinal).Count(),
        match.Opponent.Hypothesis.Sum(c => c.Copies), match.Actions.Count(a => a.Kind == "PlayPreview"), match.SequenceTruncated,
        match.Rank, match.FactionMmr ? null : match.MmrAfter, match.StartedAtUtc, match.MmrUnconfirmed);

    public string RatingLabel => FactionMmr is { } mmr ? $"{mmr} fMMR" + (MmrUnconfirmed ? " (unconfirmed)" : "") : Rank is { } rank ? $"Rank {rank}"
        : UnqualifiedMmr is { } other ? $"{other} MMR*" : "Not recorded";

    public static string Clean(string? value, string fallback = "Unknown faction") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    public static MatchOutcome ParseOutcome(string? result) => result?.Trim().ToUpperInvariant() switch
    { "VICTORY" => MatchOutcome.Win, "DEFEAT" => MatchOutcome.Loss, "DRAW" => MatchOutcome.Draw, _ => MatchOutcome.Unknown };
}

public sealed record MatchAnalysisLoad(MatchAnalysisEntry[] Entries, int UnreadableFiles, int DuplicateFiles);
public sealed record MatchRatingPoint(int Game, int? Rating, DateOnly Date, string Patch);
public sealed record MatchRatingSeries(string Faction, MatchRatingPoint[] Points, int UnorderedGames);
public sealed record MatchBreakdown(string Key, int Games, int Wins, int Losses, int Draws, int Unknown,
    double Share, double? WinRate);
public sealed record MatchAnalysisTotals(int Games, int Wins, int Losses, int Draws, int Unknown,
    double? WinRate, int ActiveDays, double? AverageFactionMmr, long? NetMmrChange, int MmrSamples,
    int MmrChangeSamples, double? AverageOpponentIdentities, double? AverageInferredCopies,
    double? AveragePlayPreviews, int InferredPatchGames, int TruncatedSequences);

public static class MatchAnalysis
{
    public static readonly string[] Factions = ["Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate"];

    public static MatchAnalysisLoad Load(string directory, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(directory)) return new([], 0, 0);
        var entries = new Dictionary<(Guid, Guid), MatchAnalysisEntry>();
        var unreadable = 0; var duplicates = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.gvm", SearchOption.AllDirectories))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var record = MatchAnalysisEntry.From(path, LocalMatchStore.Read(path));
                var key = (record.InstallationId, record.MatchId);
                if (entries.TryGetValue(key, out var previous))
                {
                    duplicates++;
                    if (previous.Revision > record.Revision || previous.Revision == record.Revision && previous.CaptureStopped) continue;
                }
                entries[key] = record;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
            { unreadable++; }
        }
        return new(entries.Values.OrderByDescending(m => m.Date).ThenByDescending(m => m.StartedAtUtc).ThenBy(m => m.MatchId).ToArray(), unreadable, duplicates);
    }

    public static MatchAnalysisEntry[] Filter(IEnumerable<MatchAnalysisEntry> records, string? patch) =>
        records.Where(m => patch is null || string.Equals(m.Patch, patch, StringComparison.Ordinal)).ToArray();

    public static string[] Patches(IEnumerable<MatchAnalysisEntry> records) => records.Select(m => m.Patch)
        .Distinct(StringComparer.Ordinal).OrderByDescending(p => Version.TryParse(p, out var v) ? v : new Version(0, 0))
        .ThenBy(p => p, StringComparer.Ordinal).ToArray();

    public static MatchBreakdown[] Breakdown(IReadOnlyCollection<MatchAnalysisEntry> entries,
        Func<MatchAnalysisEntry, string> key, IEnumerable<string>? includeEmpty = null)
    {
        var groups = entries.GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return groups.Keys.Concat(includeEmpty ?? []).Distinct(StringComparer.Ordinal).Select(name =>
        {
            var matches = groups.GetValueOrDefault(name) ?? [];
            var totals = Totals(matches);
            return new MatchBreakdown(name, totals.Games, totals.Wins, totals.Losses, totals.Draws, totals.Unknown,
                entries.Count == 0 ? 0 : 100d * totals.Games / entries.Count, totals.WinRate);
        }).OrderByDescending(g => g.Games).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
    }

    public static MatchAnalysisTotals Totals(IReadOnlyCollection<MatchAnalysisEntry> entries)
    {
        var wins = entries.Count(m => m.Outcome == MatchOutcome.Win);
        var losses = entries.Count(m => m.Outcome == MatchOutcome.Loss);
        var draws = entries.Count(m => m.Outcome == MatchOutcome.Draw);
        var mmrs = entries.Where(m => !m.MmrUnconfirmed && m.FactionMmr.HasValue).Select(m => m.FactionMmr!.Value).ToArray();
        var deltas = entries.Where(m => !m.MmrUnconfirmed && m.MmrChange.HasValue).Select(m => m.MmrChange!.Value).ToArray();
        return new(entries.Count, wins, losses, draws, entries.Count - wins - losses - draws,
            wins + losses == 0 ? null : 100d * wins / (wins + losses), entries.Select(m => m.Date).Distinct().Count(),
            mmrs.Length == 0 ? null : mmrs.Average(), deltas.Length == 0 ? null : deltas.Sum(x => (long)x), mmrs.Length, deltas.Length,
            entries.Count == 0 ? null : entries.Average(m => m.ObservedOpponentIdentities),
            entries.Count == 0 ? null : entries.Average(m => m.HypothesizedOpponentCopies),
            entries.Count == 0 ? null : entries.Average(m => m.PlayPreviews), entries.Count(m => m.PatchInferred),
            entries.Count(m => m.SequenceTruncated));
    }

    public static MatchRatingSeries RatingProgress(IEnumerable<MatchAnalysisEntry> entries, string faction)
    {
        var points = new List<MatchRatingPoint>(); var game = 0; var unordered = 0;
        foreach (var day in entries.Where(m => m.UserFaction == faction).GroupBy(m => m.Date).OrderBy(g => g.Key))
        {
            // A date alone cannot order multiple games. Keep their x-axis slots,
            // but leave a gap rather than draw an invented trajectory.
            var ordered = day.Count() == 1 || day.All(m => m.StartedAtUtc.HasValue) &&
                day.Select(m => m.StartedAtUtc).Distinct().Count() == day.Count();
            if (!ordered) unordered += day.Count();
            foreach (var match in day.OrderBy(m => m.StartedAtUtc))
                points.Add(new(++game, ordered && !match.MmrUnconfirmed ? match.FactionMmr : null, day.Key, match.Patch));
        }
        return new(faction, points.ToArray(), unordered);
    }
}
