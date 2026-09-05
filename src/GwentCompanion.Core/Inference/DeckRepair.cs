using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record DeckRepairOption(DeckDefinition Deck, int Swaps, string Reason);
public sealed record DeckRepairResult(IReadOnlyList<DeckRepairOption> Options, string Message);

/// <summary>Bounded, builder-only substitutions. Never edits library evidence or changes deck headers.</summary>
public static class DeckRepair
{
    private static bool Payoff(CardDefinition c) => c.Name is "Renfri" or "Golden Nekker" or "Ciri: Nova" or "Shupe's Day Off" or "Radeyah";
    public static DeckRepairResult Suggest(DeckDefinition source, IEnumerable<DeckDefinition> library, IReadOnlyList<CardDefinition> catalog,
        IReadOnlyList<DeckCard>? protectedCards = null, IReadOnlySet<DeckCopyKey>? excluded = null, CancellationToken cancellation = default)
    {
        var values = new CurrentCardValues(catalog); var original = values.Deck(source);
        var leader = GwentOneCardCatalog.StartingLeaders(catalog).FirstOrDefault(c => c.Faction == original.Faction && c.Name == original.Leader);
        if (leader is null || original.CardCount < 25 || original.CardCount > 40)
            return new([], "Choose a leader and start with a complete 25–40 card deck. Auto-fill can finish partial drafts first.");
        if (DeckBuildValidation.Errors(original, archetypes: true).Count == 0)
            return new([], "Already valid with current card values. Use Recommended or the update filter to explore optional changes.");
        excluded ??= new HashSet<DeckCopyKey>();
        var keep = (protectedCards ?? []).Concat(original.Cards.Where(c => Payoff(c.Card))).GroupBy(c => c.Card.Id)
            .ToDictionary(g => g.Key, g => g.Max(c => c.Count));
        if (keep.Any(k => original.CountOf(k.Key) < k.Value)) return new([], "Protected copies are not all present in this deck.");
        var devotion = original.Cards.All(c => c.Card.Faction != "Neutral");
        bool Allowed(DeckDefinition d) => d.CardCount == original.CardCount && keep.All(k => d.CountOf(k.Key) >= k.Value) &&
            !excluded.Any(k => d.CountOf(k.CardId) >= k.Copy) && (!devotion || d.Cards.All(c => c.Card.Faction != "Neutral")) &&
            !d.Cards.Any(c => Payoff(c.Card) && original.CountOf(c.Card.Id) == 0) && DeckBuildValidation.Errors(d, archetypes: true).Count == 0;
        var donors = library.Where(d => d.Faction == original.Faction && d.Leader == original.Leader &&
                (original.Stratagem is null || d.Stratagem is null || d.Stratagem.Id == original.Stratagem.Id))
            .DistinctBy(DeckMetaAnalyzer.CompositionKey).Select(values.Deck).ToArray();
        var report = DeckRelatedCards.Rank(donors, catalog, original.Cards, original.Cards, original.Faction, original.Leader, null, excluded);
        var maxScore = report.Scores.Values.DefaultIfEmpty(1).Max();
        var pool = catalog.Where(c => report.Scores.ContainsKey(c.Id) && c.CanBeInStartingDeck && FactionCompatibility.IsPlayableBy(c, original.Faction) &&
                (!devotion || c.Faction != "Neutral") && (!Payoff(c) || original.ContainsName(c.Name)))
            .OrderByDescending(c => report.Scores[c.Id]).ThenBy(c => c.Provision).ThenBy(c => c.Id).Take(60).ToArray();
        int Swaps(DeckDefinition d) => original.Cards.Sum(c => Math.Max(0, c.Count - d.CountOf(c.Card.Id)));
        double Cost(DeckDefinition d) => original.Cards.Sum(c => Math.Max(0, c.Count - d.CountOf(c.Card.Id)) * (c.Card.Provision + (c.Card.IsGold ? 3 : 0))) -
            d.Cards.Sum(c => Math.Max(0, c.Count - original.CountOf(c.Card.Id)) * report.Scores.GetValueOrDefault(c.Card.Id) / Math.Max(1e-12, maxScore));
        var results = new Dictionary<string, DeckRepairOption>();
        void Add(DeckDefinition d, string reason)
        {
            if (Allowed(d) && Swaps(d) is > 0 and <= 4)
                results.TryAdd(DeckLibrary.Fingerprint(d), new(d, Swaps(d), reason));
        }
        foreach (var donor in donors)
            Add(original with { Cards = donor.Cards }, "Nearby library composition · " + donor.Name);
        if (pool.Length == 0 && results.Count == 0) return new([], "No related replacements in the library. Add compatible templates or release selected cards; nothing was changed.");
        var beam = new[] { original };
        var visited = new HashSet<string> { DeckLibrary.Fingerprint(original) };
        for (var depth = 1; depth <= 4; depth++)
        {
            cancellation.ThrowIfCancellationRequested();
            var next = new List<DeckDefinition>();
            foreach (var state in beam)
            foreach (var remove in state.Cards.Where(c => original.CountOf(c.Card.Id) > 0 && c.Count > keep.GetValueOrDefault(c.Card.Id)))
            foreach (var add in pool)
            {
                cancellation.ThrowIfCancellationRequested();
                if (add.Id == remove.Card.Id || state.CountOf(add.Id) >= (add.IsGold ? 1 : 2) || excluded.Contains(new(add.Id, state.CountOf(add.Id) + 1))) continue;
                // Budget repair must make progress; equal-cost replacements are useful for unit/faction/archetype violations.
                if (add.Provision > remove.Card.Provision && state.ProvisionTotal > 150 + leader.Provision) continue;
                var cards = state.Cards.Select(c => c.Card.Id == remove.Card.Id ? c with { Count = c.Count - 1 } : c)
                    .Where(c => c.Count > 0).ToList();
                var index = cards.FindIndex(c => c.Card.Id == add.Id);
                if (index < 0) cards.Add(new(add)); else cards[index] = cards[index] with { Count = cards[index].Count + 1 };
                var proposal = original with { Cards = DeckBuilderOrder.Sort(cards).ToArray() };
                if (!visited.Add(DeckLibrary.Fingerprint(proposal))) continue;
                Add(proposal, "Recommended substitutions · shared cards with " + report.Lists + " related lists");
                next.Add(proposal);
            }
            if (results.Values.Any(o => o.Swaps <= depth)) break;
            beam = next.OrderBy(d => DeckBuildValidation.Errors(d, archetypes: true).Count)
                .ThenBy(d => Math.Max(0, d.ProvisionTotal - 150 - leader.Provision)).ThenBy(Cost)
                .ThenBy(DeckLibrary.Fingerprint).Take(24).ToArray();
            if (beam.Length == 0) break;
        }
        var options = results.Values.OrderBy(o => o.Swaps).ThenBy(o => Cost(o.Deck)).ThenByDescending(o => o.Deck.ProvisionTotal)
            .ThenBy(o => DeckLibrary.Fingerprint(o.Deck)).Take(3).ToArray();
        return new(options, options.Length == 0
            ? "No legal repair found within four substitutions. Try releasing protected cards or adding related library lists; nothing was changed."
            : "Leader, stratagem, deck size, selected copies and archetype payoffs are preserved. Review before applying; the source library list is unchanged.");
    }
}
