using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class DeckExportWindow
{
    internal static async Task<int> RunAutomaticExportSmokeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) return 1;
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var folder = Path.Combine(project, "diagnostics/automatic-export-ui-smoke"); Directory.CreateDirectory(folder);
        var windows = new List<DeckExportWindow>();
        try
        {
            static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            var livePath = Path.Combine(project, "cache/deck-library.json"); var hash = SHA256.HashData(File.ReadAllBytes(livePath));
            var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
            var source = DeckLibrary.Load(livePath).Decks.Select(new CurrentCardValues(catalog).Deck)
                .First(d => d.CardCount == 25 && d.Stratagem is not null && d.Cards.Any(c => c.Count == 2) && DeckBuildValidation.Errors(d).Count == 0)
                with { Id = "automatic-export-test", Name = "Automatic transfer test" };
            var payload = PlayGwentExport.CreateRequest(source, source.Name, catalog);
            const string deckHash = "0123456789abcdef0123456789abcdef";
            string VerifiedBody(bool omitCopy = false) => JsonSerializer.Serialize(new { id = 123,
                cards = (omitCopy ? payload.CardTemplateIds.Distinct() : payload.CardTemplateIds.Reverse()).Select(id => new { _source = new { id } }) });
            DeckExportWindow New(string name, Func<string, string?, Task<PlayGwentReply>> reply)
            {
                var library = new DeckLibrary(); library.Merge([source]);
                var window = new DeckExportWindow(library, Path.Combine(folder, name + ".json"), source, catalog, () => { }, false)
                    { RequestForSmoke = reply, _ready = true };
                window.RefreshButtons(); windows.Add(window); return window;
            }
            static async Task Idle(DeckExportWindow window)
            {
                var until = DateTimeOffset.UtcNow.AddSeconds(10);
                while (window._busy) { if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Exporter did not settle."); await Task.Delay(10); }
            }
            var signedIn = false; var calls = new List<(string Path, string? Body)>(); var missingCards = true;
            var success = New("success", async (path, body) =>
            {
                calls.Add((path, body)); await Task.Delay(5);
                if (path == PlayGwentExport.LoginCheckPath) return signedIn ? new(200, "{\"result\":\"OK\"}") : new(0, "", Redirected: true);
                if (path == PlayGwentExport.CreatePath) return new(200, "{\"id\":123,\"deckHash\":\"" + deckHash + "\"}");
                if (path == PlayGwentExport.ReadDeckPath("123")) return new(200, VerifiedBody());
                if (path == PlayGwentExport.GuidePath("123")) return new(200, "{\"id\":987}");
                if (path == PlayGwentExport.ImportPath(deckHash)) return missingCards ? new(402, "{\"total_crafting_cost\":800}") : new(200, "true");
                throw new InvalidOperationException("Unexpected request " + path);
            });
            Check(success.TransferCards.Items.Count == source.Cards.Count && success.Website.Visibility == Visibility.Hidden && success.ExportPreviewPanel.Visibility == Visibility.Visible,
                "The initial popup does not show the selected deck instead of the blank library.");
            DeckBuilderSmoke.Render(success, Path.Combine(folder, "automatic-export.png"), 1120, 780);
            DeckBuilderSmoke.Render(success, Path.Combine(folder, "automatic-export-compact.png"), 790, 590);
            success.Overview.Text = "A transfer test, not a published guide."; success.IncludeGuide.IsChecked = true;
            success.CreateClicked(success, new()); success.CreateClicked(success, new()); await Idle(success);
            Check(success._pendingExport is not null && success._receipt is null && calls.All(c => c.Path == PlayGwentExport.LoginCheckPath),
                "Signing out attempted a write or created an uncertain receipt.");
            Check(success.CancelQueuedButton.Visibility == Visibility.Visible && !success.ExportDetailsPanel.IsEnabled && success.Website.Visibility == Visibility.Visible,
                "Queued sign-in did not expose the browser, cancellation and immutable prepared details.");
            signedIn = true; await success.ContinueQueuedExportAsync();
            Check(success._receipt is { CardsVerified: true, GuideCreated: true, OutcomeUnknown: false } && success.ImportButton.IsEnabled && success._pendingExport is null,
                "Sign-in did not automatically resume and verify the full export.");
            Check(calls.Count(c => c.Path == PlayGwentExport.CreatePath) == 1 && calls.Count(c => c.Path == PlayGwentExport.GuidePath("123")) == 1, "Double-click caused duplicate writes.");
            var sent = JsonSerializer.Deserialize<PlayGwentCreateRequest>(calls.Single(c => c.Path == PlayGwentExport.CreatePath).Body!, PlayGwentExport.Json)!;
            Check(sent.CardTemplateIds.SequenceEqual(payload.CardTemplateIds), "Automatic transfer changed the header or physical copies.");
            using (var guide = JsonDocument.Parse(calls.Single(c => c.Path == PlayGwentExport.GuidePath("123")).Body!))
                Check(!guide.RootElement.GetProperty("publish").GetBoolean() && guide.RootElement.GetProperty("content").GetString()!.Contains("A transfer test"), "Optional notes missing or guide published.");
            Check(DeckLibrary.Load(success._path).Find(source.Id)?.Export is { CardsVerified: true }, "Verified receipt not persisted.");
            success.ImportClicked(success, new()); await Idle(success);
            Check(success._receipt?.GameImported == false && success.ExportStatus.Text.Contains("Nothing was crafted"), "Missing cards were crafted or reported as imported.");
            missingCards = false; success.ImportClicked(success, new()); await Idle(success);
            Check(success._receipt?.GameImported == true && calls.Where(c => c.Path.EndsWith("/import")).All(c => c.Body is null), "Owned-card import sent a crafting confirmation.");

            var cancelWrites = 0; var cancel = New("cancel", (path, _) => { if (path != PlayGwentExport.LoginCheckPath) cancelWrites++; return Task.FromResult(new PlayGwentReply(200, "false")); });
            cancel.CreateClicked(cancel, new()); await Idle(cancel); cancel.CancelQueuedClicked(cancel, new()); await cancel.ContinueQueuedExportAsync();
            Check(cancelWrites == 0 && cancel._pendingExport is null && cancel._receipt is null && !cancel._loginTimer.IsEnabled, "Cancel did not stop queued export.");

            var lostWrites = 0; var lost = New("lost-response", (path, _) =>
            {
                if (path == PlayGwentExport.LoginCheckPath) return Task.FromResult(new PlayGwentReply(200, "{\"result\":\"OK\"}"));
                lostWrites++; return Task.FromResult(new PlayGwentReply(0, "", Error: "Response lost"));
            });
            lost.CreateClicked(lost, new()); await Idle(lost); lost.CreateClicked(lost, new()); await lost.ContinueQueuedExportAsync();
            Check(lostWrites == 1 && lost._receipt?.OutcomeUnknown == true && lost.RetryButton.Visibility == Visibility.Visible && !lost.CreateButton.IsEnabled,
                "Unknown write was automatically retried.");

            var mismatchWrites = 0; var badCopy = true;
            var mismatch = New("mismatched-copies", (path, _) =>
            {
                if (path == PlayGwentExport.LoginCheckPath) return Task.FromResult(new PlayGwentReply(200, "{\"result\":\"OK\"}"));
                if (path == PlayGwentExport.CreatePath) { mismatchWrites++; return Task.FromResult(new PlayGwentReply(200, "{\"id\":123,\"deckHash\":\"" + deckHash + "\"}")); }
                return Task.FromResult(new PlayGwentReply(200, VerifiedBody(badCopy)));
            });
            mismatch.CreateClicked(mismatch, new()); await Idle(mismatch);
            Check(mismatch._receipt is { CardsVerified: false, DeckHash: not null } && !mismatch.ImportButton.IsEnabled && mismatch.CreateButton.IsEnabled,
                "Missing duplicate copy was accepted or its link discarded.");
            mismatch.ImportClicked(mismatch, new()); Check(mismatchWrites == 1, "Unverified deck imported.");
            badCopy = false; mismatch.CreateClicked(mismatch, new()); await Idle(mismatch);
            Check(mismatchWrites == 1 && mismatch._receipt?.CardsVerified == true, "Verification retry created another deck.");

            var offline = New("offline", (_, _) => Task.FromResult(new PlayGwentReply(0, "", Error: "Offline")));
            offline.CreateClicked(offline, new()); await Idle(offline);
            Check(offline._receipt is null && offline._pendingExport is null && offline.ExportStatus.Text.Contains("No deck was sent"), "Offline preflight left a false uncertain write.");
            foreach (var (name, reply, category) in new[] {
                ("unexpected-auth", new PlayGwentReply(200, "{\"account\":\"private-response-fixture\"}"), "JSON object"),
                ("auth-server-error", new PlayGwentReply(503, "private-response-fixture"), "HTTP 503"),
                ("auth-html", new PlayGwentReply(200, "<html>private-response-fixture</html>"), "non-JSON") })
            {
                var writes = 0;
                var rejected = New(name, (path, _) => { if (path != PlayGwentExport.LoginCheckPath) writes++; return Task.FromResult(reply); });
                rejected.CreateClicked(rejected, new()); await Idle(rejected);
                Check(writes == 0 && rejected._receipt is null && rejected._pendingExport is null && !rejected._loginTimer.IsEnabled && rejected.CreateButton.IsEnabled &&
                    rejected.ExportStatus.Text.Contains(category) && !rejected.ExportStatus.Text.Contains("private-response-fixture"),
                    "Failed sign-in preflight wrote a deck, blocked retry or exposed response contents.");
                if (name == "unexpected-auth")
                    DeckBuilderSmoke.Render(rejected, Path.Combine(folder, "sign-in-error-compact.png"), 790, 590);
            }
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(livePath))), "Export smoke changed the live library.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: native full-list preview, compact/wide layout, official result:OK sign-in/resume, exact header and duplicate-copy payload, double-click guard, saved-link verification, optional unpublished guide, cancel, no automatic write retry, mismatched-copy import block and verification-only retry, offline/malformed/HTTP-error preflight without response-content disclosure, owned-card import without crafting. Mock transport only; no browser/account/network writes or live-library changes.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally { foreach (var window in windows) { window.Closing -= window.ClosingExport; window.Close(); } }
    }
}
