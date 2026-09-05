using GwentCompanion.Core.Domain;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.GameState;

public sealed record LiveSynergyReading(PlayerSide Side, string Name, string Value, bool? Active, int Minimum,
    int? Maximum, string Reason);

/// <summary>Compact current-board counters. Unknown/partial scans stay grey rather than becoming false negatives.</summary>
public static class LiveSynergyMeter
{
    public static LiveSynergyReading Read(GameStateSnapshot state, PlayerSide side, string name, string? startingLeader = null,
        bool leaderConfirmed = true, GrantedMechanicEstimate? granted = null, IEnumerable<CardDefinition>? catalog = null,
        FunctionalSynergyEstimate? functional = null, PirateArmorEstimate? pirateArmor = null,
        CultistSynergyEstimate? cultist = null)
    {
        if (name.Equals("NG Cultist", StringComparison.OrdinalIgnoreCase))
        {
            var boost = cultist?.Boost ?? 0;
            var approximate = cultist?.Approximate == true;
            return new(side, name, $"{(approximate ? "≈" : "")}+{boost}", cultist?.Activated == true ? true : null,
                approximate ? 0 : boost, cultist?.Activated == true && !approximate ? boost : null,
                cultist?.Reason ?? "The Eternal Eclipse's Chapter 1 is not yet confirmed; the Prologue alone does not activate its boost infusion.");
        }
        var ownRows = state.Rows.Where(row => row.Side == side).ToArray();
        bool RowFresh(GameRowState row) => state.At is { } at && row.ScannedAt is { } scan && scan <= at &&
            at - scan <= GwentRules.DynamicFactLifetime;
        var complete = ownRows.All(row => RowFresh(row) && row.Coverage == RowCoverage.Complete);
        var visibleIds = ownRows.Where(RowFresh).SelectMany(row => row.VisibleInstanceIds).ToHashSet();
        var visibleCards = state.Cards.Where(card => visibleIds.Contains(card.InstanceId) && card.Presence == CardPresence.Visible).ToArray();
        // Artwork passes are intentionally sparse. Retain contacts that were on this
        // side of the board moments ago when a partial pass misses them; label the
        // result as approximate instead of visibly oscillating the counter.
        var roundAt = state.Round?.At;
        GameCardInstance[] remembered = state.At is not { } now ? [] : state.Cards.Where(card =>
            card.Location.Value.Controller == side && card.Location.Value.Zone == CardZone.Board &&
            (card.Presence == CardPresence.LastKnown && card.LastSeen <= now && now - card.LastSeen <= GwentRules.DynamicFactLifetime ||
             card.Presence == CardPresence.Uncertain && roundAt is { } start && card.LastSeen >= start)).ToArray();
        var cards = visibleCards.Concat(remembered).DistinctBy(card => card.InstanceId).ToArray();
        var units = cards.Where(card => card.Card.Kind == CardKind.Unit).ToArray();
        var definitions = (catalog ?? state.Cards.Select(card => card.Card)).DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        bool Unlocked(GameCardInstance card) => card.Status(CardStatus.Locked)?.Value != true;
        int Mechanic(GameCardInstance card, string keyword) => Unlocked(card) ? MechanicValue(card.Card.AbilityText, keyword) : 0;
        bool Has(GameCardInstance card, string keyword) => Mechanic(card, keyword) > 0;
        LiveSynergyReading Count(string keyword, int bonus = 0, string? reason = null, bool approximateBonus = false)
        {
            var currentByIdentity = units.GroupBy(card => card.Card.Id).ToDictionary(group => group.Key,
                group => (Seen: group.Count(), Active: group.Sum(card => Mechanic(card, keyword))));
            var playedByIdentity = state.RecentEvents.Where(item => item.Kind == "PlayPreview" && item.Side == side &&
                    item.CardId is not null && (roundAt is null || item.At >= roundAt) &&
                    definitions.GetValueOrDefault(item.CardId!) is { Kind: CardKind.Unit } definition && DeclaresMechanic(definition.AbilityText, keyword))
                .Select(item => (Id: item.CardId!, Value: MechanicValue(definitions[item.CardId!].AbilityText, keyword)))
                .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.Sum(item => item.Value));
            var retainedPlays = playedByIdentity.Sum(pair => Math.Max(0, pair.Value - currentByIdentity.GetValueOrDefault(pair.Key).Active));
            var current = currentByIdentity.Values.Sum(item => item.Active);
            var grantedEstimate = granted?.Estimated ?? 0;
            var count = current + retainedPlays + bonus + grantedEstimate;
            var rememberedMechanic = remembered.Any(card => Has(card, keyword));
            var functionalEstimate = functional?.MaximumPoints ?? functional?.MinimumPoints ?? 0;
            var functionalApproximate = functional is { MinimumPoints: var functionalLow, MaximumPoints: var functionalHigh } &&
                functionalHigh != functionalLow || functional?.Display.StartsWith("≈", StringComparison.Ordinal) == true ||
                functional?.Display.EndsWith('*') == true;
            var approximate = rememberedMechanic || retainedPlays > 0 || approximateBonus || functionalApproximate ||
                granted is { Minimum: var low, Maximum: var high } && low != high;
            var total = count + functionalEstimate;
            var value = $"+{total}{(functional?.Display.EndsWith('*') == true ? "*" : "")}";
            var detail = reason ?? $"Current unlocked units that actually have {keyword}";
            if (rememberedMechanic) detail += "; retained same-round engine contact(s) missed by partial artwork passes.";
            if (retainedPlays > 0) detail += $"; retained {retainedPlays} same-round printed engine play(s) whose board artwork is unresolved.";
            if (granted is not null) detail += " " + granted.Reason;
            if (functional is not null) detail += " " + functional.Reason;
            if (!complete && !rememberedMechanic && retainedPlays == 0) detail += "; board scan is partial.";
            var minimum = current + (approximateBonus ? 0 : bonus) + (granted?.Minimum ?? 0) + (functional?.MinimumPoints ?? 0);
            return new(side, name, value, minimum > 0 || functional is { MaximumPoints: > 0 } ? true : complete && count == 0 ? false : null,
                minimum, complete && !approximate ? total : null, detail);
        }
        if (name.Equals("Dominance", StringComparison.OrdinalIgnoreCase))
        {
            var value = GwentRules.Dominance(state, side);
            return new(side, name, value.Known ? value.Value == true ? "ON" : "OFF" : "?", value.Known ? value.Value : null,
                value.Value == true ? 1 : 0, value.Known ? value.Value == true ? 1 : 0 : null, value.Reason);
        }
        if (name.Equals("Bloodthirst", StringComparison.OrdinalIgnoreCase))
        {
            var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            var enemyRows = state.Rows.Where(row => row.Side == enemy).ToArray();
            var enemyVisible = enemyRows.Where(RowFresh).SelectMany(row => row.VisibleInstanceIds).ToHashSet();
            var enemyCards = state.Cards.Where(card => card.Card.Kind == CardKind.Unit &&
                (enemyVisible.Contains(card.InstanceId) && card.Presence == CardPresence.Visible ||
                 state.At is { } current && card.Location.Value.Controller == enemy && card.Location.Value.Zone == CardZone.Board &&
                 card.Presence == CardPresence.LastKnown && current - card.LastSeen <= GwentRules.DynamicFactLifetime)).ToArray();
            var damaged = enemyCards.Count(card => GwentRules.TryReadDamaged(state, card, out var value) && value);
            var enemyComplete = enemyRows.All(row => RowFresh(row) && row.Coverage == RowCoverage.Complete) &&
                enemyCards.All(card => card.Presence == CardPresence.Visible && GwentRules.TryReadDamaged(state, card, out _));
            var approximate = enemyCards.Any(card => card.Presence == CardPresence.LastKnown);
            var text = $"+{damaged}";
            return new(side, name, text, damaged > 0 ? true : enemyComplete ? false : null, damaged,
                enemyComplete ? damaged : null, "Current damaged enemy units, using the visible red power state when available" +
                (enemyComplete ? "." : approximate ? "; recent contacts retained across a partial artwork pass." : "; unread or unseen units keep this a lower bound."));
        }
        if (name.Equals("Pirate Armor", StringComparison.OrdinalIgnoreCase))
        {
            var onslaught = startingLeader?.Equals("Onslaught", StringComparison.OrdinalIgnoreCase) == true;
            var observed = pirateArmor?.ObservedTriggers ?? 0;
            if (!onslaught)
                return new(side, name, "+0", leaderConfirmed && !string.IsNullOrWhiteSpace(startingLeader) ? false : null,
                    0, leaderConfirmed && !string.IsNullOrWhiteSpace(startingLeader) ? 0 : null,
                    leaderConfirmed && !string.IsNullOrWhiteSpace(startingLeader)
                        ? $"{startingLeader} does not grant Onslaught's Pirate/Ship hand Armor."
                        : "Onslaught is not confirmed; its Pirate/Ship hand-Armor counter is unavailable.");
            var minimum = leaderConfirmed ? observed : 0;
            return new(side, name, $"+{observed}", observed > 0 && leaderConfirmed ? true : null,
                minimum, null, (pirateArmor?.Reason ?? "No reliable enemy undamaged-to-damaged transition has been observed yet.") +
                (leaderConfirmed ? "" : " The leader is inferred rather than confirmed, so the guaranteed minimum remains 0."));
        }
        if (name.Equals("Crew", StringComparison.OrdinalIgnoreCase))
        {
            var pockets = 0;
            foreach (var row in ownRows.Where(RowFresh))
            {
                var ordered = cards.Where(card => card.Location.Value.Row == row.Row)
                    .OrderBy(card => card.Location.Value.Region?.Left ?? 2).ToArray();
                bool Soldier(int index) => ordered[index]!.Card.Kind == CardKind.Unit && ordered[index]!.Card.HasCategory("Soldier");
                for (var i = 0; i + 1 < ordered.Length; i++) if (Soldier(i) && Soldier(i + 1)) pockets++;
                for (var i = 0; i + 2 < ordered.Length; i++) if (Soldier(i) && !Soldier(i + 1) && Soldier(i + 2)) pockets++;
            }
            var approximate = remembered.Length > 0;
            return new(side, name, $"+{pockets}",
                pockets > 0 ? true : complete ? false : null, pockets, complete && !approximate ? pockets : null,
                "Crew pockets between two Soldiers, including a pocket already occupied by a non-Soldier." +
                (complete && !approximate ? "" : approximate ? " Recent contacts are retained across a partial artwork pass." : " Partial row census gives a lower bound."));
        }
        if (name.Equals("Symbiosis", StringComparison.OrdinalIgnoreCase))
        {
            var gift = startingLeader?.Equals("Nature's Gift", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
            return Count("Symbiosis", gift, gift == 1 ?
                (leaderConfirmed ? "Current unlocked Symbiosis units plus Nature's Gift's +1 passive engine." :
                    "Current unlocked Symbiosis units plus a likely Nature's Gift passive inferred from strongly matching cached decks.") : null,
                gift == 1 && !leaderConfirmed);
        }
        if (name.Equals("Hoard", StringComparison.OrdinalIgnoreCase) || name.Equals("Tribute", StringComparison.OrdinalIgnoreCase))
        {
            var coins = state.Player(side).Coins;
            var fresh = state.At is { } at && coins is not null && coins.At <= at && at - coins.At <= GwentRules.DynamicFactLifetime;
            return new(side, name, $"{(fresh ? coins!.Value : 0)}c", fresh ? coins!.Value > 0 : null, fresh ? coins!.Value : 0,
                fresh ? coins!.Value : null, "Current coin reserve; individual Hoard/Tribute thresholds still differ by card.");
        }
        return Count(name);
    }

    internal static bool DeclaresMechanic(string? abilityText, string keyword)
        => MechanicValue(abilityText, keyword) > 0;

    internal static int MechanicValue(string? abilityText, string keyword)
    {
        if (string.IsNullOrWhiteSpace(abilityText)) return 0;
        // Quoted Infusions and prose such as “trigger your Thrive” describe another
        // card/effect; they do not mean that the visible unit currently owns the keyword.
        var text = Regex.Replace(abilityText, "\"[^\"]*\"", "", RegexOptions.CultureInvariant);
        var pattern = $@"(?:^|[.\r\n]\s*|\()\s*{Regex.Escape(keyword)}(?:\s+(?<value>\d+))?(?=\s*(?:[.:)]|Deploy\b))";
        return Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["value"].Success ? int.Parse(match.Groups["value"].Value) : 1)
            .DefaultIfEmpty(0).Max();
    }
}
