using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using Microsoft.Web.WebView2.Core;

namespace GwentCompanion.App;

public partial class DeckExportWindow
{
    // Real WebView2 + production scripts/receiver/navigation/controller, but every URL is
    // intercepted and served by this fixture. Never forwards a request to a live account.
    internal static async Task<int> RunExportBrowserSmokeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) return 1;
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var folder = Path.Combine(project, "diagnostics/export-browser-smoke"); Directory.CreateDirectory(folder);
        DeckExportWindow? window = null;
        var trace = new List<string>();
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
            var source = DeckLibrary.Load(Path.Combine(project, "cache/deck-library.json")).Decks.Select(new CurrentCardValues(catalog).Deck)
                .First(d => d.CardCount == 25 && d.Stratagem is not null && DeckBuildValidation.Errors(d).Count == 0)
                with { Id = "browser-export-fixture", Name = "Browser transport fixture" };
            var library = new DeckLibrary(); library.Merge([source]);
            window = new DeckExportWindow(library, Path.Combine(folder, "library.json"), source, catalog, () => { }, false);
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Show();
            _ = new WindowInteropHelper(window).EnsureHandle();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(folder, "isolated-browser"));
            await window.Website.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
            var view = window.Website.CoreWebView2;
            view.NavigationStarting += (_, args) => trace.Add("Navigation starting " + new Uri(args.Uri).AbsolutePath);
            view.NavigationCompleted += (_, args) => trace.Add($"Navigation completed: {args.IsSuccess}, {args.WebErrorStatus}");
            view.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            var loggedIn = false; var invalidAuth = true; var createCount = 0; var verifiedReads = 0; var blockedRequests = 0;
            var loginHeadersMatch = true;
            var expected = PlayGwentExport.CreateRequest(source, source.Name, catalog);
            PlayGwentCreateRequest? received = null;
            const string hash = "abcdef0123456789abcdef0123456789";
            view.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri); var status = 200; var headers = "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store";
                trace.Add(args.Request.Method + " " + uri.AbsolutePath);
                var body = "<!doctype html><title>Offline export fixture</title><form id='loginForm' method='get' action='/en/decks/builder/smoke-sign-in'><button>Sign in</button></form>";
                if (uri.Host != "www.playgwent.com") { blockedRequests++; status = 403; body = "External request blocked by fixture"; }
                else if (uri.AbsolutePath == PlayGwentExport.LoginCheckPath)
                {
                    loginHeadersMatch &= args.Request.Method == "GET" && args.Request.Headers.Contains("X-GOG-Cache-Control") &&
                        args.Request.Headers.GetHeader("X-GOG-Cache-Control") == "client.ttl=0, varnish.ttl=0, akamai.ttl=0, varnish.grace=0";
                    if (invalidAuth) { headers = "Content-Type: application/json"; body = "{\"result\":\"unexpected\"}"; }
                    else if (loggedIn) { headers = "Content-Type: application/json"; body = "{\"result\":\"OK\"}"; }
                    else { status = 302; headers = "Location: " + PlayGwentExport.LibraryUrl + "/smoke-sign-in"; body = ""; }
                }
                else if (uri.AbsolutePath.EndsWith("/smoke-sign-in"))
                { loggedIn = true; status = 302; headers = "Location: " + PlayGwentExport.LibraryUrl; body = ""; }
                else if (uri.AbsolutePath == PlayGwentExport.CreatePath)
                {
                    createCount++;
                    using var reader = new StreamReader(args.Request.Content!, Encoding.UTF8, leaveOpen: true);
                    received = JsonSerializer.Deserialize<PlayGwentCreateRequest>(reader.ReadToEnd(), PlayGwentExport.Json);
                    headers = "Content-Type: application/json";
                    body = "{\"id\":123,\"deckHash\":\"" + hash + "\"}";
                }
                else if (uri.AbsolutePath == PlayGwentExport.ReadDeckPath("123"))
                { verifiedReads++; headers = "Content-Type: application/json"; body = JsonSerializer.Serialize(new { id = 123, cards = expected.CardTemplateIds.Select(id => new { _source = new { id } }) }); }
                else if (uri.AbsolutePath != new Uri(PlayGwentExport.LibraryUrl).AbsolutePath && uri.AbsolutePath != "/en/decks/" + hash)
                { blockedRequests++; status = 404; body = "Unexpected fixture URL"; }
                args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 302 ? "Found" : "Fixture", headers);
            };
            window.ConfigureBrowser(); view.Navigate(PlayGwentExport.LibraryUrl);
            var until = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!window._ready) { if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Hidden WebView2 navigation did not complete."); await Task.Delay(25); }
            window.CreateClicked(window, new());
            until = DateTimeOffset.UtcNow.AddSeconds(10);
            while (window._busy) { if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Invalid sign-in response did not settle."); await Task.Delay(25); }
            if (createCount != 0 || window._receipt is not null || window._pendingExport is not null || !window.CreateButton.IsEnabled || !window.ExportStatus.Text.Contains("JSON object"))
                throw new InvalidOperationException("Malformed sign-in response allowed a write or blocked a clean retry.");
            invalidAuth = false;
            window.CreateClicked(window, new());
            until = DateTimeOffset.UtcNow.AddSeconds(20);
            while (window._receipt?.CardsVerified != true)
            {
                if (DateTimeOffset.UtcNow > until || window.ExportStatus.Text.StartsWith("Export stopped:")) throw new InvalidOperationException(window.ExportStatus.Text);
                await Task.Delay(25);
            }
            if (createCount != 1 || verifiedReads != 1 || received is null || !received.CardTemplateIds.SequenceEqual(expected.CardTemplateIds) || !loggedIn || !loginHeadersMatch)
                throw new InvalidOperationException("Real browser scripts lost card copies, duplicated creation or did not resume sign-in.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: real WebView2 initialization while preview is visible; production JavaScript fetch/postMessage/native receiver; malformed preflight blocks writes but permits retry; read-only sign-in with official cache header and result:OK response; form-based sign-in redirect and automatic resume; one full-deck POST; exact-copy verification; {blockedRequests} irrelevant requests intercepted. All responses are local fixtures; no external site/account requests or live-library writes.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally { File.WriteAllLines(Path.Combine(folder, "trace.txt"), trace); if (window is not null) { window.Closing -= window.ClosingExport; window.Close(); } }
    }
}
