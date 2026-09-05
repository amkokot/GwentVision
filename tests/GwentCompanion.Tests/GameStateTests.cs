using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Capture;

internal static class GameStateTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T19:00:00Z");
    private static readonly CardDefinition Unit = new("state-unit", "Test unit", "Monsters", CardKind.Unit, 4, 5, AbilityText: "");
    private static readonly GwentVisualObservation Screen = new(GwentViewKind.Board, false, 0, 0, null);
    private static CardSighting Sight(double x = .4, double y = .35, PlayerSide side = PlayerSide.Opponent, CardDefinition? card = null,
        CardSightSource source = CardSightSource.Board) => new(card ?? Unit, side, source, new(x, y, x + .055, y + .09), .1, .5);
    private static StateFact<T> Fact<T>(T value, int second = 0, EvidenceKind kind = EvidenceKind.Visual) => new(value, At.AddSeconds(second), 1, kind, "Test evidence");
    private static VisualGameStateFrame Frame(int second, IReadOnlyList<CardSighting>? sights = null, bool scanned = true,
        GwentVisualObservation? screen = null, GameStateMeasurements? measurements = null, IReadOnlyList<VisionEvidenceEvent>? events = null) =>
        new(At.AddSeconds(second), screen ?? Screen, sights ?? [], events ?? [], scanned, Measurements: measurements);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string root)
    {
        Contacts(); EventsAndZones(); RoundsAndMetadata(); MeasurementsAndRules(); MemoryBounds(); ThreatFreshness();
        await JournalAsync(root); await LatestReplayAsync(root);
        Console.WriteLine("Game-state regression passed: contacts, chronology, hidden/partial state, rules, journal and latest recording.");
    }

    private static void ThreatFreshness()
    {
        var tracker = new GameStateTracker(); tracker.Reset("freshness");
        var before = tracker.Observe(Frame(0, [Sight()], screen: Screen with { UserScore = 0, OpponentScore = 5 })).After;
        var unchanged = tracker.Observe(Frame(1, [Sight()], screen: Screen with { UserScore = 0, OpponentScore = 5 })).After;
        Check(!ThreatBoardFreshness.Invalidated(before, unchanged), "Unchanged counters invalidated a board.");
        var spawned = tracker.Observe(Frame(2, scanned: false, screen: Screen with { UserScore = 1, OpponentScore = 5 })).After;
        Check(ThreatBoardFreshness.Invalidated(before, spawned), "Leader token's score change reused a stale pre-spawn board.");
        Check(ThreatBoardFreshness.MissingScoringSide(spawned), "A positive scoreboard with no recognized unit was treated as an empty board.");
        Check(!ThreatBoardFreshness.Invalidated(before, before with { Cards = [] }),
            "A sparse scan with unchanged counters invalidated the last complete Reach board.");
        var seenToken = tracker.Observe(Frame(3, [Sight(side: PlayerSide.Opponent), Sight(y: .7, side: PlayerSide.User)], screen: Screen with { UserScore = 1, OpponentScore = 5 })).After;
        Check(!ThreatBoardFreshness.MissingScoringSide(seenToken), "Recognized scoring units should release the empty-side safeguard.");
        var leader = unchanged with { Opponent = unchanged.Opponent with { LeaderCharges = Fact(2, 3) } };
        Check(ThreatBoardFreshness.Invalidated(before, leader), "New leader charge evidence reused a cached calculation.");
        var schedule = new VisionScanSchedule();
        Check(schedule.NextIncludesBoard(At, 0, 5), "Initial board scan missing.");
        Check(!schedule.NextIncludesBoard(At.AddSeconds(1), 1, 5), "Changed score starved every preview pass.");
        Check(schedule.NextIncludesBoard(At.AddSeconds(2)), "Score change did not prioritize next eligible board pass.");
        Check(!schedule.NextIncludesBoard(At.AddSeconds(3), 1, 5), "Unchanged score repeatedly forced board passes.");
        var thinning = new VisionScanSchedule();
        var gang = new CardDefinition("203089", "Renfri's Gang", "Neutral", CardKind.Unit, 5, 5);
        var gangPreview = Sight(card: gang, source: CardSightSource.PlayPreview);
        Check(thinning.NextIncludesBoard(At), "Thinning schedule initial scan missing.");
        thinning.ObservePreview(At, [gangPreview]);
        Check(!thinning.NextIncludesBoard(At.AddMilliseconds(500), artworkBacklogged: true) &&
            thinning.NextIncludesBoard(At.AddSeconds(1), artworkBacklogged: true),
            "Matching-copy follow-up ignored its one-second load guard.");
        thinning.ObservePreview(At.AddSeconds(1), [gangPreview]);
        Check(thinning.NextIncludesBoard(At.AddSeconds(2), artworkBacklogged: true) &&
            thinning.NextIncludesBoard(At.AddSeconds(3), artworkBacklogged: true) &&
            thinning.NextIncludesBoard(At.AddSeconds(4), artworkBacklogged: true) &&
            !thinning.NextIncludesBoard(At.AddSeconds(5), artworkBacklogged: true),
            "Matching-copy preview did not receive exactly four bounded board confirmations.");
    }

    private static void Contacts()
    {
        var tracker = new GameStateTracker(); tracker.Reset("contacts");
        var first = tracker.Observe(Frame(0, [Sight()]));
        var id = first.After.Cards.Single().InstanceId;
        Check(first.After.Phase == GamePhase.Playing && first.After.Rows.Length == 4, "Did not initialize a four-row board.");
        Check(first.After.Cards.Single().Power is null && first.After.Cards.Single().Armor is null && first.After.Cards.Single().Owner is null,
            "Printed power, zero armor or original ownership was fabricated.");
        var two = tracker.Observe(Frame(1, [Sight(.41), Sight(.52), Sight(.4, .5, PlayerSide.User)]));
        Check(two.After.Cards.Length == 3 && two.After.Cards.Any(card => card.InstanceId == id), "Copies/sides collapsed or stable contact lost.");
        Check(two.After.Cards.Count(card => card.Location.Value.Controller == PlayerSide.Opponent) == 2, "Opponent duplicate count wrong.");
        Check(first.After.Cards.Length == 1 && first.After.Cards.Single().LastSeen == At, "Later observation mutated an earlier snapshot.");
        var three = tracker.Observe(Frame(2, [Sight(.415), Sight(.525), Sight(.4, .5, PlayerSide.User)]));
        Check(three.After.Cards.Length == 3, "Repeated board frames invented copies.");
        Check(!tracker.Observe(Frame(2, [Sight(.65)])).Accepted && !tracker.Observe(Frame(1)).Accepted && tracker.Current.Revision == 3,
            "Duplicate/out-of-order evidence changed state.");
        var overlap = tracker.Observe(Frame(3, [Sight(.415), Sight(.416)]));
        Check(overlap.After.Rows.Sum(row => row.VisibleInstanceIds.Length) == 1, "Overlapping outputs created two contacts.");
        var skipped = tracker.Observe(Frame(4, scanned: false));
        Check(skipped.After.Rows.Single(row => row.Side == PlayerSide.Opponent && row.Row == BoardRow.Melee).ScannedAt == At.AddSeconds(3), "Skipped pass refreshed a row timestamp.");
        var history = tracker.Observe(Frame(5, [Sight(.7)], screen: Screen with { View = GwentViewKind.MoveHistory }));
        Check(history.After.Rows.Sum(row => row.VisibleInstanceIds.Length) == 1 && history.After.BoardObscured &&
            GwentRules.RowSpace(history.After, PlayerSide.Opponent, BoardRow.Melee).Maximum == 9, "History overwrote board or left obscured row usable.");
        var selection = tracker.Observe(Frame(6, [Sight(.7)], screen: Screen with { IsCardSelectionOverlay = true }));
        Check(selection.After.Cards.Length == 3 && !GwentRules.Dominance(selection.After, PlayerSide.Opponent).Known, "Overlay introduced board cards or allowed stale condition.");
        var missing = tracker.Observe(Frame(12));
        Check(missing.After.Cards.All(card => card.Presence == CardPresence.Uncertain) && missing.After.ZoneEvidence.Length == 0, "Missed cards treated as alive/dead/in graveyard.");
        tracker.Reset("movement"); tracker.Observe(Frame(0, [Sight()]));
        var moved = tracker.Observe(Frame(1, [Sight(.4, .2)]));
        Check(moved.After.Cards.Length == 1 && moved.After.Cards.Single().Location.Value.Row == BoardRow.Ranged, "Local row movement duplicated a contact.");
        Check(moved.Events.Any(item => item.Kind == "RowChanged"), "Row movement not recorded.");
        tracker.Reset("new-match");
        Check(tracker.Current.Cards.Length == 0 && tracker.Current.Round is null && tracker.Current.Opponent.HandCount is null, "Previous match leaked into reset.");
        var tooltip = tracker.Observe(Frame(0, [Sight(.3), Sight(.65)], screen: Screen with
            { HasCardTooltip = true, TooltipRegion = new(.63, .34, .80, .60) }));
        Check(tooltip.After.Cards.Length == 1 && tooltip.After.Cards.Single().Location.Value.Region?.Left == .3 &&
            tooltip.After.Rows.All(row => row.Coverage != RowCoverage.Complete), "Small tooltip suppressed clear contacts or admitted covered ones.");
        Console.WriteLine("  Stable/duplicate contacts, separate controllers, overlays, skips, movement, expiry and immutable snapshots passed.");
    }

    private static void EventsAndZones()
    {
        var tracker = new GameStateTracker(); tracker.Reset("events");
        var special = Unit with { Id = "special", Kind = CardKind.Special, Power = 0 };
        var preview = Sight(card: special, source: CardSightSource.PlayPreview);
        var first = tracker.Observe(Frame(0, [preview], events: [new(At, preview, "Special preview")]));
        Check(first.After.Cards.Length == 0 && first.Events.Single().Kind == "PlayPreview" && first.After.ZoneEvidence.Length == 0, "Preview special invented on board/in graveyard.");
        var history = Sight(source: CardSightSource.History);
        var second = tracker.Observe(Frame(1, [history], events: [new(At.AddSeconds(1), history, "Recovered old play")]));
        Check(second.After.Cards.Length == 0 && second.Events.Single().Kind == "HistoricalAction", "History became current play/arrival.");
        var page = new VisibleZoneInspection([Unit, special], "Visible graveyard page");
        tracker.Observe(Frame(2, screen: Screen with { IsCardSelectionOverlay = true }) with { GraveyardInspection = page });
        var scrolled = tracker.Observe(Frame(3, screen: Screen with { IsCardSelectionOverlay = true }) with { GraveyardInspection = page });
        Check(scrolled.After.ZoneEvidence.Length == 2 && scrolled.After.ZoneEvidence.All(item => item.MinimumCopies == 1 && item.Side is null && !item.OrderKnown), "Scrolling guessed side/order or duplicated a copy.");
        var created = new ZoneIdentityEvidence(Unit.Id, Unit.Name, PlayerSide.Opponent, CardZone.Deck, At, At.AddSeconds(4), 1, false, CardProvenance.Created, "Reviewed creation");
        var revealed = tracker.Observe(Frame(4, measurements: new(Zones: [created])));
        Check(revealed.After.ZoneEvidence.Any(item => item.Zone == CardZone.Deck && item.Origin == CardProvenance.Created) && revealed.After.Cards.Length == 0, "Created deck card lost provenance or became board card.");
        Check(!GwentRules.TriggersDeploy(GameActionKind.Summon) && GwentRules.TriggersDeploy(GameActionKind.Play) &&
            GwentRules.CreatesNewCard(GameActionKind.Spawn) && !GwentRules.CreatesNewCard(GameActionKind.Summon), "Play/summon/create semantics conflated.");
        Console.WriteLine("  Preview/history/board separation, partial scrolling zones, generated origin and play-versus-summon semantics passed.");
    }

    private static void RoundsAndMetadata()
    {
        var tracker = new GameStateTracker(); tracker.Reset("rounds", new("own", "Own deck", "Monsters", "Blood Scent", 15, [new(Unit)]));
        tracker.Observe(Frame(0, [Sight()]));
        tracker.Observe(Frame(1, screen: Screen with { ScreenHeader = "ROUND 1", IsCardSelectionOverlay = true }));
        var round = tracker.Observe(Frame(2, screen: Screen with { ScreenHeader = "ROUND 1", IsCardSelectionOverlay = true }));
        Check(round.After.Round?.Value == 1 && round.After.Cards.Single().Presence == CardPresence.Uncertain, "Round confirmation/stale board guard failed.");
        Check(round.After.User.StartingDeckReference is not null && round.After.User.DeckCount is null, "Known list mistaken for remaining draw pile.");
        var counts = tracker.Observe(Frame(3, screen: Screen with { OpponentHandCount = 0, OpponentDeckCount = 7, UserHandCount = 1 }));
        Check(counts.After.Opponent.HandCount?.Value == 0 && counts.After.User.HandCount?.Value == 1 && counts.After.Opponent.DeckCount?.Value == 7, "Real zero/counts lost or sides mixed.");
        var skipped = tracker.Observe(Frame(4, scanned: false));
        Check(skipped.After.Opponent.HandCount?.At == At.AddSeconds(3), "Missing HUD observation refreshed old count.");
        tracker.Observe(Frame(5, measurements: new(Opponent: new(PlayerSide.Opponent, StartingLeader: Fact("Blood Scent", 5), Faction: Fact("Monsters", 5)))));
        var replacement = Unit with { Id = "curse", Name = "Renfri's Curse", Kind = CardKind.Leader, Faction = "Neutral" };
        var leaderSight = Sight(card: replacement, source: CardSightSource.PlayPreview);
        var changed = tracker.Observe(Frame(6, [leaderSight], events: [new(At.AddSeconds(6), leaderSight, "Visible replacement leader")]));
        Check(changed.After.Opponent.StartingLeader?.Value == "Blood Scent" && changed.After.Opponent.CurrentLeader?.Value == replacement.Name &&
            changed.After.Opponent.Faction?.Value == "Monsters", "Leader replacement changed original faction/leader.");
        tracker.Observe(Frame(7, screen: Screen with { ScreenHeader = "FINAL ROUND" }));
        Check(tracker.Observe(Frame(8, screen: Screen with { ScreenHeader = "FINAL ROUND" })).After.Round?.Value == 3, "Final round not recorded.");
        tracker.Observe(Frame(9, screen: Screen with { ScreenHeader = "ROUND 2" }));
        Check(tracker.Observe(Frame(10, screen: Screen with { ScreenHeader = "ROUND 2" })).After.Round?.Value == 3, "Round moved backwards.");
        tracker.Observe(Frame(11, screen: Screen with { ScreenHeader = "VICTORY" }));
        Check(tracker.Observe(Frame(12, [Sight()])).After.Phase == GamePhase.Ended, "Result silently started another match.");
        var cleared = tracker.Observe(Frame(13, measurements: new(User: new(PlayerSide.User), Opponent: new(PlayerSide.Opponent), ReplaceStartingMetadata: true)));
        Check(cleared.After.User.StartingDeckReference is null && cleared.After.Opponent.StartingLeader is null &&
            cleared.After.Opponent.CurrentLeader?.Value == replacement.Name, "Retracted starting reference was retained, or cleared the independent current leader.");
        Console.WriteLine("  Round/result boundaries, real-zero HUD values, original/current leaders and known own-deck separation passed.");
    }

    private static void MeasurementsAndRules()
    {
        var tracker = new GameStateTracker(); tracker.Reset("rules");
        var ally = Sight(.4, .5, PlayerSide.User); var enemy = Sight(.4, .35);
        var partial = tracker.Observe(Frame(0, [ally, enemy]));
        Check(!GwentRules.Dominance(partial.After, PlayerSide.User).Known && !GwentRules.ScoreGap(partial.After, PlayerSide.User).Known, "Missing stats produced exact answer.");
        Check(GwentRules.RowSpace(partial.After, PlayerSide.User, BoardRow.Melee) is { Minimum: 0, Maximum: 8 }, "Partial board assumed empty slots.");
        CardStateMeasurement Measurement(CardSighting sight, int power, int basePower, int armor) => new(sight.Card.Id, sight.Side, sight.Region,
            Fact(power, 1), Fact(basePower, 1), Fact(armor, 1), Statuses: [new(CardStatus.Shield, Fact(false, 1))]);
        var fullRows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row => new RowStateMeasurement(side, row, RowCoverage.Complete))).ToArray();
        var boundedTracker = new GameStateTracker(); boundedTracker.Reset("bounded-dominance");
        var bounded = boundedTracker.Observe(Frame(1, [ally, enemy], measurements: new(Cards: [Measurement(ally, 7, 5, 0), Measurement(enemy, 4, 5, 2)],
            Rows: fullRows.Select(row => row with { Coverage = RowCoverage.Partial }).ToArray(),
            User: new(PlayerSide.User, Score: Fact(7, 1)), Opponent: new(PlayerSide.Opponent, Score: Fact(4, 1)))));
        Check(GwentRules.Dominance(bounded.After, PlayerSide.User) is { Known: true, Value: true } &&
              LiveSynergyMeter.Read(bounded.After, PlayerSide.User, "Dominance") is { Value: "ON", Active: true },
            "A fresh enemy score ceiling did not safely prove Dominance on a partial artwork census.");
        var measured = tracker.Observe(Frame(1, [ally, enemy], measurements: new(Cards: [Measurement(ally, 7, 5, 0), Measurement(enemy, 4, 5, 2)],
            Rows: fullRows, User: new(PlayerSide.User, Score: Fact(7, 1)), Opponent: new(PlayerSide.Opponent, Score: Fact(4, 1)))));
        Check(GwentRules.Dominance(measured.After, PlayerSide.User) is { Known: true, Value: true }, "Known powers did not enable dominance.");
        Check(GwentRules.Bloodthirst(measured.After, PlayerSide.User) is { Minimum: 1, Maximum: 1 }, "Bloodthirst wrong on complete measured board.");
        var enemyId = measured.After.Cards.Single(card => card.Location.Value.Controller == PlayerSide.Opponent).InstanceId;
        Check(GwentRules.Barricade(measured.After, enemyId) is { Known: true, Value: true }, "Armor did not enable barricade.");
        Check(GwentRules.RowSpace(measured.After, PlayerSide.User, BoardRow.Melee) is { Minimum: 8, Maximum: 8 }, "Complete row space wrong.");
        Check(GwentRules.ScoreGap(measured.After, PlayerSide.User) is { Known: true, Value: -3 }, "Score perspective wrong.");
        Check(GwentRules.TryReadDamageTarget(measured.After, enemyId, out var target), "Measured primitive target unreadable.");
        var damage = GwentRules.Damage(target!, 5);
        Check(damage.Unit.Power == 1 && damage.Unit.Armor == 0 && damage.DirectScoreChange == -3 && !damage.Destroyed, "Armor/damage calculation wrong.");
        Check(measured.After.Cards.Single(card => card.InstanceId == enemyId).Power?.Value == 4, "Hypothetical damage changed live state.");
        var shielded = target! with { Statuses = ImmutableHashSet.Create(CardStatus.Shield, CardStatus.Locked) };
        var shieldHit = GwentRules.Damage(shielded, 20);
        Check(shieldHit.DirectScoreChange == 0 && shieldHit.Unit.Armor == 2 && !shieldHit.Unit.Statuses.Contains(CardStatus.Shield) && shieldHit.Unit.Statuses.Contains(CardStatus.Locked), "Shield/armor ordering wrong.");
        Check(GwentRules.Damage(shielded, 0).Unit.Statuses.Contains(CardStatus.Shield), "Zero damage removed shield.");
        Check(GwentRules.Damage(target!, 20) is { Destroyed: true, DirectScoreChange: -4 }, "Overkill inflated points.");
        Check(GwentRules.Damage(target!, 3, ignoreArmor: true).Unit.Armor == 2, "Armor-piercing consumed armor.");
        Check(GwentRules.Boost(target!, 3).Unit.Power == 7 && GwentRules.Heal(target!, 10).Unit.Power == 5, "Boost/heal confused base power.");
        Check(GwentRules.Purify(shielded).Unit.Statuses.Count == 0 && GwentRules.Purify(shielded).Unit.Armor == 2, "Purify removed armor or retained statuses.");
        var older = tracker.Observe(Frame(2, [ally, enemy], measurements: new(Cards: [new(enemy.Card.Id, enemy.Side, enemy.Region, Power: Fact(1))], User: new(PlayerSide.User, Score: Fact(1)))));
        Check(older.After.User.Score?.Value == 7 && older.After.Cards.Single(card => card.InstanceId == enemyId).Power?.Value == 4, "Delayed measurement overwrote newer value.");
        var played = Sight(source: CardSightSource.PlayPreview);
        var newPlay = tracker.Observe(Frame(3, [played], scanned: false, events: [new(At.AddSeconds(3), played, "New play, scoreboard not updated yet")]));
        Check(!GwentRules.ScoreGap(newPlay.After, PlayerSide.User).Known, "Pre-play score was treated as the post-play gap.");
        var stale = tracker.Observe(Frame(9, scanned: false));
        Check(!GwentRules.ScoreGap(stale.After, PlayerSide.User).Known && !GwentRules.TryReadDamageTarget(stale.After, enemyId, out _), "Stale data produced exact calculation.");
        Console.WriteLine("  Partial-state refusal, measured scores/conditions, stale guards and pure damage/boost/heal/purify primitives passed.");
    }

    private static void MemoryBounds()
    {
        var tracker = new GameStateTracker(); tracker.Reset("bounds");
        for (var i = 0; i < 400; i++) tracker.Observe(Frame(i, [Sight(card: Unit with { Id = "contact" + i })]));
        Check(tracker.Current.Cards.Length == GameStateTracker.MaximumContacts && tracker.Current.RecentEvents.Length <= GameStateTracker.MaximumRecentEvents, "Session state unbounded.");
    }

    private static async Task JournalAsync(string root)
    {
        var directory = Path.Combine(root, "GwentCompanion/diagnostics", "state-journal-test-" + Guid.NewGuid().ToString("N"));
        var tracker = new GameStateTracker(); tracker.Reset("journal");
        var first = tracker.Observe(Frame(0, [Sight()]));
        await using (var journal = new GameStateJournal(directory))
        {
            await journal.AppendAsync(first); await journal.AppendAsync(first);
            await journal.AppendAsync(tracker.Observe(Frame(1, scanned: false)));
            var third = tracker.Observe(Frame(2, scanned: false)); await journal.AppendAsync(third); await journal.SaveFinalAsync(third.After);
            Check(journal.CheckpointsWritten == 2, "Journal duplicated or wrote every skipped sample.");
        }
        var restored = JsonSerializer.Deserialize<GameStateSnapshot>(await File.ReadAllTextAsync(Path.Combine(directory, "game-state-final.json")), GameStateJournal.Json)!;
        Check(restored.SessionId == "journal" && restored.Cards.Single().Card.Id == Unit.Id && restored.Cards.Single().Power is null && restored.Rows.Length == 4, "Snapshot JSON lost state/unknowns.");
        var record = new RecordedPlayEvent("event", At, Unit.Id, Unit.Name, Unit.Kind, 1, PlayerSide.User, 1, Sight().Region, null, "during.png", null, "test",
            BeforePosition: PositionNotation.Write(CalculationPositionAdapter.FromObserved(first.Before)),
            DuringPosition: PositionNotation.Write(CalculationPositionAdapter.FromObserved(first.After)),
            AfterPosition: PositionNotation.Write(CalculationPositionAdapter.FromObserved(restored)));
        var roundTrip = JsonSerializer.Deserialize<RecordedPlayEvent>(JsonSerializer.Serialize(record, GameStateJournal.Json), GameStateJournal.Json)!;
        Check(PositionNotation.Read(roundTrip.BeforePosition!).Zones.Sum(zone => zone.Cards.Length) == 0 &&
            PositionNotation.Read(roundTrip.DuringPosition!).Zones.Sum(zone => zone.Cards.Length) == 1 && roundTrip.AfterPosition is not null, "Play context did not round-trip.");
        Check(PositionNotation.Write(PositionNotation.Read(await File.ReadAllTextAsync(Path.Combine(directory, "position-history.gvn")))) ==
            await File.ReadAllTextAsync(Path.Combine(directory, "position-final.gvn")), "Compact change log did not reconstruct final position.");
        Check(!File.Exists(Path.Combine(directory, "game-state-final.json.tmp")), "Atomic snapshot left pending write.");
        Console.WriteLine("  Journal throttling, final-state persistence and before/during/after event JSON round-trip passed.");
    }

    private static async Task LatestReplayAsync(string root)
    {
        const string session = "20260827-184848";
        var tracker = new GameStateTracker(); tracker.Reset(session);
        var frames = 0; var previews = 0; var boardScans = 0; var maximumContacts = 0; var maximumVisible = 0;
        var snapshots = new List<object>(); var totalEvents = new Dictionary<string, int>();
        GamePosition? previousPosition = null; var compactLog = new List<string>(); var maxPositionChars = 0;
        foreach (var line in File.ReadLines(Path.Combine(root, "GwentCompanion/sessions", session, "vision-observations.jsonl")))
        {
            var frame = JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!;
            var update = GameStateVisionAdapter.Apply(tracker, frame); frames++;
            var position = CalculationPositionAdapter.FromObserved(update.After);
            var notation = PositionNotation.Write(position); maxPositionChars = Math.Max(maxPositionChars, notation.Length);
            var patch = PositionNotation.Changes(previousPosition, position); if (patch.Length > 0) compactLog.Add(patch);
            previousPosition = position;
            Check(update.Accepted && update.After.Revision == frames, "Latest recording ordering/revision mismatch.");
            Check(update.After.Cards.Select(card => card.InstanceId).Distinct().Count() == update.After.Cards.Length, "Replay instance ID collision.");
            Check(update.After.Cards.All(card => card.Power is null && card.Armor is null), "Replay fabricated unread power/armor.");
            Check(update.After.Rows.All(row => row.Coverage != RowCoverage.Complete), "Artwork-only replay claimed complete board.");
            if (!frame.BoardWasScanned || frame.Screen.IsCardSelectionOverlay || frame.Screen.HasCardTooltip && frame.Screen.TooltipRegion is null || frame.Screen.View == GwentViewKind.MoveHistory)
                Check(update.After.Rows.All(row => row.ScannedAt != frame.SampledAt), "Unscanned/obscured frame refreshed board.");
            if (frame.BoardWasScanned) boardScans++;
            maximumContacts = Math.Max(maximumContacts, update.After.Cards.Length);
            maximumVisible = Math.Max(maximumVisible, update.After.Rows.Sum(row => row.VisibleInstanceIds.Length));
            foreach (var change in update.Events) totalEvents[change.Kind] = totalEvents.GetValueOrDefault(change.Kind) + 1;
            if (frame.Events.Any(item => item.Sighting.Source == CardSightSource.PlayPreview))
            {
                previews += frame.Events.Count(item => item.Sighting.Source == CardSightSource.PlayPreview);
                snapshots.Add(new { At = frame.SampledAt, BeforeRevision = update.Before.Revision, DuringRevision = update.After.Revision,
                    Contacts = update.After.Cards.Length, Visible = update.After.Rows.Sum(row => row.VisibleInstanceIds.Length),
                    Round = update.After.Round?.Value, OpponentHand = update.After.Opponent.HandCount?.Value });
            }
        }
        Check(frames > 2000 && previews > 0 && maximumVisible > 0 && maximumContacts <= GameStateTracker.MaximumContacts, "Replay empty/unbounded.");
        var report = new { Session = session, Frames = frames, PlayPreviews = previews, BoardScanFlags = boardScans, MaximumContacts = maximumContacts,
            MaximumCompactPositionCharacters = maxPositionChars,
            MaximumRecognizedVisibleContacts = maximumVisible, FinalContactsWithUnknownPower = tracker.Current.Cards.Count(card => card.Power is null), Events = totalEvents, PlayContexts = snapshots,
            Note = "Latest saved recognition stream only; no new OCR or hidden-deck truth. Reconstruction invariants, not complete board recognition accuracy. Early recordings excluded." };
        await File.WriteAllTextAsync(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.19-state-replay.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.19-state-final.json"), JsonSerializer.Serialize(tracker.Current, GameStateJournal.Json));
        var compact = string.Join('\n', compactLog);
        Check(PositionNotation.Write(PositionNotation.Read(compact)) == PositionNotation.Write(previousPosition!), "Latest compact change log failed to reproduce final position.");
        await File.WriteAllTextAsync(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.19-position-replay.gvn"), compact);
        Console.WriteLine($"  Latest recording {session}: {frames} frames, {previews} preview contexts, max {maximumVisible} recognized board contacts; unread stats stay unknown.");
        Console.WriteLine($"  Compact latest-match position peaked at {maxPositionChars} characters; change-log replay matches final position.");
    }

    public static async Task LatestHudAsync(string root)
    {
        using var reader = new ScreenStateRecognizer();
        var hud = new OpponentHudRecognizer(); var outputs = new List<GwentVisualObservation>();
        foreach (var file in new[] { "frame-000663-184954491.jpg", "frame-000678-184955997.jpg" })
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions/20260827-184848", file));
            var image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var pixels = BitmapFrameAdapter.ToPixelFrame(image);
            outputs.Add(await hud.ReadAsync(pixels, Screen, At.AddSeconds(outputs.Count * 1.5), reader));
        }
        Check(outputs[0].UserHandCount is null && outputs[0].UserScore is null && outputs[0].OpponentScore is null, "HUD accepted a single reading.");
        var result = outputs[1];
        Console.WriteLine($"  Latest pixel HUD: hand user={result.UserHandCount}, opponent={result.OpponentHandCount}; score user={result.UserScore}, opponent={result.OpponentScore}.");
        Check(result.UserHandCount == 9 && result.OpponentHandCount == 8, "Inspected latest-frame hand counts wrong.");
        Check(result.UserScore == 10 && result.OpponentScore == 10, "Inspected latest-frame scores wrong.");
        Check(OpponentHudRecognizer.ParseScore("10 points") is null && OpponentHudRecognizer.ParseScore("99999") is null && OpponentHudRecognizer.ParseScore("0") == 0,
            "Score parser accepted contamination or lost zero.");
        var tracker = new GameStateTracker(); tracker.Reset("pixel-hud");
        var state = tracker.Observe(Frame(2, screen: result)).After;
        Check(state.User.Score?.Value == 10 && GwentRules.ScoreGap(state, PlayerSide.User) is { Known: true, Value: 0 }, "Pixel HUD failed state/rules integration.");
        hud.Reset(); outputs.Clear();
        foreach (var file in new[] { "frame-002672-185315397.jpg", "frame-002688-185316992.jpg" })
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions/20260827-184848", file));
            var image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            outputs.Add(await hud.ReadAsync(BitmapFrameAdapter.ToPixelFrame(image), Screen, At.AddSeconds(10 + outputs.Count * 1.5), reader));
        }
        result = outputs[1];
        Console.WriteLine($"  Gold-leading HUD: hand user={result.UserHandCount}, opponent={result.OpponentHandCount}; score user={result.UserScore}, opponent={result.OpponentScore}.");
        Check(result.UserScore == 34 && result.OpponentScore == 38 && result.UserHandCount == 5 && result.OpponentHandCount == 4,
            "Gold leading score, zoom/hand state or separate sides failed.");
        var obscured = await hud.ReadAsync(new PixelFrame(1280, 720, new byte[1280 * 720 * 4]), Screen with { IsCardSelectionOverlay = true }, At.AddSeconds(20), reader);
        Check(obscured.UserScore is null && obscured.OpponentScore is null, "Overlay inherited old confirmed score.");
        var blank = await hud.ReadAsync(new PixelFrame(1280, 720, new byte[1280 * 720 * 4]), Screen, At.AddSeconds(22), reader);
        Check(blank.UserScore is null && blank.UserHandCount is null && blank.OpponentScore is null, "Blank/menu-like frame fabricated counters.");
        Console.WriteLine("Latest-frame HUD regression passed (two independently inspected frame pairs, white/gold scores, overlay/blank controls).");
    }
}
