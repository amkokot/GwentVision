using System.Collections.Immutable;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class CalculationPositionTests
{
    private static void Check(bool test, string message) { if (!test) throw new InvalidOperationException(message); }
    private static PositionCard Card(CardDefinition definition, string instance, int? power = null, int armor = 0, params CardStatus[] statuses) =>
        new(instance, definition.Id, power ?? definition.Power, definition.Power, armor, statuses.ToImmutableHashSet(), 0, 0);
    private static GamePosition Put(GamePosition position, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] cards) =>
        position with { Zones = position.Zones.Select(item => item.Side == side && item.Zone == zone && item.Row == row
            ? item with { Cards = cards.ToImmutableArray(), Complete = true, TotalCount = cards.Length } : item).ToImmutableArray() };

    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var thunder = catalog.Single(card => card.Name == "Alzur's Thunder");
        var swallow = catalog.Single(card => card.Name == "Swallow");
        var body = new CardDefinition("test-body", "Plain test body", "Neutral", CardKind.Unit, 4, 5, AbilityText: "", PrintedArmor: 0);
        var deploy = body with { Id = "test-deploy", Power = 3, AbilityText = "Deploy: Damage an enemy unit by 2." };
        var armored = body with { Id = "test-armor", PrintedArmor = 2 };
        var engine = new CardCalculationEngine(catalog.Concat([body, deploy, armored]));
        Check(CardCalculationEngine.CompileSimple(thunder) is { Effect: DirectEffect.Damage, Amount: 5 } &&
            CardCalculationEngine.CompileSimple(swallow) is { Effect: DirectEffect.Boost, Amount: 6 }, "Actual cached basic spell rules not compiled exactly.");
        var initial = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null, Card(thunder, "spell"));
        initial = Put(initial, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", power: 8, armor: 2));
        var hit = engine.Resolve(initial, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy");
        Check(hit.Supported && hit.PointSwing == 3 && hit.After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single().Power == 5,
            "Damage did not account for armor/current power.");
        Check(initial.Zone(PlayerSide.User, CardZone.Hand).Cards.Length == 1 && initial.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single().Power == 8,
            "Calculation changed the supplied position.");
        Check(hit.After!.Zone(PlayerSide.User, CardZone.Hand).Cards.Length == 0 && hit.After.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Single().InstanceId == "spell",
            "Special did not move hand -> graveyard.");
        var shield = Put(initial, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 8, 2, CardStatus.Shield));
        Check(engine.Resolve(shield, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy") is { Supported: true, PointSwing: 0 }, "Shield incorrectly contributed damage points.");
        var kill = Put(initial, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 3));
        var killed = engine.Resolve(kill, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy");
        Check(killed is { Supported: true, PointSwing: 3 } && killed.After!.Zone(PlayerSide.Opponent, CardZone.Graveyard).Cards.Single().Power == 5,
            "Overkill/kill destination/reset-to-base wrong.");
        var doomed = engine.Resolve(Put(initial, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 3, 0, CardStatus.Doomed)),
            "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy");
        Check(doomed.Supported && doomed.After!.Zone(PlayerSide.Opponent, CardZone.Banished).Cards.Length == 1 && doomed.After.Zone(PlayerSide.Opponent, CardZone.Graveyard).Cards.Length == 0,
            "Doomed was confused with an ordinary graveyard entry.");
        var boost = Put(initial, PlayerSide.User, CardZone.Hand, null, Card(swallow, "boost"));
        Check(engine.Resolve(boost, "boost", PlayerSide.User, GameActionKind.Play, targetId: "enemy") is { Supported: true, PointSwing: -6 }, "Boosting opponent had wrong score sign.");
        var deployed = Put(initial, PlayerSide.User, CardZone.Hand, null, Card(deploy, "unit"));
        var onPlay = engine.Resolve(deployed, "unit", PlayerSide.User, GameActionKind.Play, targetId: "enemy");
        Check(onPlay is { Supported: true, PointSwing: 3 } && onPlay.Events.Contains("DeployResolved"), "Played body/deploy armor accounting wrong.");
        var summonPosition = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Deck, null, Card(deploy, "unit"), Card(body, "next"));
        var summoned = engine.Resolve(summonPosition, "unit", PlayerSide.User, GameActionKind.Summon);
        Check(summoned is { Supported: true, PointSwing: 3 } && !summoned.Events.Contains("DeployResolved") &&
            summoned.After!.Zone(PlayerSide.User, CardZone.Deck).Cards.Single().InstanceId == "next", "Summon executed Deploy or broke deck order.");
        var resurrect = engine.Resolve(Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Graveyard, null, Card(armored, "return")),
            "return", PlayerSide.User, GameActionKind.Summon);
        Check(resurrect.Supported && resurrect.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single().Armor == 2,
            "Graveyard return did not restore printed armor.");
        var full = Put(summonPosition, PlayerSide.User, CardZone.Board, BoardRow.Melee, Enumerable.Range(0, 9).Select(i => Card(body, "row" + i)).ToArray());
        Check(!engine.Resolve(full, "unit", PlayerSide.User, GameActionKind.Summon).Supported, "Overfull row allowed an extra body.");
        var protectedTarget = Put(initial, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy"), Card(body, "guard", 5, 0, CardStatus.Defender));
        Check(!engine.Resolve(protectedTarget, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy").Supported, "Ignored row Defender.");
        var unknownAbility = catalog.Single(card => card.Name == "Roach");
        var unknownDeck = Put(initial, PlayerSide.Opponent, CardZone.Deck, null, Card(unknownAbility, "roach"));
        Check(!engine.Resolve(unknownDeck, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy").Supported,
            "Unmodeled off-board trigger was silently treated as zero.");
        Check(!engine.Resolve(initial with { RowEffectsKnownInactive = false }, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy").Supported,
            "Unknown row effects were silently ignored.");
        var partial = initial with { Zones = initial.Zones.Select(zone => zone.Zone == CardZone.Graveyard ? zone with { Complete = false } : zone).ToImmutableArray() };
        Check(!engine.Resolve(partial, "spell", PlayerSide.User, GameActionKind.Play, targetId: "enemy").Supported, "Partial graveyard claimed complete calculation.");
        var notation = PositionNotation.Write(initial);
        Check(notation.Length < 800 && !notation.Contains(body.Name) && !notation.Contains("Confidence"), "Calculation state is not compact/ID-based.");
        Check(PositionNotation.Write(PositionNotation.Read(notation)) == notation, "Compact position did not round-trip.");
        var weatherNotation = PositionNotation.Write(initial with { RowEffectsKnownInactive = false, RowEffects =
            [new(PlayerSide.Opponent, BoardRow.Melee, "Blood Moon", 2), new(PlayerSide.Opponent, BoardRow.Ranged, "Frost", null)] });
        Check(PositionNotation.Write(PositionNotation.Read(weatherNotation)) == weatherNotation,
            "Compact position lost row-effect identity, row, or duration.");
        var patch = PositionNotation.Changes(initial, hit.After!);
        Check(PositionNotation.Write(PositionNotation.Read(notation + "\n" + patch)) == PositionNotation.Write(hit.After!), "Changed-lines log did not reconstruct position.");
        Check(PositionNotation.Changes(initial, initial) == "", "Unchanged state emitted moves.");
        var observer = new GameStateTracker(); observer.Reset("notation");
        var unknownPosition = CalculationPositionAdapter.FromObserved(observer.Current);
        Check(!unknownPosition.Zones.Any(zone => zone.Complete) && PositionNotation.Write(PositionNotation.Read(PositionNotation.Write(unknownPosition))) == PositionNotation.Write(unknownPosition),
            "Unknown zones became known empty or notation lost unknowns.");
        var directory = Path.Combine(root, "GwentCompanion/diagnostics");
        File.WriteAllText(Path.Combine(directory, "v0.1.19-position-example.gvn"), notation + "\n; Play Alzur's Thunder: armor 2 absorbs 2, power 8 -> 5, net +3\n" + patch);
        Console.WriteLine($"  Compact notation: {notation.Length} characters for a complete minimal position; {patch.Length}-character change log.");
        Console.WriteLine("  Calculation tests passed: actual Thunder/Swallow, body/Deploy/summon distinctions, armor/Shield/overkill, Doomed, graveyard return, row limits, Defender and unsupported-trigger refusal.");
    }
}
