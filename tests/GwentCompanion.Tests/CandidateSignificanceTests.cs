using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class CandidateSignificanceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CardDefinition Card(string id) => new(id, id, "Monsters", CardKind.Unit, 4, IsGold: true);
    private static ObservedCard Seen(CardDefinition c, double confidence = .99) => new(c, CardProvenance.ProbableStartingDeck, confidence, At, "Fixture");
    private static DeckDefinition Full(string id, params CardDefinition[] cards) => new(id, id, "Monsters", "",
        15, cards.Concat(Enumerable.Range(0, 25 - cards.Length).Select(i => Card(id + i))).Select(c => new DeckCard(c)).ToArray(), SourceUpdatedAt: At);
    private static CandidateSignificance Value(DeckMetaReport report, string id, int copy = 1) =>
        report.Cards.Single(c => c.Card.Id == id).CopyRecommendations![copy - 1].Significance!;

    public static void Run(string root)
    {
        var distinctive = CandidateSignificance.Calculate(.7, .1, 30, 1, 1, true);
        var common = CandidateSignificance.Calculate(.7, .7, 30, 1, 1, true);
        Check(distinctive.Value > common.Value && distinctive.Specificity > .8 && common.Specificity == 0,
            "Equal inclusion support must distinguish a package link from a common staple.");
        Check(CandidateSignificance.Calculate(.99, .01, 1, 1, 1, true).Value < .5, "A one-list coincidence received a near-certain score.");
        Check(CandidateSignificance.Calculate(.7, .1, 30, .02, 1, true).Value < distinctive.Value, "Old-only evidence was not discounted.");
        Check(CandidateSignificance.Calculate(.7, .1, 30, 1, .2, true).Value < distinctive.Value, "Poor observed-deck fit was not discounted.");
        Check(CandidateSignificance.Calculate(.7, .1, 30, 1, 1, false).Specificity == 0, "No-context popularity was labeled distinctive.");
        foreach (var p in new[] { -1d, 0, .2, 1, 2, double.NaN, double.PositiveInfinity })
        foreach (var n in new[] { -1d, 0, 1, 100, double.NaN })
        {
            var s = CandidateSignificance.Calculate(p, .1, n, 1, 1, true);
            Check(double.IsFinite(s.Value) && s.Value is >= 0 and <= 1, "Significance escaped its finite 0–1 domain.");
            if (n <= 0 || double.IsNaN(n)) Check(s.Value == 0, "No empirical support created a numerical recommendation.");
        }
        var anchor = Card("anchor"); var partner = Card("partner"); var staple = Card("staple"); var other = Card("other");
        var decks = Enumerable.Range(0, 20).Select(i => Full("list" + i, i < 5 ? [anchor, partner, staple] : [other, staple])).ToArray();
        var analyzer = new DeckMetaAnalyzer();
        DeckMetaReport Analyze(IEnumerable<DeckDefinition>? source = null, ObservedCard[]? observed = null, DeckCard[]? picks = null) =>
            analyzer.AnalyzeAt(source ?? decks, observed ?? [], "Monsters", null, null, At, assumptions: picks);
        var before = Analyze(); var chosen = Analyze(picks: [new(anchor)]); var seen = Analyze(observed: [Seen(anchor)]);
        Check(Value(chosen, partner.Id).Value > Value(before, partner.Id).Value && Value(seen, partner.Id).Value > Value(chosen, partner.Id).Value,
            "A picked/observed package source failed to strengthen the partner appropriately.");
        Check(chosen.ObservedIdentities == 0 && chosen.Cards.Single(c => c.Card.Id == partner.Id).SupportingDecks == 5,
            "Significance fabricated observed cards or support counts.");
        var weak = Analyze(observed: [Seen(anchor, .2)]);
        Check(Value(weak, partner.Id).Value < Value(seen, partner.Id).Value, "Weak OCR was as persuasive as a confident original sighting.");
        var twins = Enumerable.Range(0, 40).Select(i => decks[0] with { Id = "twin" + i,
            Cards = decks[0].Cards.SkipLast(1).Append(new DeckCard(Card("variant" + i))).ToArray() });
        Check(Value(Analyze(twins, [Seen(anchor)]), partner.Id).EffectiveSupportingFamilies < 1.01,
            "Near-identical variants inflated significance support.");
        var duplicate = Analyze(decks.Concat(decks.Select(d => d with { Id = "copy" + d.Id })), [Seen(anchor)]);
        Check(Math.Abs(Value(duplicate, partner.Id).Value - Value(seen, partner.Id).Value) < 1e-12, "Duplicate URLs inflated significance.");
        var partial = decks[0] with { Id = "partial", Cards = [new(anchor)] };
        Check(Math.Abs(Value(Analyze(decks.Append(partial), [Seen(anchor)]), partner.Id).Value - Value(seen, partner.Id).Value) < 1e-12,
            "Incomplete list supplied negative evidence about an unobserved partner.");
        var generated = Analyze(observed: [Seen(anchor) with { Provenance = CardProvenance.Created }]);
        Check(Value(generated, partner.Id) == Value(before, partner.Id), "Generated card conditioned a starting-deck package.");
        var blocked = analyzer.AnalyzeAt(decks, [Seen(anchor)], "Monsters", StartingDeckRules.EvaluateObservedDeck([]) with {
            Musicians = new("Musicians", ConstraintState.Confirmed, "Fixture") }, null, At);
        Check(blocked.Cards.All(c => c.CopyRecommendations!.All(r => r.Significance!.Value == 0)), "Forbidden lists contributed significance.");
        // Old commonality should not conceal a new package. Report raw counts and recent counts separately.
        var history = Enumerable.Range(0, 30).Select(i => Full("old" + i, anchor, other) with { SourceUpdatedAt = At.AddYears(-2) });
        var recent = Enumerable.Range(0, 8).Select(i => Full("fresh" + i, anchor, partner));
        var mixed = Analyze(history.Concat(recent).Concat(Enumerable.Range(0, 20).Select(i => Full("rest" + i, staple))), [Seen(anchor)]);
        var association = mixed.Cards.Single(c => c.Card.Id == partner.Id).Associations.Single(a => a.ObservedCard == anchor.Name);
        Check(association.JointDecks == 8 && association.ConditionDecks == 38 && association.RecentJointDecks == 8 && association.RecentConditionDecks == 8,
            "Association display lost all-history vs recent evidence counts.");
        Check(Value(mixed, partner.Id).Value > Value(mixed, other.Id).Value, "Old-package frequency overwhelmed the recent package.");
        ActualCache(root, analyzer);
        Console.WriteLine("Candidate significance: bounds, package specificity, sample/recency/fit reliability, variants, partials, origins, rules and explicit picks passed.");
    }

    private static void ActualCache(string root, DeckMetaAnalyzer analyzer)
    {
        var decks = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json")).Decks;
        var sigvald = decks.SelectMany(d => d.Cards).First(c => c.Card.Id == "202282").Card;
        var before = analyzer.AnalyzeAt(decks, [], "Skellige", null, null, At);
        var after = analyzer.AnalyzeAt(decks, [], "Skellige", null, null, At, assumptions: [new(sigvald)]);
        foreach (var id in new[] { "202277", "202456", "113320", "203246" })
        {
            Check(Value(after, id).Value > Value(before, id).Value, "Actual Sigvald prior failed to raise " + id);
            Console.WriteLine($"  Sigvald → {after.Cards.Single(c => c.Card.Id == id).Card.Name}: {Value(before, id).Value:0.00} → {Value(after, id).Value:0.00}");
        }
        var output = new { Method = "Significance is an evidence-weighted ranking index, not a probability or p-value. Explicit Sigvald pick; same complete cache before/after.",
            Cards = after.Cards.Select(c => new { c.Card.Name, Before = before.Cards.Single(b => b.Card.Id == c.Card.Id).CopyRecommendations![0].Significance,
                After = c.CopyRecommendations![0].Significance, c.Associations }).OrderByDescending(c => c.After!.Value).Take(40) };
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.23-sigvald-significance.json"),
            JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Evaluate(string root, bool validation)
    {
        var decks = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json")).Decks.Where(DeckMetaAnalyzer.IsComplete).ToArray();
        var context = new PatchPredictionContext(validation ? "14.7" : "14.6", StrictHistory: true);
        var at = PatchRecency.Start(context.TargetPatch).AddDays(15);
        DeckDefinition[] Cohort(string patch) => decks.Where(d => PatchRecency.Resolve(d, context with { TargetPatch = patch }) is
                { Available: true, Distance: 0, DateFallback: false })
            .GroupBy(DeckMetaAnalyzer.CompositionKey).Select(g => g.OrderBy(d => d.Id, StringComparer.Ordinal).First())
            .GroupBy(d => d.Faction).SelectMany(g => g.OrderBy(d => d.Id, StringComparer.Ordinal).Take(4)).ToArray();
        var developmentKeys = validation ? Cohort("14.6").Select(DeckMetaAnalyzer.CompositionKey).ToHashSet() : [];
        var held = Cohort(context.TargetPatch).Where(d => !developmentKeys.Contains(DeckMetaAnalyzer.CompositionKey(d))).ToArray();
        Check(held.Length >= 12, "Too few disjoint patch-held-out targets for evaluation.");
        var heldKeys = held.Select(DeckMetaAnalyzer.CompositionKey).ToHashSet();
        // Exclude every alias of every held target, including older sightings of the
        // same exact composition. Near variants and same-patch peers may remain.
        var training = decks.Where(d => !heldKeys.Contains(DeckMetaAnalyzer.CompositionKey(d))).ToArray();
        var results = new List<EvaluationCase>(); var timer = Stopwatch.StartNew(); var analyzer = new DeckMetaAnalyzer();
        foreach (var target in held)
        {
            var random = new Random(173);
            var reveal = target.Cards.OrderBy(c => c.Card.Id).Select(c => (c.Card, Order: random.Next())).OrderBy(c => c.Order).Select(c => c.Card).ToArray();
            foreach (var n in new[] { 0, 1, 3, 6 }) foreach (var visible in new[] { false, true })
            {
                var seen = reveal.Take(n).Select(c => Seen(c) with { ObservedAt = at }).ToArray();
                var report = analyzer.AnalyzeAt(training, seen, target.Faction, null, target.Leader, at,
                    visible ? target.Stratagem?.Id : null, patchContext: context);
                var candidates = report.Cards.SelectMany(c => c.CopyRecommendations!.Select((r, i) => new { Id = c.Card.Id, Copy = i + 1, R = r }))
                    .Where(c => c.R.Score > 0 && !seen.Any(o => o.Card.Id == c.Id && o.ObservedCopies >= c.Copy)).ToArray();
                foreach (var method in new[] { "conditioned-share", "significance" })
                {
                    var ranked = candidates.OrderByDescending(c => method == "significance" ? c.R.Significance!.Value : c.R.Score)
                        .ThenByDescending(c => c.R.Score).ThenBy(c => c.Id, StringComparer.Ordinal).ThenBy(c => c.Copy).ToArray();
                    foreach (var k in new[] { 5, 10, 20 })
                    {
                        var top = ranked.Take(k).ToArray();
                        results.Add(new(target.Id, target.Faction, n, visible, method, k, top.Length,
                            top.Count(c => target.CountOf(c.Id) >= c.Copy), top.Select(c => c.R.Significance!.Specificity).DefaultIfEmpty(0).Average(),
                            top.Select(c => c.R.Significance!.EffectiveSupportingFamilies).DefaultIfEmpty(0).Average()));
                    }
                }
            }
        }
        var summary = results.GroupBy(c => (c.Method, c.Seen, c.K)).Select(g => new { g.Key.Method, g.Key.Seen, g.Key.K,
            Precision = g.Sum(c => c.Hits) / (double)g.Sum(c => c.Count), MeanSpecificity = g.Average(c => c.Specificity), MeanSupport = g.Average(c => c.Support) }).ToArray();
        var path = Path.Combine(root, $"GwentCompanion/diagnostics/v0.1.23-significance-{(validation ? "validation" : "development")}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Method = "Disjoint patch cohorts, all target composition aliases held out; near variants and same-patch peers may remain. Later-patch records and ambiguous future ranges excluded before metadata deduplication. Known leader; stratagem hidden/visible in paired branches. Top unseen copies before auto-fill; compare model share vs significance ranking. Curated-cache test, not live pixel accuracy or a day-by-day historical backtest.",
            Target = context, HeldDecks = held.Length, ExcludedFutureOrUnknownRecords = training.Count(d => !PatchRecency.Resolve(d, context).Available),
            ElapsedSeconds = timer.Elapsed.TotalSeconds, Summary = summary, Cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var s in summary.Where(s => s.K == 10)) Console.WriteLine($"{s.Method}, {s.Seen} seen: top 10 precision {s.Precision:P1}, specificity {s.MeanSpecificity:0.00}");
        Console.WriteLine($"Saved {path}; {timer.Elapsed.TotalSeconds:0.0}s");
    }
    private sealed record EvaluationCase(string Deck, string Faction, int Seen, bool StratagemVisible, string Method,
        int K, int Count, int Hits, double Specificity, double Support);
}
