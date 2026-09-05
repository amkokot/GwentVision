using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class RecordedSequenceTests
{
    private static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    private static GamePosition Put(GamePosition p, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] cards) =>
        p with { Zones = p.Zones.Select(z => z.Side == side && z.Zone == zone && z.Row == row
            ? z with { Cards = cards.ToImmutableArray(), TotalCount = cards.Length } : z).ToImmutableArray() };

    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var engine = new TacticalPlayEngine(catalog);
        CardDefinition Get(string name) => catalog.Single(card => card.Name == name);
        PositionCard C(string name, string id, int? power = null, int armor = 0, int charges = 0, int cooldown = 0) =>
            new(id, Get(name).Id, power ?? Get(name).Power, Get(name).Power, armor, PlayRules.Compile(Get(name)).PrintedStatuses, charges, cooldown, true);
        GamePosition Empty() => GamePosition.EmptyKnown() with { Round = 2 };
        var checks = new List<object>();
        void Save(string name, ResolvedAction action, int observed, int? maximum, string beforeFrame, string afterFrame)
        {
            Check(action.Points == observed, $"{name}: expected realized {observed}; got {action.Points}: {string.Join("; ", action.Missing)}");
            Check(maximum is null || maximum >= observed, name + " maximum below legal recorded line");
            checks.Add(new { Name = name, ObservedSwing = observed, Resolved = action.Points, Maximum = maximum,
                BeforeFrame = beforeFrame, AfterFrame = afterFrame, action.Line, AfterPosition = PositionNotation.Write(action.After!),
                Note = "Reviewed board/input fixture, not automatic visual reconstruction. Card/leader/order stages are separate." });
            Console.WriteLine($"Sequence {name}: observed {observed}, replay {action.Points}, max {maximum?.ToString() ?? "not claimed"}");
        }

        var fruit = Put(Empty(), PlayerSide.User, CardZone.Board, BoardRow.Ranged, C("Gernichora's Fruit", "fruit", 1));
        var fiend = Put(Put(fruit, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Duén Canell Guardian", "guardian")), PlayerSide.User, CardZone.Hand, null, C("Fiend", "play"));
        var result = engine.ResolvePlay(fiend, "play", PlayerSide.User, new Dictionary<string, PlaySelection> { ["play"] = new(BoardRow.Ranged, 1) });
        Save("Fiend / Thrive", result, 9, engine.Maximum(fiend, "play", PlayerSide.User).MaximumPoints, "frame-000487-184936895.jpg", "frame-000519-184940090.jpg");
        Check(engine.ResolvePlay(fiend, "play", PlayerSide.User, new Dictionary<string, PlaySelection>()).Points is null, "Missing recorded row was optimized silently");
        Check(engine.ResolvePlay(fiend, "play", PlayerSide.User, new Dictionary<string, PlaySelection> { ["play"] = new(BoardRow.Ranged, 99) }).Points is null, "Illegal placement accepted");

        var tutor = Put(Put(fruit, PlayerSide.User, CardZone.Hand, null, C("Oneiromancy", "play")), PlayerSide.User, CardZone.Deck, null,
            C("Barbegazi", "barbegazi"), C("Old Speartip", "speartip"));
        tutor = Put(tutor, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Mahakam Pass", "pass"));
        result = engine.ResolvePlay(tutor, "play", PlayerSide.User, new Dictionary<string, PlaySelection>
            { ["play"] = new(TutorId: "barbegazi"), ["barbegazi"] = new(BoardRow.Ranged, 0) });
        Save("Chosen Oneiromancy / Barbegazi", result, 7, engine.Maximum(tutor, "play", PlayerSide.User).MaximumPoints, "frame-004540-185622188.jpg", "frame-004640-185632193.jpg");
        Check(engine.Maximum(tutor, "play", PlayerSide.User).MaximumPoints == 13, "Higher tutor alternative must remain available to maximum search");
        Check(result.After!.Zone(PlayerSide.User, CardZone.Deck).Cards.Single().InstanceId == "speartip", "Replay removed unchosen tutor alternative");

        // Full visible late-round board. Siren had a Shield before Backup Plan; it absorbs the initial 2 damage.
        var late = Put(Empty(), PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Barbegazi", "barbegazi", 14), C("Gernichora's Fruit", "fruit", 6));
        late = Put(late, PlayerSide.User, CardZone.Board, BoardRow.Ranged, C("Siren", "siren", charges: 1) with { Statuses = C("Siren", "x").Statuses!.Add(CardStatus.Shield) });
        late = Put(late, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Mahakam Pass", "pass"));
        late = late with { User = late.User with { LastPlayedUnitId = "siren" }, Opponent = late.Opponent with { StartingDeckIds = ImmutableHashSet<string>.Empty } };
        var backup = Put(late, PlayerSide.Opponent, CardZone.Hand, null, C("Backup Plan", "play"));
        result = engine.ResolvePlay(backup, "play", PlayerSide.Opponent, new Dictionary<string, PlaySelection>
            { ["play"] = new(CreatedCardId: Get("Elven Wardancer").Id), ["spawn-play-1"] = new(BoardRow.Ranged, 0, "barbegazi") });
        Save("Backup Plan / Shield / Wardancer", result, 5, null, "frame-005260-185734181.jpg", "frame-005389-185747087.jpg");
        Check(result.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single() is { Power: 4 } siren && !siren.Statuses!.Contains(CardStatus.Shield), "Backup Plan must consume Shield, not score damage");
        Check(result.After.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged).Cards.Single().Original == false, "Created Wardancer counted as original deck card");
        var illegalCreate = backup with { Opponent = backup.Opponent with { StartingDeckIds = ImmutableHashSet.Create(Get("Elven Wardancer").Id) } };
        Check(engine.ResolvePlay(illegalCreate, "play", PlayerSide.Opponent, new Dictionary<string, PlaySelection> { ["play"] = new(CreatedCardId: Get("Elven Wardancer").Id) }).Points is null, "Backup Plan created a starting-deck identity");

        // Call of the Forest resolves before the three leader charges. Do not force this line to spend Schirru's Order early.
        var schirru = Put(Empty(), PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Barbegazi", "barbegazi", 13), C("Gernichora's Fruit", "fruit", 6));
        schirru = Put(schirru, PlayerSide.User, CardZone.Board, BoardRow.Ranged, C("Siren", "siren", charges: 1), C("Lady of the Lake", "lady"));
        schirru = Put(schirru, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Mahakam Pass", "pass"));
        schirru = Put(Put(schirru, PlayerSide.Opponent, CardZone.Hand, null, C("Call of the Forest", "call")), PlayerSide.Opponent, CardZone.Deck, null, C("Schirrú", "schirru"));
        schirru = schirru with { Opponent = schirru.Opponent with { CurrentLeaderId = "200167", LeaderCharges = 3 } };
        var call = engine.ResolvePlay(schirru, "call", PlayerSide.Opponent, new Dictionary<string, PlaySelection>
            { ["call"] = new(TutorId: "schirru"), ["schirru"] = new(BoardRow.Melee, 1) });
        Check(call.Points == 4, "Tutor must add base 3 and boost 1, without auto-spending Order: " + string.Join("; ", call.Missing));
        Check(engine.Maximum(schirru, "call", PlayerSide.Opponent).MaximumPoints == 6, "Card-only maximum: 4 body/boost + 4 destroyed Siren - 2 Siren Deathwish boosting adjacent Lady; no leader");
        Check(engine.MaximumLeader(call.After!, PlayerSide.Opponent).Estimate.MaximumPoints == 9, "Separate leader maximum must be 9 on this board");
        var p = call.After!; var total = call.Points!.Value;
        for (var i = 0; i < 3; i++)
        {
            var leader = engine.ResolveLeader(p, PlayerSide.Opponent, "schirru");
            Check(leader.Points == 3, "Guerilla charge failed: " + string.Join("; ", leader.Missing)); total += leader.Points!.Value; p = leader.After!;
        }
        var order = engine.ResolveOrder(p, "schirru", PlayerSide.Opponent);
        Check(order.Points == 13, "Boosted Schirru did not destroy 13-power Barbegazi"); total += order.Points!.Value;
        Save("Call / three separate leader charges / Schirrú Order", new(total, order.After, call.Line.Concat(order.Line).ToArray(), [], [], true),
            26, null, "frame-005689-185817083.jpg", "frame-005785-185826692.jpg");
        Check(engine.MaximumOrders(p, PlayerSide.Opponent).MaximumPoints >= 13, "Order maximum below observed Order");
        Check(engine.ResolveLeader(order.After!, PlayerSide.Opponent, "schirru").Points is null, "Fourth leader charge accepted");
        Check(engine.ResolveOrder(order.After!, "schirru", PlayerSide.Opponent).Points is null, "Spent Schirru Order reused");
        Check(PositionNotation.Write(PositionNotation.Read(PositionNotation.Write(order.After!))) == PositionNotation.Write(order.After!), "Extended position context lost in notation");

        // Complete visible crowded board reviewed at 18:54:11. Existing Orders are deliberately NOT spent in replay.
        PositionCard Warrior(string id, int power) => C("Nekker Warrior", id, power) with { ExtraThrive = 1, Statuses = ImmutableHashSet.Create(CardStatus.Infused) };
        var crowded = Put(Empty() with { Round = 1 }, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            C("Barbegazi", "barbegazi", 10, charges: 1), Warrior("warrior1", 4), C("Portal", "portal"), C("Fiend", "fiend1", 8));
        crowded = Put(crowded, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Warrior("warrior2", 2), C("Griffin", "griffin", 4),
            C("Siren", "siren", charges: 1, cooldown: 1), C("Fiend", "fiend2", 5) with { Statuses = ImmutableHashSet.Create(CardStatus.Bleeding), StatusTurns = ImmutableDictionary<CardStatus, int>.Empty.Add(CardStatus.Bleeding, 1) });
        crowded = Put(crowded, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, C("Miner", "miner"), C("Oakcritters", "oak1", 3),
            C("Oakcritters", "oak2", 5), C("Dwarven Agitator", "agitator", armor: 4), C("Wandering Treant", "tree1", 1), C("Wandering Treant", "tree2", 1));
        crowded = Put(crowded, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Duén Canell Guardian", "guardian"),
            C("Dwarven Skirmisher", "skirmisher", 1, armor: 1), C("Mahakam Pass", "pass"), C("Braenn", "braenn", 12, charges: 3));
        crowded = Put(crowded, PlayerSide.Opponent, CardZone.Hand, null, C("Ciaran aep Easnillen", "ciaran"));
        result = engine.ResolvePlay(crowded, "ciaran", PlayerSide.Opponent, new Dictionary<string, PlaySelection> { ["ciaran"] = new(BoardRow.Melee, 4, "siren") });
        Save("Ciaran / lock / movement on crowded board", result, 6, engine.Maximum(crowded, "ciaran", PlayerSide.Opponent).MaximumPoints,
            "frame-003230-185411185.jpg", "frame-003341-185422287.jpg");
        Check(result.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Last() is { InstanceId: "siren" } moved && moved.Statuses!.Contains(CardStatus.Locked), "Ciaran did not lock and move the reviewed target");
        Check(result.After.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single(card => card.InstanceId == "fiend2").Power == 5, "Opponent play ticked player's Bleeding early");

        // A known spent Portal is inert; a running timer advances and uses the known deck inventory.
        var withPortal = Put(fiend, PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Portal", "portal"));
        Check(engine.Maximum(withPortal, "play", PlayerSide.User).MaximumPoints == 9, "Spent Portal blocked unrelated play");
        withPortal = Put(withPortal, PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Portal", "portal", charges: 1, cooldown: 1));
        withPortal = Put(withPortal, PlayerSide.User, CardZone.Deck, null, C("Nekker", "portal-target"));
        var portalMaximum = engine.Maximum(withPortal, "play", PlayerSide.User);
        Check(portalMaximum.MaximumPoints == 9 + Get("Nekker").Power && portalMaximum.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards
                .Single(card => card.InstanceId == "portal").Charges == 0,
            "Active Portal timer did not summon the known 4-provision unit exactly once: points=" +
            (portalMaximum.MaximumPoints?.ToString() ?? "null") + " missing=" + string.Join(';', portalMaximum.Missing));
        var malformed = schirru with { Zones = schirru.Zones.SetItem(0, schirru.Zones[1]) };
        Check(engine.ResolveLeader(malformed, PlayerSide.Opponent, "pass").Points is null, "Replay accepted duplicate zone keys.");
        Check(engine.MaximumLeader(malformed, PlayerSide.Opponent).Estimate.MaximumPoints is null, "Leader maximum accepted duplicate zone keys.");
        var unknownInfusion = Put(fiend, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            C("Fiend", "mystery") with { Statuses = ImmutableHashSet.Create(CardStatus.Infused) });
        Check(engine.ResolveEndTurn(unknownInfusion, PlayerSide.User).Points is null, "Replay silently ignored an unknown infusion.");
        var infused = Put(Empty(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            C("Nekker Warrior", "infused") with { ExtraThrive = 1, Statuses = ImmutableHashSet.Create(CardStatus.Infused) });
        Check(RoundTransitions.Advance(infused, catalog, PlayerSide.User).Position.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Single().ExtraThrive == 0,
            "Round cleanup retained a removed Thrive infusion.");
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-sequence-replay.json"), JsonSerializer.Serialize(new
            { Session = "20260827-184848", Scope = "Reviewed chosen-action regression. Not a complete-match detector accuracy estimate.", Cases = checks }, GameStateJournal.Json));
    }
}
