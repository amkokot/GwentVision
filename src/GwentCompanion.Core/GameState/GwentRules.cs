using System.Collections.Immutable;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.GameState;

public sealed record RuleAssessment<T>(T? Value, bool Known, string Reason);
public sealed record CountAssessment(int Minimum, int? Maximum, string Reason);
public sealed record UnitPrimitiveState(int Power, int BasePower, int Armor, ImmutableHashSet<CardStatus> Statuses);
public sealed record PrimitiveEffectResult(UnitPrimitiveState Unit, int DirectScoreChange, bool Destroyed,
    ImmutableArray<string> TriggerHooks, string Scope = "Direct primitive only; replacements, prevention abilities and chained triggers are not resolved.");

/// <summary>
/// Small explicit rules kernel, not a text-to-code interpreter or complete GWENT simulator.
/// Live observations are never mutated by a hypothetical effect. Unknown inputs block exact state queries.
/// </summary>
public static class GwentRules
{
    public const string Version = "gwent-foundation/1";
    public const int MaximumRowSize = 9;
    public const int MaximumHandSize = 10;
    public const int MaximumCoins = 9;
    public static readonly TimeSpan DynamicFactLifetime = TimeSpan.FromSeconds(3);

    public static bool TriggersDeploy(GameActionKind action) => action == GameActionKind.Play;
    public static bool CreatesNewCard(GameActionKind action) => action is GameActionKind.Create or GameActionKind.Spawn;

    public static RuleAssessment<int> ScoreGap(GameStateSnapshot state, PlayerSide perspective)
    {
        var own = state.Player(perspective).Score; var other = state.Player(Other(perspective)).Score;
        var actionAt = state.RecentEvents.Where(item => item.Kind == "PlayPreview").Select(item => (DateTimeOffset?)item.At).Max();
        if (state.Phase != GamePhase.Playing || !Fresh(state, own) || !Fresh(state, other) ||
            (state.Round is { } round && (own!.At < round.At || other!.At < round.At)) ||
            (actionAt is { } action && (own!.At < action || other!.At < action)))
            return new(default, false, "Both current scoreboard values must be read after the latest recognized play/round; catalog power is not a score.");
        return new(other!.Value - own!.Value, true, "Positive means behind; a tie requires one more point to lead.");
    }

    public static CountAssessment RowSpace(GameStateSnapshot state, PlayerSide side, BoardRow row)
    {
        var board = state.Rows.Single(item => item.Side == side && item.Row == row);
        if (!FreshRow(state, board)) return new(0, MaximumRowSize, "Row scan is absent, obscured or stale.");
        if (board.VisibleInstanceIds.Length > MaximumRowSize) return new(0, MaximumRowSize, "Conflicting row census exceeds board capacity.");
        var free = Math.Max(0, MaximumRowSize - board.VisibleInstanceIds.Length);
        return board.Coverage == RowCoverage.Complete ? new(free, free, "Complete current row census, including artifacts.") :
            new(0, free, "Recognized cards give an upper bound on free slots, not guaranteed space.");
    }

    public static RuleAssessment<bool> Dominance(GameStateSnapshot state, PlayerSide side, string? excludedInstanceId = null)
    {
        // A prospective Deploy check should use the pre-play board (or explicitly exclude the entering card).
        var units = VisibleUnits(state).Where(card => card.InstanceId != excludedInstanceId).ToArray();
        var own = units.Where(card => card.Location.Value.Controller == side && Fresh(state, card.Power)).ToArray();
        var enemy = units.Where(card => card.Location.Value.Controller != side && Fresh(state, card.Power)).ToArray();
        if (CompleteBoard(state) && units.All(card => Fresh(state, card.Power)))
        {
            if (own.Length == 0) return new(false, true, "No allied unit controls the highest power.");
            var enemyMax = enemy.Select(card => card.Power!.Value).DefaultIfEmpty(0).Max();
            return new(own.Max(card => card.Power!.Value) >= enemyMax, true, "An allied unit shares or exceeds the highest enemy power on the complete measured board.");
        }

        // Even with a partial artwork census, a fresh total score is a safe ceiling
        // for every individual unit on that side. This proves some states without
        // treating an unseen or unread card as zero.
        var actionAt = state.RecentEvents.Where(item => item.Kind == "PlayPreview").Select(item => (DateTimeOffset?)item.At).Max();
        bool CurrentScore(StateFact<int>? score) => Fresh(state, score) && (actionAt is null || score!.At >= actionAt);
        var ownScore = state.Player(side).Score; var enemyScore = state.Player(Other(side)).Score;
        var ownMax = own.Select(card => card.Power!.Value).DefaultIfEmpty(0).Max();
        var enemyMaxKnown = enemy.Select(card => card.Power!.Value).DefaultIfEmpty(0).Max();
        if (own.Length > 0 && CurrentScore(enemyScore) && ownMax >= enemyScore!.Value)
            return new(true, true, $"A measured allied {ownMax}-power unit meets or exceeds the enemy's fresh {enemyScore.Value}-point total, so no unseen enemy unit can be higher.");
        if (enemy.Length > 0 && CurrentScore(ownScore) && enemyMaxKnown > ownScore!.Value)
            return new(false, true, $"A measured enemy {enemyMaxKnown}-power unit exceeds the allied fresh {ownScore.Value}-point total, so no unseen ally can tie it.");
        return new(false, false, "Dominance is unresolved: the partial board and fresh score ceilings do not prove which side controls the highest-power unit.");
    }

