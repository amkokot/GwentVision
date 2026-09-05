using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class DeckBuilderExportTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException) { return; } throw new InvalidOperationException(message); }

    public static void Run(string root)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
        var library = DeckLibrary.Load(Path.Combine(project, "cache/deck-library.json"));
        CardDefinition Card(string name) => catalog.Single(c => c.Name == name);
        var sigvald = Card("Sigvald"); var nekker = Card("Golden Nekker"); var core = Card("Knut the Callous");
        var sigvaldExample = DeckAutoFill.Build(library.Decks, catalog, [new(sigvald), new(nekker)], new HashSet<DeckCopyKey>(), "Skellige", null, null, true);
        Check(sigvaldExample.Cards.Any(c => c.Card.Id == sigvald.Id) && (sigvald.Provision < 10 || sigvaldExample.Errors.Any(e => e.Contains("Sigvald"))),
            "The requested Sigvald example must preserve the selection and flag current balance incompatibility.");
        var seeds = new[] { new DeckCard(core), new DeckCard(nekker) };
        var timer = Stopwatch.StartNew();
        var fill = DeckAutoFill.Build(library.Decks, catalog, seeds, new HashSet<DeckCopyKey>(), "Skellige", null, null, true);
        Check(fill.Leader is { Faction: "Skellige" } && fill.Cards.Sum(c => c.Count) >= 25,
            $"Knut + Golden Nekker did not complete a deck: {fill.Cards.Sum(c => c.Count)} cards; {fill.Reason} {string.Join(" ", fill.Errors)}");
        Check(fill.Cards.Any(c => c.Card.Id == core.Id) && fill.Cards.Any(c => c.Card.Id == nekker.Id), "Fixed cards were dropped.");
        Check(fill.Errors.Count == 0, "Auto-filled deck is invalid: " + string.Join(" ", fill.Errors));
        Check(fill.Cards.All(c => c.Card.Provision < 10 || c.Card.Name is "Golden Nekker" or "Ciri: Nova"), "Golden Nekker auto-fill restriction violated.");
        Console.WriteLine($"Knut/Nekker: {fill.Cards.Sum(c => c.Count)} cards, {fill.Cards.Sum(c => c.Count * c.Card.Provision)}p, {fill.Leader!.Name}, {timer.ElapsedMilliseconds}ms.");
        var off = DeckAutoFill.Build(library.Decks, catalog, seeds, new HashSet<DeckCopyKey>(), "Skellige", fill.Leader, fill.Stratagem, false);
        Check(off.Cards.Sum(c => c.Count) == 2, "Auto-fill off retained generated guesses.");
        var again = DeckAutoFill.Build(library.Decks, catalog, seeds, new HashSet<DeckCopyKey>(), "Skellige", fill.Leader, fill.Stratagem, true);
        Check(again.Cards.Any(c => c.Card.Id == core.Id) && seeds.Length == 2, "Suggestions contaminated the fixed inputs.");
        var bronzePair = fill.Cards.First(c => !c.Card.IsGold && c.Count == 2);
        var excluded = new HashSet<DeckCopyKey> { new(bronzePair.Card.Id, 2) };
        var oneFixed = seeds.Append(bronzePair with { Count = 1 }).ToArray();
        var withoutSecond = DeckAutoFill.Build(library.Decks, catalog, oneFixed, excluded, "Skellige", fill.Leader, fill.Stratagem, true);
        Check(withoutSecond.Cards.Single(c => c.Card.Id == bronzePair.Card.Id).Count == 1, "Excluding copy 2 dropped fixed copy 1 or re-added copy 2.");
        Check(withoutSecond.Cards.Sum(c => c.Count) >= 25 && withoutSecond.Errors.Count == 0,
            "Alternative completion after excluding one bronze copy was not legal: " + string.Join(" ", withoutSecond.Errors));
        var high = catalog.First(c => c.Faction == "Skellige" && c.Kind == CardKind.Unit && c.CanBeInStartingDeck && c.Provision >= 10 && c.Name is not ("Golden Nekker" or "Ciri: Nova"));
        var conflict = DeckAutoFill.Build(library.Decks, catalog, seeds.Append(new DeckCard(high)).ToArray(), new HashSet<DeckCopyKey>(), "Skellige", fill.Leader, fill.Stratagem, true);
        Check(conflict.Errors.Count > 0 && conflict.Cards.Any(c => c.Card.Id == high.Id), "Conflicting fixed card was silently discarded.");
        foreach (var faction in GwentOneCardCatalog.StartingLeaders(catalog).Select(c => c.Faction).Distinct())
        {
            var seed = catalog.First(c => c.Faction == faction && c.Kind == CardKind.Unit && c.Provision == 4 && c.CanBeInStartingDeck);
            var factionFill = DeckAutoFill.Build(library.Decks, catalog, [new(seed)], new HashSet<DeckCopyKey>(), faction, null, null, true);
            Check(factionFill.Cards.Any(c => c.Card.Id == seed.Id) && factionFill.Errors.Count == 0 && factionFill.Cards.Sum(c => c.Count) >= 25,
                faction + " completion failed: " + string.Join(" ", factionFill.Errors));
        }

        var stratagem = fill.Stratagem ?? Card("Tactical Advantage");
        var deck = new DeckDefinition("export-test", "Sigvald test", "Skellige", fill.Leader.Name, fill.Leader.Provision, fill.Cards, Stratagem: stratagem);
        Check(DeckSearchCatalog.ContainsCards(deck, "2x " + bronzePair.Card.Name), "Copy-aware include query failed.");
        Check(!DeckSearchCatalog.ContainsCards(deck, "3x " + bronzePair.Card.Name), "Copy-aware include matched too few copies.");
        Check(!DeckSearchCatalog.ExcludesCards(deck, "Missing identity, " + bronzePair.Card.Name), "Excluded-card query must reject any listed identity.");
        Check(DeckSearchCatalog.ExcludesCards(deck, "3x " + bronzePair.Card.Name), "Excluded copy count rejected a smaller pair.");
        Check(!DeckSearchCatalog.ExcludesCards(null, "Renfri") && !DeckSearchCatalog.ExcludesCards(deck with { Cards = seeds }, "Renfri"), "Unknown/incomplete lists cannot establish card absence.");
        var request = PlayGwentExport.CreateRequest(deck, deck.Name, catalog);
        Check(request.CardTemplateIds.Length == deck.CardCount + 2 && request.CardTemplateIds[0] == int.Parse(fill.Leader.Id) &&
            request.CardTemplateIds[1] == int.Parse(stratagem.Id), "Leader / stratagem separated incorrectly from the actual copies.");
        Check(request.CardTemplateIds.Count(id => id == int.Parse(bronzePair.Card.Id)) == 2, "Export dropped duplicate bronze copies.");
        Reject(() => PlayGwentExport.CreateRequest(deck with { Stratagem = null }, deck.Name, catalog), "Missing stratagem exported.");
        Reject(() => PlayGwentExport.CreateRequest(deck with { Cards = seeds }, deck.Name, catalog), "Incomplete deck exported.");
        Reject(() => PlayGwentExport.CreateRequest(deck, new string('x', 51), catalog), "Overlength website name exported.");
        var badCard = deck.Cards[0].Card with { Id = "not-in-catalog" };
        Reject(() => PlayGwentExport.CreateRequest(deck with { Cards = deck.Cards.Append(new(badCard)).ToArray() }, deck.Name, catalog), "Unknown card ID exported.");
        var details = new DeckDetails("<script>alert('x')</script>", "Line one\nLine two", "Keep Sigvald", "Control", "My guide");
        var html = details.ToGuideHtml();
        Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("<br>"), "Guide text can inject HTML or loses line breaks.");
        using var guide = JsonDocument.Parse(PlayGwentExport.GuideBody(deck.Name, details));
        Check(!guide.RootElement.GetProperty("publish").GetBoolean() && guide.RootElement.GetProperty("content").GetString() == html, "Guide was not a private draft with structured content.");
        var script = PlayGwentExport.RequestScript("fixture", PlayGwentExport.CreatePath, JsonSerializer.Serialize(request, PlayGwentExport.Json));
        Check(script.Contains("location.origin !== 'https://www.playgwent.com'") && script.Contains("credentials: 'same-origin'") &&
            script.Contains("redirect: 'manual'") && script.Contains("opaqueredirect") && !script.Contains("expected_cost"), "Export origin / spending guard missing.");
        Check(PlayGwentExport.Authentication(new(200, "{\"result\":\"OK\"}")) == PlayGwentAuthentication.SignedIn,
            "The official login-check response {result: OK} was rejected; a signed-in export would stop before sending cards.");
        Check(PlayGwentExport.Authentication(new(200, "true")) == PlayGwentAuthentication.SignedIn &&
            PlayGwentExport.Authentication(new(200, "false")) == PlayGwentAuthentication.SignedOut &&
            PlayGwentExport.Authentication(new(0, "", true)) == PlayGwentAuthentication.SignedOut &&
            PlayGwentExport.Authentication(new(403, "")) == PlayGwentAuthentication.SignedOut &&
            PlayGwentExport.Authentication(new(0, "", Error: "offline")) == PlayGwentAuthentication.Unknown &&
            PlayGwentExport.Authentication(new(200, "<html>login</html>")) == PlayGwentAuthentication.Unknown, "Login preflight confused offline/login/success.");
        foreach (var body in new[] { "{}", "[]", "null", "1", "\"OK\"", "{\"result\":true}", "{\"result\":null}", "{\"result\":\"ERROR\"}", "{\"result\":\"ok\"}", "{\"userLogged\":true}" })
            Check(PlayGwentExport.Authentication(new(200, body)) == PlayGwentAuthentication.Unknown, "Unexpected sign-in JSON allowed an export: " + body);
        Check(PlayGwentExport.Authentication(new(503, "{\"result\":\"OK\"}")) == PlayGwentAuthentication.Unknown &&
            PlayGwentExport.Authentication(new(200, "{\"result\":\"OK\"}", Error: "offline")) == PlayGwentAuthentication.Unknown,
            "A failed response bypassed the sign-in check.");
        var loginScript = PlayGwentExport.RequestScript("login", PlayGwentExport.LoginCheckPath, null);
        Check(loginScript.Contains("\"method\":\"GET\"") && loginScript.Contains("'X-GOG-Cache-Control': 'client.ttl=0, varnish.ttl=0, akamai.ttl=0, varnish.grace=0'"),
            "Authentication check sends a write or omits the website's cache-bypass header.");
        Check(PlayGwentExport.AuthenticationFailureMessage(new(503, "private-response-fixture")).Contains("HTTP 503") &&
            PlayGwentExport.AuthenticationFailureMessage(new(200, "{\"account\":\"private-response-fixture\"}")).Contains("JSON object") &&
            PlayGwentExport.AuthenticationFailureMessage(new(200, "<html>private-response-fixture</html>")).Contains("non-JSON"),
            "Sign-in failure hides the response category.");
        foreach (var reply in new[] { new PlayGwentReply(200, "{\"account\":\"private-response-fixture\"}"), new(200, "<html>private-response-fixture</html>"),
            new(503, "private-response-fixture"), new(0, "", Error: "private-response-fixture") })
            Check(!PlayGwentExport.AuthenticationFailureMessage(reply).Contains("private-response-fixture"), "Sign-in diagnostics expose response content.");
        Reject(() => PlayGwentExport.RequestScript("login", PlayGwentExport.LoginCheckPath, "{}"), "Read-only check accepts a body.");
        string WebsiteDeck(IEnumerable<int> ids, string id = "123") => JsonSerializer.Serialize(new { id, cards = ids.Select(cardId => new { _source = new { id = cardId } }) });
        PlayGwentExport.VerifyDeck(new(200, WebsiteDeck(request.CardTemplateIds.Reverse())), request, "123");
        Reject(() => PlayGwentExport.VerifyDeck(new(200, WebsiteDeck(request.CardTemplateIds.Distinct())), request, "123"), "Lost bronze copy passed verification.");
        Reject(() => PlayGwentExport.VerifyDeck(new(200, WebsiteDeck(request.CardTemplateIds.Skip(1))), request, "123"), "Missing leader passed verification.");
        Reject(() => PlayGwentExport.VerifyDeck(new(200, WebsiteDeck(request.CardTemplateIds.Skip(2))), request, "123"), "Missing stratagem passed verification.");
        Reject(() => PlayGwentExport.VerifyDeck(new(200, WebsiteDeck(request.CardTemplateIds, "other")), request, "123"), "Wrong website deck passed verification.");
        Reject(() => PlayGwentExport.VerifyDeck(new(200, "{\"id\":123,\"cards\":[null]}"), request, "123"), "Malformed website card accepted.");
        Reject(() => PlayGwentExport.VerifyDeck(new(403, "{}"), request, "123"), "Unauthenticated verification passed.");
        Reject(() => PlayGwentExport.RequestScript("id", "https://evil.example/steal", "{}"), "Arbitrary export endpoint permitted.");
        Check(!PlayGwentExport.IsWebsite("https://www.playgwent.com.evil.example/") && !PlayGwentExport.IsWebsite("http://www.playgwent.com/") &&
            !PlayGwentExport.AllowedNavigation("file:///C:/secrets") && PlayGwentExport.AllowedNavigation("https://login.gog.com/auth"), "Browser origin allowlist broken.");
        var receipt = PlayGwentExport.Created(new(200, "{\"id\":123,\"deckHash\":\"0123456789abcdef0123456789abcdef\"}"), DateTimeOffset.UnixEpoch);
        Reject(() => PlayGwentExport.Created(new(200, "{\"id\":\"../other\",\"deckHash\":\"x\"}"), DateTimeOffset.Now), "Malformed server receipt accepted.");
        Reject(() => PlayGwentExport.Created(new(403, "{}"), DateTimeOffset.Now), "Unauthenticated creation treated as success.");
        Reject(() => PlayGwentExport.Created(new(200, "<html>login</html>"), DateTimeOffset.Now), "Login HTML treated as a created deck.");

        var saved = new DeckLibrary(); saved.Merge([deck]); saved.SetDetails(deck.Id, details); saved.SetExport(deck.Id, receipt);
        var folder = Path.Combine(project, "diagnostics", "deck-builder-export-tests"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "deck-library.json"); saved.Save(path);
        var restored = DeckLibrary.Load(path).Find(deck.Id)!;
        Check(restored.Details == details && restored.Export == receipt && restored.Sources.Contains(PlayGwentExport.Origin + "/en/decks/" + receipt.DeckHash),
            "Local save lost details, export receipt or generated source link.");
        var oldFingerprint = DeckLibrary.Fingerprint(deck);
        saved.Rename(deck.Id, "Renamed"); saved.SetDetails(deck.Id, details with { Overview = "Changed" });
        Check(DeckLibrary.Fingerprint(saved.Find(deck.Id)!.Deck) == oldFingerprint, "Names and notes changed composition identity.");
        var variant = deck with { Id = "variant-test", Cards = deck.Cards.Skip(1).ToArray() };
        saved.Merge([variant]); Check(saved.Decks.Length == 2 && saved.Find(deck.Id)!.Deck.CardCount == deck.CardCount, "Saving an edited variant destroyed its original.");
        saved.Save(path); Check(DeckLibrary.Load(path).Decks.Length == 2, "Variant did not survive cache round-trip.");

        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var builder = XDocument.Load(Path.Combine(project, "src/GwentCompanion.App/DeckBuilderWindow.xaml"));
        var popup = XDocument.Load(Path.Combine(project, "src/GwentCompanion.App/DeckExportWindow.xaml"));
        Check(!builder.Descendants().Any(el => el.Attribute("AcceptsReturn")?.Value == "True") &&
            popup.Descendants().Any(el => el.Attribute(x + "Name")?.Value == "Overview"), "Optional text windows clutter the main builder.");
        Check(builder.Descendants().Any(el => el.Attribute(x + "Name")?.Value == "AutoFill") &&
            builder.Descendants().Any(el => el.Attribute(x + "Name")?.Value == "TemplatePopup") &&
            builder.Descendants().Any(el => el.Attribute(x + "Name")?.Value == "TemplateChoice" && el.Name.LocalName == "ListBox") &&
            builder.Descendants().Any(el => el.Attribute(x + "Name")?.Value == "SaveDeckButton"), "Builder is missing toggle/templates/local save.");
        Check(!builder.Descendants().Any(el => el.Name.LocalName == "Expander" && el.Attribute("Header")?.Value == "Auto-fill details"),
            "Auto-fill details still consumes builder height.");
        foreach (var file in Directory.GetFiles(Path.Combine(project, "src/GwentCompanion.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        foreach (var tip in XDocument.Load(file).Descendants().Attributes("ToolTip"))
            Check(tip.Value.Length <= 90 && !tip.Value.Contains("RelativeSource Self"), "Verbose/self-repeating tooltip: " + Path.GetFileName(file) + " :: " + tip.Value);
        Console.WriteLine("PASS deck builder/export: real Knut–Nekker completion, Sigvald balance conflict, fixed cards, exclusions, toggle, legality, payloads, notes, safe origins, receipts, templates and cache variants.");
    }
}
