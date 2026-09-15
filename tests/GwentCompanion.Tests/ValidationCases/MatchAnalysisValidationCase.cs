using System.IO;
using GwentCompanion.Core.Data;

internal sealed class MatchAnalysisValidationCase : IContributorValidationCase
{
    public string Id => "match-analysis";
    public string Kind => "data-integrity";
    public string Summary => "Filter saved patches, deduplicate revisions, and keep missing outcomes out of win-rate denominators.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool ok, string message) => ContributorValidationContext.Check(ok, message);
        var directory = context.PathFromRoot("diagnostics", "match-analysis-tests", Guid.NewGuid().ToString("N"));
        var store = new LocalMatchStore(directory);
        var install = Guid.NewGuid(); var player = new MatchPlayer("Monsters", "Fruits of Ysgith", null, [], [], []);
        CompactMatch Match(string patch, string? result, string faction = "Nilfgaard") => new(install, Guid.NewGuid(),
            new(2026, 9, 10), "fixture", "rules", patch, true, 1, true, result is not null, result,
            null, null, null, false, null, player, player with { Faction = faction }, [], []);
        var win = Match("14.9", "VICTORY") with { FactionMmr = true, MmrAfter = 2408, MmrChange = 8 };
        var loss = Match("14.9", "DEFEAT") with { FactionMmr = false, MmrAfter = 9999, MmrChange = 100 };
        var draw = Match("14.9", "DRAW", "Skellige");
        var unknown = Match("14.9", null, "Unknown faction") with { ResultObserved = true };
        foreach (var match in new[] { win, loss, draw, unknown, Match("14.10", "VICTORY") }) store.Save(match);
        var duplicate = Path.Combine(directory, "copied.gvm"); File.Copy(store.Save(win), duplicate);
        File.WriteAllText(Path.Combine(directory, "damaged.gvm"), "not a match");
        var data = MatchAnalysis.Load(directory);
        Check(data.Entries.Length == 5 && data.DuplicateFiles == 1 && data.UnreadableFiles == 1, "Loader counted duplicates or failed to isolate damaged files.");
        Check(MatchAnalysis.Patches(data.Entries).SequenceEqual(new[] { "14.10", "14.9" }), "Patch ordering is lexical instead of numeric.");
        var filtered = MatchAnalysis.Filter(data.Entries, "14.9");
        Check(filtered.Length == 4 && filtered.All(m => m.Patch == "14.9" && m.PatchInferred), "Saved patch / inferred metadata lost or filter leaked another patch.");
        Check(LocalMatchStore.Read(store.Save(win)).Patch == "14.9", "Patch was not persisted in the compressed record.");
        var totals = MatchAnalysis.Totals(filtered);
        Check(totals is { Wins: 1, Losses: 1, Draws: 1, Unknown: 1, WinRate: 50, MmrSamples: 1, AverageFactionMmr: 2408, NetMmrChange: 8 }, "Unknown results/draws or unqualified MMR contaminated statistics.");
        var groups = MatchAnalysis.Breakdown(filtered, m => m.OpponentFaction, MatchAnalysis.Factions);
        Check(groups.Single(g => g.Key == "Nilfgaard") is { Games: 2, Share: 50, WinRate: 50 }, "Faction denominator incorrect.");
        Check(groups.Single(g => g.Key == "Skellige") is { Draws: 1, WinRate: null }, "Draw-only sample should have no decisive win rate.");
        Check(groups.Single(g => g.Key == "Syndicate") is { Games: 0, Share: 0, WinRate: null }, "Absent factions should remain visible without invented win rates.");
        Check(groups.Sum(g => g.Share) == 100, "Unknown opponents must count in exposure denominator.");
        var review = MatchAnalysis.MatchupToReview([
            new("Nilfgaard", 5, 5, 0, 0, 0, 33.3, 100),
            new("Monsters", 4, 1, 3, 0, 0, 26.7, 25),
            new("Northern Realms", 2, 0, 2, 0, 0, 13.3, 0)]);
        Check(review is { Key: "Monsters" }, "An undefeated five-game faction displaced a matchup with recorded losses.");
        Check(MatchAnalysis.MatchupToReview([new("Nilfgaard", 5, 5, 0, 0, 0, 100, 100)]) is null,
            "An undefeated faction was presented as a matchup to review.");
        Check(MatchAnalysis.Totals([]) is { Games: 0, WinRate: null, AverageFactionMmr: null }, "Empty sample became a real zero rating / win rate.");
        Check(MatchAnalysisEntry.From("", draw with { Rank = 3 }).RatingLabel == "Rank 3", "Known ladder rank displayed as missing MMR.");
        Check(MatchAnalysisEntry.From("", loss).RatingLabel == "9999 MMR*", "Unqualified rating was hidden or mislabeled as faction MMR.");
        var start = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var progress = new[] { win with { StartedAtUtc = start }, win with { StartedAtUtc = start.AddMinutes(10), MmrAfter = null },
            win with { StartedAtUtc = start.AddMinutes(20), MmrAfter = 2420 } }.Select(m => MatchAnalysisEntry.From("", m)).Reverse().ToArray();
        Check(MatchAnalysis.RatingProgress(progress, "Monsters").Points.Select(p => p.Rating).SequenceEqual(new int?[] { 2408, null, 2420 }),
            "Faction graph lost chronological order or filled a missing rating.");
        Check(MatchAnalysis.RatingProgress(progress, "Skellige").Points.Length == 0, "Faction graph mixed factions.");
        var legacy = progress.Select(m => m with { StartedAtUtc = null }).ToArray();
        Check(MatchAnalysis.RatingProgress(legacy, "Monsters") is { UnorderedGames: 3 } ambiguous && ambiguous.Points.All(p => p.Rating is null),
            "Legacy same-day records invented a game order.");
        store.Save(win with { Revision = 2, Result = "DRAW" });
        Check(MatchAnalysis.Load(directory).Entries.Single(m => m.MatchId == win.MatchId).Outcome == MatchOutcome.Draw, "Older duplicate won over newer correction.");
        return Task.CompletedTask;
    }
}
