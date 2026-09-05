using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public enum DeckSlotState { Unknown, Observed, Predicted, Pinned, Reference, Selected }
public sealed record DeckCopyKey(string CardId, int Copy);
public sealed class DeckProjectionEdits
{
    public Dictionary<DeckCopyKey, CardDefinition> Included { get; } = [];
    public HashSet<DeckCopyKey> Excluded { get; } = [];
    public void Include(CardDefinition card, int copy)
    {
        // Choosing a second physical copy necessarily assumes the first, too.
        for (var number = 1; number <= Math.Clamp(copy, 1, card.IsGold ? 1 : 2); number++)
        {
            var key = new DeckCopyKey(card.Id, number);
            Excluded.Remove(key); Included[key] = card;
        }
    }
    public void Exclude(CardDefinition card, int copy)
    {
        var key = new DeckCopyKey(card.Id, copy);
        Included.Remove(key); Excluded.Add(key);
    }
    public void Clear() { Included.Clear(); Excluded.Clear(); }
    public DeckProjectionEdits Snapshot()
    {
        var copy = new DeckProjectionEdits();
        foreach (var item in Included) copy.Included.Add(item.Key, item.Value);
        copy.Excluded.UnionWith(Excluded);
        return copy;
    }
}
public sealed record ProjectedDeckSlot(int Position, CardDefinition? Card, int Copy, DeckSlotState State,
    double? ModelShare, CardMetaSignal? Meta, bool DeviatesFromPin, string Reason, DeckPackageHint? PackageHint = null);
public sealed record OpponentDeckProjection(IReadOnlyList<ProjectedDeckSlot> Slots, DeckMetaReport Meta,
    int ObservedCopies, int UnknownSlots, int PinDeviations, double PinInfluence,
    ConstraintAssessment Devotion, string DevotionAssumption, string Summary, IReadOnlyList<DeckPackageHint>? PackageHints = null,
    string? SingletonPairHypothesis = null);

