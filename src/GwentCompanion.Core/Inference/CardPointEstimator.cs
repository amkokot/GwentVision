using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public enum PointEstimateStatus
{
    ExactImmediate,
    BoundedImmediate,
    NeedsBoardContext,
    Unsupported,
}

public sealed record CardPointEstimate(
    string CardId,
    string CardName,
    int PrintedPower,
    int? MinimumImmediatePoints,
    int? MaximumImmediatePoints,
    PointEstimateStatus Status,
    IReadOnlyList<string> RequiredContext,
    string Explanation);

public sealed class CardPointEstimator
{
    private static readonly Regex SimpleDamage = new(
        @"^(?:Deploy(?: \((?:Melee|Ranged)\))?: )?Damage (?:an enemy|a) unit by (?<amount>\d+)\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SimpleBoost = new(
        @"^(?:Deploy(?: \((?:Melee|Ranged)\))?: )?Boost (?:an allied|a) unit by (?<amount>\d+)\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] BoardDependencies =
    [
        "adjacent", "allied", "enemy", "row", "highest", "lowest", "random", "target",
        "armor", "armour", "status", "boosted", "damaged", "power", "graveyard", "deck",
        "hand", "coin", "hoard", "fee", "deathwish", "thrive", "assimilate", "harmony",
        "bloodthirst", "barricade", "bonded", "devotion", "counter", "timer", "cooldown",
        "order", "zeal", "spawn", "summon", "play", "create", "destroy", "consume",
        "heal", "reset", "set", "vitality", "bleeding", "weather", "infuse", "replay",
    ];

    public CardPointEstimate Estimate(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var text = Normalize(card.AbilityText);
        var printedBody = card.Kind == CardKind.Unit ? card.Power : 0;
        if (text.Length == 0)
        {
            if (card.Kind != CardKind.Unit || card.AbilityText is null)
            {
                return new CardPointEstimate(
                    card.Id,
                    card.Name,
                    printedBody,
                    null,
                    null,
                    PointEstimateStatus.Unsupported,
                    ["missing ability text"],
                    "Ability text is missing or this is a non-unit; printed power is not a verified point maximum.");
            }

            return new CardPointEstimate(
                card.Id,
                card.Name,
                printedBody,
                printedBody,
                printedBody,
                PointEstimateStatus.ExactImmediate,
                Array.Empty<string>(),
                "The supplied ability text is explicitly empty; intrinsic value is the printed body, excluding board triggers.");
        }

        if (!text.Contains("Disloyal", StringComparison.OrdinalIgnoreCase))
        {
            var damage = SimpleDamage.Match(text);
            if (damage.Success)
            {
                var amount = int.Parse(damage.Groups["amount"].Value);
                return Bounded(card, printedBody, amount, "A sufficiently large valid target realizes the maximum damage.");
            }

            var boost = SimpleBoost.Match(text);
            if (boost.Success)
            {
                var amount = int.Parse(boost.Groups["amount"].Value);
                return Bounded(card, printedBody, amount, "A valid allied target realizes the maximum boost.");
            }
        }

        var dependencies = BoardDependencies
            .Where(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return new CardPointEstimate(
            card.Id,
            card.Name,
            printedBody,
            null,
            null,
            dependencies.Length > 0 ? PointEstimateStatus.NeedsBoardContext : PointEstimateStatus.Unsupported,
            dependencies.Length > 0 ? dependencies : ["unmodeled ability"],
            "This ability is not fully modeled. Neither a lower bound nor an upper value is inferred from printed power; board and chained effects remain unresolved.");
    }

    public IReadOnlyList<CardPointEstimate> BuildCatalog(IEnumerable<CardDefinition> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => Estimate(group.First()))
            .OrderBy(item => item.CardName)
            .ToArray();
    }

    private static CardPointEstimate Bounded(
        CardDefinition card,
        int printedBody,
        int effectMaximum,
        string explanation) =>
        new(
            card.Id,
            card.Name,
            printedBody,
            printedBody,
            printedBody + effectMaximum,
            PointEstimateStatus.BoundedImmediate,
            ["valid target", "target power/space"],
            explanation + " This is intrinsic value only, excluding board-triggered and chained effects.");

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text.Trim(), @"\s+", " ");
}
