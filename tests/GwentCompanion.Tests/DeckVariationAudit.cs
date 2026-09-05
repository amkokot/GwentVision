using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class DeckVariationAudit
{
    public static void Run(string root)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var decks = DeckLibrary.Load(Path.Combine(project, "cache/deck-library.json")).Decks;
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
        var metric = new DeckVariationSimilarity(decks, catalog);
        var pairs = new List<(DeckDefinition A, DeckDefinition B, DeckOverlap Overlap, int Replacements)>();
        var timer = Stopwatch.StartNew(); var checkedPairs = 0;
        foreach (var cohort in decks.Where(d => d.CardCount >= 25).GroupBy(d => (d.Faction, d.Leader, d.CardCount)))
        {
            var lists = cohort.ToArray();
            for (var i = 0; i < lists.Length; i++) for (var j = i + 1; j < lists.Length; j++)
            {
                checkedPairs++; var overlap = metric.Compare(lists[i], lists[j]);
                if (overlap.Ratio >= .75) pairs.Add((lists[i], lists[j], overlap, DeckVariants.Replacements(lists[i], lists[j])));
            }
        }
        var report = new StringBuilder($"# Provision-weighted variation audit\n\n{decks.Length} cached compositions; {checkedPairs:N0} same-faction/leader/size complete-list pairs.\n\n");
        report.AppendLine("Overlap = shared physical copies × common current-catalog price / larger listed provision spend. Leader allowance is not spent provisions. Stratagem differences retain separate variants without reducing card overlap.\n");
        report.AppendLine("| Threshold | Qualifying pairs | Lists with a neighbour | Median replacements | Max replacements |\n|---|---:|---:|---:|---:|");
        foreach (var threshold in new[] { .8, .85, .88, .9, .92, .95, .97 })
        {
            var selected = pairs.Where(p => p.Overlap.Ratio >= threshold).ToArray();
            report.AppendLine($"| {threshold:P0} | {selected.Length} | {selected.SelectMany(p => new[] { p.A.Id, p.B.Id }).Distinct().Count()} | {(selected.Length == 0 ? 0 : selected.OrderBy(p => p.Replacements).ElementAt(selected.Length / 2).Replacements)} | {selected.Select(p => p.Replacements).DefaultIfEmpty().Max()} |");
        }
        string Diff(DeckDefinition a, DeckDefinition b) => string.Join(", ", a.Cards.Where(c => c.Count > b.CountOf(c.Card.Id)).Select(c => $"{c.Card.Name} ×{c.Count - b.CountOf(c.Card.Id)}"));
        foreach (var band in new[] { (.85, .88), (.88, .90), (.90, .92), (.92, .95), (.95, 1.01) })
        {
            report.AppendLine($"\n## Examples {band.Item1:P0}–{band.Item2:P0}\n");
            foreach (var p in pairs.Where(p => p.Overlap.Ratio >= band.Item1 && p.Overlap.Ratio < band.Item2)
                         .OrderByDescending(p => p.Replacements).ThenBy(p => p.A.Id).Take(10))
                report.AppendLine($"- {p.Overlap.Ratio:P2} · {p.Overlap.SharedProvisions}/{Math.Max(p.Overlap.LeftProvisions, p.Overlap.RightProvisions)}p · {p.Replacements} replacements · {p.A.Faction}/{p.A.Leader}: **{p.A.Name}** ↔ **{p.B.Name}**.\n  - A-only: {Diff(p.A, p.B)}\n  - B-only: {Diff(p.B, p.A)}");
        }
        var folder = Path.Combine(project, "diagnostics", "deck-variation-audit"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "overlap.md"), report.ToString());
        File.WriteAllText(Path.Combine(folder, "pairs.json"), JsonSerializer.Serialize(pairs.Select(p => new { Left = p.A.Id, Right = p.B.Id, p.Overlap, p.Replacements })));
        Console.WriteLine(report.ToString().Split("## Examples")[0]); Console.WriteLine($"Audit: {timer.ElapsedMilliseconds}ms; {folder}");
    }
}
