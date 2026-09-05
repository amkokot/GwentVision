using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.GameState;

public sealed record FunctionalSynergyEstimate(string Mechanic, string Display, int MinimumPoints,
    int? MaximumPoints, string Reason);

/// <summary>
/// Reports cards that behave like an additional engine without pretending they
/// own the printed keyword. Values are immediate, best-case points/reach added
/// by the next package trigger; deferred Vitality is labelled explicitly.
/// </summary>
public static class FunctionalSynergyMeter
{
    private static readonly Regex DirectPlayPayoff = new(
        @"Whenever you play (?:an? )?(?<package>Alchemy|Crime|Organic|Tactic|Warrior)(?: card)?,?\s*(?:then )?(?<effect>boost|damage)[^.]*? by (?<amount>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChargePayoff = new(
        @"Order(?:\s*\([^)]*\))?:\s*(?<effect>Damage|Boost)[^.]*? by (?<order>\d+).*?Whenever you play (?:an? )?(?<package>Tactic|Warrior)(?: card)?,?\s*gain (?<charges>\d+) Charge",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static FunctionalSynergyEstimate? Read(GameStateSnapshot state, PlayerSide side, string mechanic,
        string? startingLeader = null, bool leaderConfirmed = true)
    {
        if (state.At is not { } now) return null;
        var rowStart = state.Round?.At;
        var cards = state.Cards.Where(card => card.Card.Kind == CardKind.Unit &&
            card.Location.Value.Controller == side && card.Location.Value.Zone == CardZone.Board &&
            card.Status(CardStatus.Locked)?.Value != true &&
            (card.Presence == CardPresence.Visible && now - card.LastSeen <= GwentRules.DynamicFactLifetime ||
             card.Presence == CardPresence.LastKnown && now - card.LastSeen <= GwentRules.DynamicFactLifetime ||
             card.Presence == CardPresence.Uncertain && rowStart is { } start && card.LastSeen >= start))
            .DistinctBy(card => card.InstanceId).ToArray();
        var approximate = cards.Any(card => card.Presence != CardPresence.Visible);
        var contributions = new List<(int Minimum, int Maximum, string Source)>();

        if (mechanic.Equals("Raid", StringComparison.OrdinalIgnoreCase))
        {
            // Highland Warlord is a match-persistent Deploy modifier. Replays
            // legitimately stack, so count distinct play episodes, not copies.
            var warlords = state.RecentEvents.Where(item => item.Kind == "PlayPreview" && item.Side == side && item.CardId == "203113")
                .DistinctBy(item => item.Id).Count();
            if (warlords > 0)
                return new("Raid", $"+{warlords}", warlords, warlords,
                    $"{warlords} Highland Warlord Deploy{(warlords == 1 ? "" : "s")} observed this match; each adds 1 damage to Raid cards for the rest of the game. Warrior count by itself does not increase Raid damage.");
        }

        foreach (var group in cards.GroupBy(card => card.Card.Id))
        {
            var copies = group.Count();
            var definition = group.First().Card;
            var text = definition.AbilityText ?? "";
            foreach (Match match in DirectPlayPayoff.Matches(text))
            {
                if (!match.Groups["package"].Value.Equals(mechanic, StringComparison.OrdinalIgnoreCase)) continue;
                var amount = int.Parse(match.Groups["amount"].Value);
                if (text.Contains("Bonded:", StringComparison.OrdinalIgnoreCase) && copies >= 2) amount *= 2;
                contributions.Add((amount * copies, amount * copies,
                    $"{copies}× {definition.Name}: +{amount} each" +
                    (text.Contains("Bonded:", StringComparison.OrdinalIgnoreCase) && copies >= 2 ? " (Bonded)" : "")));
            }
            foreach (Match match in ChargePayoff.Matches(text))
            {
                if (!match.Groups["package"].Value.Equals(mechanic, StringComparison.OrdinalIgnoreCase)) continue;
                var points = int.Parse(match.Groups["order"].Value) * int.Parse(match.Groups["charges"].Value) * copies;
                contributions.Add((points, points, $"{copies}× {definition.Name} charge reach"));
            }
        }

        if (mechanic.Equals("Alchemy", StringComparison.OrdinalIgnoreCase) &&
            startingLeader?.Equals("Battle Trance", StringComparison.OrdinalIgnoreCase) == true)
        {
            var damaged = cards.Any(card => GwentRules.TryReadDamaged(state, card, out var value) && value);
            if (damaged)
                contributions.Add((leaderConfirmed ? 1 : 0, 1, leaderConfirmed ?
                    "Battle Trance: +1 heal with a currently damaged allied unit" :
                    "Likely Battle Trance: up to +1 heal with a currently damaged allied unit; leader is inferred, not confirmed"));
        }

        if (mechanic.Equals("Organic", StringComparison.OrdinalIgnoreCase))
        {
            if (startingLeader?.Equals("Arachas Swarm", StringComparison.OrdinalIgnoreCase) == true)
                contributions.Add((leaderConfirmed ? 1 : 0, 1, leaderConfirmed ?
                    "Arachas Swarm: +1 spawned Drone for each Organic card" :
                    "Likely Arachas Swarm: up to +1 spawned Drone per Organic card; leader is inferred, not confirmed"));
            // The direct-text pass already accounts for Kikimore Hatchling. Queen
            // is intentionally modeled by current row population: its Organic
            // trigger and ordinary Thrive trigger share the same row-wide payoff.
            foreach (var queen in cards.Where(card => card.Card.Id == "202435"))
            {
                var insectoids = cards.Count(card => card.Location.Value.Row == queen.Location.Value.Row && card.Card.HasCategory("Insectoid"));
                contributions.Add((insectoids, insectoids, $"Kikimore Queen Organic trigger: +1 to {insectoids} current Insectoid{(insectoids == 1 ? "" : "s")} on its row"));
            }
        }

        if (mechanic.Equals("Symbiosis", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var fledgling in cards.Where(card => card.Card.Id == "203152"))
            {
                var fledglingRegion = fledgling.Location.Value.Region;
                var hasLeftTarget = cards.Any(other => other.InstanceId != fledgling.InstanceId &&
                    other.Location.Value.Row == fledgling.Location.Value.Row &&
                    fledglingRegion is { } source && other.Location.Value.Region is { } target && target.Left < source.Left);
                contributions.Add((hasLeftTarget ? 2 : 0, 2, "Naiad Fledgling: Vitality 2"));
            }
            var dryads = cards.Count(card => card.Card.HasCategory("Dryad"));
            foreach (var _ in cards.Where(card => card.Card.Id == "203149"))
                contributions.Add((dryads, dryads, $"Aucwenn: Vitality {dryads} from {dryads} current Dryad{(dryads == 1 ? "" : "s")}"));
        }
        else if (mechanic.Equals("Thrive", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var _ in cards.Where(card => card.Card.Id == "202832"))
                contributions.Add((1, 1, "Koshchey: 1-point spawned body per own Thrive trigger"));
            foreach (var queen in cards.Where(card => card.Card.Id == "202435"))
            {
                var insectoids = cards.Count(card => card.Location.Value.Row == queen.Location.Value.Row && card.Card.HasCategory("Insectoid"));
                contributions.Add((insectoids, insectoids, $"Kikimore Queen: +1 to {insectoids} current Insectoid{(insectoids == 1 ? "" : "s")} on its row"));
            }
        }
        else if (mechanic.Equals("Intimidate", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var cleaver in cards.Where(card => card.Card.Id == "202890" && card.Location.Value.Region is not null))
            {
                var ordered = cards.Where(card => card.Location.Value.Row == cleaver.Location.Value.Row && card.Location.Value.Region is not null)
                    .OrderBy(card => card.Location.Value.Region!.Value.Left).ToArray();
                var index = Array.FindIndex(ordered, card => card.InstanceId == cleaver.InstanceId);
                var adjacent = (index > 0 && ordered[index - 1].Card.HasCategory("Crownsplitters") ? 1 : 0) +
                    (index >= 0 && index + 1 < ordered.Length && ordered[index + 1].Card.HasCategory("Crownsplitters") ? 1 : 0);
                if (adjacent > 0) contributions.Add((adjacent, adjacent, $"Cleaver: {adjacent} adjacent Crownsplitter bonus"));
            }
        }

        if (contributions.Count == 0) return null;
        var minimum = contributions.Sum(item => item.Minimum);
        var maximum = contributions.Sum(item => item.Maximum);
        var deferred = mechanic.Equals("Symbiosis", StringComparison.OrdinalIgnoreCase) &&
            contributions.Any(item => item.Source.Contains("Vitality", StringComparison.Ordinal));
        var pointRange = minimum == maximum ? minimum.ToString() : $"{minimum}–{maximum}";
        var display = $"+{pointRange}{(deferred ? "*" : "")}";
        var reason = string.Join("; ", contributions.Select(item => item.Source)) + ". " +
            (deferred ? "*Vitality is deferred and needs capacity/turns to realize; it is not a literal extra Symbiosis keyword." :
                "Immediate best-case contribution from currently active, unlocked functional engines.");
        if (approximate) reason += " Recent board contacts are retained across a partial artwork pass.";
        return new(mechanic, display, minimum, maximum, reason);
    }
}
