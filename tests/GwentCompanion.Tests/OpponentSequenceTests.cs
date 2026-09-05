using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

internal static class OpponentSequenceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
    private static int _checks;
    private static void Check(bool pass, string reason) { _checks++; if (!pass) throw new InvalidOperationException(reason); }
    private static CardDefinition Card(string id, string faction = "Nilfgaard") => new(id, id, faction, CardKind.Unit, 4);
    private static CardSighting Sight(string id, PlayerSide side = PlayerSide.Opponent, CardSightSource source = CardSightSource.PlayPreview,
        double distance = .05) => new(Card(id), side, source, new(.80, .2, .94, .45), distance, .2);
    private static GwentVisualObservation Screen(int? hand = 10) => new(GwentViewKind.Board, false, 0, 0, null, OpponentHandCount: hand);
    private static bool Frame(OpponentSequenceTracker tracker, double t, string faction = "Nilfgaard", string? leader = null,
        int? round = 1, bool opening = true, GwentVisualObservation? screen = null, CardSighting[]? board = null, string? current = null) =>
        tracker.ObserveFrame(At.AddSeconds(t), screen ?? Screen(), board ?? [], true, round, opening, faction, leader, current ?? leader);
    private static bool Play(OpponentSequenceTracker tracker, double t, string id, CardProvenance origin = CardProvenance.ProbableStartingDeck,
        PlayerSide side = PlayerSide.Opponent, CardSightSource source = CardSightSource.PlayPreview, double distance = .05) =>
        tracker.ObserveEvent(new(At.AddSeconds(t), Sight(id, side, source, distance), "Synthetic reviewed sequence fixture"), origin);
    private static ObservedCard Seen(string id, string faction = "Nilfgaard") => new(Card(id, faction), CardProvenance.ProbableStartingDeck, .95, At, "Fixture");

    public static void Run(string root)
    {
        var t = new OpponentSequenceTracker();
        Frame(t, 1, "Skellige", "Patricidal Fury");
        Frame(t, 2, "Skellige", "Patricidal Fury");
        Check(Play(t, 2, "202182", CardProvenance.Spawned), "Opening Arnjolf did not suggest Sihil.");
        Check(t.Evidence.Single().RuleKey == "pf-opening-sihil", "Wrong leader sequence.");
        Check(!Play(t, 2, "202182", CardProvenance.Spawned) && t.Evidence.Count == 1, "Duplicate event reinforced a clue.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury", opening: false);
        Check(!Play(t, 1, "202182", CardProvenance.Spawned), "Late attachment inferred turn one.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury");
        Frame(t, 2, "Skellige", "Patricidal Fury", screen: Screen(null));
        Check(!Play(t, 2, "202182", CardProvenance.Spawned), "Missing fresh hand measurement inferred first-turn leader use.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury", round: 2);
        Check(!Play(t, 1, "202182", CardProvenance.Spawned), "Round two inferred an opening.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury"); Frame(t, 20, "Skellige", "Patricidal Fury");
        Check(!Play(t, 20, "202182", CardProvenance.Spawned), "Capture gap inferred uninterrupted opening.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury", current: "Other leader");
        Check(!Play(t, 1, "202182", CardProvenance.Spawned), "Replaced leader generated a PF opening clue.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury"); Play(t, 1, "ordinary");
        Frame(t, 2, "Skellige", "Patricidal Fury"); Play(t, 2, "user", side: PlayerSide.User);
        Frame(t, 3, "Skellige", "Patricidal Fury");
        Check(!Play(t, 3, "202182", CardProvenance.Spawned), "Leader after the intervening user play counted as turn one.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury"); Play(t, 1, "ambiguous", CardProvenance.Unknown);
        Frame(t, 2, "Skellige", "Patricidal Fury"); Play(t, 2, "user", side: PlayerSide.User);
        Frame(t, 3, "Skellige", "Patricidal Fury");
        Check(!Play(t, 3, "202182", CardProvenance.Spawned), "Unknown-origin opponent play did not close the opening after user response.");
        var sirens = new[] { Sight("202181", PlayerSide.User, CardSightSource.Board) };
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury");
        Check(!Frame(t, 2, "Skellige", "Patricidal Fury", board: sirens), "One frame was treated as a verified Siren.");
        Check(Frame(t, 3, "Skellige", "Patricidal Fury", board: sirens), "Repeated opening Siren did not suggest Sihil.");
        t.Reset(); Frame(t, 1, "Skellige", "Patricidal Fury");
        Frame(t, 2, "Skellige", "Patricidal Fury", screen: Screen(8), board: sirens);
        Check(!Frame(t, 3, "Skellige", "Patricidal Fury", screen: Screen(8), board: sirens), "Eight-card hand inferred turn one.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596"); Frame(t, 2);
        Check(Play(t, 2, "202601"), "Allgod then Offering was missed.");
        var combo = t.Evidence.Single();
        t.Reset(); Frame(t, 1); Play(t, 1, "202601"); Frame(t, 2);
        Check(!Play(t, 2, "202596"), "Reversed sequence matched.");
        foreach (var bad in new[] { CardProvenance.Created, CardProvenance.Stolen, CardProvenance.Replayed, CardProvenance.Unknown })
        {
            t.Reset(); Frame(t, 1); Play(t, 1, "202596", bad); Frame(t, 2);
            Check(!Play(t, 2, "202601"), "Generated/unknown opener leaked into starting deck.");
        }
        t.Reset(); Frame(t, 1); Play(t, 1, "202596", side: PlayerSide.User); Frame(t, 2);
        Check(!Play(t, 2, "202601"), "Cross-side sequence matched.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596", source: CardSightSource.History); Frame(t, 2);
        Check(!Play(t, 2, "202601"), "History order was treated as live order.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596", distance: .3); Frame(t, 2);
        Check(!Play(t, 2, "202601"), "Weak vision identity matched.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596"); Frame(t, 2, round: 2);
        Check(!Play(t, 2, "202601"), "Sequence crossed a round boundary.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596");
        for (var second = 11; second < 191; second += 10) Frame(t, second);
        Frame(t, 191); Check(!Play(t, 191, "202601"), "Expired sequence matched.");
        t.Reset(); Frame(t, 1); Play(t, 1, "202596"); Frame(t, 2, screen: Screen() with { IsCardSelectionOverlay = true });
        Check(!Play(t, 2, "202601"), "Selection preview became a play.");
        t.Reset(); Frame(t, 1, "Syndicate", "Hidden Cache");
        Check(Play(t, 1, "202388"), "Sausage Maker / Hidden Cache clue missed.");
        t.Reset(); Frame(t, 1, "Syndicate", "Jackpot");
        Check(!Play(t, 1, "202388"), "Sausage Maker matched wrong leader.");

        var observed = new[] { Seen("202596"), Seen("202601") };
        var applicable = OpponentSequenceRules.Applicable([combo, combo], observed, "Nilfgaard", null);
        Check(applicable.Count == 1, "Repeated clues inflated counts.");
        Check(OpponentSequenceRules.Applicable([combo], [observed[0]], "Nilfgaard", null).Count == 0, "Retracted original evidence kept clue active.");
        Check(OpponentSequenceRules.Applicable([combo], [.. observed, Seen("162212")], "Nilfgaard", null).Count == 0, "Already observed target was double weighted.");
        Check(OpponentSequenceRules.Applicable([combo with { Confidence = double.NaN }], observed, "Nilfgaard", null).Count == 0, "Nonfinite confidence accepted.");

        DeckDefinition Deck(string id, bool target) => new(id, id, "Nilfgaard", "Imperial Formation", 15,
            observed.Select(o => new DeckCard(o.Card)).Concat(Enumerable.Range(0, 23).Select(i => new DeckCard(Card(i == 0 && target ? "162212" : id + i))))
                .ToArray(), SourceUpdatedAt: At, CachedAt: At);
        var decks = new[] { Deck("with", true), Deck("without", false) };
        var analyzer = new DeckMetaAnalyzer();
        var baseline = analyzer.AnalyzeAt(decks, observed, "Nilfgaard", null, "Imperial Formation", At);
        var report = analyzer.AnalyzeAt(decks, observed, "Nilfgaard", null, "Imperial Formation", At, sequenceEvidence: [combo]);
        var b = baseline.Cards.Single(c => c.Card.Id == "162212"); var r = report.Cards.Single(c => c.Card.Id == "162212");
        Check(r.ConditionalPresence > b.ConditionalPresence, "Sequence did not change recommendation ranking.");
        Check(report.CorpusDecks == baseline.CorpusDecks && report.ObservedIdentities == baseline.ObservedIdentities && r.SupportingDecks == b.SupportingDecks,
            "A heuristic invented observations or support samples.");
        Check(r.CopyRecommendations![0].LiveEvidence!.Contains("uncalibrated") && !r.CopyRecommendations[0].StronglySupported,
            "Sequence was not labelled or became confidence proof.");
        var repeated = analyzer.AnalyzeAt(decks, observed, "Nilfgaard", null, "Imperial Formation", At, sequenceEvidence: [combo, combo]);
        Check(repeated.Cards.Single(c => c.Card.Id == "162212").ConditionalPresence == r.ConditionalPresence, "Duplicate clues changed scores.");
        var edits = new DeckProjectionEdits(); edits.Exclude(Card("162212"), 1);
        var projection = new OpponentDeckProjector().Build(decks, observed, "Nilfgaard", edits: edits, sequenceEvidence: [combo]);
        Check(projection.ObservedCopies == 2 && projection.Slots.All(s => s.Card?.Id != "162212"), "Sequence ignored exclusion or changed observed copies.");
        Check(!analyzer.AnalyzeAt([decks[1]], observed, "Nilfgaard", null, "Imperial Formation", At, sequenceEvidence: [combo]).Cards.Any(c => c.Card.Id == "162212"),
            "Sequence invented an uncached target.");
        var catalog = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")));
        Check(OpponentSequenceRules.All.All(rule => catalog.Any(c => c.Id == rule.TargetCardId) &&
            rule.RequiredOriginalCards.All(id => catalog.Any(c => c.Id == id))), "Rule references unknown card IDs.");
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json")).Decks;
        var pf = new OpponentSequenceEvidence("pf-opening-sihil", At, 1, .95, "Reviewed opening fixture; not a new match.");
        var pfBase = analyzer.AnalyzeAt(library, [], "Skellige", null, "Patricidal Fury", At);
        var pfSequence = analyzer.AnalyzeAt(library, [], "Skellige", null, "Patricidal Fury", At, sequenceEvidence: [pf]);
        var sihilBase = pfBase.Cards.Single(c => c.Card.Id == "201632");
        var sihilSequence = pfSequence.Cards.Single(c => c.Card.Id == "201632");
        Check(sihilSequence.ConditionalPresence > sihilBase.ConditionalPresence && pfSequence.ObservedIdentities == 0 &&
            pfSequence.CorpusDecks == pfBase.CorpusDecks, "PF sequence failed against the real cache or invented an observation.");
        // Hard confirmed constraints must still exclude the hypothetical target.
        var gn = new OpponentKnowledge(); gn.Resolve(DeckCondition.GoldenNekker, At, "Reviewed resolved condition");
        var forbidden = analyzer.AnalyzeAt(library, [], "Skellige", gn.Assess([]), "Patricidal Fury", At, sequenceEvidence: [pf]);
        Check(forbidden.Cards.Single(c => c.Card.Id == "201632").ConditionalPresence == 0, "Sequence overrode a confirmed provision constraint.");
        var restored = JsonSerializer.Deserialize<OpponentSequenceEvidence[]>(JsonSerializer.Serialize(new[] { combo }));
        Check(restored?.Single() == combo, "Evidence did not survive serialization.");
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/sequential-prediction-regression.json"), JsonSerializer.Serialize(new {
            Checks = _checks, BaselineFalseCiriShare = b.ConditionalPresence, SequenceFalseCiriShare = r.ConditionalPresence,
            CorpusDecks = report.CorpusDecks, report.ObservedIdentities,
            RealCacheSihilBaseline = sihilBase.ConditionalPresence, RealCacheSihilWithOpening = sihilSequence.ConditionalPresence,
            RealCacheSkelligeLists = pfSequence.CorpusDecks,
            Note = "Synthetic functional and leakage regression, not held-out archive accuracy or calibrated probability."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS sequential-prediction: {_checks} checks; synthetic False Ciri share {b.ConditionalPresence:F3} -> {r.ConditionalPresence:F3}; support unchanged.");
    }
}
