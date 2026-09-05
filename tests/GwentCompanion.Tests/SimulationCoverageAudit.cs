using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class SimulationCoverageAudit
{
    public static void Run(string root)
    {
        var companion = Path.Combine(root, "GwentCompanion");
        var catalog = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(companion, "cache", "gwent-one-cards.json")))
            .Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .DistinctBy(card => card.Id).ToArray();
        var observed = ReadObservedCounts(Path.Combine(companion, "cache", "observed-decks"));
        var approximate = new ApproximatePointModel(catalog);
        var profiles = CreatePointProfiles.LoadOrBuild(Path.Combine(companion, "cache", "create-point-profiles.json"), catalog);
        var empty = GamePosition.EmptyKnown();
        var rows = catalog.Select(card =>
        {
            var rule = PlayRules.Compile(card);
            var unresolved = string.Join(" ", new[] { rule.Unmodeled, rule.UnmodeledDeploy, rule.Partial }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var direct = approximate.Estimate(card, empty, PlayerSide.User);
            var statistical = card.AbilityText?.Contains("Create", StringComparison.Ordinal) == true
                ? profiles.Estimate(card, empty, PlayerSide.User, catalog, new("Scoia'tael", "Monsters")) : null;
            if (statistical?.ModeledCards == 0) statistical = null;
            var probabilityModel = statistical is not null || card.Id == "202397";
            var strict = rule.Unmodeled is null && rule.UnmodeledDeploy is null && rule.Partial is null;
            return new
            {
                card.Id, card.Name, card.Faction, card.Kind, card.Provision,
                Observed = observed.GetValueOrDefault(card.Name),
                Strict = strict,
                Direct = direct is not null,
                Statistical = probabilityModel,
                Resolution = strict ? "ExactTransition" : probabilityModel ? "StatisticalDistribution" : direct?.Quality switch
                {
                    PointEstimateQuality.StateAware => "StateAwareBound",
                    PointEstimateQuality.Bounded => "BoundedPartial",
                    PointEstimateQuality.BaselineOnly => "BodyOnly",
                    _ => "Unsupported"
                },
                Signals = Signals(unresolved),
                Unresolved = unresolved,
            };
        }).ToArray();
        var gaps = rows.Where(row => !row.Strict)
            .OrderByDescending(row => row.Observed).ThenByDescending(row => row.Signals.Contains("immediate"))
            .ThenByDescending(row => row.Provision).ThenBy(row => row.Name).ToArray();
        var output = Path.Combine(companion, "diagnostics", "simulation-gap-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Strict = rows.Count(row => row.Strict), Total = rows.Length,
            Resolution = rows.GroupBy(row => row.Resolution).OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count()),
            PointRelevantGaps = gaps.Count(row => row.Signals.Length > 0),
            ObservedGaps = gaps.Count(row => row.Observed > 0),
            Families = gaps.SelectMany(row => row.Signals).GroupBy(signal => signal)
                .OrderByDescending(group => group.Count()).ToDictionary(group => group.Key, group => group.Count()),
            Gaps = gaps,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"simulation gaps strict={rows.Count(row => row.Strict)}/{rows.Length} observed={gaps.Count(row => row.Observed > 0)} output={output}");
        foreach (var row in gaps.Take(80))
            Console.WriteLine($"{row.Observed,3} {row.Provision,2} {row.Name,-32} [{string.Join(',', row.Signals)}] {row.Unresolved}");
    }

    private static string[] Signals(string text)
    {
        var signals = new List<string>();
        void Add(string signal, string pattern)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)) signals.Add(signal);
        }
        Add("immediate", @"Deploy|Damage|Boost|Heal|Destroy|Banish|Consume|Spawn|Summon|Play .* from");
        Add("play-engine", @"Whenever|When .*play|first time .*enters");
        Add("turn-engine", @"At the (?:start|end) of your turn|Timer");
        Add("click", @"Order|Fee|Charge|Cooldown");
        Add("graveyard", @"graveyard|Deathwish");
        Add("deck", @"deck|starting deck");
        Add("weather", @"Frost|Fog|Rain|Storm|Cataclysm|Blood Moon");
        Add("scenario", @"Scenario|Chapter|Counter");
        Add("status", @"Bleeding|Vitality|Poison|Bounty|Shield|Lock|Infuse");
        Add("random", @"random|Create");
        return signals.Distinct().ToArray();
    }

    private static Dictionary<string, int> ReadObservedCounts(string directory)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("Cards", out var cards)) continue;
                foreach (var card in cards.EnumerateArray())
                {
                    if (!card.TryGetProperty("Name", out var nameElement) || nameElement.GetString() is not { } name) continue;
                    var copies = card.TryGetProperty("ObservedCopies", out var countElement) ? Math.Max(1, countElement.GetInt32()) : 1;
                    result[name] = result.GetValueOrDefault(name) + copies;
                }
            }
            catch (JsonException) { }
        }
        return result;
    }
}
