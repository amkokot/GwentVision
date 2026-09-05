using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record PlayGwentCreateRequest(string Name, int[] CardTemplateIds);
public sealed record PlayGwentReply(int Status, string Body, bool Redirected = false, string? Error = null);
public enum PlayGwentAuthentication { Unknown, SignedOut, SignedIn }

/// <summary>Small adapter for the official builder's current wire format; no credentials or automatic crafting.</summary>
public static class PlayGwentExport
{
    public const string Origin = "https://www.playgwent.com";
    public const string LibraryUrl = Origin + "/en/decks/builder";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool ValidHash(string hash) => Regex.IsMatch(hash, "\\A[a-fA-F0-9]{16,64}\\z", RegexOptions.CultureInvariant);
    public static bool ValidId(string id) => Regex.IsMatch(id, "\\A[a-zA-Z0-9-]{1,80}\\z", RegexOptions.CultureInvariant);
    public static bool IsWebsite(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "www.playgwent.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    public static bool AllowedNavigation(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "www.playgwent.com" or "playgwent.com" or "login.gog.com" or "auth.gog.com" or "www.gog.com" or "gog.com";

    public static PlayGwentCreateRequest CreateRequest(DeckDefinition deck, string websiteName, IEnumerable<CardDefinition> catalog)
    {
        if (string.IsNullOrWhiteSpace(websiteName) || websiteName.Trim().Length > 50)
            throw new InvalidDataException("Use a website deck name of 1–50 characters.");
        var definitions = catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var leader = GwentOneCardCatalog.StartingLeaders(definitions.Values).SingleOrDefault(c => c.Faction == deck.Faction && c.Name == deck.Leader)
            ?? throw new InvalidDataException("The starting leader could not be resolved to a current card ID.");
        var current = deck with { LeaderProvisionBonus = leader.Provision,
            Cards = deck.Cards.Select(c => new DeckCard(definitions.GetValueOrDefault(c.Card.Id)
                ?? throw new InvalidDataException($"Unknown card: {c.Card.Name}."), c.Count)).ToArray(),
            Stratagem = deck.Stratagem is null ? throw new InvalidDataException("Choose a stratagem before exporting.") :
                definitions.GetValueOrDefault(deck.Stratagem.Id) ?? throw new InvalidDataException("Unknown stratagem.") };
        var errors = DeckBuildValidation.Errors(current);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        static int Id(CardDefinition card) => int.TryParse(card.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : throw new InvalidDataException("Invalid card-template ID: " + card.Name);
        return new(websiteName.Trim(), new[] { Id(leader), Id(current.Stratagem!) }.Concat(current.Cards
            .SelectMany(c => Enumerable.Repeat(Id(c.Card), c.Count))).ToArray());
    }

    public static string GuideBody(string name, DeckDetails details)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 50) throw new InvalidDataException("Guide title must be 1–50 characters.");
        if (!details.HasContent) throw new InvalidDataException("Enter some optional details to create a guide draft.");
        var content = details.ToGuideHtml();
        if (content.Length > 20000) throw new InvalidDataException("Shorten the guide text (20,000 formatted characters maximum).");
        return JsonSerializer.Serialize(new { locale = "en", name = name.Trim(), content, publish = false }, Json);
    }

    public static DeckExportReceipt Created(PlayGwentReply reply, DateTimeOffset at)
    {
        if (reply.Redirected || reply.Status is < 200 or >= 300 || reply.Error is not null)
            throw new InvalidDataException("PlayGWENT did not confirm creation. Sign in or check the website response.");
        using var parsed = JsonDocument.Parse(reply.Body);
        var root = parsed.RootElement;
        var id = root.TryGetProperty("id", out var identity) ? identity.ToString() : "";
        var hash = root.TryGetProperty("deckHash", out var hashed) ? hashed.GetString() ?? "" : "";
        if (!ValidId(id) || !ValidHash(hash)) throw new InvalidDataException("Unexpected creation response. Check your website library before retrying; the deck may already exist.");
        return new(id, hash, at);
    }

    public static string CreatePath => "/en/decks/builder/api/create";
    public static string LoginCheckPath => "/en/decks/builder/api/login-check";
    public static string ReadDeckPath(string id) => ValidId(id) ? $"/en/decks/builder/api/{id}/" : throw new ArgumentException("Invalid website deck ID.");
    public static string GuidePath(string id) => ValidId(id) ? $"/en/decks/builder/api/{id}/create-guide" : throw new ArgumentException("Invalid website deck ID.");
    public static string ImportPath(string hash) => ValidHash(hash) ? $"/en/decks/builder/api/{hash}/import" : throw new ArgumentException("Invalid deck hash.");

