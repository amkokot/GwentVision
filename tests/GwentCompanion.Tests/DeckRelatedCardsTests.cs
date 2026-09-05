using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class DeckRelatedCardsTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var cards = Enumerable.Range(0, 25).Select(i => new DeckCard(new("related" + i, "Core " + i, "Skellige", CardKind.Unit, 5))).ToArray();
        var low = new CardDefinition("low", "Related low provision", "Skellige", CardKind.Unit, 4);
        var high = low with { Id = "high", Name = "Unrelated high provision", Provision = 14 };
        var baseDeck = new DeckDefinition("a", "Full deck", "Skellige", "Onslaught", 16, cards);
        var near = baseDeck with { Id = "b", Cards = cards.Take(24).Append(new DeckCard(low)).ToArray() };
        var far = baseDeck with { Id = "c", Cards = cards.Take(4).Concat(Enumerable.Range(0, 20).Select(i => new DeckCard(low with { Id = "filler" + i }))).Append(new DeckCard(high)).ToArray() };
        var corpus = new[] { baseDeck, near, far }; var catalog = corpus.SelectMany(d => d.Cards).Select(c => c.Card).ToArray();
        RelatedCardReport Rank(IEnumerable<DeckDefinition> source, IReadOnlyList<DeckCard>? focus = null, IReadOnlySet<DeckCopyKey>? excluded = null) =>
            DeckRelatedCards.Rank(source, catalog, cards, focus ?? cards, "Skellige", "Onslaught", null, excluded ?? new HashSet<DeckCopyKey>());
        var full = Rank(corpus);
        Check(full.Scores.GetValueOrDefault(low.Id) > 0 && full.Scores.GetValueOrDefault(low.Id) > full.Scores.GetValueOrDefault(high.Id),
            "Full deck recommendations reverted to provisions or favored unrelated cards.");
        Check(full.Lists == 1 && !full.ExactCore && Math.Abs(full.BestOverlap - .96) < 1e-12, "Nearest-list overlap is incorrect.");
        var focused = Rank(corpus, [cards[24]]);
        Check(focused.Lists == 0, "Focused recommendation ignored the selected card.");
        var focusedAlternative = far with { Id = "focus", Cards = far.Cards.Skip(1).Append(cards[24]).ToArray() };
        focused = Rank(corpus.Append(focusedAlternative), [cards[24]]);
        Check(focused.ExactCore && focused.Scores.GetValueOrDefault(high.Id) > 0 && !focused.Scores.ContainsKey(low.Id), "Selected-card focus failed to change the ranking basis.");
        var repeated = Rank(corpus.Concat(corpus.Select(d => d with { Id = d.Id + "copy", Name = "Alias" })));
        Check(full.Lists == repeated.Lists && full.Scores.OrderBy(p => p.Key).SequenceEqual(repeated.Scores.OrderBy(p => p.Key)), "Duplicate lists inflated recommendations.");
        Check(Rank([near with { Leader = "Ursine Ritual" }]).Lists == 0 && Rank([near with { Faction = "Monsters" }]).Lists == 0,
            "Recommendation headers ignored.");
        Check(Rank([near], excluded: new HashSet<DeckCopyKey> { new(low.Id, 1) }).Lists == 0, "Excluded copies returned as recommendations.");
        Check(Rank([baseDeck]).Lists == 0, "Exact full list invented alternative cards.");
        var bronze = cards[0];
        var extraCopy = baseDeck with { Cards = cards.SkipLast(1).Select(c => c.Card.Id == bronze.Card.Id ? c with { Count = 2 } : c).ToArray() };
        Check(Rank([extraCopy]).Scores.ContainsKey(bronze.Card.Id), "Available second bronze copy was not recommended.");
        Console.WriteLine("PASS related cards: full-deck alternatives beat unrelated high provisions; explicit focus; duplicate invariance; headers/exclusions; exact-list no-data case and physical-copy availability.");
    }
}
