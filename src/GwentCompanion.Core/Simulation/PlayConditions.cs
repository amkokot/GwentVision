using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public enum ConditionTruth { NotMet, Met, Unknown }
public enum TriggerMoment { Play, EndTurn, Deploy }
public sealed record TriggerCondition(string Key, TriggerMoment Moment, CardZone? SourceZone = null,
    string? PlayedCategory = null, bool Unit = false, bool Gold = false, bool Deathwish = false,
    BoardRow? PlayedRow = null, int OpponentWins = 0, int Elves = 0, int Coins = 0,
    bool Dominance = false, string? AlliedCategory = null, int CategoryCount = 0, int Bloodthirst = 0);

/// <summary>Declarative event gates. Membership selects subscriptions first; these cheap predicates run before effect branching.</summary>
public static class PlayConditions
{
    public static IReadOnlyList<TriggerCondition> Table { get; } =
    [
        new("roach", TriggerMoment.Play, CardZone.Deck, Gold: true),
        new("ronvid", TriggerMoment.Play, CardZone.Graveyard, PlayedCategory: "Soldier"),
        new("brigade", TriggerMoment.Play, CardZone.Deck, PlayedCategory: "Soldier", OpponentWins: 1),
        new("toad", TriggerMoment.Play, CardZone.Graveyard, Unit: true, Deathwish: true, PlayedRow: BoardRow.Ranged),
        new("wyvern-shield", TriggerMoment.Play, CardZone.Graveyard, Unit: true),
        new("aelirenn", TriggerMoment.EndTurn, CardZone.Deck, Elves: 5),
        new("tugo", TriggerMoment.EndTurn, CardZone.Board),
        new("redanian", TriggerMoment.EndTurn, Coins: 9),
        new("winter-queen", TriggerMoment.EndTurn, CardZone.Deck),
        new("anglerfish", TriggerMoment.EndTurn),
        new("drummond-berserker", TriggerMoment.EndTurn, CardZone.Board),
        new("frog", TriggerMoment.EndTurn, CardZone.Board),
        new("peaches", TriggerMoment.EndTurn, CardZone.Board, Coins: 4),
        new("lost-round", TriggerMoment.Deploy, OpponentWins: 1),
        new("hoard4", TriggerMoment.Deploy, Coins: 4),
        new("dominance", TriggerMoment.Deploy, Dominance: true),
        new("dwarf", TriggerMoment.Deploy, AlliedCategory: "Dwarf", CategoryCount: 1),
        new("hound", TriggerMoment.EndTurn, CardZone.Board, Dominance: true),
        new("warcrier", TriggerMoment.EndTurn, CardZone.Board, Bloodthirst: 1),
        new("hamadryad", TriggerMoment.EndTurn, CardZone.Board),
        new("portal-timer", TriggerMoment.EndTurn, CardZone.Board)
    ];
    public static TriggerCondition? Find(string key) => Table.FirstOrDefault(rule => rule.Key == key);
    public static bool MatchesEvent(TriggerCondition rule, TriggerMoment moment, CardZone zone,
        PlayRule? played = null, BoardRow? row = null) =>
        rule.Moment == moment && (rule.SourceZone is null || rule.SourceZone == zone) &&
        (!rule.Unit || played?.Card.Kind == CardKind.Unit) && (!rule.Gold || played?.Card.IsGold == true) &&
        (rule.PlayedCategory is null || played?.Card.HasCategory(rule.PlayedCategory) == true) &&
        (!rule.Deathwish || played?.Deathwish is not null) && (rule.PlayedRow is null || rule.PlayedRow == row);

    public static ConditionTruth Evaluate(TriggerCondition rule, GamePosition p, PlayerSide owner, Func<string, PlayRule?> definitions)
    {
        var own = owner == PlayerSide.User ? p.User : p.Opponent; var opponent = owner == PlayerSide.User ? p.Opponent : p.User;
        var unknown = false;
        if (rule.OpponentWins > 0)
        {
            // Round one itself is sufficient negative evidence even without crown OCR.
            var wins = opponent.RoundsWon ?? (p.Round == 1 ? 0 : (int?)null);
            if (wins is null) unknown = true; else if (wins < rule.OpponentWins) return ConditionTruth.NotMet;
        }
        if (rule.Coins > 0)
        {
            var required = Math.Max(0, rule.Coins - (own.CurrentLeaderId == "202577" ? 2 : 0));
            if (own.Coins is null) unknown = true; else if (own.Coins < required) return ConditionTruth.NotMet;
        }
        if (rule.Elves > 0)
        {
            var rows = p.Zones.Where(zone => zone.Side == owner && zone.Zone == CardZone.Board).ToArray();
            var count = rows.SelectMany(zone => zone.Cards).Count(card => definitions(card.CardId)?.Card.HasCategory("Elf") == true);
            if (count < rule.Elves) { if (rows.All(zone => zone.Complete)) return ConditionTruth.NotMet; unknown = true; }
        }
        var board = p.Zones.Where(zone => zone.Zone == CardZone.Board).ToArray();
        var ownRows = board.Where(zone => zone.Side == owner).ToArray(); var otherRows = board.Where(zone => zone.Side != owner).ToArray();
        bool Unit(PositionCard card) => definitions(card.CardId)?.Card.Kind == CardKind.Unit;
        if (rule.Dominance)
        {
            var allied = ownRows.SelectMany(zone => zone.Cards).Where(Unit).ToArray();
            var enemy = otherRows.SelectMany(zone => zone.Cards).Where(Unit).ToArray();
            if (board.Any(zone => !zone.Complete) || allied.Concat(enemy).Any(card => card.Power is null)) unknown = true;
            else if (allied.Length == 0 || allied.Max(card => card.Power) < enemy.Select(card => card.Power ?? 0).DefaultIfEmpty(0).Max()) return ConditionTruth.NotMet;
        }
        if (rule.AlliedCategory is { } category)
        {
            var count = ownRows.SelectMany(zone => zone.Cards).Count(card => definitions(card.CardId)?.Card.HasCategory(category) == true);
            if (count < rule.CategoryCount) { if (ownRows.All(zone => zone.Complete)) return ConditionTruth.NotMet; unknown = true; }
        }
        if (rule.Bloodthirst > 0)
        {
            var units = otherRows.SelectMany(zone => zone.Cards).Where(Unit).ToArray();
            if (units.Count(card => card.Power < card.BasePower) < rule.Bloodthirst)
            {
                if (otherRows.All(zone => zone.Complete) && units.All(card => card.Power is not null && card.BasePower is not null)) return ConditionTruth.NotMet;
                unknown = true;
            }
        }
        return unknown ? ConditionTruth.Unknown : ConditionTruth.Met;
    }
}
