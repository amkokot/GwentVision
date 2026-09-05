using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class TacticalWatchTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static GwentVisualObservation Screen => new(GwentViewKind.Board, false, 0, 0, null);
    private static CardSighting Sight(CardDefinition card, double x = .3, PlayerSide side = PlayerSide.Opponent) => new(card, side, CardSightSource.Board, new(x, .2, x + .04, .3), .08, .8);
    public static void Rules(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        CardDefinition C(string id) => cards.Single(card => card.Id == id);
        var watch = new TacticalWatch(cards);
        TacticalReport Report(int second = 2, string? faction = "Scoia'tael", string? leader = null, DeckDefinition? pin = null,
            IEnumerable<TriggerOpportunity>? triggers = null, IEnumerable<DeckDefinition>? corpus = null, IEnumerable<CardDefinition>? picks = null,
            IEnumerable<SummonCandidateEvidence>? summonEvidence = null) =>
            watch.Build(faction, leader, pin, picks ?? [], [], null, corpus ?? [], triggers ?? [], At.AddSeconds(second), summonEvidence: summonEvidence);
        TacticalState State(string id, int second = 2) => Report(second).Plays.Single(row => row.Rule.Card.Id == id).State;
        var dwarf = Sight(C("201559"));
        watch.ObserveFrame(At, Screen, [dwarf], true, 1);
        watch.ObserveFrame(At, Screen, [dwarf], true, 1);
        Check(State("202384") == TacticalState.Unknown, "One/duplicate frame must not prove a Dwarf.");
        watch.ObserveFrame(At.AddSeconds(1), Screen, [dwarf], true, 1);
        Check(State("202384") == TacticalState.Ready && State("201559") == TacticalState.Ready, "Two fresh Dwarf sightings activate Justice/Volunteers conditions.");
        Check(State("202384", 10) == TacticalState.Unknown, "Board identity must expire.");
        watch.ObserveFrame(At.AddSeconds(3), Screen with { IsCardSelectionOverlay = true }, [dwarf], true, 1);
        Check(State("202384", 3) == TacticalState.Unknown, "Graveyard/selection is not current board presence.");
        watch.Reset();
        var crown = Sight(cards.First(card => card.HasCategory("Crownsplitters") && card.Kind == CardKind.Unit));
        watch.ObserveFrame(At, Screen, [crown], true, 1); watch.ObserveFrame(At.AddSeconds(1), Screen, [crown], true, 1);
        Check(State("202384") == TacticalState.Ready && State("201559") == TacticalState.Unknown, "Crownsplitter enables Justice, not Volunteers.");
        watch.Reset(); watch.SetCondition(TacticalCondition.Dominance, true, At);
        Check(Report(faction: "Monsters").Plays.Single(row => row.Rule.Card.Id == "132310").State == TacticalState.Ready, "Reviewed Dominance condition.");
        Check(Report(31, "Monsters").Plays.Single(row => row.Rule.Card.Id == "132310").State == TacticalState.Unknown, "Manual fact expires.");
        watch.ObserveEvent(new(At.AddSeconds(1), dwarf with { Source = CardSightSource.PlayPreview }, "new play"));
        Check(Report(faction: "Monsters").Plays.Single(row => row.Rule.Card.Id == "132310").State == TacticalState.Unknown, "Next play invalidates manual conditions.");
        watch.Reset();
        var roach = C("112210");
        Check(Report(triggers: [new(roach.Id, At, "gold")]).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Pending, "Detection failure alone is amber, never red.");
        var softRoachMiss = Report(60, summonEvidence: [new(roach.Id, .6, 1, 1, "Covered first gold with no Roach arrival.")])
            .Summons.Single(row => row.Rule.Card.Id == roach.Id);
        Check(softRoachMiss.State == TacticalState.Pending && softRoachMiss.Label.Contains("Gold missed ×1"),
            "First covered missed gold did not leave a durable amber Roach caution.");
        watch.Review(roach.Id, WatchReview.Missed, At);
        Check(Report().Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Missed, "Reviewed missed opportunity is red.");
        watch.ObserveFrame(At.AddSeconds(1), Screen, [Sight(roach)], true, 1);
        watch.ObserveFrame(At.AddSeconds(2), Screen, [Sight(roach)], true, 1);
        Check(Report().Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen, "Later arrival clears stale reviewed absence.");
        watch.Keep(roach.Id, true);
        watch.ObserveEvent(new(At.AddSeconds(3), Sight(C("162202")) with { Source = CardSightSource.PlayPreview }, "Assire recovery"));
        Check(Report(4).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen,
            "A validated Roach arrival must remain green after a generic graveyard recovery play.");
        Check(Report(40).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen,
            "A validated summon must remain green after its board contact expires.");
        watch.ReturnedToDeck(roach.Id, At.AddSeconds(4));
        watch.ObserveEvent(new(At.AddSeconds(5), Sight(roach) with { Source = CardSightSource.History }, "old history reopened"));
        Check(Report(40).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen,
            "Even a reviewed Roach return must not erase the validated arrival state.");
        watch.Review(roach.Id, WatchReview.Arrived, At.AddSeconds(40));
        Check(Report(40).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen, "Reviewed arrival consumes a verified return, like a fresh board sighting.");
        watch.ReturnedToDeck(roach.Id, At.AddSeconds(40));
        watch.ObserveFrame(At.AddSeconds(41), Screen, [Sight(roach)], true, 1);
        watch.ObserveFrame(At.AddSeconds(42), Screen, [Sight(roach)], true, 1);
        Check(Report(42).Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen, "New arrival consumes the return watch.");
        watch.Review("202397", WatchReview.Missed, At);
        Check(Report().Summons.Single(row => row.Rule.Card.Id == "202397").State != TacticalState.Missed, "Knickers RNG cannot give negative evidence.");
        var anglerfish = C("203217");
        watch.Keep(anglerfish.Id, true);
        watch.ObserveFrame(At.AddSeconds(43), Screen, [Sight(anglerfish)], true, 2);
        watch.ObserveFrame(At.AddSeconds(44), Screen, [Sight(anglerfish)], true, 2);
        Check(Report(44, "Skellige").Summons.Single(row => row.Rule.Card.Id == anglerfish.Id).State == TacticalState.Seen,
            "Anglerfish arrival was not validated.");
        watch.ReturnedToDeck(anglerfish.Id, At.AddSeconds(45));
        Check(Report(45, "Skellige").Summons.Single(row => row.Rule.Card.Id == anglerfish.Id).State == TacticalState.Pending,
            "A verified Anglerfish return should reopen its Rain trigger watch.");
        watch.Reset();
        Check(Report(faction: "Monsters", leader: "White Frost").Summons.Single(row => row.Rule.Card.Id == "202608").Promoted, "White Frost keeps Winter Queen visible.");
        Check(Report(faction: "Skellige", leader: "Rage of the Sea").Summons.Single(row => row.Rule.Card.Id == "203217").Promoted, "Rain leader keeps Anglerfish visible.");
        Check(!Report(faction: "Monsters").Summons.Any(row => row.Rule.Card.Id == "203217"), "Faction shortlist.");
        Check(Report(faction: "Monsters", picks: [C("203217")]).Summons.Any(row => row.Rule.Card.Id == "203217" && row.Promoted && row.OutsideFaction), "Explicit off-faction evidence stays accessible.");
        var pin = new DeckDefinition("pin", "pin", "Northern Realms", "Inspired Zeal", 15, [new(C("200088"))]);
        Check(Report(faction: null, pin: pin).Summons.Single(row => row.Rule.Card.Id == "200088").Promoted, "Pinned Hubert visible.");
        var linked = Enumerable.Range(0, 3).Select(i => new DeckDefinition("link" + i, "sample", "Nilfgaard", "Imperial Formation", 15,
            [new(C("202908")), new(C("162201"), 24 - i), new(roach, i)], LastEdited: At.AddDays(-i))).ToArray();
        var baseline = Enumerable.Range(0, 7).Select(i => new DeckDefinition("other" + i, "sample", "Nilfgaard", "Imprisonment", 15,
            [new(C("162201"), 25 - i), new(roach, i)], LastEdited: At.AddDays(-i))).ToArray();
        Check(Report(faction: "Nilfgaard", leader: "Imperial Formation", corpus: linked.Concat(baseline)).Summons.Single(row => row.Rule.Card.Id == "202908").Promoted, "Catalog-derived leader correlation.");
        Check(!Report(faction: "Nilfgaard", leader: "Imperial Formation", corpus: Enumerable.Range(0, 10).Select(i => linked[0] with { Id = "duplicate" + i }).Concat(baseline)).Summons.Single(row => row.Rule.Card.Id == "202908").Promoted, "Duplicate lists cannot manufacture leader support.");
        Check(Report(faction: "Monsters").Summons.All(row => row.Rule.Card.Kind != CardKind.Leader), "Only card interactions in watch.");
        watch.Reset();
        var toad = C("203119"); var mammuna = C("202995");
        var toadGrave = new ZoneHypothesis(toad.Id, PlayerSide.Opponent, CardZone.Graveyard, 1, true, At, "fixture");
        watch.ObserveInventory([toadGrave], At);
        var toadReady = watch.Build("Monsters", null, null, [], [], null, [], [], At, [toadGrave]).Summons.Single(row => row.Rule.Card.Id == toad.Id);
        Check(toadReady.State == TacticalState.Ready && toadReady.Label.Contains("graveyard"), "Giant Toad graveyard return was not green/ready.");
        var griffinGrave = new ZoneHypothesis("132307", PlayerSide.Opponent, CardZone.Graveyard, 1, true, At, "fixture");
        var mammunaReady = watch.Build("Monsters", null, null, [mammuna], [], null, [], [], At, [griffinGrave]).Summons.Single(row => row.Rule.Card.Id == mammuna.Id);
        Check(mammunaReady.State == TacticalState.Ready && mammunaReady.Label.Contains("Griffin"), "One graveyard Griffin with no board copy did not activate Mammuna.");
        watch.ObserveFrame(At.AddSeconds(1), Screen, [], true, 3);
        var exhausted = new[] { new ZoneHypothesis("132307", PlayerSide.Opponent, CardZone.Graveyard, 2, true, At, "fixture"),
            new ZoneHypothesis("112405", PlayerSide.Opponent, CardZone.Graveyard, 2, true, At, "fixture") };
        var mammunaBlocked = watch.Build("Monsters", null, null, [mammuna], [], null, [], [], At.AddSeconds(2), exhausted).Summons.Single(row => row.Rule.Card.Id == mammuna.Id);
        Check(mammunaBlocked.State == TacticalState.NotMet, "Exhausted round-three Griffin/Fiend lines did not turn Mammuna red.");
        watch.Reset(); watch.ObserveInventory([toadGrave], At);
        watch.ObserveFrame(At.AddSeconds(1), Screen, [Sight(toad)], true, 1);
        watch.ObserveFrame(At.AddSeconds(2), Screen, [Sight(toad)], true, 1);
        var toadGone = toadGrave with { Copies = 0, At = At.AddSeconds(2) }; watch.ObserveInventory([toadGone], At.AddSeconds(2));
        Check(watch.Build("Monsters", null, null, [], [], null, [], [], At.AddSeconds(2), [toadGone]).Summons.Single(row => row.Rule.Card.Id == toad.Id).State == TacticalState.Seen,
            "Resolved Giant Toad return did not remain green/seen.");
        Console.WriteLine($"  Tactical catalog: {Report(faction: null).Summons.Count} summon/thinning cards; {Report(faction: null).Plays.Count} conditional bonuses.");
    }
    public static void Zones(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        CardDefinition C(string id) => cards.Single(card => card.Id == id);
        var ledger = new ZoneEvidenceLedger(); var human = C("152313");
        ledger.Record(human, null, VisibleCardZone.Graveyard, At);
        Check(ledger.OriginRisk(Sight(human), []) is null, "Unknown-side inspection cannot affect the opponent.");
        ledger.Record(human, PlayerSide.Opponent, VisibleCardZone.Graveyard, At, ZoneEntryRoute.HeulynSetup);
        ledger.Record(human, PlayerSide.Opponent, VisibleCardZone.Graveyard, At.AddSeconds(2));
        Check(ledger.Entries.Count == 2 && ledger.OriginRisk(Sight(human), [])?.Provenance == CardProvenance.Unknown, "Repeated scrolling preserves generated-copy ambiguity, not extra copies.");
        Check(ledger.OriginRisk(Sight(human), [new(human, CardProvenance.ConfirmedStartingDeck, 1, At)]) is null, "Generated copy does not erase independently known original.");
        Check(ledger.OriginRisk(Sight(C("203042")), []) is null, "Other identities are unaffected.");
        var mutations = new DeckMutationLedger();
        mutations.Observe(new(At, Sight(C("203278")) with { Source = CardSightSource.PlayPreview }, "Heulyn"), null, "Skellige");
        Check(mutations.OriginRisk(Sight(human) with { Source = CardSightSource.PlayPreview }, At.AddSeconds(2), []) is null, "Heulyn alone does not downgrade every later Human play.");
        Check(mutations.OriginRisk(Sight(human), At.AddSeconds(2), [])?.Provenance == CardProvenance.Unknown, "Unresolved graveyard/board route remains ambiguous.");
        var parsed = VisibleZoneInspectionReader.Parse("RIOGHAN THE UNDYING\nSpawn Roach to the right of self.\nAlbrich", cards);
        Check(parsed?.Cards.Count == 2 && parsed.Cards.Any(card => card.Id == "203042"), "Exact standalone names, not mentioned card names.");
        var traps = new HiddenTrapLedger(); traps.Add(At); traps.Add(At);
        Check(traps.Candidates(1, cards).All(item => !item.Weakened), "Wall-clock duration alone cannot narrow a trap.");
        traps.Survived(1, TrapOpportunity.Special);
        Check(traps.Candidates(1, cards).Single(item => item.Card.Id == "143201").Weakened && traps.Candidates(2, cards).All(item => !item.Weakened), "Trigger survival belongs to one placed trap, not every copy.");
        traps.Reveal(1, C("143201")); Check(traps.Candidates(1, cards).Count == 1, "Flip resolves the same placeholder.");
        traps.Remove(2); traps.Survived(2, TrapOpportunity.FaceUpCard); Check(traps.Entries[1].Survived.Count == 0, "Removed traps do not accumulate missed triggers.");
    }
    public static void Recordings(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };
        var reports = new List<object>();
        foreach (var session in new[] { "20260827-101339", "20260827-081435" })
        {
            var watch = new TacticalWatch(cards); var frames = 0; var positives = 0; var overlays = 0;
            foreach (var line in File.ReadLines(Path.Combine(root, "GwentCompanion", "sessions", session, "vision-observations.jsonl")))
            {
                var frame = JsonSerializer.Deserialize<CardVisionResult>(line, options)!;
                foreach (var evidence in frame.Events) watch.ObserveEvent(evidence);
                watch.ObserveFrame(frame.SampledAt, frame.Screen, frame.Sightings, frame.BoardWasScanned, null);
                var report = watch.Build(null, null, null, [], [], null, [], [], frame.SampledAt);
                Check(report.Summons.All(row => row.State != TacticalState.Missed), "Saved detections alone must never produce red.");
                if (frame.Screen.IsCardSelectionOverlay || frame.Screen.View != GwentViewKind.Board)
                { overlays++; Check(watch.Board.Confirmed(frame.SampledAt).Count == 0, "Historical/selection imagery cannot sustain current board conditions."); }
                positives += report.Plays.Count(row => row.State == TacticalState.Ready); frames++;
            }
            reports.Add(new { Session = session, Frames = frames, OverlayFrames = overlays, PositiveBonusFrameRows = positives, FalseRed = 0,
                Note = "Replays saved recognition snapshots, not fresh pixel detection or comprehensive summon ground truth." });
            Console.WriteLine($"  Tactical replay {session}: {frames} snapshots, {overlays} overlay/history exclusions, {positives} positive bonus rows, zero unreviewed reds.");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.10-tactical-replay.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }
}
