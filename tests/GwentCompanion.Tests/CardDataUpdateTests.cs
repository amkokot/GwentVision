using System.IO;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class CardDataUpdateTests
{
    public static async Task RunAsync(string root, bool live)
    {
        var source = Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json");
        var original = File.ReadAllText(source); var baseline = CardDataSnapshot.Parse(original);
        var directory = Path.Combine(root, "GwentCompanion/diagnostics/card-data-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var active = Path.Combine(directory, "cards.json"); File.WriteAllText(active, original);
        var updater = new CardDataUpdater(active);
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Reject(Action action, string message)
        { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or IOException) { return; } throw new Exception(message); }
        string Mutate(Action<JsonObject> action)
        { var json = JsonNode.Parse(original)!.AsObject(); action(json); return json.ToJsonString(); }
        string RowKey(JsonObject json, string id) => json["response"]!.AsObject().First(p => p.Value!["id"]!["card"]!.GetValue<int>().ToString() == id).Key;
        JsonObject Attributes(JsonObject json, string id) => json["response"]![RowKey(json, id)]!["attributes"]!.AsObject();
        var unit = baseline.Cards.First(c => c.CanBeInStartingDeck && c.Kind == CardKind.Unit && c.Faction != "Neutral");
        var leader = GwentOneCardCatalog.StartingLeaders(baseline.Cards).First(c => c.Faction == unit.Faction);
        var updatedJson = Mutate(json =>
        {
            Attributes(json, unit.Id)["power"] = unit.Power + 1;
            Attributes(json, unit.Id)["provision"] = unit.Provision + 1;
            Attributes(json, leader.Id)["provision"] = leader.Provision + 1;
        });
        var update = CardDataSnapshot.Parse(updatedJson); var first = updater.Install(update);
        Check(first.Changed && first.Changes.Count == 2 && first.Changes.Any(c => c.Contains("leader bonus")), "Same-patch numeric update not detected.");
        Check(File.ReadAllText(updater.BackupPath) == original && File.ReadAllText(active) == updatedJson, "Atomic active/previous copies incorrect.");
        Check(Directory.GetFiles(Path.Combine(directory, "card-data-history")).Length == 1, "Original patch not archived.");
        var previousBytes = File.ReadAllText(updater.BackupPath);
        Check(!updater.Install(update).Changed && File.ReadAllText(updater.BackupPath) == previousBytes, "Repeated update replaced rollback copy.");
        Check(!updater.Install(CardDataSnapshot.Parse(updatedJson + "\n")).Changed, "Formatting-only change triggered reload.");
        Reject(() => updater.Install(CardDataSnapshot.Parse(Mutate(j => j["request"]!["REQUEST"]!["version"] = "1.0.0"))), "Downgrade accepted.");
        Reject(() => updater.Install(CardDataSnapshot.Parse(Mutate(j => j["response"]!.AsObject().Remove(RowKey(j, unit.Id))))), "Missing existing card accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j => j["request"]!["status"] = 500)), "API error accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j => j["request"]!["REQUEST"]!["language"] = "de")), "Non-English accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j => j["request"]!["REQUEST"]!["version"] = "latest")), "Unversioned catalogue accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j => Attributes(j, unit.Id)["power"] = -1)), "Negative power accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j => j["response"]![RowKey(j, unit.Id)]!["id"]!["card"] = int.Parse(leader.Id))), "Duplicate ID accepted.");
        Reject(() => CardDataSnapshot.Parse(Mutate(j =>
        {
            var response = j["response"]!.AsObject(); var single = response[RowKey(j, unit.Id)]!.DeepClone();
            response.Clear(); response["0"] = single;
        })), "Single-card payload accepted.");
        Reject(() => CardDataSnapshot.Parse("{\"Version\":5,\"Records\":[]}"), "Deck library accepted as card data.");
        Reject(() => CardDataSnapshot.Parse("<html>server failure</html>"), "HTML error accepted.");
        using (var gate = new FileStream(active + ".update-lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Reject(() => updater.Install(baseline), "Concurrent update lock ignored.");
        Check(File.ReadAllText(active) == updatedJson && File.ReadAllText(updater.BackupPath) == previousBytes, "Failed update changed active/backup.");
        Check(updater.RestorePrevious().Changed && File.ReadAllText(active) == original && File.ReadAllText(updater.BackupPath) == updatedJson, "Rollback failed.");
        Check(updater.RestorePrevious().Changed && File.ReadAllText(active) == updatedJson, "Undo rollback failed.");
        var blockedPath = Path.Combine(directory, "blocked", "cards.json"); Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        File.WriteAllText(blockedPath, original); File.WriteAllText(Path.Combine(directory, "blocked", "card-data-history"), "block directory creation");
        Reject(() => new CardDataUpdater(blockedPath).Install(update), "Backup failure was ignored.");
        Check(File.ReadAllText(blockedPath) == original, "Backup failure overwrote active data.");
        var fresh = new CardDataUpdater(Path.Combine(directory, "fresh", "cards.json"));
        Check(fresh.Install(baseline).Changed && fresh.Current()?.Cards.Count == baseline.Cards.Count, "First-time installation failed.");
        Check((await CardDataUpdater.ImportAsync(active)).Cards.Single(c => c.Id == unit.Id).Power == unit.Power + 1, "Offline importer failed.");

        var at = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var deck = new DeckDefinition("test", "Historical", unit.Faction, leader.Name, leader.Provision,
            [new(unit, 2)], Patches: [new(baseline.Version, false, "source")],
            Occurrences: [new("source-event", baseline.Version, "Import", "source", at)], CachedAt: at);
        var library = new DeckLibrary(); library.Merge([deck]); library.EnsureVariationGroups(baseline.Cards);
        var before = JsonSerializer.Serialize(library.Records); var groupsBefore = JsonSerializer.Serialize(library.VariationGroups);
        var values = new CurrentCardValues(update.Cards); var view = values.Deck(library.Decks[0]);
        Check(view.ProvisionTotal == (unit.Provision + 1) * 2 && view.Cards[0].Card.Power == unit.Power + 1 && view.LeaderProvisionBonus == leader.Provision + 1,
            "Current values did not override cached deck numbers.");
        Check(DeckLibrary.Fingerprint(deck) == DeckLibrary.Fingerprint(view), "Updating stats changed composition identity.");
        Check(JsonSerializer.Serialize(library.Records) == before && JsonSerializer.Serialize(library.VariationGroups) == groupsBefore,
            "Current view changed source data or variation groups.");
        Check(view.Occurrences == library.Decks[0].Occurrences && view.Patches == library.Decks[0].Patches && view.CachedAt == at,
            "Update created observations or changed patch dates.");
        var unknown = unit with { Id = "unknown-id", Power = 42 }; Check(values.Card(unknown) == unknown, "Unknown historical card lost.");
        library.Merge([view with { Id = "import-copy" }]);
        Check(library.Records.Count == 1 && DeckOccurrences.Evidence(library.Decks[0].Occurrences).Length == 1, "Current-valued reimport inflated evidence.");
        ReachChecks(directory);

        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(updatedJson) }))))
            Check((await CardDataUpdater.DownloadAsync(client)).Cards.Count == baseline.Cards.Count, "Download path failed.");
        string OfficialJson(IEnumerable<CardDefinition> cards) => JsonSerializer.Serialize(cards.Select(card => new
        {
            _source = new { id = int.Parse(card.Id), name = card.Name, power = card.Power, provisions_cost = card.Provision, armour = card.PrintedArmor ?? 0 }
        }));
        var officialValues = baseline.Cards.Select(card => card.Id == unit.Id ? card with { Power = card.Power + 1 } :
            card.Id == leader.Id ? card with { Provision = card.Provision + 1 } : card).ToArray();
        var overlay = PlayGwentCardValueOverlay.Apply(baseline,
            OfficialJson(officialValues.Where(card => card.Kind != CardKind.Leader)),
            OfficialJson(officialValues.Where(card => card.Kind == CardKind.Leader)), DateTimeOffset.Parse("2026-08-31T23:00:00Z"));
        Check(overlay.Version == "14.9.0" && overlay.Source == PlayGwentCardValueOverlay.SourceName && overlay.Cards.Count == baseline.Cards.Count &&
            overlay.Cards.Single(card => card.Id == unit.Id).Power == unit.Power + 1 &&
            overlay.Cards.Single(card => card.Id == leader.Id).Provision == leader.Provision + 1,
            "Month-end PlayGWENT overlay did not preserve membership or apply official numeric values as patch 14.9.");
        Reject(() => PlayGwentCardValueOverlay.Apply(baseline, "[]", "[]", DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            "Incomplete official value data was accepted.");
        var unsafeValues = baseline.Cards.Select(card => card.Id == unit.Id ? card with { Power = card.Power + 2 } : card).ToArray();
        Reject(() => PlayGwentCardValueOverlay.Apply(baseline,
            OfficialJson(unsafeValues.Where(card => card.Kind != CardKind.Leader)),
            OfficialJson(unsafeValues.Where(card => card.Kind == CardKind.Leader)), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            "A multi-point official-source disagreement was accepted as a Balance Council overlay.");
        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            try { await CardDataUpdater.DownloadAsync(client); throw new Exception("HTTP failure accepted."); }
            catch (HttpRequestException) { }
        }
        using (var client = new HttpClient(new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); })))
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            try { await CardDataUpdater.DownloadAsync(client, cancel.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        using (var client = new HttpClient(new FakeHandler((_, _) =>
        {
            var content = new StringContent("{}"); content.Headers.ContentLength = CardDataUpdater.MaxBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        })))
        {
            try { await CardDataUpdater.DownloadAsync(client); throw new Exception("Oversized response accepted."); }
            catch (InvalidDataException) { }
        }
        if (live)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var downloaded = await CardDataUpdater.DownloadAsync(client);
            var isolated = new CardDataUpdater(Path.Combine(directory, "live-check", "cards.json"));
            var result = isolated.Install(downloaded);
            Console.WriteLine($"LIVE {result.Source} {result.Version}: {downloaded.Cards.Count} cards; installed only in diagnostics fixture.");
        }
        Check(File.ReadAllText(source) == original, "Tests touched the active user catalogue.");
        var message = "PASS card updates: validation, failure preservation, no-op, offline import, HTTP errors/cancel/size, atomic backup, rollback, first install, current values and history/identity invariance.";
        File.WriteAllText(Path.Combine(directory, "result.txt"), message); Console.WriteLine(message);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }

    private static void ReachChecks(string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var body = new CardDefinition("update-body", "Update body", "Monsters", CardKind.Unit, 4, 5, AbilityText: "", PrintedArmor: 0);
        var token = body with { Id = "update-token", Name = "Update Token", Power = 2, CanBeInStartingDeck = false };
        var spawner = body with { Id = "update-spawner", Name = "Update spawner", Power = 1, AbilityText = "Deploy: Spawn an Update Token on this row." };
        CardDefinition[] before = [body, token, spawner];
        CardDefinition[] after = [body with { Power = 6 }, token with { Power = 3 }, spawner];
        var values = new CurrentCardValues(after);
        var empty = GamePosition.EmptyKnown() with { Round = 1, ActivePlayer = PlayerSide.User };
        ThreatReport Calculate(CardDefinition[] definitions, CardDefinition hover, GamePosition? board = null) =>
            new ThreatAnalyzer(definitions).Analyze(ThreatPositionBuilder.Build(board ?? empty, definitions, hover, null, [], []), 0, []);
        Check(Calculate(before, body).PlayerPlay.MaximumPoints == 5 && Calculate(after, values.Card(body)).PlayerPlay.MaximumPoints == 6,
            "Updated base power did not change Reach from 5 to 6.");
        Check(Calculate(before, spawner).PlayerPlay.MaximumPoints == 3 && Calculate(after, spawner).PlayerPlay.MaximumPoints == 4,
            "Spawned-unit Reach used stale token power.");
        var boosted = empty with { Zones = empty.Zones.Select(z => z.Side == PlayerSide.User && z.Zone == CardZone.Hand
            ? z with { Cards = [new("boosted-hand", body.Id, 9, 6, 0, ImmutableHashSet<CardStatus>.Empty)], TotalCount = 1 } : z).ToImmutableArray() };
        Check(Calculate(after, values.Card(body), boosted).PlayerPlay.MaximumPoints == 9,
            "Catalogue power overwrote visible boosted hand power.");
        var unread = empty with { Zones = empty.Zones.Select(z => z.Side == PlayerSide.User && z.Zone == CardZone.Hand
            ? z with { Cards = [new("unread-hand", body.Id, null, null, null, null)], TotalCount = 1 } : z).ToImmutableArray() };
        var resolved = ThreatPositionBuilder.Build(unread, after, values.Card(body), null, [], []).Position.Zone(PlayerSide.User, CardZone.Hand).Cards.Single();
        Check(resolved.Power == 6 && resolved.BasePower == 6, "Unread Reach stats did not use the updated catalogue.");
        var profilePath = Path.Combine(directory, "create-profiles.json");
        var oldProfiles = CreatePointProfiles.LoadOrBuild(profilePath, before);
        var newProfiles = CreatePointProfiles.LoadOrBuild(profilePath, after);
        Check(oldProfiles.Fingerprint != newProfiles.Fingerprint && oldProfiles.Values[body.Id][0] == 5 && newProfiles.Values[body.Id][0] == 6,
            "Create estimates reused cached values after a power update.");
        Check(CreatePointProfiles.Signature(after) != CreatePointProfiles.Signature(after.Select(c => c.Id == body.Id ? c with { Provision = 5 } : c)),
            "Provision changes did not invalidate Create estimates.");
        Console.WriteLine("PASS Reach update: body 5→6, spawn 3→4, observed boost stays 9, unread stats use new power; Create cache rebuilds for power/provisions.");
    }
}
