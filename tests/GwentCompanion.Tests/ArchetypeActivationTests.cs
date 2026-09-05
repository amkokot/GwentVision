using System.Text.Json;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class ArchetypeActivationTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed record ReplayRow(string Frame, CardVisionResult Result);
    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        CardDefinition Card(string name) => catalog.First(card => card.Name == name);
        var devotionCards = catalog.Where(card => card.AbilityText?.Contains("Devotion", StringComparison.Ordinal) == true).Select(card => card.Id).ToHashSet();
        Check(DevotionInteractionCatalog.All.Select(item => item.CardId).ToHashSet().SetEquals(devotionCards),
            "The positive Devotion interaction catalog does not cover every current Devotion card.");
        var at = DateTimeOffset.Parse("2026-08-28T16:00:00Z");
        VisionEvidenceEvent E(CardDefinition card, int seconds, CardSightSource source = CardSightSource.PlayPreview, PlayerSide side = PlayerSide.Opponent) =>
            new(at.AddSeconds(seconds), new(card, side, source, new(.3,.2,.4,.4), .05, .2, "fixture"), "fixture");
        var shupe = Card("Shupe's Day Off"); var family = new CardAppearanceFamilies(catalog).Normalize(Card("Shupe: Mage"));
        var tracker = new DeckConditionEvidenceTracker();
        Check(tracker.Observe(E(shupe, 0)) is null, "Shupe play alone became proof");
        Check(tracker.Observe(E(family, 8, CardSightSource.Board))?.Condition == DeckCondition.Singleton, "Shupe family did not confirm its root's condition");
        tracker.Reset(); tracker.Observe(E(shupe, 0, side: PlayerSide.User));
        Check(tracker.Observe(E(family, 8, CardSightSource.Board)) is null, "Cross-side activation");
        tracker.Reset(); tracker.Observe(E(shupe, 0, CardSightSource.History));
        Check(tracker.Observe(E(family, 8, CardSightSource.Board)) is null, "History scroll timestamp became activation chronology");
        tracker.Reset(); tracker.Observe(E(family, 0, CardSightSource.Board)); tracker.Observe(E(shupe, 3));
        Check(tracker.Observe(E(family, 8, CardSightSource.Board)) is null, "Pre-existing form became a new resolved effect");
        tracker.Reset(); tracker.Observe(E(shupe, 0)); Check(tracker.Observe(E(family, 91, CardSightSource.Board)) is null, "Stale activation window");
        tracker.Reset(); tracker.Observe(E(Card("Radeyah"), 0));
        Check(tracker.Observe(E(catalog.First(card => card.Kind == CardKind.Stratagem), 4, CardSightSource.Board))?.Condition == DeckCondition.Singleton, "Radeyah new stratagem activation");
        var gn = Card("Golden Nekker"); tracker.Reset(); tracker.Observe(E(gn, 0));
        Check(tracker.Observe(E(Card("Fiend"), 2)) is null, "Incomplete Nekker chain confirmed");
        tracker.Observe(E(Card("Tempering"), 4));
        Check(tracker.Observe(E(Card("The Mushy Truffle"), 7))?.Condition == DeckCondition.GoldenNekker, "Nekker unit/special/artifact chain did not confirm");
        tracker.Reset(); tracker.Observe(E(gn, 0)); tracker.Observe(E(Card("Fiend"), 2)); tracker.Observe(E(Card("Fiend"), 3, side: PlayerSide.User));
        tracker.Observe(E(Card("Tempering"), 4));
        Check(tracker.Observe(E(Card("The Mushy Truffle"), 7)) is null, "Separate turns became Nekker chain");
        tracker.Reset(); tracker.Observe(E(Card("Renfri"), 0));
        Check(tracker.Observe(E(Card("Curse of Lust"), 5, CardSightSource.Board))?.Condition == DeckCondition.Renfri, "Renfri actual curse identity did not confirm");
        tracker.Reset(); tracker.Observe(E(Card("Renfri"), 0));
        var before = new GameStateTracker().Current;
        var after = before with { At = at.AddSeconds(3), Opponent = before.Opponent with
            { CurrentLeader = new("Renfri curse + blessing", at.AddSeconds(3), 1, EvidenceKind.Inferred, "Presence-only inference") } };
        Check(tracker.Observe(new GameStateUpdate(before, after, [], true)) is null, "Presence-based leader hypothesis became circular confirmation");
        after = after with { Opponent = after.Opponent with { CurrentLeader = new("Curse of Lust", at.AddSeconds(3), 1, EvidenceKind.Visual, "Read actual ability") } };
        Check(tracker.Observe(new GameStateUpdate(before, after, [], true))?.Condition == DeckCondition.Renfri, "Measured leader replacement");
        var nova = Card("Ciri: Nova"); tracker.Reset(); tracker.Observe(E(nova, 0));
        var novaBody = new GameCardInstance("nova", nova, new(new(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, null), at, 1, EvidenceKind.Visual, "fixture"),
            at, at.AddSeconds(3), CardPresence.Visible, Statuses: [new(CardStatus.Resilience, new(true, at.AddSeconds(3), 1, EvidenceKind.Visual, "fixture")),
                new(CardStatus.Shield, new(true, at.AddSeconds(3), 1, EvidenceKind.Visual, "fixture")), new(CardStatus.Veil, new(true, at.AddSeconds(3), 1, EvidenceKind.Visual, "fixture"))]);
        Check(tracker.Observe(new GameStateUpdate(before, after with { Cards = [novaBody] }, [], true))?.Condition == DeckCondition.GoldenNekker, "Nova measured conditional statuses");
        var knowledge = new OpponentKnowledge(); knowledge.Observe(E(shupe, 0)); knowledge.Observe(E(family, 8, CardSightSource.Board));
        Check(knowledge.Assess([]).Shupe.State == ConstraintState.Confirmed, "Knowledge did not adopt automatic resolution");
        var duplicate = new ObservedCard(Card("Fiend"), CardProvenance.ConfirmedStartingDeck, 1, at, "fixture", 2);
        Check(knowledge.Assess([duplicate]).Shupe.State == ConstraintState.Unknown, "Activation silently erased conflicting copy evidence");
        var devotionKnowledge = new OpponentKnowledge(); devotionKnowledge.Suggest(DeckCondition.Devotion, at, "fixture devotion payoff");
        Check(devotionKnowledge.Assess([]).Renfri.State == ConstraintState.RuledOut, "Likely Devotion did not exclude Renfri.");
        devotionKnowledge.Resolve(DeckCondition.Devotion, at, "verified devotion"); devotionKnowledge.Resolve(DeckCondition.Renfri, at, "verified Renfri");
        Check(devotionKnowledge.Assess([]) is { Devotion.State: ConstraintState.Unknown, Renfri.State: ConstraintState.Unknown },
            "Contradictory resolved Devotion/Renfri effects were both displayed as valid.");
        var marine = Card("Kerack Marine"); var ally = Card("Dun Banner");
        var location = new StateFact<CardLocation>(new(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, null), at, 1, EvidenceKind.Visual, "fixture");
        GameCardInstance Unit(string id, CardDefinition card, int power, DateTimeOffset time) => new(id, card, location with { At = time }, at, time,
            CardPresence.Visible, Power: new(power, time, 1, EvidenceKind.Visual, "fixture"));
        var marineTracker = new DevotionEvidenceTracker(); marineTracker.ObserveEvent(E(marine, 0), "Northern Realms");
        var marineBefore = before with { At = at.AddSeconds(1), Phase = GamePhase.Playing, Cards = [Unit("marine", marine, 4, at.AddSeconds(1)), Unit("ally", ally, 4, at.AddSeconds(1))] };
        var marineAfter = marineBefore with { At = at.AddSeconds(2), Cards = [Unit("marine", marine, 4, at.AddSeconds(1)), Unit("ally", ally, 8, at.AddSeconds(2))] };
        Check(marineTracker.ObserveState(new(marineBefore, marineAfter, [], true))?.Contains("Kerack Marine") == true,
            "Measured Kerack Marine +4 payoff did not produce Devotion evidence.");
        var whoreson = Card("Whoreson Junior"); var whoresonTracker = new DevotionEvidenceTracker();
        whoresonTracker.ObserveEvent(E(whoreson, 0), "Syndicate");
        var victim = Card("Fiend");
        var juniorBefore = before with { At = at.AddSeconds(1), Phase = GamePhase.Playing,
            Cards = [new GameCardInstance("victim", victim, new(new(PlayerSide.User, CardZone.Board, BoardRow.Melee, null), at, 1, EvidenceKind.Visual, "fixture"),
                at, at.AddSeconds(1), CardPresence.Visible, Power: new(8, at.AddSeconds(1), 1, EvidenceKind.Visual, "fixture"),
                BasePower: new(8, at.AddSeconds(1), 1, EvidenceKind.Visual, "fixture"))] };
        var juniorAfter = juniorBefore with { At = at.AddSeconds(2), Cards = [juniorBefore.Cards[0] with
            { LastSeen = at.AddSeconds(2), Power = new(2, at.AddSeconds(2), 1, EvidenceKind.Visual, "fixture") }] };
        Check(whoresonTracker.ObserveState(new(juniorBefore, juniorAfter, [], true))?.Contains("Whoreson Junior") == true,
            "Whoreson Junior damaging an unboosted target did not produce Devotion evidence.");
        var renfriCheck = new OpponentKnowledge(); renfriCheck.SetStartingSize(25);
        var nonUnit = new ObservedCard(Card("Tempering"), CardProvenance.ProbableStartingDeck, 1, at, "fixture");
        Check(renfriCheck.Assess([nonUnit]).Renfri.State == ConstraintState.RuledOut, "25-card deck with original non-unit did not rule out Renfri");
        Check(renfriCheck.Assess([nonUnit with { Provenance = CardProvenance.Created }]).Renfri.State != ConstraintState.RuledOut, "Generated non-unit ruled out Renfri");
        renfriCheck.SetStartingSize(26);
        Check(renfriCheck.Assess([nonUnit]).Renfri.State != ConstraintState.RuledOut, "26-card deck with one non-unit can still contain 25 units");
        Check(renfriCheck.Assess([nonUnit with { ObservedCopies = 2 }]).Renfri.State == ConstraintState.RuledOut, "26-card deck with two original non-units cannot contain 25 units");
        renfriCheck.SetStartingSize(null);
        Check(renfriCheck.Assess([nonUnit]).Renfri.State != ConstraintState.RuledOut, "Assumed 25-card minimum was treated as known exact size");
        renfriCheck.SetStartingSize(25);
        var impossible = renfriCheck.Assess([nonUnit]);
        var renfriDeck = new DeckDefinition("renfri-fixture", "Renfri fixture", "Neutral", "", 18, [new(Card("Renfri")), new(Card("Fiend"),24)]);
        Check(new DeckInferenceEngine().CompatibleDecks([renfriDeck], [], null, impossible).Count == 0, "Ruled-out Renfri still supplies recommendation priors");
        var projected = new OpponentDeckProjector().Build([renfriDeck], [nonUnit], null, pinned:renfriDeck, constraints:impossible, catalog:catalog);
        Check(projected.Slots.All(slot => slot.Card?.Name != "Renfri"), "Pinned Renfri survived rule exclusion");
        var edits = new DeckProjectionEdits(); edits.Include(Card("Renfri"), 1);
        Check(new OpponentDeckProjector().Build([renfriDeck], [nonUnit], null, edits:edits, constraints:impossible, catalog:catalog)
            .Slots.All(slot => slot.Card?.Name != "Renfri"), "Speculated Renfri survived later rule exclusion");
        // Current event ledger replay, not obsolete embedded events in the older benchmark export.
        var rows = JsonSerializer.Deserialize<ReplayRow[]>(File.ReadAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-final-streaming.json")), GameStateJournal.Json)!;
        knowledge.Reset(); var ledger = new MatchVisionLedger(); var replayed = new List<VisionEvidenceEvent>();
        foreach (var row in rows)
        {
            var result = row.Result;
            foreach (var evidence in ledger.Observe(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, result.HoveredCard, result.ArtworkWasScanned))
            { replayed.Add(evidence); knowledge.Observe(evidence); }
        }
        Check(knowledge.Assess([]).Shupe.State == ConstraintState.Confirmed, "Recent recorded Shupe activation still not confirmed");
        var replayPath = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.27-archetype-replay.json");
        File.WriteAllText(replayPath, JsonSerializer.Serialize(new { Events = replayed }, GameStateJournal.Json));
        var saved = SavedVisionEvents.Read(replayPath, catalog); var restored = new OpponentKnowledge();
        foreach (var item in saved) restored.Observe(item);
        Check(saved.Any(item => item.Sighting.Card.Id.StartsWith("visual-family:")) && restored.Assess([]).Shupe.State == ConstraintState.Confirmed,
            "Saved replay dropped the Shupe visual family or its activation");
        Console.WriteLine($"PASS archetypes: Shupe/Radeyah/Nekker/Renfri/Nova activations, side/time/history/conflict guards; recent {rows.Length}-frame Shupe sequence confirmed and survives saved replay.");
    }
}
