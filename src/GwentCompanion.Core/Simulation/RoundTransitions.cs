using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record RoundTransitionResult(GamePosition Position, IReadOnlyList<string> Unresolved);

public static class RoundTransitions
{
    /// <summary>Known-position cleanup only. Does not invent the subsequent draws/mulligans, nor fire Deathwish on round cleanup.</summary>
    public static RoundTransitionResult Advance(GamePosition p, IEnumerable<CardDefinition> catalog, PlayerSide? winner = null, bool tied = false)
    {
        var rules = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id, PlayRules.Compile);
        var missing = new List<string>();
        var zones = p.Zones.ToDictionary(zone => (zone.Side, zone.Zone, zone.Row));
        foreach (var row in p.Zones.Where(zone => zone.Zone == CardZone.Board))
        {
            var retained = new List<PositionCard>();
            foreach (var card in row.Cards)
            {
                if (card.Statuses is null) { retained.Add(card); missing.Add("Unknown round-cleanup statuses: " + card.InstanceId); continue; }
                if (card.Statuses.Contains(CardStatus.Resilience))
                {
                    retained.Add(card with { Power = card.Power > card.BasePower ? card.BasePower : card.Power, Armor = 0,
                        Statuses = card.Statuses.Remove(CardStatus.Resilience) });
                    continue;
                }
                var destinationSide = rules.GetValueOrDefault(card.CardId)?.Reaction == "vypper"
                    ? row.Side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User : row.Side;
                var key = (destinationSide, card.Statuses.Contains(CardStatus.Doomed) ? CardZone.Banished : CardZone.Graveyard, (BoardRow?)null);
                var destination = zones[key];
                zones[key] = destination with { Cards = destination.Cards.Add(card with { Power = card.BasePower, Armor = 0,
                    Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null, Charges = 0, Cooldown = 0, ExtraThrive = 0 }), TotalCount = destination.TotalCount + 1 };
            }
            zones[(row.Side, row.Zone, row.Row)] = row with { Cards = retained.ToImmutableArray(), TotalCount = row.Complete ? retained.Count : null };
        }
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var graveKey = (side, CardZone.Graveyard, (BoardRow?)null); var deckKey = (side, CardZone.Deck, (BoardRow?)null);
            var grave = zones[graveKey]; var deck = zones[deckKey];
            var echoes = grave.Cards.Where(card => rules.GetValueOrDefault(card.CardId)?.Echo == true).ToArray();
            if (echoes.Length > 1) missing.Add("Multiple Echo cards: relative top-deck ordering requires observation.");
            zones[graveKey] = grave with { Cards = grave.Cards.Except(echoes).ToImmutableArray(), TotalCount = grave.TotalCount - echoes.Length };
            zones[deckKey] = deck with { Cards = echoes.Select(card => card with { Statuses = (card.Statuses ?? []).Add(CardStatus.Doomed) }).Concat(deck.Cards).ToImmutableArray(),
                TotalCount = deck.TotalCount + echoes.Length };
        }
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var graveKey = (side, CardZone.Graveyard, (BoardRow?)null); var banishedKey = (side, CardZone.Banished, (BoardRow?)null);
            var grave = zones[graveKey]; var phoenixes = grave.Cards.Where(card => rules.GetValueOrDefault(card.CardId)?.Reaction == "phoenix-round").ToArray();
            if (phoenixes.Length == 0) continue;
            var hatchling = rules.Values.FirstOrDefault(rule => rule.Card.Name == "Phoenix Hatchling");
            if (hatchling is null) { missing.Add("Phoenix Hatchling definition unavailable at round start."); continue; }
            foreach (var phoenix in phoenixes)
            {
                grave = grave with { Cards = grave.Cards.Remove(phoenix), TotalCount = grave.TotalCount - 1 };
                var banished = zones[banishedKey];
                zones[banishedKey] = banished with { Cards = banished.Cards.Add(phoenix), TotalCount = banished.TotalCount + 1 };
                var rows = Enum.GetValues<BoardRow>().Where(row => zones[(side, CardZone.Board, (BoardRow?)row)].Cards.Length < 9).ToArray();
                if (rows.Length == 0) { missing.Add("Phoenix Hatchling has no open allied row."); continue; }
                if (rows.Length > 1) missing.Add("Phoenix Hatchling random row requires the observed outcome; Melee is the deterministic replay placeholder.");
                var targetKey = (side, CardZone.Board, (BoardRow?)rows[0]); var target = zones[targetKey];
                var created = new PositionCard(phoenix.InstanceId + "-hatchling", hatchling.Card.Id, hatchling.Card.Power, hatchling.Card.Power,
                    hatchling.Card.PrintedArmor ?? 0, hatchling.PrintedStatuses, hatchling.InitialCharges, hatchling.Zeal ? 0 : 1, false,
                    ImmutableDictionary<CardStatus, int>.Empty);
                zones[targetKey] = target with { Cards = target.Cards.Add(created), TotalCount = target.TotalCount + 1 };
            }
            zones[graveKey] = grave;
        }
        PositionResources Resources(PositionResources player)
        {
            var wins = player.RoundsWon is { } known && (winner is not null || tied) ? known + (winner == player.Side || tied ? 1 : 0) : (int?)null;
            return player with { Coins = player.Coins / 2, RoundsWon = wins, Passed = false,
                LeaderCharges = player.CurrentLeaderId == "202577" ? 1 : player.LeaderCharges };
        }
        missing.Add("Round-start draw, mulligan and other card-specific round-start effects have not been resolved.");
        return new(p with { Zones = p.Zones.Select(zone => zones[(zone.Side, zone.Zone, zone.Row)]).ToImmutableArray(),
            Round = p.Round is >= 1 and < 3 ? p.Round + 1 : null, User = Resources(p.User), Opponent = Resources(p.Opponent), ActivePlayer = null }, missing);
    }
}
