using System.Diagnostics;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class BuilderFillFilterTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var token = catalog.Single(c => c.Id == "202706");
        Check(!StartingDeckRules.IsStartingCard(token), "Woodland Spirit is a generated unit, not a starting card.");
        foreach (var faction in GwentOneCardCatalog.StartingLeaders(catalog).Select(c => c.Faction).Distinct())
        {
            var seed = catalog.First(c => c.Faction == faction && StartingDeckRules.IsStartingCard(c) && c.Kind == CardKind.Unit && c.Provision == 4);
            var timer = Stopwatch.StartNew();
            var result = DeckAutoFill.Build([], catalog, [new(seed)], new HashSet<DeckCopyKey>(), faction, null, null, true);
            Check(result.Leader?.Faction == faction && result.Cards.Sum(c => c.Count) == 25 && result.Errors.Count == 0,
                "Empty-library completion failed for " + faction + ": " + string.Join("; ", result.Errors));
            Check(result.Cards.Any(c => c.Card.Id == seed.Id) && result.Cards.All(c => StartingDeckRules.IsStartingCard(c.Card)), "Seed lost or token suggested.");
            Console.WriteLine($"PASS {faction}: empty-library fill {timer.ElapsedMilliseconds}ms.");
            Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Catalogue fallback is not responsive.");
            var manual = DeckAutoFill.Build([], catalog, [new(seed)], new HashSet<DeckCopyKey>(), faction, result.Leader, null, false);
            Check(manual.Cards.Sum(c => c.Count) == 1, "Turning auto-fill off retained suggestions.");
        }
        foreach (var name in new[] { "Renfri", "Shupe's Day Off", "Golden Nekker" })
        {
            var seed = catalog.Single(c => c.Name == name);
            var fill = DeckAutoFill.Build([], catalog, [new(seed)], new HashSet<DeckCopyKey>(), "Monsters", null, null, true);
            Check(fill.Errors.Count == 0 && fill.Cards.Sum(c => c.Count) == 25, name + " fallback failed: " + string.Join("; ", fill.Errors));
        }
        var leader = catalog.Single(c => c.Name == "Force of Nature");
        var collectible = catalog.First(c => c.Faction == "Monsters" && c.Provision == 4 && StartingDeckRules.IsStartingCard(c));
        StoredObservedCard Seen(CardDefinition c, CardProvenance provenance, int copies = 1) => new(c.Id, c.Name, c.Faction, c.Provision, .99, provenance, ObservedCopies: copies);
        var raw = new[] { Seen(token, CardProvenance.Spawned), Seen(token, CardProvenance.ConfirmedStartingDeck),
            Seen(leader, CardProvenance.ConfirmedStartingDeck), Seen(collectible, CardProvenance.Created),
            Seen(collectible, CardProvenance.ProbableStartingDeck, 2), Seen(collectible, CardProvenance.Replayed, 5) };
        var starting = ObservedDeckStore.StartingCards(raw, catalog);
        Check(starting.Length == 1 && starting[0].Id == collectible.Id && starting[0].ObservedCopies == 2, "Generated sightings polluted the original-copy count.");
        Check(raw.Length == 6 && raw[0].Provenance == CardProvenance.Spawned, "Raw evidence was mutated.");
        var rejectedPath = Path.Combine(root, "GwentCompanion/diagnostics", "invalid-token-draft-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            DeckEditorDraftStore.Save(rejectedPath, new("test-token", "Invalid token draft", "Monsters", leader.Id, null, [new(token)], DateTimeOffset.UtcNow));
            throw new InvalidOperationException("Draft store accepted a generated token.");
        }
        catch (InvalidDataException) { Check(!File.Exists(rejectedPath), "Rejected token draft was written."); }
        foreach (var record in new ObservedDeckStore().Load(Path.Combine(root, "GwentCompanion/cache/observed-decks")))
            Check(ObservedDeckStore.StartingCards(record.Cards, catalog).All(c => c.Id != token.Id && StartingDeckRules.CountsAgainstStartingDeck(c.Provenance)),
                "Saved match still exposes a token/generated card.");
        var exclusions = new HashSet<DeckCopyKey> { new(collectible.Id, 2) };
        var excludedFill = DeckAutoFill.Build([], catalog, [new(collectible)], exclusions, "Monsters", leader, null, true);
        Check(excludedFill.Errors.Count == 0 && excludedFill.Cards.Single(c => c.Card.Id == collectible.Id).Count == 1, "Excluded bronze copy was reintroduced.");
        var tooMany = DeckAutoFill.Build([], catalog, [new(collectible, 3)], new HashSet<DeckCopyKey>(), "Monsters", leader, null, true);
        Check(tooMany.Errors.Count > 0 && tooMany.Cards.Single().Count == 3, "Invalid fixed cards silently replaced.");
        var noFaction = DeckAutoFill.Build([], catalog, [], new HashSet<DeckCopyKey>(), null, null, null, true);
        Check(noFaction.Cards.Count == 0 && noFaction.Reason.Contains("faction"), "Blank draft invented a faction.");
        var smallLeader = leader with { Id = "tail-leader", Name = "Tail test", Provision = 0 };
        var fixedTail = Enumerable.Range(0, 23).Select(i => new DeckCard(new CardDefinition("tail-fixed-" + i,
            "Fixed " + i, "Monsters", CardKind.Unit, 6, IsGold: true))).ToArray();
        var expensive = new CardDefinition("tail-expensive", "Popular but unaffordable", "Monsters", CardKind.Unit, 8, IsGold: true);
        var cheapA = expensive with { Id = "tail-a", Name = "Affordable A", Provision = 5 };
        var cheapB = cheapA with { Id = "tail-b", Name = "Affordable B" };
        var donors = Enumerable.Range(0, 2).Select(i => new DeckDefinition("tail-donor-" + i, "Donor " + i, "Monsters", smallLeader.Name, 0,
            [.. fixedTail, new(expensive), new(expensive with { Id = "invalid-donor-" + i, CanBeInStartingDeck = false })])).ToArray();
        var tail = DeckAutoFill.Build(donors, fixedTail.Select(c => c.Card).Concat(new[] { expensive, cheapA, cheapB, smallLeader }),
            fixedTail, new HashSet<DeckCopyKey>(), "Monsters", smallLeader, null, true);
        Check(tail.Errors.Count == 0 && tail.Cards.Sum(c => c.Count) == 25 && tail.Cards.All(c => c.Card.Id != expensive.Id),
            "Popular card stranded the last slot despite an affordable legal completion.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { DeckAutoFill.Build([], catalog, [], new HashSet<DeckCopyKey>(), "Monsters", leader, null, true, cancelled.Token); throw new InvalidOperationException("Cancelled fill returned a result."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS catalogue fallback, constraints, exclusions, cancellation, token/provenance filtering and preserved journals.");
    }
}