public sealed class OpponentDeckProjector
{
    // Display threshold, not a declaration of statistical certainty. Guesses are rebuilt
    // from current evidence on every projection, never retained as observed cards.
    public const double TentativeFillThreshold = .35;
    public OpponentDeckProjection Build(IEnumerable<DeckDefinition> decks, IEnumerable<ObservedCard> observations,
        string? faction, DeckDefinition? pinned = null, DeckProjectionEdits? edits = null,
        ObservedStartingDeckAssessment? constraints = null, int minimumSize = 25, string? startingLeader = null,
        IEnumerable<CardDefinition>? catalog = null, string? startingStratagemId = null, OpponentEncounterPrior? encounters = null,
        IReadOnlyList<SummonCandidateEvidence>? summonEvidence = null, IReadOnlyList<DeckCompositionClue>? compositionClues = null,
        IReadOnlyList<OpponentSequenceEvidence>? sequenceEvidence = null)
    {
        var library = decks.ToArray();
        var evidence = observations.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) &&
                item.Card.CanBeInStartingDeck && item.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
        var rules = constraints ?? StartingDeckRules.EvaluateObservedDeck(evidence);
        var effectiveFaction = string.IsNullOrWhiteSpace(faction) ? pinned?.Faction : faction;
        var deviations = pinned is null ? 0 : evidence.Sum(item => Math.Max(0, item.ObservedCopies - pinned.CountOf(item.Card.Id)));
        var matched = pinned is null ? 0 : evidence.Sum(item => Math.Min(item.ObservedCopies, pinned.CountOf(item.Card.Id)) * Math.Clamp(item.Confidence, 0, 1));
        var pinAllowed = pinned is not null && (string.IsNullOrWhiteSpace(faction) || pinned.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)) &&
                         DeckMetaAnalyzer.Allowed(pinned, rules) && pinned.CardCount >= minimumSize &&
                         (string.IsNullOrWhiteSpace(startingLeader) || string.IsNullOrWhiteSpace(pinned.Leader) || pinned.Leader.Equals(startingLeader, StringComparison.OrdinalIgnoreCase));
        var influence = pinAllowed ? ReferenceInfluence(matched, deviations) : 0;
        var count = Math.Max(Math.Max(25, minimumSize), Math.Max(pinAllowed ? pinned!.CardCount : 0, evidence.Sum(item => item.ObservedCopies)));
        var rows = new List<ProjectedDeckSlot>();
        foreach (var item in evidence)
        for (var copy = 1; copy <= item.ObservedCopies; copy++)
            rows.Add(new(0, item.Card, copy, DeckSlotState.Observed, null, null,
                pinned is not null && pinned.CountOf(item.Card.Id) < copy,
                item.Provenance == CardProvenance.ConfirmedStartingDeck ? "Confirmed starting-deck evidence." :
                    "Observed; starting-deck membership is probable, not proof against creation or replay."));
        // Manual assumptions are soft priors, never observed evidence. A subsequent sighting wins.
        if (pinned is null && edits is not null)
        foreach (var (key, card) in edits.Included.OrderBy(item => item.Key.Copy))
        {
            if (rows.Count >= count || !card.CanBeInStartingDeck || card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact)) continue;
            if (rules.Renfri.State == ConstraintState.RuledOut && card.Name == "Renfri") continue;
            if (rows.Any(row => row.Card!.Id == key.CardId && row.Copy == key.Copy)) continue;
            if (key.Copy > 1 && !rows.Any(row => row.Card!.Id == key.CardId && row.Copy == key.Copy - 1)) continue;
            rows.Add(new(0, card, key.Copy, DeckSlotState.Selected, null, null, false,
                "Manually selected assumption, not a sighting. Related suggestions are reweighted. Click to return it to candidates. This choice does not change Devotion or provision evidence."));
        }
        var assumptions = rows.Where(r => r.State == DeckSlotState.Selected).GroupBy(r => r.Card!.Id)
            .Select(g => new DeckCard(g.First().Card!, g.Max(r => r.Copy))).ToArray();
        var meta = new DeckMetaAnalyzer().Analyze(library, evidence, effectiveFaction, rules, startingLeader, startingStratagemId,
            pinAllowed ? new(pinned!, influence) : null, encounters, assumptions,summonEvidence:summonEvidence,
            compositionClues:compositionClues, sequenceEvidence:sequenceEvidence);
        var weakFit = meta.ObservedIdentities > 0 && meta.BestObservedCoverage < .5;
        var canSuggest = meta.CorpusDecks > 0 && (meta.CorpusDecks >= 3 || evidence.Any(item => item.Confidence >= .6) ||
            assumptions.Length > 0 || !string.IsNullOrWhiteSpace(startingLeader) || !string.IsNullOrWhiteSpace(startingStratagemId));
        var signals = meta.Cards.ToDictionary(item => item.Card.Id);
        rows = rows.Select(r => r with { Meta = signals.GetValueOrDefault(r.Card!.Id) }).ToList();
        var definitions = (catalog ?? []).Concat(evidence.Select(item => item.Card)).Concat(assumptions.Select(item => item.Card))
            .Concat(meta.Cards.Select(item => item.Card)).DistinctBy(card => card.Id).ToArray();
        var packages = pinned is null ? DeckPackageHints.Build(library.Concat(encounters?.CompleteDecks ?? []), definitions,
            evidence, assumptions, effectiveFaction, rules) : [];
        var packageById = packages.ToDictionary(h => h.Card.Id);
        var candidates = meta.Cards.Select(item => item.Card).Concat(pinAllowed ? pinned!.Cards.Select(item => item.Card) : [])
            .Concat(packages.Where(h => h.AutoFill).Select(h => h.Card))
            .DistinctBy(card => card.Id)
            .Where(card => string.IsNullOrWhiteSpace(effectiveFaction) || FactionCompatibility.IsPlayableBy(card, effectiveFaction))
            .SelectMany(card => Enumerable.Range(1, card.IsGold ? 1 : 2).Select(copy =>
            {
                var signal = signals.GetValueOrDefault(card.Id);
                var listed = pinAllowed && pinned!.CountOf(card.Id) >= copy;
                var package = copy == 1 ? packageById.GetValueOrDefault(card.Id) : null;
                var share = signal?.CopyPresence.ElementAtOrDefault(copy - 1) ?? 0;
                var blended = influence * (listed ? 1 : 0) + (1 - influence) * share;
                // Once a pin has demonstrably diverged, a corroborated replacement must
                // compete with its stale unseen cards. The variant prior already includes
                // reference proximity; a strong multi-observation profile may override it.
                if (deviations > 0 && signal?.CopyRecommendations?.ElementAtOrDefault(copy - 1)?.StronglySupported == true)
                    blended = Math.Max(blended, .9 * share);
                if (package?.AutoFill == true && blended < TentativeFillThreshold)
                    return new ProjectedDeckSlot(0, card, copy, DeckSlotState.Predicted, null, signal, false,
                        "Tentative unseen package suggestion. " + package.Explanation, package);
                return new ProjectedDeckSlot(0, card, copy, listed ? DeckSlotState.Pinned : DeckSlotState.Predicted,
                    blended, signal, false, listed ? "Unseen card assumed from the pinned reference; not observed. " + signal?.CopyRecommendations?.ElementAtOrDefault(copy - 1)?.LiveEvidence :
                    "Tentative unseen suggestion; not observed or statistically certain. Replaced automatically as evidence changes. " +
                    signal?.CopyRecommendations?.ElementAtOrDefault(copy - 1)?.Evidence + (weakFit ? ". Weak absolute cache fit." : ""));
            })).Where(row => !rows.Any(known => known.Card!.Id == row.Card!.Id && known.Copy == row.Copy))
            .Where(row => pinned is not null || edits?.Excluded.Contains(new(row.Card!.Id, row.Copy)) != true)
            .Where(row => (!string.IsNullOrWhiteSpace(faction) || pinAllowed) && (row.PackageHint?.AutoFill == true ||
                row.ModelShare >= TentativeFillThreshold && (row.State == DeckSlotState.Pinned || canSuggest && row.Meta?.SupportingDecks > 0)))
            .OrderByDescending(row => row.PackageHint?.AutoFill == true ? .8 : row.ModelShare ?? 0)
            .ThenBy(row => row.Card!.Id, StringComparer.Ordinal).ThenBy(row => row.Copy);
        var leader = definitions.FirstOrDefault(card => card.Kind == CardKind.Leader && card.Faction == effectiveFaction &&
            !string.IsNullOrWhiteSpace(startingLeader) && card.Name.Equals(startingLeader, StringComparison.OrdinalIgnoreCase));
        var capacity = pinAllowed ? 150 + pinned!.LeaderProvisionBonus : leader is { Provision: > 0 } ? 150 + leader.Provision : (int?)null;
        var minimumProvision = rules.Musicians?.State is ConstraintState.Confirmed or ConstraintState.Likely ? 5 : 4;
        var used = rows.Sum(row => row.Card!.Provision);
        var added = 0;
        var limit = weakFit && influence < .5 ? 8 : count;
        foreach (var row in candidates)
        {
            if (rows.Count >= count || added >= limit) break;
            if (rows.Any(known => known.Card!.Id == row.Card!.Id && known.Copy == row.Copy)) continue;
            // A copy-2 guess cannot appear without a copy-1 slot. Reserve a conservative
            // minimum provisions per unknown slot when the original/pinned leader gives a capacity.
            if (row.Copy > 1 && !rows.Any(known => known.Card!.Id == row.Card!.Id && known.Copy == row.Copy - 1)) continue;
            if (capacity is not null && used + row.Card!.Provision + minimumProvision * (count - rows.Count - 1) > capacity) continue;
            // Reapply hard constraints to every automatic row, independently of the cached
            // model and pin. Renfri may have non-units only in the slots above 25 units.
            var withoutRenfri = rules.Renfri.State == ConstraintState.RuledOut ? rules :
                rules with { Renfri = new("Renfri", ConstraintState.Unknown, "Handled by slot capacity") };
            if (!DeckMetaAnalyzer.CardAllowed(row.Card!, row.Copy, withoutRenfri)) continue;
            if (rules.Renfri.State is ConstraintState.Likely or ConstraintState.Confirmed &&
                rows.Count(r => r.Card!.Kind != CardKind.Unit) + (row.Card!.Kind == CardKind.Unit ? 0 : 1) > count - 25) continue;
            rows.Add(row); used += row.Card!.Provision; added++;
        }
        rows = rows.OrderBy(row => row.Card!, DeckBuilderOrder.Comparer).ThenBy(row => row.Copy).ToList();
        var unknown = count - rows.Count;
        while (rows.Count < count) rows.Add(new(0, null, 0, DeckSlotState.Unknown, null, null, false, "Unknown identity and provision value; position is not draw order."));
        var assumption = pinned is null ? "No pinned Devotion assumption." :
            $"Pinned list: {(StartingDeckRules.EvaluateExactDeck(pinned).Devotion.State == ConstraintState.Confirmed ? "Devotion" : "non-Devotion")}; assumption only" +
            (deviations > 0 || !pinAllowed ? " — reference conflicts with evidence." : ".");
        return new(rows.Select((row, index) => row with { Position = index + 1 }).ToArray(), meta, evidence.Sum(item => item.ObservedCopies),
            unknown, deviations, influence, rules.Devotion, assumption,
            $"{count} slots · {evidence.Sum(item => item.ObservedCopies)} observed · {rows.Count - unknown - evidence.Sum(item => item.ObservedCopies)} assumed · {unknown} unknown. " +
            "Provision order, not draw order. This is a working hypothesis, not a reconstructed legal deck." +
            " Unseen guesses start early and update automatically; significance is recommendation strength, not probability." +
            (weakFit ? $" Weak cache fit: at best {meta.BestMatchedIdentities}/{meta.ObservedIdentities} identities matched; tentative fill limited to 8 unless strongly pinned." : "") +
            (pinned is null ? " Decks larger than 25 remain possible." : $" Pin influence {influence:P0}; {deviations} missing observed copy/copies."), packages,
            SingletonPairHint.Build(library,evidence,rules,edits));
    }

    // Positive overlap protects a plausible variant. At 3 discrepancies, 0 matches gives
    // ~15% direct influence; 15 matches gives ~56%. This is a user-prior heuristic, not odds.
    public static double ReferenceInfluence(double matchingCopies, int missingCopies) =>
        .85 / (1 + Math.Pow(Math.Max(0, missingCopies), 2) / (2 + Math.Max(0, matchingCopies)));

    public static IReadOnlyList<ProjectedDeckSlot> Reference(DeckDefinition deck, IEnumerable<ObservedCard>? observations = null)
    {
        var observed = (observations ?? []).Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))
            .GroupBy(item => item.Card.Id).ToDictionary(group => group.Key, group => group.Max(item => item.ObservedCopies));
        return DeckBuilderOrder.Sort(deck.Cards)
            .SelectMany(item => Enumerable.Range(1, item.Count).Select(copy => new ProjectedDeckSlot(0, item.Card, copy,
                observed.GetValueOrDefault(item.Card.Id) >= copy ? DeckSlotState.Observed : DeckSlotState.Reference,
                null, null, false, "Selected deck reference; unseen does not mean still in draw pile.")))
            .Select((row, index) => row with { Position = index + 1 }).ToArray();
    }
}
