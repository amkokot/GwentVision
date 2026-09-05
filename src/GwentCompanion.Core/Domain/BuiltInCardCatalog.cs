namespace GwentCompanion.Core.Domain;

public static class BuiltInCardCatalog
{
    public static IReadOnlyList<CardDefinition> Cards { get; } =
    [
        new(
            "203088",
            "Renfri",
            "Neutral",
            CardKind.Unit,
            16,
            7,
            true,
            new HashSet<string>(["Human", "Cursed", "Bandit"], StringComparer.OrdinalIgnoreCase),
            new Uri("https://www.playgwent.com/uploads/media/assets_preview/0001/43/thumb_42775_assets_preview_small_614922c765b965718b48b9cec8d868b297daad95.jpg"),
            AbilityText: "Deploy: If your starting deck has at least 25 units, Create a curse to replace your leader ability, then Create a blessing and Infuse your leader ability with it."),
    ];

    public static IReadOnlyList<CardDefinition> Merge(IEnumerable<CardDefinition> cards) =>
        cards.Concat(Cards)
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
}
