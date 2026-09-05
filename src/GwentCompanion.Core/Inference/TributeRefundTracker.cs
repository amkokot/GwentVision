using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Inference;

public sealed record TributeRefundObservation(string Id, PlayerSide Side, string SourceCardId, string SourceCardName,
    int PaidCoins, int RefundedCoins, int CumulativeCompatibleRefunds, int CompatibleEventCount,
    bool Verified, bool NoRefund, DateTimeOffset At, string Evidence);

/// <summary>
/// Conservatively compares fresh coin HUD values around a visible Tribute play. A partial
/// refund is strong evidence; an unchanged counter is only compatible because the Tribute
/// may have been declined. Repeated compatible events can become a deck hypothesis.
/// </summary>
public sealed class TributeRefundTracker
{
    private sealed record Pending(string Id, PlayerSide Side, CardDefinition Card, DateTimeOffset At,
        int BeforeCoins, int Profit, int Cost);
    private readonly Dictionary<PlayerSide, (int Coins, DateTimeOffset At)> _lastCoins = [];
    private readonly Dictionary<PlayerSide, Pending> _pending = [];
    private readonly Dictionary<PlayerSide, int> _compatible = [];
    private readonly Dictionary<PlayerSide, int> _compatibleEvents = [];
    private string? _session;

    public void Reset() { _lastCoins.Clear(); _pending.Clear(); _compatible.Clear(); _compatibleEvents.Clear(); _session = null; }

    public IReadOnlyList<TributeRefundObservation> Observe(GameStateUpdate update, IReadOnlyList<CardDefinition> catalog)
    {
        if (!update.Accepted || update.After.At is not { } now) return [];
        if (_session != update.After.SessionId) { Reset(); _session = update.After.SessionId; }
        var definitions = catalog.GroupBy(card => card.Id).ToDictionary(group => group.Key, group => group.First());
        var output = new List<TributeRefundObservation>();
        foreach (var play in update.Events.Where(item => item.Kind == "PlayPreview" && item.Side is not null && item.CardId is not null))
        {
            if (!definitions.TryGetValue(play.CardId!, out var card) || play.Side is not { } side) continue;
            var costs = Regex.Matches(card.AbilityText ?? "", @"\bTribute\s+(\d+)\b", RegexOptions.IgnoreCase)
                .Select(match => int.Parse(match.Groups[1].Value)).Where(value => value > 0).ToArray();
            if (costs.Length == 0 || !_lastCoins.TryGetValue(side, out var before) || play.At - before.At > TimeSpan.FromSeconds(5)) continue;
            var profit = Regex.Matches(card.AbilityText ?? "", @"\bProfit\s+(\d+)\b", RegexOptions.IgnoreCase)
                .Select(match => int.Parse(match.Groups[1].Value)).Sum();
            var available = Math.Min(GwentRules.MaximumCoins, before.Coins + profit);
            var reduction = update.Before.Player(side).StartingLeader?.Value.Contains("Off the Books", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
            var payable = costs.Select(cost => Math.Max(0, cost - reduction)).Where(cost => cost <= available).DefaultIfEmpty(0).Max();
            if (payable > 0) _pending[side] = new(play.Id, side, card, play.At, before.Coins, profit, payable);
        }
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var fact = update.After.Player(side).Coins;
            if (fact is null || !fact.IsFresh(now, GwentRules.DynamicFactLifetime)) continue;
            if (_pending.TryGetValue(side, out var pending) && fact.At - pending.At >= TimeSpan.FromSeconds(.75))
            {
                if (fact.At - pending.At > TimeSpan.FromSeconds(12)) _pending.Remove(side);
                else
                {
                    var available = Math.Min(GwentRules.MaximumCoins, pending.BeforeCoins + pending.Profit);
                    var withoutRefund = available - pending.Cost;
                    if (fact.Value >= withoutRefund && fact.Value <= available)
                    {
                        if (fact.Value > withoutRefund)
                        {
                            var refunded = fact.Value - withoutRefund;
                            var verified = fact.Value < available;
                            var cumulative = Math.Min(12, _compatible.GetValueOrDefault(side) + refunded);
                            _compatible[side] = cumulative;
                            var compatibleEvents = _compatibleEvents.GetValueOrDefault(side) + 1;
                            _compatibleEvents[side] = compatibleEvents;
                            output.Add(new(pending.Id + ":coin", side, pending.Card.Id, pending.Card.Name,
                                pending.Cost, refunded, cumulative, compatibleEvents, verified, false, fact.At,
                                verified
                                    ? $"Fresh coin HUD fell {available - fact.Value} instead of the paid Tribute's {pending.Cost}; {refunded} coin(s) were refunded."
                                    : $"Fresh coin HUD stayed at {available} after a payable Tribute card. This is compatible with a full refund, but declining the Tribute remains possible."));
                        }
                        else
                            output.Add(new(pending.Id + ":coin", side, pending.Card.Id, pending.Card.Name,
                                pending.Cost, 0, _compatible.GetValueOrDefault(side), _compatibleEvents.GetValueOrDefault(side),
                                false, true, fact.At,
                                $"Fresh coin HUD paid the full {pending.Cost}-coin Tribute with no refund. King of Beggars was not in deck; it may be absent from the list or bricked in hand."));
                        _pending.Remove(side);
                    }
                }
            }
            _lastCoins[side] = (fact.Value, fact.At);
        }
        return output;
    }
}
