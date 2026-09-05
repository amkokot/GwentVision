using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Data;

namespace GwentCompanion.Core.Inference;

public sealed record CardAssociation(string ObservedCard, double Lift, int JointDecks, int ConditionDecks = 0,
    int RecentJointDecks = 0, int RecentConditionDecks = 0, double EffectiveFamilies = 0);
public sealed record DeckReferencePrior(DeckDefinition Deck, double Strength);
public sealed record CardCopyRecommendation(double Score, int SupportingMatches, int MatchingDecks,
    int IndependentMatches, double Lower95, bool StronglySupported, CandidateSignificance? Significance = null, string? LiveEvidence = null)
{
    public string Evidence => MatchingDecks == 0 ? "No comparable complete lists" :
        $"{SupportingMatches}/{MatchingDecks} closest complete lists" + (IndependentMatches < MatchingDecks ? $" ({IndependentMatches} families)" : "") + (LiveEvidence is null ? "" : ". " + LiveEvidence);
}
public sealed record CardMetaSignal(CardDefinition Card, double RecentPrevalence, double OlderPrevalence,
    double GlobalRecentPrevalence, double ConditionalPresence, double AssociationLift,
    int SupportingDecks, int RecentSupportingDecks, IReadOnlyList<double> CopyPresence,
    IReadOnlyList<CardAssociation> Associations, bool StrategyLinked, bool Rising,
    IReadOnlyList<CardCopyRecommendation>? CopyRecommendations = null, ReturningDeckPattern? ReturningPattern = null);
public sealed record DeckMetaReport(IReadOnlyList<CardMetaSignal> Cards, int CorpusDecks, int RecentDecks,
    int OlderDecks, DateTimeOffset? NewestDatedDeck, IReadOnlyList<RankedDeck> RankedDecks,
    int ObservedIdentities = 0, int BestMatchedIdentities = 0, string? TargetPatch = null,
    int PatchDatedDecks = 0, int DateFallbackDecks = 0, double HalfLifePatches = 2)
{
    public double BestObservedCoverage => ObservedIdentities == 0 ? 1 : BestMatchedIdentities / (double)ObservedIdentities;
}

