using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>The calculation representation: no screenshots, timestamps, confidence, URLs or full card definitions.</summary>
public sealed record PositionCard(string InstanceId, string CardId, int? Power, int? BasePower, int? Armor,
    ImmutableHashSet<CardStatus>? Statuses, int? Charges = null, int? Cooldown = null,
    bool? Original = null, ImmutableDictionary<CardStatus, int>? StatusTurns = null, int ExtraThrive = 0);

/// <summary>Cards are ordered left-to-right on board, top-to-bottom in deck, or in hand/graveyard display order.</summary>
public sealed record PositionZone(PlayerSide Side, CardZone Zone, BoardRow? Row,
    ImmutableArray<PositionCard> Cards, bool Complete, int? TotalCount = null);

/// <summary>A known row effect. A null GamePosition.RowEffects means the four-row effect scan is incomplete.</summary>
public sealed record PositionRowEffect(PlayerSide AffectedSide, BoardRow Row, string Name, int? RemainingTurns);

/// <summary>
/// A value carried by a named card while it is in any non-banished zone.  This is separate from a board
/// instance's power: examples are Aerondight's damage and Hen Gaidth Sword's stored soul.
/// </summary>
public sealed record PositionCardValue(PlayerSide Side, string CardId, string Kind, int? Minimum = null,
    int? Maximum = null, string? StoredCardId = null);

public sealed record PositionResources(PlayerSide Side, string? CurrentLeaderId, int? LeaderCharges, int? Coins,
    bool? Passed, bool PassiveEffectsKnownInactive = false, int? RoundsWon = null,
    string? LastPlayedUnitId = null, ImmutableHashSet<string>? StartingDeckIds = null, bool? Devotion = null);

public sealed record GamePosition(ImmutableArray<PositionZone> Zones, PositionResources User, PositionResources Opponent,
    int? Round = null, PlayerSide? ActivePlayer = null, bool RowEffectsKnownInactive = false,
    ImmutableArray<PositionRowEffect>? RowEffects = null, ImmutableArray<PositionCardValue>? CardValues = null)
{
    public PositionZone Zone(PlayerSide side, CardZone zone, BoardRow? row = null) =>
        Zones.Single(item => item.Side == side && item.Zone == zone && item.Row == row);

    public static GamePosition EmptyKnown() => new(Enum.GetValues<PlayerSide>().SelectMany(side => new[]
        {
            new PositionZone(side, CardZone.Board, BoardRow.Melee, [], true, 0),
            new PositionZone(side, CardZone.Board, BoardRow.Ranged, [], true, 0),
            new PositionZone(side, CardZone.Hand, null, [], true, 0),
            new PositionZone(side, CardZone.Deck, null, [], true, 0),
            new PositionZone(side, CardZone.Graveyard, null, [], true, 0),
            new PositionZone(side, CardZone.Banished, null, [], true, 0)
        }).ToImmutableArray(), new(PlayerSide.User, null, 0, 0, false, true), new(PlayerSide.Opponent, null, 0, 0, false, true),
        RowEffectsKnownInactive: true, RowEffects: [], CardValues: []);
}

