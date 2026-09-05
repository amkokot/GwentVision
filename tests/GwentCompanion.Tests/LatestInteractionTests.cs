using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class LatestInteractionTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var engine = new TacticalPlayEngine(cards); var checks = new List<object>();
        CardDefinition Definition(string name) => cards.Single(c => c.Name == name);
        PositionCard C(string name, string id, int? power = null, int charges = 0) => new(id, Definition(name).Id,
            power ?? Definition(name).Power, Definition(name).Power, Definition(name).PrintedArmor ?? 0,
            PlayRules.Compile(Definition(name)).PrintedStatuses, charges, 0, true);
        GamePosition Put(GamePosition p, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] c) =>
            p with { Zones = p.Zones.Select(z => z.Side == side && z.Zone == zone && z.Row == row ? z with
                { Cards = c.ToImmutableArray(), TotalCount = c.Length } : z).ToImmutableArray() };
        void Save(string name, ResolvedAction realized, int observed, int? maximum, string frames)
        {
            Check(realized.Points == observed, name + $": observed {observed}, modeled {realized.Points}; " + string.Join("; ", realized.Missing));
            Check(maximum is null || maximum >= observed, name + " maximum below chosen line");
            checks.Add(new { Name = name, ObservedSwing = observed, Modeled = realized.Points, Maximum = maximum, Frames = frames,
                Position = PositionNotation.Write(realized.After!), realized.Line });
            Console.WriteLine($"{name}: realized={observed}, max={maximum?.ToString() ?? "not claimed"}");
        }
        var p = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Gernichora's Fruit", "fruit", 4));
        p = Put(p, PlayerSide.User, CardZone.Hand, null, C("Rotfiend", "rot"));
        var played = engine.ResolvePlay(p, "rot", PlayerSide.User, new Dictionary<string, PlaySelection> { ["rot"] = new(BoardRow.Ranged, 0) });
        Save("Rotfiend body + Thrive", played, 6, engine.Maximum(p, "rot", PlayerSide.User).MaximumPoints, "000519-112619159 -> 000533-112622063 (39 -> 45)");

        var consume = Put(played.After!, PlayerSide.User, CardZone.Board, BoardRow.Ranged, C("Siren", "siren", charges: 1), C("Rotfiend", "rot"));
        consume = Put(consume, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, C("Volunteer", "small", 2), C("Volunteer", "big", 11));
        var resolved = engine.ResolveOrder(consume, "siren", PlayerSide.User, "rot", new Dictionary<string, string> { ["rot"] = "small" });
        Save("Siren consumes Rotfiend / observed random overkill", resolved, 2, engine.MaximumOrders(consume, PlayerSide.User, endTurn: false).MaximumPoints,
            "000533-112622063 -> 000598-112633791 (45/52 -> 45/50)");
        Check(engine.MaximumOrders(consume, PlayerSide.User, endTurn: false).MaximumPoints == 5, "Rotfiend maximum should preserve favorable random target");
        Check(engine.ResolveOrder(consume, "siren", PlayerSide.User, "rot").Points is null, "Unspecified random target silently optimized in replay");
        Check(resolved.After!.Zone(PlayerSide.Opponent, CardZone.Banished).Cards.Single().InstanceId == "small", "Doomed Volunteer was placed in graveyard");
        Check(resolved.After.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Single().InstanceId == "rot", "Consumed original Rotfiend not recorded in graveyard");
        var blocked = Put(consume, PlayerSide.User, CardZone.Board, BoardRow.Ranged, C("Siren", "siren", charges: 1),
            C("Rotfiend", "rot") with { Statuses = ImmutableHashSet.Create(CardStatus.Locked) });
        Check(engine.ResolveOrder(blocked, "siren", PlayerSide.User, "rot").Points == 0, "Locked Deathwish fired");
        var shielded = Put(consume, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged,
            C("Volunteer", "shield", 7) with { Statuses = ImmutableHashSet.Create(CardStatus.Shield, CardStatus.Immune) });
        Check(engine.ResolveOrder(shielded, "siren", PlayerSide.User, "rot").Points == 0,
            "Random damage ignored Shield or treated Immunity as protection from random selection");
        var armored = Put(consume, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, C("Volunteer", "armor", 7) with { Armor = 3 });
        Check(engine.ResolveOrder(armored, "siren", PlayerSide.User, "rot").Points == 2, "Random damage ignored Armor");

        var create = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Hand, null, C("Artorius Vigo", "vigo"));
        create = create with { Opponent = create.Opponent with { StartingDeckIds = ImmutableHashSet.Create(Definition("Squirrel").Id) } };
        resolved = engine.ResolvePlay(create, "vigo", PlayerSide.Opponent, new Dictionary<string, PlaySelection>
        { ["vigo"] = new(BoardRow.Melee, 0, CreatedCardId: Definition("Squirrel").Id), ["spawn-play-1"] = new(BoardRow.Ranged, 0) });
        Save("Artorius / 1-power Squirrel / Assimilate", resolved, 4, engine.Maximum(create, "vigo", PlayerSide.Opponent).MaximumPoints,
            "000281-112553672 -> 000533-112622063 (48 -> 52)");
        var squirrel = resolved.After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged).Cards.Single();
        Check(squirrel.Power == 1 && squirrel.Original == false && resolved.After.Zone(PlayerSide.Opponent, CardZone.Deck).Cards.Length == 0,
            "Created Squirrel was counted as an original deck copy or used printed strength");
        Check(engine.Maximum(create with { Opponent = create.Opponent with { StartingDeckIds = null } }, "vigo", PlayerSide.Opponent).MaximumPoints is null,
            "Unknown Artorius options treated as a known maximum");

        var warrior = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Gernichora's Fruit", "fruit", 5));
        warrior = Put(warrior, PlayerSide.User, CardZone.Hand, null, C("Nekker Warrior", "warrior"));
        resolved = engine.ResolvePlay(warrior, "warrior", PlayerSide.User, new Dictionary<string, PlaySelection> { ["warrior"] = new(BoardRow.Ranged, 0) });
        Save("Nekker Warrior / existing Thrive prevents self-damage", resolved, 8, engine.Maximum(warrior, "warrior", PlayerSide.User).MaximumPoints,
            "000598-112633791 -> 000789-112653526 (45 -> 53)");

        // These are deliberately reaction projections, not a fabricated complete-board reconstruction.
        var tugo = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            C("Henry var Attre", "henry", 6), C("Radeyah", "radeyah", 7), C("Tugo the Elder", "tugo"));
        resolved = engine.ResolveEndTurn(tugo, PlayerSide.Opponent);
        Save("Tugo end-turn lowest boost (card body is separate)", resolved, 1, null, "11:29:55 -> 11:30:00 (22 -> 23)");
        Check(resolved.After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single(c => c.InstanceId == "henry").Power == 7,
            "Tugo boosted the wrong unit");
        var tied = Put(tugo, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Tugo the Elder", "tugo", 12), C("Volunteer", "a", 2), C("Volunteer", "b", 2));
        Check(engine.ResolveEndTurn(tied, PlayerSide.Opponent).Points is null, "Unspecified lowest-unit tie invented a resolved state");
        var might = Put(tied, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, C("Volunteer", "ten", 10));
        Check(engine.ResolveEndTurn(might, PlayerSide.Opponent).Points == 2, "Might did not boost every tied lowest unit");
        var lockedTugo = Put(tugo, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Tugo the Elder", "tugo") with { Statuses = ImmutableHashSet.Create(CardStatus.Locked) });
        Check(engine.ResolveEndTurn(lockedTugo, PlayerSide.Opponent).Points == 0, "Locked Tugo triggered");

        var buhurt = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Hand, null, C("Buhurt", "play"));
        buhurt = Put(Put(buhurt, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, C("Volunteer", "own")), PlayerSide.User, CardZone.Board, BoardRow.Melee, C("Griffin", "enemy"));
        resolved = engine.ResolvePlay(buhurt, "play", PlayerSide.Opponent, new Dictionary<string, PlaySelection> { ["play"] = new(TargetId: "enemy", AlliedTargetId: "own") });
        Check(resolved.Points == 6, "Buhurt counted only friendly gain, not the enemy's 3 points");
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-interactions.json"), JsonSerializer.Serialize(new
        { Session = "20260828-112523", Scope = "Reviewed relevant-reaction projections; not automatic full-match state or proof that all other cards/effects are implemented. Random choices explicit; maximum remains separate.",
            MightSource = "https://www.playgwent.com/en/news/48457/patch-notes-11-7", Cases = checks }, GameStateJournal.Json));
    }
}
