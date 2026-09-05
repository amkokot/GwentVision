using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed class PlayGwentDeckPageParser
{
    private static readonly Regex StateAttribute = new(
        "data-state=(?:'(?<single>[^']+)'|\"(?<double>[^\"]+)\")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public DeckDefinition Parse(
        string html,
        Uri sourceUri,
        string? displayName = null,
        int recencyRank = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        ArgumentNullException.ThrowIfNull(sourceUri);

        return ParseStateJson(ExtractStateJson(html), sourceUri, displayName, recencyRank);
    }

    public string ExtractStateJson(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        var stateMatch = StateAttribute.Match(html);
        if (!stateMatch.Success)
        {
            throw new InvalidDataException("The PlayGWENT page does not contain a public deck payload. The link may have expired.");
        }

        var encoded = stateMatch.Groups["single"].Success
            ? stateMatch.Groups["single"].Value
            : stateMatch.Groups["double"].Value;
        return WebUtility.HtmlDecode(encoded);
    }

    public DeckDefinition ParseStateJson(
        string json,
        Uri sourceUri,
        string? displayName = null,
        int recencyRank = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(sourceUri);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement guide = default;
        var isGuide = sourceUri.AbsolutePath.Contains("/decks/guides/", StringComparison.OrdinalIgnoreCase);
        if (isGuide)
        {
            if (!root.TryGetProperty("guide", out guide) || guide.ValueKind != JsonValueKind.Object ||
                !guide.TryGetProperty("id", out var guideId) || guideId.ToString() != sourceUri.Segments[^1].Trim('/'))
                throw new InvalidDataException("The public guide does not match the requested guide ID.");
            root = guide;
        }
        if (!root.TryGetProperty("deck", out var deck) || deck.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The PlayGWENT payload does not contain a deck object.");
        }

        var id = RequiredString(deck, "hash");
        var faction = FriendlyFaction(ReadNestedString(deck, "faction", "slug"));
        var leader = deck.GetProperty("leader");
        var leaderName = FirstString(leader, "localizedName", "name");
        var leaderBonus = OptionalInt(leader, "provisionsCost");
        var cards = ParseCards(deck.GetProperty("cards"), sourceUri);
        // A balance update rewrites the nested deck's modified date. The guide's
        // own date describes the authored list and must win for recency weighting.
        var modified = isGuide ? OptionalDateTimeOffset(guide, "modified") ?? OptionalDateTimeOffset(deck, "modified") : OptionalDateTimeOffset(deck, "modified");
        var name = string.IsNullOrWhiteSpace(displayName)
            ? $"{faction} · {leaderName}"
            : displayName.Trim();

        return new DeckDefinition(
            id,
            name,
            faction,
            leaderName,
            leaderBonus,
            cards,
            sourceUri,
            modified,
            recencyRank,
            ParseStratagem(deck, sourceUri),
            SourceUpdatedAt: modified);
    }

    private static CardDefinition? ParseStratagem(JsonElement deck, Uri sourceUri)
    {
        if (!deck.TryGetProperty("stratagem", out var card) || card.ValueKind != JsonValueKind.Object) return null;
        return new CardDefinition(RequiredInt(card, "id").ToString(System.Globalization.CultureInfo.InvariantCulture),
            FirstString(card, "localizedName", "name"), FriendlyFaction(ReadNestedString(card, "faction", "slug")),
            CardKind.Stratagem, 0, IsGold: true, ArtUri: ReadArtUri(card, sourceUri), AbilityText: ReadAbilityText(card), CanBeInStartingDeck: false);
    }

    private static IReadOnlyList<DeckCard> ParseCards(JsonElement cards, Uri sourceUri)
    {
        var result = new List<DeckCard>();
        foreach (var card in cards.EnumerateArray())
        {
            var id = RequiredInt(card, "id").ToString(System.Globalization.CultureInfo.InvariantCulture);
            var faction = FriendlyFaction(ReadNestedString(card, "faction", "slug"));
            var categories = OptionalString(card, "categoryName")?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definition = new CardDefinition(
                id,
                FirstString(card, "localizedName", "name"),
                faction,
                ParseKind(OptionalString(card, "type")),
                OptionalInt(card, "provisionsCost"),
                OptionalInt(card, "power"),
                string.Equals(OptionalString(card, "cardGroup"), "gold", StringComparison.OrdinalIgnoreCase),
                categories,
                ReadArtUri(card, sourceUri),
                ReadSecondaryFactions(card),
                ReadAbilityText(card));
            result.Add(new DeckCard(definition, OptionalInt(card, "repeatCount") + 1));
        }

        return result;
    }

    private static string? ReadAbilityText(JsonElement card)
    {
        if (!card.TryGetProperty("tooltip", out var tooltip) || tooltip.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var line in tooltip.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var token in line.EnumerateArray())
            {
                var value = OptionalString(token, "value");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }

            parts.Add(" ");
        }

        var text = Regex.Replace(string.Concat(parts), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlySet<string> ReadSecondaryFactions(JsonElement card)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!card.TryGetProperty("secondaryFactions", out var factions) ||
            factions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var faction in factions.EnumerateArray())
        {
            if (!faction.TryGetProperty("value", out var value) || !value.TryGetInt32(out var number))
            {
                continue;
            }

            var name = number switch
            {
                1 => "Monsters",
                2 => "Nilfgaard",
                3 => "Northern Realms",
                4 => "Scoia'tael",
                5 => "Skellige",
                6 => "Syndicate",
                _ => null,
            };
            if (name is not null)
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static Uri? ReadArtUri(JsonElement card, Uri sourceUri)
    {
        if (!card.TryGetProperty("previewImg", out var images) ||
            images.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(OptionalString(images, "small")) ||
            !Uri.TryCreate(sourceUri, OptionalString(images, "small"), out var artUri))
        {
            return null;
        }

        return artUri;
    }

    private static CardKind ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "unit" => CardKind.Unit,
        "special" => CardKind.Special,
        "artifact" => CardKind.Artifact,
        "stratagem" => CardKind.Stratagem,
        "leader" => CardKind.Leader,
        _ => CardKind.Unknown,
    };

    private static string FriendlyFaction(string value) => value.ToLowerInvariant() switch
    {
        "northernrealms" => "Northern Realms",
        "nilfgaard" => "Nilfgaard",
        "scoiatael" => "Scoia'tael",
        "monsters" => "Monsters",
        "skellige" => "Skellige",
        "syndicate" => "Syndicate",
        "neutral" => "Neutral",
        _ => value,
    };

    private static string ReadNestedString(JsonElement parent, string objectName, string propertyName) =>
        RequiredString(parent.GetProperty(objectName), propertyName);

    private static string FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = OptionalString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidDataException($"None of the required fields were present: {string.Join(", ", names)}.");
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidDataException($"The deck payload is missing '{name}'.");

    private static string? OptionalString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
        {
            return number;
        }

        throw new InvalidDataException($"The deck payload is missing numeric field '{name}'.");
    }

    private static int OptionalInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    }

    private static DateTimeOffset? OptionalDateTimeOffset(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }
}
