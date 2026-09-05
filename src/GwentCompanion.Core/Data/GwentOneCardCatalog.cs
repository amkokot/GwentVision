using System.Text.Json;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Optional, cached public catalog. Never reads data from the game process.</summary>
public static class GwentOneCardCatalog
{
    /// <summary>Starting abilities only; neutral replacement abilities (e.g. Renfri curses) are not deck choices.</summary>
    public static CardDefinition[] StartingLeaders(IEnumerable<CardDefinition> catalog) => catalog
        .Where(card => card.Kind == CardKind.Leader && !string.IsNullOrWhiteSpace(card.Faction) &&
            !card.Faction.Equals("Neutral", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(card => card.Id).OrderBy(card => card.Faction).ThenBy(card => card.Name).ToArray();

    public static IReadOnlyList<CardDefinition> Load(string path)
    {
        if (!File.Exists(path)) return [];
        return Parse(File.ReadAllText(path));
    }

    public static IReadOnlyList<CardDefinition> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("response").EnumerateObject().Select(entry =>
        {
            var card = entry.Value;
            var attributes = card.GetProperty("attributes");
            var id = card.GetProperty("id");
            var faction = NormalizeFaction(attributes.GetProperty("faction").GetString()!);
            var secondary = NormalizeFaction(attributes.GetProperty("factionSecondary").GetString() ?? "");
            var type = attributes.GetProperty("type").GetString();
            _ = Enum.TryParse<CardKind>(type, true, out var kind);
            if (type == "Ability") kind = CardKind.Leader;
            return new CardDefinition(
                id.GetProperty("card").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                card.GetProperty("name").GetString()!, faction, kind,
                attributes.GetProperty("provision").GetInt32(), attributes.GetProperty("power").GetInt32(),
                attributes.GetProperty("color").GetString() == "Gold",
                card.GetProperty("category").GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
                new Uri($"https://gwent.one/image/gwent/assets/card/art/medium/{id.GetProperty("art").GetInt32()}.jpg"),
                string.IsNullOrWhiteSpace(secondary) ? null : new HashSet<string>([secondary], StringComparer.OrdinalIgnoreCase),
                card.GetProperty("ability").GetString(),
                CanBeInStartingDeck: attributes.GetProperty("set").GetString() != "NonOwnable" && kind is CardKind.Unit or CardKind.Special or CardKind.Artifact,
                PrintedArmor: attributes.TryGetProperty("armor", out var armor) ? armor.GetInt32() : null);
        }).ToArray();
    }

    private static string NormalizeFaction(string faction) => faction switch
    {
        "Monster" => "Monsters",
        "Scoiatael" => "Scoia'tael",
        _ => faction,
    };
}
