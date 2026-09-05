using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record DeckAutoFillResult(IReadOnlyList<DeckCard> Cards, CardDefinition? Leader,
    CardDefinition? Stratagem, string Reason, IReadOnlyList<string> Errors);

/// <summary>Builder-only completion. Fixed clicks are the only inputs; suggestions never feed back as evidence.</summary>
public static class DeckAutoFill
{
    public static DeckAutoFillResult Build(IEnumerable<DeckDefinition> library, IEnumerable<CardDefinition> catalog,
        IReadOnlyList<DeckCard> fixedCards, IReadOnlySet<DeckCopyKey> excluded, string? faction, CardDefinition? leader,
        CardDefinition? stratagem, bool enabled, CancellationToken cancellation = default)
    {
        var definitions = catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        CardDefinition Current(CardDefinition c) => definitions.GetValueOrDefault(c.Id) ?? c;
        var seeds = fixedCards.Select(c => c with { Card = Current(c.Card) }).ToArray();
        var seedFactions = seeds.Where(c => c.Card.Faction != "Neutral" && c.Card.SecondaryFactions.Count == 0)
            .Select(c => c.Card.Faction).Distinct().ToArray();
        faction ??= leader?.Faction ?? (seedFactions.Length == 1 ? seedFactions[0] : null);
        var leaders = definitions.Values.Where(c => c.Kind == CardKind.Leader && c.Faction == faction).ToArray();
        leader = leader is null ? null : Current(leader);
        DeckDefinition Draft(IReadOnlyList<DeckCard> cards) => new("builder-preview", "Draft", faction ?? "", leader?.Name ?? "",
            leader?.Provision ?? 0, cards, Stratagem: stratagem);
        if (!enabled || faction is null)
            return new(seeds, leader, stratagem, enabled ? "Pick a faction card or choose a faction to start auto-fill." : "Auto-fill off. Only your fixed cards are shown.",
                DeckBuildValidation.Errors(Draft(seeds), archetypes: true));
        // Revalue cached compositions against the current card catalog before budgeting.
        var corpus = library.Where(d => d.Faction == faction).Select(d => d with
        { Cards = d.Cards.Select(c => c with { Card = Current(c.Card) }).ToArray(),
            LeaderProvisionBonus = leaders.FirstOrDefault(l => l.Name == d.Leader)?.Provision ?? d.LeaderProvisionBonus }).ToArray();
        cancellation.ThrowIfCancellationRequested();
        // Completion is a builder operation, not a full opponent inference pass. Rank
        // compatible library compositions directly so an explicit click responds promptly.
        var selectedLeader = leader;
        var selectedStratagem = stratagem;
        var ranked = corpus.Where(d => (selectedLeader is null || d.Leader == selectedLeader.Name) &&
                (selectedStratagem is null || d.Stratagem?.Id == selectedStratagem.Id))
            .DistinctBy(DeckMetaAnalyzer.CompositionKey)
            .OrderByDescending(d => seeds.Sum(s => Math.Min(s.Count, d.CountOf(s.Card.Id)) * s.Card.Provision))
            .ThenByDescending(DeckMetaAnalyzer.SourceDate).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        // A compatible complete composition keeps linked packages together and is preferred
        // to splicing individual popular cards into an incoherent list.
        var exact = ranked.FirstOrDefault(d =>
            (leader is null || d.Leader == leader.Name) && seeds.All(s => d.CountOf(s.Card.Id) >= s.Count) &&
            !excluded.Any(key => d.CountOf(key.CardId) >= key.Copy) &&
            DeckBuildValidation.Errors(d, archetypes: true).Count == 0 &&
            leaders.Any(l => l.Name == d.Leader));
        if (exact is not null)
        {
            leader ??= leaders.First(l => l.Name == exact.Leader);
            stratagem ??= exact.Stratagem is null ? null : Current(exact.Stratagem);
            return new(DeckBuilderOrder.Sort(exact.Cards).ToArray(), leader, stratagem,
                $"Auto-filled from a matching library composition: {exact.Name}. Your {seeds.Sum(c => c.Count)} fixed cards are preserved; other slots remain suggestions.",
                DeckBuildValidation.Errors(Draft(exact.Cards), archetypes: true));
        }
        leader ??= ranked.Select(d => leaders.FirstOrDefault(l => l.Name == d.Leader)).FirstOrDefault(l => l is not null)
            ?? leaders.OrderByDescending(l => l.Provision).ThenBy(l => l.Name).FirstOrDefault();
        if (leader is null) return new(seeds, null, stratagem, "Choose a leader to budget a new composition.", ["Leader unknown."]);
        var errors = DeckBuildValidation.Errors(Draft(seeds), complete: false, archetypes: true);
        if (errors.Count > 0) return new(seeds, leader, stratagem, "Your fixed cards conflict. Auto-fill will not remove them.", errors);
        var chosen = seeds.ToDictionary(c => c.Card.Id, c => c);
        var target = Math.Max(25, seeds.Sum(c => c.Count));
        var requiredUnits = seeds.Any(c => c.Card.Name == "Renfri") ? 25 : 13;
        var singleton = seeds.Any(c => c.Card.Name is "Shupe's Day Off" or "Radeyah");
        var nekker = seeds.Any(c => c.Card.Name is "Golden Nekker" or "Ciri: Nova");
        var scores = DeckRelatedCards.Rank(ranked, definitions.Values, seeds, seeds, faction, leader.Name, stratagem?.Id, excluded).Scores;
        var candidates = definitions.Values
            .Where(c => c.CanBeInStartingDeck && c.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(c => FactionCompatibility.IsPlayableBy(c, faction) && (!nekker || c.Provision < 10 || c.Name is "Golden Nekker" or "Ciri: Nova"))
            // Do not introduce a new deck-wide restriction while greedily filling an unrelated core.
            .Where(c => c.Name switch { "Renfri" => requiredUnits == 25, "Shupe's Day Off" or "Radeyah" => singleton,
                "Golden Nekker" or "Ciri: Nova" => nekker, _ => true })
            .ToArray();
        // Reserve the actual cheapest legal tail, including unit count, singleton and
        // excluded-copy limits. A flat 4p reserve can strand the last slots.
        bool CanFinish()
        {
            var needed = target - chosen.Values.Sum(c => c.Count);
            var unitsNeeded = Math.Max(0, requiredUnits - chosen.Values.Where(c => c.Card.Kind == CardKind.Unit).Sum(c => c.Count));
            if (unitsNeeded > needed) return false;
            var pool = new List<CardDefinition>();
            foreach (var card in candidates)
                for (var copy = (chosen.GetValueOrDefault(card.Id)?.Count ?? 0) + 1; copy <= (card.IsGold || singleton ? 1 : 2); copy++)
                {
                    if (excluded.Contains(new(card.Id, copy))) break;
                    pool.Add(card);
                }
            var cheapestUnits = pool.Where(c => c.Kind == CardKind.Unit).OrderBy(c => c.Provision).Take(unitsNeeded).ToArray();
            if (cheapestUnits.Length != unitsNeeded) return false;
            foreach (var card in cheapestUnits) pool.Remove(card);
            var rest = pool.OrderBy(c => c.Provision).Take(needed - unitsNeeded).ToArray();
            return rest.Length == needed - unitsNeeded && chosen.Values.Sum(c => c.Count * c.Card.Provision) +
                cheapestUnits.Sum(c => c.Provision) + rest.Sum(c => c.Provision) <= 150 + leader.Provision;
        }
        while (chosen.Values.Sum(c => c.Count) < target)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = target - chosen.Values.Sum(c => c.Count);
            var units = chosen.Values.Where(c => c.Card.Kind == CardKind.Unit).Sum(c => c.Count);
            var budget = 150 + leader.Provision - chosen.Values.Sum(c => c.Count * c.Card.Provision);
            var options = candidates.Where(c => chosen.GetValueOrDefault(c.Id)?.Count < (c.IsGold || singleton ? 1 : 2) || !chosen.ContainsKey(c.Id))
                .Where(c => !excluded.Contains(new(c.Id, (chosen.GetValueOrDefault(c.Id)?.Count ?? 0) + 1)))
                .Where(c => c.Provision + 4 * (remaining - 1) <= budget && (requiredUnits - units < remaining || c.Kind == CardKind.Unit))
                .OrderByDescending(c => scores.GetValueOrDefault(c.Id))
                .ThenBy(c => c.Provision).ThenBy(c => c.Id, StringComparer.Ordinal);
            CardDefinition? next = null;
            foreach (var option in options)
            {
                cancellation.ThrowIfCancellationRequested();
                var previous = chosen.GetValueOrDefault(option.Id);
                chosen[option.Id] = new(option, (previous?.Count ?? 0) + 1);
                var feasible = CanFinish();
                if (previous is null) chosen.Remove(option.Id); else chosen[option.Id] = previous;
                if (feasible) { next = option; break; }
            }
            if (next is null) break;
            chosen[next.Id] = new(next, (chosen.GetValueOrDefault(next.Id)?.Count ?? 0) + 1);
        }
        var result = DeckBuilderOrder.Sort(chosen.Values).ToArray();
        return new(result, leader, stratagem, "No complete compatible library list found. Filled from legal catalogue cards, preferring related library cards; review these AUTO suggestions.",
            DeckBuildValidation.Errors(Draft(result), archetypes: true));
    }
}
