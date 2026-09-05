using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>Connected one-card substitutions, indexed by the multiset left after removing one copy.</summary>
public static class DeckCompositionFamilies
{
    public static int[] Build(IReadOnlyList<DeckDefinition> decks)
    {
        var parent = Enumerable.Range(0, decks.Count).ToArray();
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < decks.Count; i++)
        {
            var copies = decks[i].Cards.SelectMany(c => Enumerable.Repeat(c.Card.Id, c.Count)).Order(StringComparer.Ordinal).ToArray();
            for (var remove = 0; remove < copies.Length; remove++)
            {
                if (remove > 0 && copies[remove] == copies[remove - 1]) continue;
                // Length prefixes avoid ambiguity even for synthetic/user card IDs.
                var key = decks[i].Faction.ToUpperInvariant() + "|" + string.Concat(copies.Where((_, j) => j != remove).Select(id => id.Length + ":" + id));
                if (signatures.TryGetValue(key, out var previous)) parent[Root(i)] = Root(previous);
                else signatures.Add(key, i);
            }
        }
        return Enumerable.Range(0, decks.Count).Select(Root).ToArray();
    }
}