    public static PlayGwentAuthentication Authentication(PlayGwentReply reply)
    {
        if (reply.Redirected || reply.Status is 401 or 403) return PlayGwentAuthentication.SignedOut;
        if (reply.Error is not null || reply.Status != 200) return PlayGwentAuthentication.Unknown;
        try
        {
            using var json = JsonDocument.Parse(reply.Body);
            var root = json.RootElement;
            // The official builder's LOGIN_CHECK reducer tests userLogged.result === "OK".
            // Do not treat arbitrary objects, strings or other truthy JSON as authenticated.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String && result.GetString() == "OK")
                return PlayGwentAuthentication.SignedIn;
            return root.ValueKind switch { JsonValueKind.True => PlayGwentAuthentication.SignedIn,
                JsonValueKind.False => PlayGwentAuthentication.SignedOut, _ => PlayGwentAuthentication.Unknown };
        }
        catch (JsonException) { return PlayGwentAuthentication.Unknown; }
    }

    public static string AuthenticationFailureMessage(PlayGwentReply reply)
    {
        // Only expose status/format, never response fields, account data or exception text.
        if (reply.Status == 0) return "Sign-in check could not reach PlayGWENT. No deck was sent; check your connection and retry.";
        if (reply.Error is not null) return "Sign-in check failed while reading the response. No deck was sent; retry the export.";
        if (reply.Status != 200) return $"Sign-in check returned HTTP {reply.Status}. No deck was sent; retry the export shortly.";
        string format;
        try
        {
            using var json = JsonDocument.Parse(reply.Body);
            format = "JSON " + json.RootElement.ValueKind.ToString().ToLowerInvariant();
        }
        catch (JsonException) { format = "non-JSON response"; }
        return $"Unrecognised sign-in response (HTTP 200; {format}). No deck was sent.";
    }

    public static void VerifyDeck(PlayGwentReply reply, PlayGwentCreateRequest expected, string id)
    {
        if (reply.Redirected || reply.Status is < 200 or >= 300 || reply.Error is not null)
            throw new InvalidDataException("The website deck could not be checked. Sign in and verify it before importing.");
        using var json = JsonDocument.Parse(reply.Body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var identity) || identity.ToString() != id ||
            !root.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected deck verification response. The saved link is retained; no second deck was created.");
        var actual = new List<int>();
        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("_source", out var source) || source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var cardId))
                throw new InvalidDataException("The website returned a card without a valid template ID.");
            actual.Add(cardId);
        }
        if (!actual.Order().SequenceEqual(expected.CardTemplateIds.Order()))
            throw new InvalidDataException("The website card list does not match this variation, including leader, stratagem and duplicate copies. Game import is blocked; review the saved link.");
    }

    // JsonSerializer escapes all user-authored text. No string is interpolated into JavaScript syntax.
    // Navigation/origin is checked both here and at the native receive boundary.
    public static string RequestScript(string requestId, string path, string? body)
    {
        var read = path == LoginCheckPath || Regex.IsMatch(path, "\\A/en/decks/builder/api/[a-zA-Z0-9-]{1,80}/\\z");
        if (!read && path != CreatePath && !Regex.IsMatch(path, "\\A/en/decks/builder/api/(?:[a-zA-Z0-9-]{1,80}/create-guide|[a-fA-F0-9]{16,64}/import)\\z"))
            throw new ArgumentException("Unsupported export endpoint.");
        if (read && body is not null) throw new ArgumentException("Read-only export checks cannot send a request body.");
        var args = JsonSerializer.Serialize(new { requestId, path, body, method = read ? "GET" : "POST" }, Json);
        return """
            (() => {
                const args =
            """ + args + ";\n" + """
                if (location.origin !== 'https://www.playgwent.com') return;
                const report = reply => window.chrome.webview.postMessage({requestId: args.requestId, ...reply});
                // The official create/guide actions send a JSON string with fetch's default text/plain type.
                // Manual redirects let a read-only login check distinguish sign-in from a network failure.
                fetch(args.path, {method: args.method, credentials: 'same-origin', redirect: 'manual', cache: 'no-store',
                    headers: args.method === 'GET' ? {'X-GOG-Cache-Control': 'client.ttl=0, varnish.ttl=0, akamai.ttl=0, varnish.grace=0'} : {},
                    ...(args.body === null ? {} : {body: args.body})})
                .then(async r => report({status: r.status, body: (await r.text()).slice(0, 500000), redirected: r.redirected || r.type === 'opaqueredirect'}))
                .catch(() => report({status: 0, body: '', error: 'No confirmed response; check the website before retrying.'}));
            })();
            """;
    }
}