/// <summary>Smoothed shares in a curated sample, not calibrated ladder probabilities.</summary>
public sealed class DeckMetaAnalyzer
{
    private sealed record Sample(DeckDefinition Deck, string[] Leaders, string[] Stratagems, PatchEvidence Patch)
    {
        private readonly Dictionary<string, int> _counts = Deck.Cards.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        public int CountOf(string id) => _counts.GetValueOrDefault(id);
    }
    public DeckMetaReport Analyze(IEnumerable<DeckDefinition> decks, IEnumerable<ObservedCard> evidence,
        string? faction, ObservedStartingDeckAssessment? constraints = null, string? startingLeader = null, string? startingStratagemId = null,
        DeckReferencePrior? reference = null, OpponentEncounterPrior? encounters = null, IEnumerable<DeckCard>? assumptions = null,
        PatchPredictionContext? patchContext = null, IReadOnlyList<SummonCandidateEvidence>? summonEvidence = null,
        IReadOnlyList<DeckCompositionClue>? compositionClues = null, IReadOnlyList<OpponentSequenceEvidence>? sequenceEvidence = null) =>
        AnalyzeAt(decks, evidence, faction, constraints, startingLeader, new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero), startingStratagemId, reference, encounters, assumptions, patchContext,summonEvidence,compositionClues,sequenceEvidence);

    public DeckMetaReport AnalyzeAt(IEnumerable<DeckDefinition> decks, IEnumerable<ObservedCard> evidence,
        string? faction, ObservedStartingDeckAssessment? constraints, string? startingLeader, DateTimeOffset now, string? startingStratagemId = null,
        DeckReferencePrior? reference = null, OpponentEncounterPrior? encounters = null, IEnumerable<DeckCard>? assumptions = null,
        PatchPredictionContext? patchContext = null, IReadOnlyList<SummonCandidateEvidence>? summonEvidence = null,
        IReadOnlyList<DeckCompositionClue>? compositionClues = null, IReadOnlyList<OpponentSequenceEvidence>? sequenceEvidence = null)
    {
        // One composition is one sample, even under different links/names/stratagems.
        // Preserve every known leader instead of arbitrarily discarding one during deduplication.
        patchContext ??= PatchRecency.Current(now);
        var all = decks.Concat(encounters?.CompleteDecks ?? []).Where(d => d.CardCount > 0)
            .Select(d => (Deck: d, Patch: PatchRecency.Resolve(d, patchContext))).Where(s => s.Patch.Available)
            .GroupBy(s => CompositionKey(s.Deck)).Select(g =>
            {
                var representative = g.OrderByDescending(s => s.Patch.Weight).ThenByDescending(s => SourceDate(s.Deck)).ThenBy(s => s.Deck.Id, StringComparer.Ordinal).First();
                return new Sample(representative.Deck with { Patches = DeckPatchMetadata.Merge(g.SelectMany(s => s.Deck.Patches ?? [])),
                    Occurrences = DeckOccurrences.Merge(g.SelectMany(s => s.Deck.Occurrences ?? [])) },
                    g.Select(s => s.Deck.Leader).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    g.Where(s => s.Deck.Stratagem is not null).Select(s => s.Deck.Stratagem!.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), representative.Patch);
            }).ToArray();
        var selected = all.Where(s => string.IsNullOrWhiteSpace(faction) || s.Deck.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)).ToArray();
        var corpus = selected.Where(s => IsComplete(s.Deck)).OrderBy(s => s.Deck.Id, StringComparer.Ordinal).ToArray();
        var observed = evidence.Where(i => StartingDeckRules.CountsAgainstStartingDeck(i.Provenance) && i.Card.CanBeInStartingDeck)
            .GroupBy(i => i.Card.Id).Select(g => g.MaxBy(i => i.ObservedCopies)!).ToArray();
        var sequences = OpponentSequenceRules.Applicable(sequenceEvidence, observed, faction, startingLeader);
        var sequenceReason = sequences.Count == 0 ? null : "Sequence heuristic (uncalibrated): " +
            string.Join(" ", sequences.Select(c => c.Reason));
        // Explicit clicks only: never pass auto-filled slots here. One identity is one soft
        // prior, and a real sighting supersedes the same assumed copy instead of double counting.
        var picked = (assumptions ?? []).Where(c => c.Count > 0 && c.Card.CanBeInStartingDeck &&
                (string.IsNullOrWhiteSpace(faction) || FactionCompatibility.IsPlayableBy(c.Card, faction)))
            .GroupBy(c => c.Card.Id).Select(g => new DeckCard(g.First().Card, Math.Clamp(g.Max(c => c.Count), 1, g.First().Card.IsGold ? 1 : 2)))
            .Where(c => CardAllowed(c.Card, c.Count, constraints) &&
                (observed.FirstOrDefault(o => o.Card.Id == c.Card.Id)?.ObservedCopies ?? 0) < c.Count).ToArray();
        var absence = (summonEvidence ?? []).Where(e=>!observed.Any(o=>o.Card.Id==e.CardId)).GroupBy(e=>e.CardId)
            .ToDictionary(g=>g.Key,g=>g.MinBy(e=>e.Weight)!);
        var recent = corpus.Where(s => s.Patch.Distance == 0).ToArray();
        var older = corpus.Where(s => s.Patch.Distance is >= 1 and <= 3).ToArray();
        var globalRecent = all.Where(s => IsComplete(s.Deck) && s.Patch.Distance == 0).ToArray();
        var families = DeckCompositionFamilies.Build(corpus.Select(s => s.Deck).ToArray());
        var sizes = families.GroupBy(i => i).ToDictionary(g => g.Key, g => g.Count());
        var returns = ReturningDeckPatterns.Find(corpus.Select(s => s.Deck).ToArray(), families, patchContext);
        var recency = corpus.Select(s => s.Patch.Weight).ToArray();
        // A recent near-identical reappearance lends a bounded prior to older
        // variants. Preserve their actual patch age for reliability/explanations.
        PatchRecency.TryIndex(patchContext.TargetPatch, out var targetIndex);
        var agePriors = recency.Select((weight, i) =>
        {
            if (returns[i] is not { } revival) return weight;
            PatchRecency.TryIndex(revival.LatestPatch, out var latestIndex);
            return Math.Max(weight, ReturningDeckPatterns.HistoricalTransfer * Math.Pow(.5, (targetIndex - latestIndex) / patchContext.HalfLifePatches));
        }).ToArray();
        var priors = corpus.Select((s, i) => agePriors[i] / sizes[families[i]]).ToArray();
        var priorTotal = priors.Sum();
        var allowed = corpus.Select(s => Allowed(s.Deck, constraints)).ToArray();
        var matches = corpus.Select(s => observed.Count(o => s.CountOf(o.Card.Id) >= o.ObservedCopies)).ToArray();
        var priorFamilyWeights = Enumerable.Range(0, corpus.Length).GroupBy(i => families[i]).Select(g => g.Sum(i => priors[i])).ToArray();
        var effectivePrior = priorTotal == 0 ? 0 : priorTotal * priorTotal / priorFamilyWeights.Sum(w => w * w);
        double Information(string id, int copies)
        {
            var presence = priorTotal == 0 ? 0 : corpus.Select((s, i) => s.CountOf(id) >= copies ? priors[i] : 0).Sum() / priorTotal;
            return Math.Clamp(-Math.Log((presence * effectivePrior + 1) / (effectivePrior + 2)), .5, 2.5);
        }
        var information = observed.Select(o => Information(o.Card.Id, o.ObservedCopies)).ToArray();
        // Whole-deck overlap preserves packages without independent pairwise Bayes factors.
        // Rare observed identities are more informative than universal staples.
        double Fit(Sample sample) => observed.Select((o, i) => sample.CountOf(o.Card.Id) >= o.ObservedCopies ? 0 :
            -1.2 * information[i] * Math.Clamp(o.Confidence, .1, 1)).Sum();
        double LeaderWeight(Sample sample) => string.IsNullOrWhiteSpace(startingLeader) || sample.Leaders.Length == 0 ? 1 :
            sample.Leaders.Contains(startingLeader, StringComparer.OrdinalIgnoreCase) ? 1 : .08;
        // Missing metadata/unseen red-coin stratagem is unknown, never evidence of absence.
        double MetadataWeight(Sample sample) => LeaderWeight(sample) *
            (string.IsNullOrWhiteSpace(startingStratagemId) || sample.Stratagems.Length == 0 ||
             sample.Stratagems.Contains(startingStratagemId, StringComparer.OrdinalIgnoreCase) ? 1 : .2);
        // A chosen reference is a soft prior, not another observed identity. Prefer nearby
        // complete variants without adding the pin to the corpus or inflating support counts.
        var pickInformation = picked.Select(c => Information(c.Card.Id, c.Count)).ToArray();
        // Positive-only, weaker than a sighting and capped across all picks. Whole-list
        // weighting preserves actual packages without multiplying every pairwise correlation.
        double PickWeight(Sample sample)
        {
            var agreement = observed.Length == 0 ? 1 : observed.Count(o => sample.CountOf(o.Card.Id) >= o.ObservedCopies) / (double)observed.Length;
            return Math.Exp(agreement * Math.Min(Math.Log(8), picked.Select((c, i) =>
                sample.CountOf(c.Card.Id) >= c.Count ? .65 * pickInformation[i] : 0).Sum()));
        }
        double ContextWeight(Sample sample) => MetadataWeight(sample) * ReferenceWeight(sample.Deck, reference) *
            (encounters?.Weight(sample.Deck, now, patchContext) ?? 1) * OccurrenceWeight(sample) * PickWeight(sample) *
            absence.Values.Where(e=>sample.CountOf(e.CardId)>0).Aggregate(1d,(w,e)=>w*Math.Clamp(e.Weight,.01,1)) *
            (compositionClues ?? []).Aggregate(1d, (weight, clue) => weight * clue.Weight(sample.Deck)) *
            OpponentSequenceRules.Weight(sample.Deck, sequences);
        double OccurrenceWeight(Sample sample)
        {
            // One library sighting per composition/patch, regardless of imported IDs, URLs,
            // rows or library saves. Preserve distinct patches and match session evidence.
            // Reviewed matches already have an MMR-aware prior.
            var repeats = DeckOccurrences.Evidence(sample.Deck.Occurrences).Where(o => o.Kind != "OpponentMatch")
                .Select(o => PatchRecency.TryIndex(o.Patch, out var index) && index <= targetIndex
                    ? Math.Pow(.5, (targetIndex - index) / patchContext.HalfLifePatches) : 0).ToArray();
            var extra = repeats.Sum() - repeats.DefaultIfEmpty(0).Max();
            return Math.Min(1.75, 1 + .2 * Math.Log(1 + Math.Max(0, extra)));
        }
        var contextWeights = corpus.Select(ContextWeight).ToArray();
        var logs = corpus.Select((s, i) => Math.Log(priors[i] * contextWeights[i]) + Fit(s) +
            (constraints?.Devotion.State == ConstraintState.Likely && s.Deck.Cards.All(c => c.Card.Faction != "Neutral") ? Math.Log(3) : 0)).ToArray();
        var maximum = logs.Where((_, i) => allowed[i]).DefaultIfEmpty(0).Max();
        var weights = logs.Select((v, i) => allowed[i] ? Math.Exp(v - maximum) : 0).ToArray();
        var total = weights.Sum();
        var bronzeEntries = corpus.Select((s, i) => new { Weight = allowed[i] ? priors[i] * contextWeights[i] : 0,
            Cards = s.Deck.Cards.GroupBy(c => c.Card.Id).Where(g => !g.First().Card.IsGold && g.First().Card.CanBeInStartingDeck).Select(g => g.Sum(c => c.Count)).ToArray() }).ToArray();
        var bronzeTotal = bronzeEntries.Sum(e => e.Weight * e.Cards.Length);
        var bronzePairPrior = bronzeTotal == 0 ? .5 : bronzeEntries.Sum(e => e.Weight * e.Cards.Count(n => n >= 2)) / bronzeTotal;
        var best = matches.Where((_, i) => allowed[i]).DefaultIfEmpty(0).Max();
        var context = Enumerable.Range(0, corpus.Length).Where(i => allowed[i] && matches[i] == best &&
            (string.IsNullOrWhiteSpace(startingLeader) || !corpus.Any(s => s.Leaders.Contains(startingLeader, StringComparer.OrdinalIgnoreCase)) ||
             corpus[i].Leaders.Contains(startingLeader, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (!string.IsNullOrWhiteSpace(startingStratagemId) && context.Any(i => corpus[i].Stratagems.Contains(startingStratagemId, StringComparer.OrdinalIgnoreCase)))
            context = context.Where(i => corpus[i].Stratagems.Contains(startingStratagemId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var coverage = observed.Length == 0 ? 0 : best / (double)observed.Length;
        var evidenceConfidence = observed.Length == 0 ? 1 : observed.Average(o => Math.Clamp(o.Confidence, 0, 1));
        var evidenceFit = corpus.Select((s, i) => observed.Length == 0 ? 1 :
            evidenceConfidence * observed.Select((o, j) => s.CountOf(o.Card.Id) >= o.ObservedCopies ? information[j] : 0).Sum() / information.Sum()).ToArray();
        // Prepare conditioning groups once, not again for every card/copy. Their
        // displayed lift uses the same recency and variant-family priors as ranking.
        var associationContexts = new List<(string Label, string? CardId, int[] Indices, double Total, double Effective, int[] Recent)>();
        void AddAssociationContext(string label, string? cardId, IEnumerable<int> source)
        {
            var indices = source.ToArray(); var sum = indices.Sum(i => priors[i]);
            var groupWeights = indices.GroupBy(i => families[i]).Select(g => g.Sum(i => priors[i])).ToArray();
            var effective = sum == 0 ? 0 : sum * sum / groupWeights.Sum(w => w * w);
            associationContexts.Add((label, cardId, indices, sum, effective,
                indices.Where(i => corpus[i].Patch.Distance is >= 0 and <= 3).ToArray()));
        }
        foreach (var o in observed) AddAssociationContext(o.Card.Name, o.Card.Id, Enumerable.Range(0, corpus.Length).Where(i => corpus[i].CountOf(o.Card.Id) >= o.ObservedCopies));
        foreach (var c in picked) AddAssociationContext("Your pick: " + c.Card.Name, c.Card.Id, Enumerable.Range(0, corpus.Length).Where(i => corpus[i].CountOf(c.Card.Id) >= c.Count));
        if (!string.IsNullOrWhiteSpace(startingLeader)) AddAssociationContext("Leader: " + startingLeader, null,
            Enumerable.Range(0, corpus.Length).Where(i => corpus[i].Leaders.Contains(startingLeader, StringComparer.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(startingStratagemId))
        {
            var name = all.Select(s => s.Deck.Stratagem).FirstOrDefault(c => c?.Id == startingStratagemId)?.Name ?? startingStratagemId;
            AddAssociationContext("Stratagem: " + name, null, Enumerable.Range(0, corpus.Length).Where(i => corpus[i].Stratagems.Contains(startingStratagemId, StringComparer.OrdinalIgnoreCase)));
        }
        var hasContext = observed.Length > 0 || picked.Length > 0 || (compositionClues?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(startingLeader) ||
            !string.IsNullOrWhiteSpace(startingStratagemId) || reference is not null || constraints is not null &&
            new[] { constraints.Devotion, constraints.Renfri, constraints.GoldenNekker, constraints.Shupe, constraints.Musicians! }
                .Any(c => c?.State is ConstraintState.Confirmed or ConstraintState.Likely);
        var signals = corpus.SelectMany(s => s.Deck.Cards).Where(c => c.Card.CanBeInStartingDeck).DistinctBy(c => c.Card.Id).Select(entry =>
        {
            var card = entry.Card;
            var seenCopies = observed.FirstOrDefault(o => o.Card.Id == card.Id)?.ObservedCopies ?? 0;
            // Second-copy rates condition on the already-observed first copy. A list lacking
            // the identity cannot supply negative evidence about its second copy.
            var eligible = Enumerable.Range(0, corpus.Length).Where(i => allowed[i] && corpus[i].CountOf(card.Id) >= seenCopies).ToArray();
            var local = context.Where(i => allowed[i] && corpus[i].CountOf(card.Id) >= seenCopies).ToArray();
            var recommendations = Enumerable.Range(1, card.IsGold ? 1 : 2).Select(copy =>
            {
                var denominator = eligible.Sum(i => weights[i]);
                var raw = denominator == 0 ? 0 : eligible.Where(i => corpus[i].CountOf(card.Id) >= copy).Sum(i => weights[i]) / denominator;
                var baseTotal = eligible.Sum(i => priors[i] * contextWeights[i]);
                var prior = baseTotal == 0 ? 0 : eligible.Where(i => corpus[i].CountOf(card.Id) >= copy).Sum(i => priors[i] * contextWeights[i]) / baseTotal;
                var familyWeights = eligible.GroupBy(i => families[i]).Select(g => g.Sum(i => weights[i])).ToArray();
                var effective = denominator == 0 ? 0 : denominator * denominator / familyWeights.Sum(w => w * w);
                var shrink = 2d / (1 + observed.Length);
                if (seenCopies > 0 && copy > seenCopies)
                {
                    // Sparse per-card copy samples back off to the faction's measured
                    // bronze-pair rate, not a mechanical assertion that Bonded means x2.
                    prior = bronzePairPrior; shrink *= 2;
                }
                var share = effective == 0 ? 0 : (raw * effective + prior * shrink) / (effective + shrink);
                if (copy <= seenCopies) share = 1;
                if (!eligible.Any(i => corpus[i].CountOf(card.Id) >= copy)) share = 0;
                var negative = absence.GetValueOrDefault(card.Id);
                // Curated cache coverage can be unanimous but is not proof. Cap confidence
                // when live negative evidence contradicts every cached variant.
                if(negative is not null && copy>seenCopies) share=Math.Min(share,negative.Weight);
                var support = local.Count(i => corpus[i].CountOf(card.Id) >= copy);
                var groups = local.GroupBy(i => families[i]).ToArray();
                // One-card variants are not independent confirmations. A family supports a
                // copy only when every member does; Wilson is a conservative sample gate.
                var successes = groups.Count(g => g.All(i => corpus[i].CountOf(card.Id) >= copy));
                var lower = WilsonLower(successes, groups.Length);
                var nearUniversal = lower >= .90 && support >= .95 * local.Length;
                var profile = observed.Length >= 5 && best == observed.Length && groups.Length >= 2 && support >= .95 * local.Length;
                var auto = coverage >= .8 && share >= .85 && (nearUniversal || profile) && copy > seenCopies && observed.All(o => o.Confidence >= .8);
                var positive = eligible.Where(i => corpus[i].CountOf(card.Id) >= copy).ToArray();
                var positiveTotal = positive.Sum(i => weights[i]);
                var positiveFamilyWeights = positive.GroupBy(i => families[i]).Select(g => g.Sum(i => weights[i])).ToArray();
                var positiveEffective = positiveTotal == 0 ? 0 : positiveTotal * positiveTotal / positiveFamilyWeights.Sum(w => w * w);
                var freshness = positiveTotal == 0 ? 0 : positive.Sum(i => weights[i] * recency[i]) / positiveTotal;
                var fit = positiveTotal == 0 ? 0 : positive.Sum(i => weights[i] * evidenceFit[i]) / positiveTotal;
                var baseline = priorTotal == 0 ? 0 : corpus.Select((s, i) => s.CountOf(card.Id) >= copy ? priors[i] : 0).Sum() / priorTotal;
                var significance = CandidateSignificance.Calculate(share, baseline, positiveEffective, freshness, fit, hasContext);
                // Expert sequence factors must never be the reason a guess is labelled strongly supported.
                return new CardCopyRecommendation(share, support, local.Length, groups.Length, lower,
                    auto && negative is null && sequences.Count == 0, significance,
                    string.Join(" ", new[] { negative?.Reason, sequenceReason }.Where(s => s is not null)) is { Length: > 0 } reason ? reason : null);
            }).ToArray();
            var recentSupport = recent.Count(s => s.CountOf(card.Id) > 0);
            var recentShare = recent.Length == 0 ? 0 : recentSupport / (double)recent.Length;
            var olderShare = older.Length == 0 ? 0 : older.Count(s => s.CountOf(card.Id) > 0) / (double)older.Length;
            var supportAll = corpus.Count(s => s.CountOf(card.Id) > 0);
            var baseShare = priorTotal == 0 ? 0 : corpus.Select((s, i) => s.CountOf(card.Id) > 0 ? priors[i] : 0).Sum() / priorTotal;
            var associations = associationContexts.Where(c => c.CardId != card.Id).Select(context =>
            {
                var joint = context.Indices.Count(i => corpus[i].CountOf(card.Id) > 0);
                var weighted = context.Total == 0 ? 0 : context.Indices.Where(i => corpus[i].CountOf(card.Id) > 0).Sum(i => priors[i]) / context.Total;
                var smoothed = (weighted * context.Effective + 2 * baseShare) / (context.Effective + 2);
                return new CardAssociation(context.Label, baseShare == 0 ? 1 : smoothed / baseShare, joint, context.Indices.Length,
                    context.Recent.Count(i => corpus[i].CountOf(card.Id) > 0), context.Recent.Length, context.Effective);
            }).ToArray();
            var strongest = associations.Where(a => a.JointDecks >= 2 && a.Lift >= 1.25).OrderByDescending(a => a.Lift).Take(3).ToArray();
            var conditional = recommendations[0].Score;
            var returningSupport = Enumerable.Range(0, corpus.Length).Where(i => returns[i] is not null && corpus[i].CountOf(card.Id) > 0 && weights[i] > 0).ToArray();
            var returningWeight = returningSupport.Sum(i => weights[i]);
            // Keep this small signal out of view until the user's evidence/pin/pick
            // makes the returning pattern a material part of this candidate's support.
            var returning = hasContext && conditional >= .1 && returningWeight >= .25 *
                Enumerable.Range(0, corpus.Length).Where(i => corpus[i].CountOf(card.Id) > 0).Sum(i => weights[i])
                ? returningSupport.OrderByDescending(i => weights[i]).Select(i => returns[i]).FirstOrDefault() : null;
            var baseline = priorTotal == 0 ? 0 : corpus.Select((s, i) => s.CountOf(card.Id) > 0 ? priors[i] : 0).Sum() / priorTotal;
            var lift = (conditional + .02) / (baseline + .02);
            return new CardMetaSignal(card, recentShare, olderShare,
                globalRecent.Length == 0 ? 0 : globalRecent.Count(s => s.CountOf(card.Id) > 0) / (double)globalRecent.Length,
                conditional, lift, supportAll, recentSupport, recommendations.Select(r => r.Score).ToArray(), strongest,
                seenCopies == 0 && strongest.Length > 0 && lift >= 1.35 && conditional - baseline >= .1,
                recent.Length >= 3 && older.Length >= 3 && recentSupport >= 2 && recentShare - olderShare >= .15, recommendations, returning);
        }).OrderByDescending(s => s.ConditionalPresence).ThenBy(s => s.Card.Id, StringComparer.Ordinal).ToArray();
        var ranked = corpus.Select((s, i) => new RankedDeck(s.Deck, total == 0 ? 0 : weights[i] / total,
            observed.Where(o => s.CountOf(o.Card.Id) >= o.ObservedCopies).Select(o => o.Card.Name).ToArray(),
            observed.Where(o => s.CountOf(o.Card.Id) < o.ObservedCopies).Select(o => "Missing " + o.Card.Name).ToArray())).ToList();
        // Partial records remain possible matches: absence is unknown. They never enter
        // frequency denominators or supply a complete-deck provision forecast.
        ranked.AddRange(selected.Where(s => !IsComplete(s.Deck)).Select(s => new RankedDeck(s.Deck, 0,
            observed.Where(o => s.CountOf(o.Card.Id) >= o.ObservedCopies).Select(o => o.Card.Name).ToArray(),
            Allowed(s.Deck, constraints) ? [] : ["Known cards conflict with established constraints"])));
        return new(signals, corpus.Length, recent.Length, older.Length, corpus.Select(s => SourceDate(s.Deck)).DefaultIfEmpty(null).Max(),
            ranked.OrderByDescending(r => r.Score).ThenByDescending(r => r.Evidence.Count).ThenByDescending(r => SourceDate(r.Deck)).ToArray(), observed.Length, best,
            patchContext.TargetPatch, corpus.Count(s => s.Patch.Label is not null && !s.Patch.DateFallback), corpus.Count(s => s.Patch.DateFallback), patchContext.HalfLifePatches);
    }

    public static string CompositionKey(DeckDefinition d) => d.Faction.ToUpperInvariant() + "|" + string.Join(';', d.Cards
        .GroupBy(c => c.Card.Id).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + ":" + g.Sum(c => c.Count)));
    public static bool IsComplete(DeckDefinition d) => d.CardCount >= 25;
    public static double ReferenceWeight(DeckDefinition deck, DeckReferencePrior? reference)
    {
        if (reference is null || !IsComplete(deck) || !deck.Faction.Equals(reference.Deck.Faction, StringComparison.OrdinalIgnoreCase)) return 1;
        var shared = deck.Cards.GroupBy(c => c.Card.Id).Sum(g => Math.Min(g.Sum(c => c.Count), reference.Deck.CountOf(g.Key)));
        var replacements = Math.Max(deck.CardCount, reference.Deck.CardCount) - shared;
        return 1 + 6 * Math.Clamp(reference.Strength, 0, 1) * Math.Pow(.65, replacements);
    }
    // Conservative source age: neither recaching nor a balance-driven server rewrite
    // may rejuvenate a list when an earlier worksheet/author date is already known.
    public static DateTimeOffset? SourceDate(DeckDefinition d) =>
        new[] { d.LastEdited, d.SourceUpdatedAt }.Where(date => date is not null).DefaultIfEmpty(d.CachedAt).Min();
    public static double RecencyWeight(DeckDefinition d, DateTimeOffset now) => PatchRecency.Resolve(d, PatchRecency.Current(now)).Weight;
    public static double WilsonLower(int successes, int count)
    {
        if (count <= 0) return 0;
        const double z = 1.959963984540054;
        var p = successes / (double)count; var zz = z * z;
        return Math.Max(0, (p + zz / (2 * count) - z * Math.Sqrt(p * (1 - p) / count + zz / (4 * count * count))) / (1 + zz / count));
    }
    internal static bool CardAllowed(CardDefinition card, int copy, ObservedStartingDeckAssessment? c) => c is null ||
        ((!Required(c.GoldenNekker) || card.Provision < 10 || card.Name is "Golden Nekker" or "Ciri: Nova") &&
         (c.Renfri.State != ConstraintState.RuledOut || card.Name != "Renfri") &&
         (!Required(c.Renfri) || card.Kind == CardKind.Unit) &&
         (!(Required(c.Shupe) || Required(c.Radeyah)) || copy == 1) &&
         (c.Devotion.State != ConstraintState.Confirmed || card.Faction != "Neutral") &&
         (c.Musicians is null || !Required(c.Musicians) || card.Provision != 4 || card.Id == "202200"));
    internal static bool Allowed(DeckDefinition deck, ObservedStartingDeckAssessment? constraints)
    {
        // Renfri needs at least 25 units, not an absolute ban on specials in a larger deck.
        if (constraints is null) return true;
        if (Required(constraints.Renfri) && IsComplete(deck) && deck.UnitCount < 25) return false;
        var withoutRenfri = constraints.Renfri.State == ConstraintState.RuledOut ? constraints :
            constraints with { Renfri = new("Renfri", ConstraintState.Unknown, "Handled by unit count") };
        return deck.Cards.GroupBy(c => c.Card.Id).All(g => CardAllowed(g.First().Card, g.Sum(c => c.Count), withoutRenfri));
    }
    private static bool Required(ConstraintAssessment value) => value.State is ConstraintState.Likely or ConstraintState.Confirmed;
}
