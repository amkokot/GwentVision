using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

internal static class PirateArmorTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var at = DateTimeOffset.UnixEpoch;
        var target = new CardDefinition("armor-target-card", "Armor target", "Monsters", CardKind.Unit, 4, 5);
        GameStateSnapshot State(string session, int second, params (string Id, int Power, int BasePower)[] values)
        {
            var observedAt = at.AddSeconds(second);
            var cards = values.Select((value, index) =>
            {
                var region = new NormalizedRegion(.1 + index * .1, .2, .15 + index * .1, .4);
                return new GameCardInstance(value.Id, target,
                    new(new(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, region), observedAt, 1, EvidenceKind.Visual, "fixture"),
                    at, observedAt, CardPresence.Visible,
                    Power: new(value.Power, observedAt, 1, EvidenceKind.Visual, "fixture"),
                    BasePower: new(value.BasePower, observedAt, 1, EvidenceKind.Visual, "fixture"));
            }).ToImmutableArray();
            return new GameStateTracker().Current with
            {
                SessionId = session,
                Revision = second,
                At = observedAt,
                Phase = GamePhase.Playing,
                Cards = cards,
                User = new(PlayerSide.User,
                    StartingLeader: new("Onslaught", at, 1, EvidenceKind.Reference, "fixture"))
            };
        }

        var tracker = new PirateArmorTracker();
        var before = State("armor", 1, ("target-1", 5, 5), ("target-2", 5, 5));
        var damaged = State("armor", 2, ("target-1", 3, 5), ("target-2", 4, 5));
        Check(tracker.Observe(new(before, damaged, [], true)) && tracker.Read(PlayerSide.User).ObservedTriggers == 2,
            "Each enemy unit that became damaged did not add one Onslaught hand-Armor trigger.");

        var stillDamaged = State("armor", 3, ("target-1", 2, 5), ("target-2", 3, 5));
        tracker.Observe(new(damaged, stillDamaged, [], true));
        Check(tracker.Read(PlayerSide.User).ObservedTriggers == 2,
            "Further damage to already-damaged enemies incorrectly added hand Armor.");

        var healed = State("armor", 4, ("target-1", 5, 5), ("target-2", 5, 5));
        var damagedAgain = State("armor", 5, ("target-1", 4, 5), ("target-2", 5, 5));
        tracker.Observe(new(stillDamaged, healed, [], true));
        tracker.Observe(new(healed, damagedAgain, [], true));
        var estimate = tracker.Read(PlayerSide.User);
        Check(estimate.ObservedTriggers == 3 &&
            LiveSynergyMeter.Read(damagedAgain, PlayerSide.User, "Pirate Armor", "Onslaught", pirateArmor: estimate)
                is { Value: "+3", Active: true, Minimum: 3, Maximum: null },
            "A healed enemy becoming damaged again was not retained in the Pirate Armor meter.");

        Check(LiveSynergyMeter.Read(damagedAgain, PlayerSide.User, "Pirate Armor", "Patricidal Fury", pirateArmor: estimate)
                is { Value: "+0", Active: false, Minimum: 0, Maximum: 0 },
            "Pirate Armor triggers leaked to a non-Onslaught leader.");

        var inactiveTracker = new PirateArmorTracker();
        var inactiveBefore = before with
        {
            SessionId = "inactive-armor",
            User = new(PlayerSide.User,
                StartingLeader: new("Patricidal Fury", at, 1, EvidenceKind.Reference, "fixture"))
        };
        var inactiveAfter = damaged with
        {
            SessionId = "inactive-armor",
            User = inactiveBefore.User
        };
        inactiveTracker.Observe(new(inactiveBefore, inactiveAfter, [], true));
        Check(inactiveTracker.Read(PlayerSide.User).ObservedTriggers == 0,
            "A non-Onslaught side accumulated Pirate Armor triggers internally.");

        GameStateSnapshot OpponentOnslaught(GameStateSnapshot state) => state with
        {
            SessionId = "opponent-armor",
            User = new(PlayerSide.User),
            Opponent = new(PlayerSide.Opponent,
                StartingLeader: new("Onslaught", at, 1, EvidenceKind.Reviewed, "fixture")),
            Cards = state.Cards.Select(card => card with
            {
                Location = new(new(PlayerSide.User, CardZone.Board, card.Location.Value.Row,
                    card.Location.Value.Region), card.Location.At, 1, EvidenceKind.Visual, "fixture")
            }).ToImmutableArray()
        };
        var opponentTracker = new PirateArmorTracker();
        opponentTracker.Observe(new(OpponentOnslaught(before), OpponentOnslaught(damaged), [], true));
        Check(opponentTracker.Read(PlayerSide.Opponent).ObservedTriggers == 2 &&
              opponentTracker.Read(PlayerSide.User).ObservedTriggers == 0,
            "Opponent Onslaught did not receive only its own enemy-damage hand-Armor triggers.");

        GameStateSnapshot WithTone(GameStateSnapshot state, bool damagedTone)
        {
            var card = state.Cards[0] with
            {
                BasePower = null,
                Damaged = new(damagedTone, state.At!.Value, 1, EvidenceKind.Visual, "fixture power color")
            };
            return state with
            {
                Cards = [card],
                Rows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row =>
                    new GameRowState(side, row, RowCoverage.Complete, state.At,
                        side == PlayerSide.Opponent && row == BoardRow.Melee ? [card.InstanceId] : [], []))).ToImmutableArray()
            };
        }
        var colorBefore = WithTone(State("color-armor", 6, ("color-target", 7, 5)), false);
        var colorAfter = WithTone(State("color-armor", 7, ("color-target", 6, 5)), true);
        var colorTracker = new PirateArmorTracker();
        colorTracker.Observe(new(colorBefore, colorAfter, [], true));
        Check(colorTracker.Read(PlayerSide.User).ObservedTriggers == 1 &&
              LiveSynergyMeter.Read(colorAfter, PlayerSide.User, "Bloodthirst") is { Value: "+1", Active: true } &&
              GwentRules.Bloodthirst(colorAfter, PlayerSide.User).Minimum == 1,
            "Red power-state evidence did not feed both Pirate Armor and Bloodthirst without a fabricated base power.");

        var newMatchBefore = State("new-armor", 1, ("target-3", 5, 5));
        var newMatchAfter = State("new-armor", 2, ("target-3", 4, 5));
        tracker.Observe(new(newMatchBefore, newMatchAfter, [], true));
        Check(tracker.Read(PlayerSide.User).ObservedTriggers == 1,
            "The Pirate Armor counter did not reset at the next match boundary.");
        Console.WriteLine("PASS Skellige Pirate Armor: observed transitions, repeat damage, leader gating, and match reset.");
    }
}
