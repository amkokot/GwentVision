using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class RecommendationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CardDefinition Card(string id) => new(id, id, "Monsters", CardKind.Unit, 4);
    private static ObservedCard Seen(CardDefinition card, int count = 1) => new(card, CardProvenance.ProbableStartingDeck, .99, At, "Fixture", count);
    private static DeckDefinition Full(string id, params CardDefinition[] known) => new(id, id, "Monsters", "White Frost", 15,
        known.Concat(Enumerable.Range(0, Math.Max(0, 25 - known.Length)).Select(i => Card(id + "-filler-" + i)))
            .GroupBy(c => c.Id).Select(g => new DeckCard(g.First(), g.Count())).ToArray(), SourceUpdatedAt: At, CachedAt: At);

    public static void Run(string root)
    {
        var analyzer = new DeckMetaAnalyzer();
        var a = Card("anchor"); var b = Card("partner"); var staple = Card("staple");
        DeckMetaReport Analyze(IEnumerable<DeckDefinition> d, ObservedCard[]? o = null, string? leader = null) =>
            analyzer.AnalyzeAt(d, o ?? [Seen(a)], "Monsters", null, leader, At);
        var six = Enumerable.Range(0, 6).Select(i => Full("pair" + i, i < 2 ? [a, a, staple] : [a, staple])).ToArray();
        var baseReport = Analyze(six);
        var second = baseReport.Cards.Single(s => s.Card.Id == a.Id).CopyRecommendations![1];
        Check(second.SupportingMatches == 2 && second.MatchingDecks == 6 && !second.StronglySupported && second.Score < .5,
            "2/6 second copies got a synthetic near-certain pairing boost.");
        var repeats = six.Concat(Enumerable.Range(0, 100).Select(i => six[0] with { Id = "duplicate" + i, Name = "Renamed" + i })).ToArray();
        var dedup = Analyze(repeats);
        Check(dedup.CorpusDecks == 6 && Math.Abs(dedup.Cards.Single(s => s.Card.Id == a.Id).CopyPresence[1] - second.Score) < 1e-10,
            "Different links/names multiplied one composition's evidence.");
        var reversed = Analyze(six.Reverse());
        Check(baseReport.Cards.Zip(reversed.Cards).All(p => p.First.Card.Id == p.Second.Card.Id && Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-10), "Input order changed tied-date estimates.");
        var partial = Full("partial", a) with { Cards = [new(a)] };
        var partialReport = Analyze(six.Append(partial), [Seen(a), Seen(b)]);
        Check(partialReport.CorpusDecks == 6 && partialReport.RankedDecks.Single(d => d.Deck.Id == "partial").Contradictions.Count == 0,
            "Partial list's missing partner was treated as evidence of absence.");
        Check(Analyze([partial]).Cards.Count == 0 && Analyze([partial]).RankedDecks.Count == 1, "Partial-only corpus invented frequencies or lost its possible match.");
        var old = Full("old", a, b) with { SourceUpdatedAt = At.AddDays(-120), CachedAt = At, LastEdited = At };
        var fresh = Full("fresh", a, staple);
        Check(Math.Abs(PatchRecency.Resolve(old, new("14.8")).Weight - .25) < 1e-10, "Recaching rejuvenated an old fallback patch.");
        Check(Analyze([old, fresh]).RecentDecks == 1 && Analyze([old, fresh]).OlderDecks == 0, "Current/previous-three patch windows regressed.");
        var undated = Full("undated", a) with { SourceUpdatedAt = null, CachedAt = null, LastEdited = null };
        Check(Analyze([undated]).RecentDecks == 0 && PatchRecency.Resolve(undated, new("14.8")).Weight < 1, "Unknown date masquerades as fresh.");
        var few = Enumerable.Range(0, 4).Select(i => Full("thin" + i, a, a)).ToArray();
        var small = Analyze(few).Cards.Single(s => s.Card.Id == a.Id).CopyRecommendations![1];
        Check(small.SupportingMatches == 4 && !small.StronglySupported && small.Lower95 < .6, "4/4 is not population certainty.");
        var plenty = Enumerable.Range(0, 40).Select(i => Full("independent" + i, a, a)).ToArray();
        Check(Analyze(plenty).Cards.Single(s => s.Card.Id == a.Id).CopyRecommendations![1].StronglySupported,
            "Adequately supported near-universal second copies should auto-fill.");
        var twins = Enumerable.Range(0, 40).Select(i => plenty[0] with { Id = "near" + i,
            Cards = plenty[0].Cards.SkipLast(1).Append(new DeckCard(Card("change" + i))).ToArray() }).ToArray();
        var twinSignal = Analyze(twins).Cards.Single(s => s.Card.Id == a.Id).CopyRecommendations![1];
        Check(twinSignal.IndependentMatches == 1 && !twinSignal.StronglySupported, "One-card variants masqueraded as independent confirmations.");
        var projector = new OpponentDeckProjector(); var edits = new DeckProjectionEdits();
        var projected = projector.Build(plenty, [Seen(a)], "Monsters", edits: edits);
        Check(projected.Slots.Any(s => s.Card?.Id == a.Id && s.Copy == 2 && s.State == DeckSlotState.Predicted), "Supported pair absent from actual deck projection.");
        edits.Exclude(a, 2);
        Check(projector.Build(plenty, [Seen(a)], "Monsters", edits: edits).Slots.All(s => s.Card?.Id != a.Id || s.Copy == 1), "Measured auto-fill ignores dismissal.");
        Check(projector.Build(plenty, [Seen(a, 2)], "Monsters", edits: edits).ObservedCopies == 2, "Observed pair failed to replace excluded guess.");
        var weak = Seen(a) with { Confidence = .2 };
        Check(!Analyze(plenty, [weak]).Cards.Single(s => s.Card.Id == a.Id).CopyRecommendations![1].StronglySupported, "Weak recognition produced automatic pair certainty.");
        var generated = Seen(a) with { Provenance = CardProvenance.Created };
        Check(Analyze(plenty, [generated]).ObservedIdentities == 0, "Generated card contaminated starting-deck conditioning.");
        var leaderDecks = Enumerable.Range(0, 8).Select(i => Full("leader" + i, a, i < 4 ? b : staple) with { Leader = i < 4 ? "Blood Scent" : "White Frost" }).ToArray();
        var blood = Analyze(leaderDecks, [], "Blood Scent").Cards.Single(s => s.Card.Id == b.Id);
        Check(blood.StrategyLinked && blood.Associations.Any(c => c.ObservedCard == "Leader: Blood Scent" && c.JointDecks == 4 && c.ConditionDecks == 4), "Leader did not condition the unseen list and association explanation.");
        Check(blood.ConditionalPresence > Analyze(leaderDecks, [], "White Frost").Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence, "Leader weighting reversed.");
        var multiLeader = Analyze(leaderDecks.Append(leaderDecks[0] with { Id = "same-list-other-leader", Leader = "White Frost" }), [], "White Frost");
        Check(multiLeader.CorpusDecks == 8 && multiLeader.Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence > Analyze(leaderDecks, [], "White Frost").Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence, "Dedup discarded valid alternative leader metadata.");
        var ta = Card("Tactical Advantage") with { Kind = CardKind.Stratagem, Faction = "Neutral" };
        var mask = ta with { Id = "Mask", Name = "Mask" };
        var stratDecks = leaderDecks.Select((d, i) => d with { Leader = "White Frost", Stratagem = i < 4 ? mask : ta }).ToArray();
        var withStrat = analyzer.AnalyzeAt(stratDecks, [], "Monsters", null, "White Frost", At, mask.Id);
        Check(withStrat.Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence > Analyze(stratDecks, [], "White Frost").Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence,
            "Known stratagem did not improve its correlated candidate.");
        Check(withStrat.Cards.Single(s => s.Card.Id == b.Id).Associations.Any(s => s.ObservedCard == "Stratagem: Mask" && s.JointDecks == 4), "Stratagem association missing from explanation.");
        var unknownStrats = stratDecks.Select(d => d with { Stratagem = null }).ToArray();
        Check(analyzer.AnalyzeAt(unknownStrats, [], "Monsters", null, "White Frost", At, mask.Id).Cards.Zip(Analyze(unknownStrats, [], "White Frost").Cards)
            .All(p => Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-10), "Missing stratagem metadata was counted as a mismatch.");
        var duplicateStrat = analyzer.AnalyzeAt(stratDecks.Append(stratDecks[0] with { Id = "alternate-strat", Stratagem = ta }), [], "Monsters", null, "White Frost", At, ta.Id);
        Check(duplicateStrat.CorpusDecks == 8, "Stratagem variant inflated composition sample size.");
        Check(new OpponentDeckProjector().Build(stratDecks, [], "Monsters", startingLeader: "White Frost", startingStratagemId: mask.Id).Meta.Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence >
              new OpponentDeckProjector().Build(stratDecks, [], "Monsters", startingLeader: "White Frost").Meta.Cards.Single(s => s.Card.Id == b.Id).ConditionalPresence, "Projection failed to pass opening stratagem into recommendations.");
        Check(Math.Abs(DeckMetaAnalyzer.WilsonLower(4, 4) - .5101091635) < 1e-8, "Wilson small-sample interval regression.");
        // The indexed family finder must agree exactly with the old pairwise definition.
        var familyInputs = leaderDecks.Concat(leaderDecks.Take(3).Select((d, i) => d with { Id = "variation" + i,
            Cards = d.Cards.Skip(1).Append(new DeckCard(Card("replacement"))).ToArray() })).ToArray();
        var actualFamilies = DeckCompositionFamilies.Build(familyInputs);
        var expectedFamilies = Enumerable.Range(0, familyInputs.Length).ToArray();
        int Root(int i) { while (expectedFamilies[i] != i) i = expectedFamilies[i]; return i; }
        for (var i = 0; i < familyInputs.Length; i++) for (var j = i + 1; j < familyInputs.Length; j++)
        {
            var shared = familyInputs[i].Cards.GroupBy(c => c.Card.Id).Sum(g => Math.Min(g.Sum(c => c.Count), familyInputs[j].CountOf(g.Key)));
            if (familyInputs[i].Faction == familyInputs[j].Faction && familyInputs[i].CardCount == familyInputs[j].CardCount && familyInputs[i].CardCount - shared <= 1)
                expectedFamilies[Root(j)] = Root(i);
        }
        for (var i = 0; i < familyInputs.Length; i++) for (var j = 0; j < familyInputs.Length; j++)
            Check((actualFamilies[i] == actualFamilies[j]) == (Root(i) == Root(j)), "Indexed one-card families changed the statistical independence gate.");
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var illusionist = library.Decks.SelectMany(d => d.Cards).First(c => c.Card.Name == "Illusionist").Card;
        var actual = analyzer.AnalyzeAt(library.Decks, [Seen(illusionist)], "Nilfgaard", null, null, At).Cards.Single(c => c.Card.Id == illusionist.Id).CopyRecommendations![1];
        var measured = library.Decks.Where(d => DeckMetaAnalyzer.IsComplete(d) && d.Faction == "Nilfgaard")
            .GroupBy(DeckMetaAnalyzer.CompositionKey).Select(g => g.First()).Where(d => d.CountOf(illusionist.Id) >= 1).ToArray();
        Check(actual.SupportingMatches == measured.Count(d => d.CountOf(illusionist.Id) >= 2) && actual.MatchingDecks == measured.Length,
            "Cached Illusionist audit no longer agrees with live recommendation stats.");
        Console.WriteLine($"  Deduplication, family support, calendar age, partials, leader conditioning, confidence, measured auto-fill; cached Illusionist {actual.SupportingMatches}/{actual.MatchingDecks}, estimate {actual.Score:P0}.");
        LatestReplay(root, library.Decks);
    }

    public static void LatestReplay(string root, DeckDefinition[] decks, string? streamPath = null)
    {
        const string session = "20260828-112523"; // Latest partial ladder match; no early AI examples.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };
        var user = new LiveDeckTracker(PlayerSide.User); var opponent = new LiveDeckTracker(PlayerSide.Opponent);
        var knowledge = new OpponentKnowledge();
        var origins = new PlayProvenanceResolver(); var mutations = new DeckMutationLedger(); var count = 0;
        var snapshots = new List<object>(); var lastCount = 0;
        foreach (var line in File.ReadLines(streamPath ?? Path.Combine(root, "GwentCompanion/sessions", session, "vision-observations.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var element = document.RootElement.TryGetProperty("Result", out var wrapped) ? wrapped : document.RootElement;
            var frame = element.Deserialize<CardVisionResult>(options)!; count++;
            knowledge.ObserveScreen(frame.Screen, frame.SampledAt);
            foreach (var e in frame.Events)
            {
                var sight = e.Sighting; var tracker = sight.Side == PlayerSide.User ? user : opponent;
                mutations.Observe(e, user.Faction, opponent.Faction);
                var origin = mutations.OriginRisk(sight, e.ObservedAt, tracker.Observations) ?? origins.Observe(e);
                knowledge.Observe(e, opponent.HasStableFaction ? opponent.Faction : null, origin.Provenance);
                tracker.ConsiderDirectPlay(sight.Card, 1 - sight.Distance, e.ObservedAt, e.Description, origin.Provenance);
            }
            if (opponent.DeckBuildingObservations.Count == lastCount) continue;
            lastCount = opponent.DeckBuildingObservations.Count;
            var p = new OpponentDeckProjector().Build(decks, opponent.DeckBuildingObservations, opponent.Faction,
                constraints: knowledge.Assess(opponent.DeckBuildingObservations),
                startingLeader: knowledge.StartingLeader, startingStratagemId: knowledge.StartingStratagemId);
            Check(p.ObservedCopies == p.Slots.Count(s => s.State == DeckSlotState.Observed), "Later-game predictions became observations.");
            Check(p.Slots.Where(s => s.State == DeckSlotState.Observed).Sum(s => s.Card!.Provision) == StartingDeckRules.ProbableProvisionLowerBound(opponent.DeckBuildingObservations), "Guesses spent observed provisions.");
            snapshots.Add(new { frame.SampledAt, p.ObservedCopies, p.UnknownSlots, p.Meta.BestObservedCoverage,
                Top = p.Meta.Cards.SelectMany(c => c.CopyRecommendations!.Select((r, i) => new { c.Card.Name, c.Card.Id, Copy = i + 1, r.Score, r.SupportingMatches, r.MatchingDecks }))
                    .Where(c => !opponent.DeckBuildingObservations.Any(o => o.Card.Id == c.Id && o.ObservedCopies >= c.Copy)).OrderByDescending(c => c.Score).Take(10).ToArray() });
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.24-" + (streamPath is null ? "live" : "reprocessed") + "-recommendation-replay.json"), JsonSerializer.Serialize(new {
            Session = session, Frames = count, Snapshots = snapshots,
            knowledge.StartingLeader, knowledge.StartingStratagemId,
            Note = "Latest saved recognition stream only. Verifies candidate/accounting integration, not pixel recall or a fully known hidden opponent list. Early AI recordings excluded." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Latest match {session}: {count} frames, {snapshots.Count} candidate snapshots; evidence and guessed slots remain separate.");
    }
}