/// <summary>Lossy by design: audit/provenance remains outside the calculator. Partial inspections are NOT zone inventories.</summary>
public static class CalculationPositionAdapter
{
    public static GamePosition FromObserved(GameStateSnapshot state)
    {
        var zones = ImmutableArray.CreateBuilder<PositionZone>();
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            foreach (var row in state.Rows.Where(item => item.Side == side))
            {
                var fresh = (!state.BoardObscured || row.ScannedAt == state.At) && state.Phase == GamePhase.Playing && state.At is { } at && row.ScannedAt is { } scanned &&
                    at - scanned <= GwentRules.DynamicFactLifetime;
                var visible = row.VisibleInstanceIds.Select(id => state.Cards.FirstOrDefault(card => card.InstanceId == id))
                    .Where(card => card is { Presence: CardPresence.Visible }).Select(card =>
                    {
                        int? Read(StateFact<int>? fact) => fresh && fact is not null && state.At is { } now && fact.Confidence >= .8 &&
                            fact.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed && fact.IsFresh(now, GwentRules.DynamicFactLifetime) ? fact.Value : null;
                        var allStatuses = Enum.GetValues<CardStatus>().All(status => card!.Status(status) is { Confidence: >= .8 } value &&
                            value.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed && value.IsFresh(state.At!.Value, GwentRules.DynamicFactLifetime));
                        var statuses = fresh && allStatuses ? card!.Statuses.Where(item => item.Active.Value).Select(item => item.Status).ToImmutableHashSet() : null;
                        return new PositionCard(card!.InstanceId, card.Card.Id, Read(card.Power), Read(card.BasePower), Read(card.Armor), statuses, Read(card.Charges), Read(card.Cooldown));
                    }).ToImmutableArray();
                var complete = fresh && row.Coverage == RowCoverage.Complete;
                zones.Add(new(side, CardZone.Board, row.Row, visible, complete, complete ? visible.Length : null));
            }
            int? Count(StateFact<int>? fact) => state.At is { } at && fact is not null && fact.IsFresh(at, GwentRules.DynamicFactLifetime) ? fact.Value : null;
            var player = state.Player(side);
            zones.Add(new(side, CardZone.Hand, null, [], false, Count(player.HandCount)));
            zones.Add(new(side, CardZone.Deck, null, [], false, Count(player.DeckCount)));
            zones.Add(new(side, CardZone.Graveyard, null, [], false, Count(player.GraveyardCount)));
            zones.Add(new(side, CardZone.Banished, null, [], false));
        }
        T? Read<T>(StateFact<T>? fact) where T : struct => state.At is { } now && fact is { Confidence: >= .8 } &&
            fact.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed && fact.IsFresh(now, GwentRules.DynamicFactLifetime) ? fact.Value : null;
        PositionResources Resources(PlayerGameState player)
        {
            var reference = player.StartingDeckReference;
            return new(player.Side, null, Read(player.LeaderCharges), Read(player.Coins), Read(player.Passed),
                RoundsWon: player.RoundsWon?.Value,
                StartingDeckIds: reference is { CardCount: >= 25 } ? reference.Cards.Select(item => item.Card.Id).ToImmutableHashSet() : null,
                Devotion: reference is { CardCount: >= 25 } ? reference.Cards.All(item => item.Card.Faction != "Neutral") : null);
        }
        var currentRows = state.Rows.Where(row => !state.BoardObscured && state.Phase == GamePhase.Playing && row.EffectsKnown &&
            row.ScannedAt is { } scanned && state.At is { } now && now - scanned <= GwentRules.DynamicFactLifetime).ToArray();
        ImmutableArray<PositionRowEffect>? effects = currentRows.Length == 4
            ? currentRows.SelectMany(row => row.Effects.Select(effect => new PositionRowEffect(row.Side, row.Row, effect.Name,
                effect.RemainingTurns is { Confidence: >= .8 } duration && state.At is { } at && duration.IsFresh(at, GwentRules.DynamicFactLifetime)
                    ? duration.Value : null))).ToImmutableArray()
            : null;
        var values = ImmutableArray.CreateBuilder<PositionCardValue>();
        foreach (var player in new[] { state.User, state.Opponent })
        {
            if (player.StartingDeckReference is not { CardCount: >= 25 } reference) continue;
            foreach (var category in new[] { "Tactic", "Nature" })
            {
                var count = reference.Cards.Where(item => item.Card.HasCategory(category)).Sum(item => item.Count);
                values.Add(new(player.Side, "starting-deck", "starting-" + category.ToLowerInvariant() + "-count", count, count));
            }
            values.AddRange(reference.Cards.Select(item => new PositionCardValue(player.Side, item.Card.Id, "starting-copy-count", item.Count, item.Count)));
        }
        return new(zones.ToImmutable(), Resources(state.User), Resources(state.Opponent), state.Round?.Value,
            Read(state.ActivePlayer), RowEffectsKnownInactive: effects is { } knownEffects && knownEffects.Length == 0, RowEffects: effects,
            CardValues: values.ToImmutable());
    }
}
