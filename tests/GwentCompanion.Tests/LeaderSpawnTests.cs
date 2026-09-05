using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Platform.Windows.Vision;

internal static class LeaderSpawnTests
{
    private static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var cards = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var engine = new TacticalPlayEngine(cards);
        var leaders = cards.Where(card => card.Kind == CardKind.Leader && card.AbilityText?.Contains("Spawn") == true).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        var named = leaders.SelectMany(leader => LeaderSpawnCatalog.NamedUnits(leader, cards)).DistinctBy(card => card.Id).ToArray();
        Check(named.Length == 15, "Leader named-unit coverage changed; review the catalog and token references: " + string.Join(", ", named.Select(card => card.Name + "#" + card.Id)));
        Check(named.All(card => references.Any(item => item.Card.Id == card.Id)), "A leader-spawned card has no recognition reference.");
        var audit = leaders.Select(leader => new { leader.Id, leader.Name, leader.Faction,
            CalculationSupported = engine.Leader(leader.Id) is not null, VariableCopy = LeaderSpawnCatalog.VariableCopy(leader),
            Units = LeaderSpawnCatalog.NamedUnits(leader, cards).Select(card => new { card.Id, card.Name, card.CanBeInStartingDeck,
                References = references.Count(item => item.Card.Id == card.Id),
                ObservedVariants = references.Count(item => item.Card.Id == card.Id && item.Path.Contains("observed-art")),
                PremiumVariants = references.Count(item => item.Card.Id == card.Id && item.Path.Contains("premium-frames")) }) }).ToArray();
        foreach (var (id, points, charges, name) in new[] { ("202117", 1, 1, "Fruit"), ("202115", 9, 3, "Deadeyes"),
            ("201743", 5, 5, "Drones"), ("132103", 9, 1, "Woodland Spirit"), ("202188", 7, 1, "Dana"), ("202347", 9, 3, "Zealots") })
        {
            var p = GamePosition.EmptyKnown() with { Opponent = GamePosition.EmptyKnown().Opponent with { CurrentLeaderId = id, LeaderCharges = charges } };
            var result = engine.LeaderContribution(p, PlayerSide.Opponent);
            Check(result.Estimate.MaximumPoints == points, $"{name}: expected {points}, got {result.Estimate.MaximumPoints}: {string.Join(';', result.Estimate.Missing)}");
            Check(result.Estimate.After!.Zones.Where(zone => zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).All(card => card.Original == false), "Leader-created body counted as an original card.");
            if (id == "202347") Check(result.Estimate.After.Opponent.Coins == 3, "Congregate coins were lost after reach valuation.");
            Check(engine.LeaderContribution(p with { Opponent = p.Opponent with { LeaderCharges = 0 } }, PlayerSide.Opponent).Estimate.MaximumPoints == 0,
                "Exhausted leader spawned future bodies again.");
            Console.WriteLine($"Leader {name}: separate +{points}; {charges} charge(s); generated origins retained.");
        }
        var foe = cards.Single(card => card.Name == "Fiend");
        var blood = GamePosition.EmptyKnown() with { Opponent = GamePosition.EmptyKnown().Opponent with { CurrentLeaderId = "202185", LeaderCharges = 3 } };
        blood = blood with { Zones = blood.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee
            ? zone with { Cards = [new("enemy", foe.Id, 8, 8, 0, ImmutableHashSet<CardStatus>.Empty, 0, 0)], TotalCount = 1 } : zone).ToImmutableArray() };
        var scent = engine.LeaderContribution(blood, PlayerSide.Opponent);
        Check(scent.Estimate.MaximumPoints == 3 && scent.Estimate.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single().Power == 8,
            "Blood Scent omitted Ekimmara or counted future enemy Bleeding as immediate points.");
        Check(LeaderSpawnCatalog.VariableCopy(cards.Single(card => card.Name == "Imposter")), "Variable copied bodies were omitted from audit.");
        var output = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-leader-spawn-audit.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { NamedUnitIdentities = named.Length,
            Note = "Reference availability and supplied-state rules checks, not proven live recognition for every leader/token. Named references do not prove spawn occurrence or original ownership.", Leaders = audit }, GameStateJournal.Json));
        Console.WriteLine("Leader token audit passed: 15 named identities; variable copies/weather/specials audited separately. " + output);
    }
}
