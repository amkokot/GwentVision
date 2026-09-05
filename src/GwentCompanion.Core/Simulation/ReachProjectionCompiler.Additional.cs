using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Simulation;

internal static partial class ReachProjectionCompiler
{
    private static ReachProjectionStep? AdditionalAtom(CardDefinition source, string text, IReadOnlyList<CardDefinition> catalog)
    {
        if (text.Equals("Heal self", StringComparison.OrdinalIgnoreCase)) return new("heal-self", int.MaxValue);
        if (text.Equals("Purify adjacent units", StringComparison.OrdinalIgnoreCase)) return new("purify-adjacent");
        if (text.Equals("Purify all other units", StringComparison.OrdinalIgnoreCase)) return new("purify-all");
        if (text.Equals("Purify all allied units on this row", StringComparison.OrdinalIgnoreCase)) return new("purify-row");
        if (text.Equals("Move self to the other row", StringComparison.OrdinalIgnoreCase)) return new("move-self");
        if (text.Equals("Summon all copies of self from your deck to this row", StringComparison.OrdinalIgnoreCase)) return new("summon-self-copies");
        if (text.Equals("Draw a card, then Discard a card", StringComparison.OrdinalIgnoreCase)) return new("draw-discard");
        if (text.Equals("Draw a card, then play a card", StringComparison.OrdinalIgnoreCase)) return new("draw-play");
        if (text.Equals("Play the top card from your opponent's deck", StringComparison.OrdinalIgnoreCase)) return new("top-play", 1, "opponent");
        if (text.Equals("Swap a unit's power with Armor", StringComparison.OrdinalIgnoreCase)) return new("swap-armor");
        if (text.Equals("Set a unit's power equal to its provision cost", StringComparison.OrdinalIgnoreCase)) return new("set-provision");
        if (text.Equals("Remove a unit's Armor and boost self by that amount", StringComparison.OrdinalIgnoreCase)) return new("take-armor");
        if (text.Equals("Boost Olaf by twice the amount he is damaged", StringComparison.OrdinalIgnoreCase)) return new("damage-boost-self", 2);
        if (text.Equals("Destroy the unit with the highest base power", StringComparison.OrdinalIgnoreCase)) return new("highest-base-destroy");
        if (text.Equals("Spawn 1-power copies of 3 enemy bronze units on their opposite row", StringComparison.OrdinalIgnoreCase)) return new("mirror-copies", 3);
        var transform = Match(text, @"^Transform (?:self )?into (?:a )?(?<name>.+)$");
        if (transform.Success && catalog.FirstOrDefault(card => card.Name.Equals(transform.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)) is { } form)
            return new("transform-self", Argument: form.Name);
        var healBoost = Match(text, @"^Heal an allied unit by (?<heal>\d+) and boost it by (?<boost>\d+)$");
        if (healBoost.Success) return new("heal-boost", int.Parse(healBoost.Groups["heal"].Value), healBoost.Groups["boost"].Value);
        var ends = Match(text, @"^Damage (?:the )?units (?:at both ends|on each end) of an enemy row by (?<amount>\d+)$");
        if (ends.Success) return new("row-ends", int.Parse(ends.Groups["amount"].Value));
        var count = Match(text, @"^Boost self by (?<amount>\d+) for each (?<category>[A-Za-z]+) (?<zone>you control|in your hand)$");
        if (count.Success && catalog.Any(card => card.HasCategory(count.Groups["category"].Value)))
            return new("category-count", int.Parse(count.Groups["amount"].Value), count.Groups["category"].Value + ";" + count.Groups["zone"].Value);
        var boostedCount = Match(text, @"^Boost self by (?<amount>\d+) for each boosted (?<where>enemy unit|unit on this row)$");
        if (boostedCount.Success) return new("boosted-count", int.Parse(boostedCount.Groups["amount"].Value), boostedCount.Groups["where"].Value);
        var categoryAll = Match(text, @"^(?<action>Boost|Damage|Heal) all (?<side>allied|enemy) (?<category>[A-Za-z]+) by (?<amount>\d+)$");
        if (categoryAll.Success && catalog.SelectMany(card => card.Categories).FirstOrDefault(category =>
                (category + "s").Equals(categoryAll.Groups["category"].Value, StringComparison.OrdinalIgnoreCase)) is { } category)
            return new("all-" + categoryAll.Groups["action"].Value.ToLowerInvariant(), int.Parse(categoryAll.Groups["amount"].Value),
                categoryAll.Groups["side"].Value + ";" + category + ";");
        return null;
    }
}