    public static CountAssessment Bloodthirst(GameStateSnapshot state, PlayerSide side)
    {
        var enemy = Other(side);
        var units = VisibleUnits(state).Where(card => card.Location.Value.Controller == enemy).ToArray();
        if (units.Length > 18) return new(0, 18, "Conflicting contact count exceeds one side's board capacity.");
        var readings = units.Select(card => TryReadDamaged(state, card, out var value)
            ? (Known: true, Value: value)
            : (Known: false, Value: false)).ToArray();
        var damaged = readings.Count(reading => reading.Known && reading.Value);
        var unread = readings.Count(reading => !reading.Known);
        var complete = state.Rows.Where(row => row.Side == enemy).All(row => FreshRow(state, row) && row.Coverage == RowCoverage.Complete);
        return new(damaged, complete ? damaged + unread : 18,
            "Counts damaged enemy units from the visible power color, with current/base power as a fallback; unread or unseen units keep the count bounded.");
    }

    public static bool TryReadDamaged(GameStateSnapshot state, GameCardInstance card, out bool damaged)
    {
        if (Fresh(state, card.Damaged)) { damaged = card.Damaged!.Value; return true; }
        if (Fresh(state, card.Power) && Fresh(state, card.BasePower))
        { damaged = card.Power!.Value < card.BasePower!.Value; return true; }
        damaged = false; return false;
    }

    public static RuleAssessment<bool> Barricade(GameStateSnapshot state, string instanceId)
    {
        var card = VisibleUnits(state).FirstOrDefault(item => item.InstanceId == instanceId);
        return card is null || !Fresh(state, card.Armor) ? new(false, false, "Current armor is unknown.") :
            new(card.Armor!.Value > 0, true, "Armor present; this condition does not itself execute the card's Barricade ability.");
    }

    public static bool TryReadDamageTarget(GameStateSnapshot state, string instanceId, out UnitPrimitiveState? target)
    {
        target = null;
        var card = VisibleUnits(state).FirstOrDefault(item => item.InstanceId == instanceId);
        if (card is null || !Fresh(state, card.Power) || !Fresh(state, card.BasePower) || !Fresh(state, card.Armor) ||
            !Fresh(state, card.Status(CardStatus.Shield))) return false;
        target = new(card.Power!.Value, card.BasePower!.Value, card.Armor!.Value,
            card.Statuses.Where(item => item.Active.Value && Fresh(state, item.Active)).Select(item => item.Status).ToImmutableHashSet());
        return true;
    }

    public static PrimitiveEffectResult Damage(UnitPrimitiveState unit, int amount, bool ignoreArmor = false)
    {
        Validate(unit, amount);
        if (amount == 0) return new(unit, 0, false, []);
        if (unit.Statuses.Contains(CardStatus.Shield))
            return new(unit with { Statuses = unit.Statuses.Remove(CardStatus.Shield) }, 0, false, ["ShieldRemoved"]);
        var absorbed = ignoreArmor ? 0 : Math.Min(unit.Armor, amount);
        var lost = Math.Min(unit.Power, amount - absorbed);
        var hooks = ImmutableArray.CreateBuilder<string>();
        if (absorbed > 0) hooks.Add("ArmorLost");
        if (absorbed > 0 && absorbed == unit.Armor) hooks.Add("Exposed");
        if (lost > 0) hooks.Add("Damaged");
        if (lost == unit.Power) hooks.Add("Destroyed: resolve prevention, Deathwish and destination separately");
        return new(unit with { Power = unit.Power - lost, Armor = unit.Armor - absorbed }, -lost, lost == unit.Power, hooks.ToImmutable());
    }

    public static PrimitiveEffectResult Boost(UnitPrimitiveState unit, int amount)
    {
        Validate(unit, amount);
        return new(unit with { Power = checked(unit.Power + amount) }, amount, false, amount == 0 ? [] : ["Boosted"]);
    }

    public static PrimitiveEffectResult Heal(UnitPrimitiveState unit, int amount)
    {
        Validate(unit, amount);
        var healed = Math.Min(amount, Math.Max(0, unit.BasePower - unit.Power));
        return new(unit with { Power = unit.Power + healed }, healed, false, healed == 0 ? [] : ["Healed"]);
    }

    public static PrimitiveEffectResult Purify(UnitPrimitiveState unit)
    {
        Validate(unit, 0);
        return new(unit with { Statuses = ImmutableHashSet<CardStatus>.Empty }, 0, false, ["StatusesRemoved"]);
    }

    private static void Validate(UnitPrimitiveState unit, int amount)
    {
        if (amount < 0 || unit.Power <= 0 || unit.BasePower <= 0 || unit.Armor < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A live unit and nonnegative effect amount/armor are required.");
    }
    private static PlayerSide Other(PlayerSide side) => side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
    private static bool Fresh<T>(GameStateSnapshot state, StateFact<T>? fact) => state.At is { } at && fact is not null &&
        fact.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed && fact.Confidence >= .8 && fact.IsFresh(at, DynamicFactLifetime);
    private static bool FreshRow(GameStateSnapshot state, GameRowState row) => !state.BoardObscured && state.Phase == GamePhase.Playing && state.At is { } at &&
        row.ScannedAt is { } scanned && scanned <= at && at - scanned <= DynamicFactLifetime;
    private static bool CompleteBoard(GameStateSnapshot state) => state.Rows.All(row => FreshRow(state, row) && row.Coverage == RowCoverage.Complete);
    private static IEnumerable<GameCardInstance> VisibleUnits(GameStateSnapshot state)
    {
        var visible = state.Rows.Where(row => FreshRow(state, row)).SelectMany(row => row.VisibleInstanceIds).ToHashSet();
        return state.Cards.Where(card => visible.Contains(card.InstanceId) && card.Card.Kind == CardKind.Unit && card.Presence == CardPresence.Visible);
    }
}
