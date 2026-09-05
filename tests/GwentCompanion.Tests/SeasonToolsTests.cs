using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class SeasonToolsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string root, bool audit, bool cacheBaseline)
    {
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/season-tools-tests"); Directory.CreateDirectory(folder);
        var leader = new CardDefinition("repair-leader", "Repair leader", "Skellige", CardKind.Leader, 15, CanBeInStartingDeck: false);
        var cards = Enumerable.Range(0, 25).Select(i => new CardDefinition("repair-" + i, "Repair " + i, "Skellige", CardKind.Unit,
            i == 0 ? 24 : i == 1 ? 8 : 6, 5, i < 2, AbilityText: "")).ToArray();
        var a = cards[0] with { Id = "replacement-a", Name = "Related A", Provision = 19 };
        var b = a with { Id = "replacement-b", Name = "Related B", Provision = 18 };
        CardDefinition[] catalog = [.. cards, a, b, leader];
        var source = new DeckDefinition("source", "Older deck", "Skellige", leader.Name, 15, cards.Select(c => new DeckCard(c)).ToArray());
        DeckDefinition Swap(CardDefinition replacement) => source with { Id = replacement.Id, Cards = source.Cards.Select(c => c.Card.Id == cards[0].Id ? new DeckCard(replacement) : c).ToArray() };
        var donors = new[] { source, Swap(a), Swap(b) };
        var before = JsonSerializer.Serialize(source);
        var options = DeckRepair.Suggest(source, donors, catalog);
        Check(options.Options.Count >= 2 && options.Options.All(o => o.Swaps == 1 && DeckBuildValidation.Errors(o.Deck, archetypes: true).Count == 0), "Minimal valid repair not found.");
        Check(options.Options.All(o => o.Deck.Leader == source.Leader && o.Deck.CardCount == 25 && o.Deck.CountOf(cards[1].Id) == 1), "Repair changed header/size/core unnecessarily.");
        Check(before == JsonSerializer.Serialize(source), "Repair mutated source.");
        Check(DeckRepair.Suggest(source, donors, catalog, [new(cards[0])]).Options.Count == 0, "Protected card removed.");
        Check(DeckRepair.Suggest(source, donors, catalog, excluded: new HashSet<DeckCopyKey> { new(a.Id, 1) }).Options.All(o => o.Deck.CountOf(a.Id) == 0), "Excluded card reintroduced.");
        var duplicated = DeckRepair.Suggest(source, donors.Concat(donors).Concat(donors), catalog);
        Check(options.Options.Select(o => DeckLibrary.Fingerprint(o.Deck)).SequenceEqual(duplicated.Options.Select(o => DeckLibrary.Fingerprint(o.Deck))), "Duplicate donors changed repairs.");
        Check(DeckRepair.Suggest(Swap(a), donors, catalog).Options.Count == 0, "Already-valid deck was silently changed.");
        Check(DeckRepair.Suggest(source with { Leader = "unknown" }, donors, catalog).Options.Count == 0, "Unknown leader guessed.");
        Check(DeckRepair.Suggest(source, [], catalog).Options.Count == 0, "Unrelated catalogue filler was presented as a recommendation.");
        // No complete legal donor: combine two well-supported cheap substitutions, preserving the selected core.
        var multiCards = cards.Select((c, i) => c with { Provision = i == 0 ? 10 : 6 }).ToArray();
        var cheapA = a with { Provision = 4 }; var cheapB = b with { Provision = 4 };
        var zeroLeader = leader with { Provision = 0 };
        var multi = source with { Cards = multiCards.Select(c => new DeckCard(c)).ToArray(), LeaderProvisionBonus = 0 };
        var d1 = multi with { Cards = multi.Cards.Select((c, i) => i == 2 ? new DeckCard(cheapA) : c).ToArray() };
        var d2 = multi with { Cards = multi.Cards.Select((c, i) => i == 3 ? new DeckCard(cheapB) : c).ToArray() };
        var combined = DeckRepair.Suggest(multi, [d1, d2], [.. multiCards, cheapA, cheapB, zeroLeader], [new(multiCards[0])]);
        Check(combined.Options.Count > 0 && combined.Options[0].Swaps == 2 && combined.Options[0].Deck.ProvisionTotal == 150, "Two-step recommendation repair failed.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { DeckRepair.Suggest(source, donors, catalog, cancellation: cancel.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        }

        var livePath = Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json");
        var raw = File.ReadAllText(livePath); var current = CardDataSnapshot.Parse(raw);
        var priorNode = JsonNode.Parse(raw)!; priorNode["request"]!["REQUEST"]!["version"] = "14.7.0";
        var newer = JsonNode.Parse(raw)!;
        var rows = newer["response"]!.AsObject().Select(p => p.Value!).Where(c => c["attributes"]!["type"]!.GetValue<string>() == "Unit").Take(4).ToArray();
        rows[0]["attributes"]!["power"] = rows[0]["attributes"]!["power"]!.GetValue<int>() + 1;
        rows[1]["attributes"]!["provision"] = rows[1]["attributes"]!["provision"]!.GetValue<int>() - 1;
        rows[2]["attributes"]!["provision"] = rows[2]["attributes"]!["provision"]!.GetValue<int>() + 1;
        rows[3]["ability"] = "Reworked ability.";
        var changes = CardBalanceChanges.Compare(CardDataSnapshot.Parse(priorNode.ToJsonString()), CardDataSnapshot.Parse(newer.ToJsonString()));
        Check(changes.Cards.Count == 4 && changes.Cards.Count(c => c.StatBuff) == 2 && changes.Cards.Count(c => c.StatNerf) == 1, "Change classification failed.");
        var sample = changes.Cards.First(c => c.StatBuff);
        Check(!(sample with { After = sample.After with { AbilityText = "Disloyal." } }).StatBuff, "Ambiguous/disloyal power labeled a buff.");
        var fixture = Path.Combine(folder, "comparison.json"); File.WriteAllText(fixture, newer.ToJsonString()); File.WriteAllText(fixture + ".baseline", priorNode.ToJsonString());
        Check(CardBalanceChanges.Load(fixture).Cards.Count == 4, "Changes lost on reload.");
        File.WriteAllText(fixture + ".previous", raw);
        Check(CardBalanceChanges.Load(fixture).FromVersion == current.Version, "Latest same-patch correction did not take precedence.");
        var bootstrap = Path.Combine(folder, "bootstrap-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bootstrap, raw);
        var requests = new List<Uri>();
        using (var client = new HttpClient(new HistoryHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host == "gwent.one"
                ? "v14.9.0 v14.8.0 v14.7.0 v14.6.0 v14.7.0" : priorNode.ToJsonString()) };
        })))
        {
            Check(await CardBalanceChanges.EnsureBaselineAsync(bootstrap, client), "Missing history not bootstrapped.");
            Check(requests.Count == 2 && requests[1].Host == "api.gwent.one" && requests[1].Query.Contains("version=14.7.0"), "Did not request the nearest earlier published version.");
            Check(!await CardBalanceChanges.EnsureBaselineAsync(bootstrap, client) && requests.Count == 2, "Existing comparison downloaded again.");
            Check(CardBalanceChanges.Load(bootstrap).FromVersion == "14.7.0" && File.ReadAllText(bootstrap) == raw, "Bootstrap rewrote the catalogue or lost its version.");
        }
        var rejected = Path.Combine(folder, "rejected-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(rejected, raw);
        using (var client = new HttpClient(new HistoryHandler(request => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.Host == "gwent.one" ? "v14.7.0" : raw) })))
        {
            try { await CardBalanceChanges.EnsureBaselineAsync(rejected, client); throw new Exception("Wrong-version history accepted."); }
            catch (InvalidDataException) { }
            Check(!File.Exists(rejected + ".baseline") && File.ReadAllText(rejected) == raw, "Rejected history altered installed data.");
        }
        using (var client = new HttpClient(new HistoryHandler(_ => throw new HttpRequestException("Offline test"))))
        {
            try { await CardBalanceChanges.EnsureBaselineAsync(rejected, client); throw new Exception("HTTP failure ignored."); }
            catch (HttpRequestException) { }
            Check(!File.Exists(rejected + ".baseline") && File.ReadAllText(rejected) == raw, "Offline history altered installed data.");
        }
        // After rollback, a newer sidecar must never masquerade as the previous patch.
        File.WriteAllText(rejected, priorNode.ToJsonString()); File.WriteAllText(rejected + ".baseline", raw);
        Check(!CardBalanceChanges.Load(rejected).Available, "Rollback used future history.");
        Check(File.ReadAllText(livePath) == raw, "Tests changed current values.");
        Console.WriteLine("PASS season tools: minimal/multi-step repair, protection, exclusions, donor dedup, no data, cancellation, current validation, persistent change filters and baseline fetch/no-op/wrong-version/offline/rollback safety.");
        if (audit) await AuditAsync(root, folder, current, cacheBaseline);
    }

    private static async Task AuditAsync(string root, string folder, CardDataSnapshot current, bool cacheBaseline)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var snapshots = new List<CardDataSnapshot>();
        foreach (var version in new[] { "14.6.0", "14.7.0" })
        {
            var path = Path.Combine(folder, version + ".json"); var updater = new CardDataUpdater(path);
            var snapshot = updater.Current() ?? await CardDataUpdater.DownloadVersionAsync(client, version);
            if (updater.Current() is null) updater.Install(snapshot); snapshots.Add(snapshot);
        }
        snapshots.Add(current);
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var report = new List<string> { "# Recent balance history and predictor-prior assessment", "", "Source: https://gwent.one/en/cards/changelog/ and versioned English gwent.one JSON.", "" };
        for (var i = 1; i < snapshots.Count; i++)
        {
            var changes = CardBalanceChanges.Compare(snapshots[i - 1], snapshots[i]);
            var buffs = changes.Cards.Where(c => c.StatBuff && c.After.CanBeInStartingDeck).ToArray();
            DeckDefinition[] Patch(string version)
            {
                PatchRecency.TryIndex(version, out var target);
                return library.Decks.Where(d => (d.Patches ?? []).Any(p => !p.Inferred && PatchRecency.TryIndex(p.Label, out var index) && index == target))
                    .DistinctBy(DeckMetaAnalyzer.CompositionKey).ToArray();
            }
            var old = Patch(changes.FromVersion!); var next = Patch(changes.ToVersion!);
            report.Add($"## {changes.FromVersion} → {changes.ToVersion}"); report.Add("");
            report.Add($"{changes.Cards.Count} changed identities; {buffs.Length} collectible stat buffs, {changes.Cards.Count(c => c.StatNerf)} stat nerfs. Explicit-patch library lists: {old.Length} before, {next.Length} after.");
            var shifts = buffs.Select(c => (Card: c, Before: old.Count(d => d.CountOf(c.CardId) > 0), After: next.Count(d => d.CountOf(c.CardId) > 0))).ToArray();
            report.Add($"Buffed cards appearing in either sample: {shifts.Count(s => s.Before + s.After > 0)} / {buffs.Length}.");
            if (old.Length > 0 && next.Length > 0) report.Add($"Shares rise for {shifts.Count(s => s.After / (double)next.Length > s.Before / (double)old.Length)}, fall for {shifts.Count(s => s.After / (double)next.Length < s.Before / (double)old.Length)}; remaining shares are equal.");
            report.Add(""); report.AddRange(changes.Cards.Select(c => "- " + c.Summary)); report.Add("");
        }
        report.Add("## Decision"); report.Add("");
        report.Add("Keep the default predictor unchanged. These are curated, composition-deduplicated lists, not independent ladder matches; repeat appearances and patch coverage do not identify the effect of a buff. A flat bonus could double-count the existing patch/recency prior and overstate ambiguous power changes. Use changes as a builder discovery filter. Revisit an opt-in, capped new-patch prior with held-out match evidence; do not manufacture observations or fitted probabilities.");
        File.WriteAllLines(Path.Combine(folder, "balance-prior-audit.md"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report.Where(s => s.Contains("identities;") || s.Contains("appearing") || s.Contains("Shares rise"))));
        if (cacheBaseline)
        {
            var path = Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json");
            Console.WriteLine("Comparison baseline cached: " + await CardBalanceChanges.EnsureBaselineAsync(path, client));
        }
    }

    private sealed class HistoryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
