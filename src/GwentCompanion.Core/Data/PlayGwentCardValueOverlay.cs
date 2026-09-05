using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GwentCompanion.Core.Data;

/// <summary>Applies bounded current numeric values from CDPR's public deck builder to gwent.one's richer catalogue.</summary>
public static class PlayGwentCardValueOverlay
{
    public const string CardsUrl = "https://www.playgwent.com/en/decks/builder/api/search";
    public const string LeadersUrl = CardsUrl + "?type=1";
    public const string SourceName = "PlayGWENT values + gwent.one metadata";
    private const int MinimumOfficialCards = 1200;
    private const int MaximumBalanceChanges = 100;

    public static async Task<CardDataSnapshot> ApplyAsync(HttpClient client, CardDataSnapshot baseline, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var cards = ReadAsync(client, CardsUrl, cancellationToken);
        var leaders = ReadAsync(client, LeadersUrl, cancellationToken);
        await Task.WhenAll(cards, leaders).ConfigureAwait(false);
        return Apply(baseline, cards.Result, leaders.Result, at);
    }

    public static CardDataSnapshot Apply(CardDataSnapshot baseline, string cardsJson, string leadersJson, DateTimeOffset at)
    {
        var official = Parse(cardsJson).Concat(Parse(leadersJson)).GroupBy(value => value.Id).ToDictionary(group => group.Key, group =>
        {
            var values = group.Distinct().ToArray();
            if (values.Length != 1) throw new InvalidDataException("PlayGWENT returned conflicting values for card " + group.Key + ".");
            return values[0];
        });
        if (official.Count < MinimumOfficialCards) throw new InvalidDataException("PlayGWENT returned an incomplete card-value catalogue.");

        var changes = baseline.Cards.Where(card => official.ContainsKey(card.Id)).Select(card => (Card: card, Value: official[card.Id]))
            .Where(item => item.Card.Power != item.Value.Power || item.Card.Provision != item.Value.Provision ||
                (item.Card.PrintedArmor ?? 0) != item.Value.Armor).ToArray();
        if (changes.Length == 0) return baseline;
        if (changes.Length > MaximumBalanceChanges || changes.Any(item => Math.Abs(item.Card.Power - item.Value.Power) > 1 ||
                Math.Abs(item.Card.Provision - item.Value.Provision) > 1 || Math.Abs((item.Card.PrintedArmor ?? 0) - item.Value.Armor) > 1))
            throw new InvalidDataException("PlayGWENT values differ too widely from gwent.one for a safe Balance Council overlay.");

        var root = JsonNode.Parse(baseline.Json)?.AsObject() ?? throw new InvalidDataException("Cannot overlay an invalid gwent.one catalogue.");
        root["gwentVisionSource"] = SourceName;
        var requested = root["request"]?["REQUEST"]?.AsObject() ?? throw new InvalidDataException("Catalogue request metadata is missing.");
        requested["version"] = OverlayVersion(baseline.Version, at);
        var byId = changes.ToDictionary(item => item.Card.Id);
        foreach (var property in root["response"]?.AsObject() ?? throw new InvalidDataException("Catalogue rows are missing."))
        {
            var row = property.Value?.AsObject();
            var id = row?["id"]?["card"]?.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id is null || !byId.TryGetValue(id, out var change)) continue;
            var attributes = row!["attributes"]?.AsObject() ?? throw new InvalidDataException("Catalogue attributes are missing for card " + id + ".");
            if (change.Card.Power != change.Value.Power) attributes["power"] = change.Value.Power;
            if (change.Card.Provision != change.Value.Provision) attributes["provision"] = change.Value.Provision;
            if ((change.Card.PrintedArmor ?? 0) != change.Value.Armor) attributes["armor"] = change.Value.Armor;
        }
        var overlaid = CardDataSnapshot.Parse(root.ToJsonString());
        if (overlaid.Cards.Count != baseline.Cards.Count) throw new InvalidDataException("The official value overlay changed catalogue membership.");
        return overlaid;
    }

    private static string OverlayVersion(string baseline, DateTimeOffset at)
    {
        var utc = at.UtcDateTime; var month = new DateTime(utc.Year, utc.Month, 1);
        if (utc.Day == DateTime.DaysInMonth(utc.Year, utc.Month)) month = month.AddMonths(1);
        var expected = new Version(month.Year - 2012, month.Month, 0); var current = Version.Parse(baseline);
        return (expected > current ? expected : current).ToString(3);
    }

    private static IEnumerable<OfficialValue> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("PlayGWENT returned invalid card values.");
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("_source", out var source) || !source.TryGetProperty("id", out var idElement) ||
                !source.TryGetProperty("power", out var powerElement) || !source.TryGetProperty("provisions_cost", out var provisionElement) ||
                !source.TryGetProperty("armour", out var armorElement)) continue;
            var id = idElement.GetInt32(); var power = powerElement.GetInt32(); var provision = provisionElement.GetInt32(); var armor = armorElement.GetInt32();
            if (id <= 0 || power is < 0 or > 1000 || provision is < 0 or > 100 || armor is < 0 or > 1000)
                throw new InvalidDataException("PlayGWENT returned an invalid numeric card value.");
            yield return new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), power, provision, armor);
        }
    }

    private static async Task<string> ReadAsync(HttpClient client, string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-GOG-Cache-Control", "client.ttl=0, varnish.ttl=0, akamai.ttl=0, varnish.grace=0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > CardDataUpdater.MaxBytes) throw new InvalidDataException("PlayGWENT returned oversized card values.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > CardDataUpdater.MaxBytes) throw new InvalidDataException("PlayGWENT card values exceed the safety limit.");
            buffer.Write(block, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private sealed record OfficialValue(string Id, int Power, int Provision, int Armor);
}
