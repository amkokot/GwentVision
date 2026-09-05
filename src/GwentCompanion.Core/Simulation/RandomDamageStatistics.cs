using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record RandomEffectStatistics(PointDistribution Distribution, string Label);

/// <summary>Small exact damage-only state graph. No sampling, optimal-search branch frequencies or speculative reactions.</summary>
public static class RandomDamageStatistics
{
    private sealed record Target(int Power, int Armor, bool Shield);
    public static PointDistribution? Packets(IReadOnlyList<(int Power, int Armor, bool Shield)> units, IReadOnlyList<int> packets,
        bool ignoreArmor = false, int stateLimit = 12000)
    {
        if (units.Count > 18 || units.Any(unit => unit.Power < 1 || unit.Armor < 0) || packets.Count > 16 || packets.Any(n => n < 1 || n > 30)) return null;
        static string Key(IEnumerable<Target> values) => string.Join(';', values.OrderBy(t => t.Power).ThenBy(t => t.Armor).ThenBy(t => t.Shield)
            .Select(t => $"{t.Power},{t.Armor},{(t.Shield ? 1 : 0)}"));
        var initial = units.Select(unit => new Target(unit.Power, unit.Armor, unit.Shield)).ToArray();
        var states = new Dictionary<string, (Target[] Units, double Mass)> { [Key(initial)] = (initial, 1) };
        var total = initial.Sum(t => t.Power);
        foreach (var damage in packets)
        {
            GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
            var next = new Dictionary<string, (Target[] Units, double Mass)>();
            foreach (var (targets, mass) in states.Values)
            {
                GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
                if (targets.Length == 0) { var old = next.GetValueOrDefault(""); next[""] = ([], old.Mass + mass); continue; }
                // Identical targets still contribute their full multiplicity before equivalent states are merged.
                for (var index = 0; index < targets.Length; index++)
                {
                    var target = targets[index]; var absorbed = ignoreArmor ? 0 : Math.Min(damage, target.Armor);
                    var changed = target.Shield ? target with { Shield = false } :
                        new Target(Math.Max(0, target.Power - damage + absorbed), target.Armor - absorbed, false);
                    var remaining = targets.Select((item, i) => i == index ? changed : item).Where(item => item.Power > 0).ToArray();
                    var key = Key(remaining); var old = next.GetValueOrDefault(key);
                    next[key] = (remaining, old.Mass + mass / targets.Length);
                    if (next.Count > stateLimit) return null; // Never turn truncation into an exact distribution.
                }
            }
            states = next;
        }
        return PointDistribution.Of(states.Values.Select(state => new PointMass(total - state.Units.Sum(t => t.Power), state.Mass)),
            0, false, "Exact direct damage under supplied targets/armor/shields; excludes additional reactions.");
    }

    public static RandomEffectStatistics? For(CardDefinition card, GamePosition position, PlayerSide side, PlayRuleBook book)
    {
        var text = PlayRules.Normalize(card.AbilityText); int[]? packets = null; var ignoreArmor = false; var label = "Direct damage";
        if (text == "Split 4 damage randomly between all enemy units. Increase the damage by 1 for each Siege Engine you control.")
        {
            var engines = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards)
                .Count(unit => book.Rules.GetValueOrDefault(unit.CardId)?.Card.HasCategory("Siege Engine") == true);
            packets = Enumerable.Repeat(1, 4 + engines).ToArray();
        }
        else if (text == "Deploy (Melee): Split 3 damage randomly between all enemy units.") packets = [1, 1, 1];
        else if (text == "Deathwish: Damage a random enemy unit by 5.") { packets = [5]; label = "Deathwish damage · only if triggered"; }
        else if (text.StartsWith("Order: Split 4 damage randomly between all enemy units, ignoring their Armor. Charges: 2", StringComparison.Ordinal))
        { packets = [1, 1, 1, 1]; ignoreArmor = true; label = "Direct damage · one leader charge"; }
        if (packets is null) return null;
        var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var rows = position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board).ToArray();
        var partial = position.Zones.Where(zone => zone.Zone == CardZone.Board).Any(zone => !zone.Complete);
        var units = rows.SelectMany(zone => zone.Cards).Where(unit => book.Rules.GetValueOrDefault(unit.CardId)?.Card.Kind == CardKind.Unit).ToArray();
        if (units.Any(unit => unit.Power is null || unit.Armor is null || unit.Statuses is null)) return null;
        // Damage/death listeners can change the target population mid-sequence. Refuse them, rather than draw a misleading chart.
        if (position.Zones.Where(zone => zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).Any(unit =>
            book.Rules.GetValueOrDefault(unit.CardId) is not { } rule || rule.Unmodeled is not null ||
            rule.Deathwish is not null && units.Any(enemyUnit => enemyUnit.InstanceId == unit.InstanceId) || rule.Reaction is not null)) return null;
        var distribution = Packets(units.Select(unit => (unit.Power!.Value, unit.Armor!.Value, unit.Statuses!.Contains(CardStatus.Shield))).ToArray(), packets, ignoreArmor);
        if (distribution is not null && partial) distribution = distribution with { Approximate = true,
            Basis = distribution.Basis + " Conditional on visible targets only; missing units/engines can change probabilities and damage." };
        return distribution is null ? null : new(distribution, (partial ? "Visible-target model · " : "") + label);
    }
}
