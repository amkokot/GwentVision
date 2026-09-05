using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class SynergyPackageAudit
{
    private sealed record Pair(string Faction, string SourceId, string Source, string TargetId, string Target, int Copies,
        int Joint, int SourceDecks, int TargetDecks, int FactionDecks, double Rate, double ReverseRate, double Lift,
        int RecentJoint, int RecentSource, double? RecentRate, int Families, int ConsistentFamilies, double FamilyLower95,
        string[] Examples, string[] Counterexamples);
    private sealed record Package(string Faction, string[] Cards, int AllTogether, int AnyPresent, int RecentTogether, int RecentAny);

    public static void Run(string root)
    {
        var at = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var decks = library.Decks.Where(DeckMetaAnalyzer.IsComplete).GroupBy(DeckMetaAnalyzer.CompositionKey)
            .Select(g => g.OrderByDescending(DeckMetaAnalyzer.SourceDate).First()).ToArray();
        var names = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(c => c.Id, c => c.Name);
        var pairs = new List<Pair>(); var packages = new List<Package>();
        foreach (var faction in decks.GroupBy(d => d.Faction))
        {
            var lists = faction.ToArray(); var families = DeckCompositionFamilies.Build(lists);
            var counts = lists.Select(d => d.Cards.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count))).ToArray();
            var recent = lists.Select(d => DeckMetaAnalyzer.SourceDate(d) is { } date && date >= at.AddDays(-124) && date <= at.AddDays(1)).ToArray();
            var occurrence = lists.SelectMany((d, i) => d.Cards.Select(c => (c.Card.Id, Index: i))).GroupBy(v => v.Id)
                .ToDictionary(g => g.Key, g => g.Select(v => v.Index).Distinct().ToArray());
            foreach (var (sourceId, source) in occurrence)
            {
                var targets = source.SelectMany(i => counts[i].Keys).Distinct();
                foreach (var targetId in targets)
                {
                    var copies = sourceId == targetId ? 2 : 1;
                    if (copies == 2 && lists.SelectMany(d => d.Cards).First(c => c.Card.Id == sourceId).Card.IsGold) continue;
                    var joint = source.Where(i => counts[i].GetValueOrDefault(targetId) >= copies).ToArray();
                    var recentN = source.Count(i => recent[i]); var recentK = joint.Count(i => recent[i]);
                    var rate = joint.Length / (double)source.Length;
                    var targetN = occurrence[targetId].Count(i => counts[i][targetId] >= copies);
                    var lift = targetN == 0 ? 0 : rate / (targetN / (double)lists.Length);
                    // Keep broad leads as well as near-universal packages. Singleton coincidences
                    // are not discoveries; specifically requested anchors remain inspectable.
                    var requested = sourceId is "202282" or "202277" or "132206" or "132207" or "132208";
                    if (!requested && !(joint.Length >= 5 && rate >= .65 && (lift >= 1.25 || copies == 2)) && !(recentK >= 5 && recentK >= .9 * recentN)) continue;
                    var groups = source.GroupBy(i => families[i]).ToArray();
                    var consistent = groups.Count(g => g.All(i => counts[i].GetValueOrDefault(targetId) >= copies));
                    string Link(int i) => lists[i].SourceUri?.AbsoluteUri ?? lists[i].Id;
                    pairs.Add(new(faction.Key, sourceId, names.GetValueOrDefault(sourceId, sourceId), targetId, names.GetValueOrDefault(targetId, targetId), copies,
                        joint.Length, source.Length, targetN, lists.Length, rate, targetN == 0 ? 0 : joint.Length / (double)targetN, lift,
                        recentK, recentN, recentN == 0 ? null : recentK / (double)recentN, groups.Length, consistent,
                        DeckMetaAnalyzer.WilsonLower(consistent, groups.Length), joint.Take(3).Select(Link).ToArray(),
                        source.Except(joint).Take(3).Select(Link).ToArray()));
                }
            }
            // Reciprocal cliques, not connected components: A-B and B-C alone must
            // never imply A-C. Directional links are retained separately below.
            var strong = pairs.Where(p => p.Faction == faction.Key && p.Copies == 1 && p.SourceId != p.TargetId &&
                p.Joint >= 5 && p.Rate >= .9 && p.ReverseRate >= .9 && p.Lift >= 1.5 &&
                (p.RecentSource < 5 || p.RecentRate >= .85)).ToArray();
            var edges = strong.Select(p => (p.SourceId, p.TargetId)).ToHashSet();
            var found = new Dictionary<string, string[]>();
            foreach (var seed in strong.OrderByDescending(p => p.Joint))
            {
                var group = new List<string> { seed.SourceId, seed.TargetId };
                foreach (var candidate in strong.Where(p => p.SourceId == seed.SourceId).OrderByDescending(p => p.Joint).Select(p => p.TargetId))
                    if (!group.Contains(candidate) && group.All(id => edges.Contains((id, candidate)) && edges.Contains((candidate, id)))) group.Add(candidate);
                var ids = group.Order(StringComparer.Ordinal).ToArray(); found.TryAdd(string.Join('|', ids), ids);
            }
            foreach (var ids in found.Values.Where(ids => !found.Values.Any(other => other.Length > ids.Length && ids.All(other.Contains))))
            {
                var any = Enumerable.Range(0, lists.Length).Where(i => ids.Any(id => counts[i].ContainsKey(id))).ToArray();
                var all = any.Where(i => ids.All(id => counts[i].ContainsKey(id))).ToArray();
                packages.Add(new(faction.Key, ids.Select(id => names.GetValueOrDefault(id, id)).ToArray(), all.Length, any.Length, all.Count(i => recent[i]), any.Count(i => recent[i])));
            }
        }
        var path = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.22-synergy-packages");
        var ordered = pairs.OrderBy(p => p.Faction).ThenBy(p => p.Source).ThenByDescending(p => p.Rate).ThenByDescending(p => p.Joint).ToArray();
        var orderedPackages = packages.OrderBy(p => p.Faction).ThenByDescending(p => p.Cards.Length).ThenByDescending(p => p.AllTogether).ToArray();
        var method = "Curated spreadsheet sample, not ladder probabilities. Complete card multisets deduplicated across URLs, leaders and stratagems; faction-specific baselines. Directed P(target|source), reverse conditional and lift. Recent means author/worksheet dated within 124 days of 2026-08-28; old patch dates are conservative bounds. Connected one-card variants are a sensitivity check, not independent people. Reciprocal >=90% edges with >=5 joint lists and lift >=1.5 form pairwise-complete groups; small groups remain tentative, not hard deck rules. Historical correlations can include previous card versions. Explicit clicks condition the existing whole-deck model; these audit groups do not become extra independent votes.";
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { Method = method, LibraryRecords = library.Records.Count, Compositions = decks.Length,
            RecentCompositions = decks.Count(d => DeckMetaAnalyzer.SourceDate(d) >= at.AddDays(-124)), Packages = orderedPackages, Pairs = ordered }, new JsonSerializerOptions { WriteIndented = true }));
        var lines = new List<string> { "# Synergy-package audit — 2026-08-28", "", method, "",
            $"{library.Records.Count} library records; {decks.Length} complete unique card compositions; {packages.Count} reciprocal package candidates; {pairs.Count} inspected directional/copy relationships.",
            "", "## Sigvald and Knut", "", "Fractions are lists containing the source, not card copies. Recent = last 124 days. Missing recent support is unknown.", "",
            "| Source | Partner | All-history | Recent | Lift within faction | Consistent variant families |", "|---|---|---:|---:|---:|---:|" };
        foreach (var p in ordered.Where(p => p.SourceId is "202282" or "202277" && p.TargetId is "202282" or "202277" or "202456" or "113320" or "203246"))
            lines.Add($"| {p.Source} | {p.Target} | {p.Joint}/{p.SourceDecks} ({p.Rate:P0}) | {p.RecentJoint}/{p.RecentSource} | {p.Lift:F1}× | {p.ConsistentFamilies}/{p.Families} |");
        lines.AddRange(["", "## Reciprocal packages", "", "These are statistical leads, not guarantees. All together/any present checks the whole group, not just individual pairs.", "",
            "| Faction | Cards | All together / any | Recent together / any |", "|---|---|---:|---:|"]);
        foreach (var p in orderedPackages) lines.Add($"| {p.Faction} | {string.Join(" + ", p.Cards)} | {p.AllTogether}/{p.AnyPresent} | {p.RecentTogether}/{p.RecentAny} |");
        lines.AddRange(["", "## Strong directional links and second copies", "", "A → B does not imply B → A. Example and counterexample source links are retained in the JSON, alongside recent counts.", "",
            "| Source → target | All-history | Recent | Reverse | Lift | Family lower 95% |", "|---|---:|---:|---:|---:|---:|"]);
        foreach (var p in ordered.Where(p => p.Joint >= 5 && p.Rate >= .95 && (p.Lift >= 1.5 || p.Copies == 2)))
            lines.Add($"| {p.Source} → {p.Target}{(p.Copies == 2 ? " ×2" : "")} | {p.Joint}/{p.SourceDecks} | {p.RecentJoint}/{p.RecentSource} | {p.ReverseRate:P0} | {p.Lift:F1}× | {p.FamilyLower95:P0} |");
        File.WriteAllLines(path + ".md", lines);
        Console.WriteLine($"Synergy audit: {decks.Length} complete compositions, {packages.Count} reciprocal groups, {pairs.Count} directional/copy links. {path}.md");
        foreach (var p in pairs.Where(p => p.SourceId == "202282" && p.TargetId is "202277" or "202456" or "113320" or "203246"))
            Console.WriteLine($"Sigvald -> {p.Target}: {p.Joint}/{p.SourceDecks}; recent {p.RecentJoint}/{p.RecentSource}");
        ValidatePrediction(decks, root, names);
    }

    private static void ValidatePrediction(DeckDefinition[] decks, string root, Dictionary<string, string> names)
    {
        var sigvald = decks.SelectMany(d => d.Cards).First(c => c.Card.Id == "202282").Card;
        var analyzer = new DeckMetaAnalyzer(); var timer = Stopwatch.StartNew();
        var before = analyzer.Analyze(decks, [], "Skellige");
        var after = analyzer.Analyze(decks, [], "Skellige", assumptions: [new(sigvald)]);
        var rows = new List<object>();
        foreach (var id in new[] { "202277", "202456", "113320", "203246" })
        {
            var b = before.Cards.Single(c => c.Card.Id == id); var a = after.Cards.Single(c => c.Card.Id == id);
            if (a.ConditionalPresence <= b.ConditionalPresence) throw new InvalidOperationException("Actual expanded-cache Sigvald pick failed to lift " + names[id]);
            if (a.SupportingDecks != b.SupportingDecks || after.ObservedIdentities != 0) throw new InvalidOperationException("Pick fabricated evidence.");
            rows.Add(new { Card = names[id], Before = b.ConditionalPresence, After = a.ConditionalPresence, a.Associations });
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.22-expanded-cache-prediction.json"),
            JsonSerializer.Serialize(new { ElapsedSeconds = timer.Elapsed.TotalSeconds, Corpus = after.CorpusDecks, Rows = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Actual-cache Sigvald prediction check passed; two analyses took {timer.Elapsed.TotalSeconds:F2}s.");
    }
}
