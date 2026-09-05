using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunCardDataSmokeAsync()
    {
        var directory = Path.Combine(FindGameRoot(), "GwentCompanion/diagnostics/card-data-ui-smoke");
        Directory.CreateDirectory(directory);
        var windows = new List<Window>();
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            var originals = new[] { LibraryPath, CardDataPath, ResolveSettingsPath(), ObservedDraftPath }.Where(File.Exists)
                .ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            var catalog = GwentOneCardCatalog.Load(CardDataPath);
            var old = File.ReadAllText(CardDataPath); var json = JsonNode.Parse(old)!;
            var library = DeckLibrary.Load(LibraryPath);
            var raw = library.Decks.First();
            var card = catalog.First(c => c.Id == raw.Cards[0].Card.Id);
            var row = json["response"]!.AsObject().First(p => p.Value!["id"]!["card"]!.GetValue<int>().ToString() == card.Id).Value!;
            row["attributes"]!["power"] = card.Power + 1;
            var active = Path.Combine(directory, "fixture-active.json"); File.WriteAllText(active, old);
            var imported = Path.Combine(directory, "fixture-update.json"); File.WriteAllText(imported, json.ToJsonString());
            var updater = new CardDataUpdater(active);
            var popup = new CardDataWindow(updater, "Import", imported, run: false); windows.Add(popup);
            DeckBuilderSmoke.Render(popup, Path.Combine(directory, "updater-progress.png"), 480, 235);
            foreach (var text in VisualDescendants<TextBlock>(popup))
                Check(Contrast(text.Foreground, popup.Background) >= 4.5, "Popup contrast failed.");
            popup.StatusText.Text = "Update stopped. Current data was kept.\nThe server is unavailable. Try again later or import a catalogue.";
            popup.Progress.Visibility = Visibility.Collapsed;
            popup.CloseButton.Content = "Close";
            DeckBuilderSmoke.Render(popup, Path.Combine(directory, "updater-error.png"), 480, 250);
            popup.Close();
            // Exercise the actual async modal flow, using only the isolated fixture above.
            var importing = new CardDataWindow(updater, "Import", imported); windows.Add(importing);
            var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            watchdog.Tick += (_, _) => importing.Close(); watchdog.Start();
            bool? applied;
            try { applied = importing.ShowDialog(); } finally { watchdog.Stop(); }
            Check(applied == true && importing.Result?.Changed == true, "Import modal did not finish with automatic reload requested.");
            Check(updater.Current()!.Cards.Single(c => c.Id == card.Id).Power == card.Power + 1, "Import did not activate fixture values.");
            await Task.Yield();

            var window = new MainWindow(); windows.Add(window); window.Loaded -= window.OnLoaded;
            window.SyncDecksButton.IsEnabled = window.UseUserDeckButton.IsEnabled = true;
            Check(window.CardUpdateBlockReason() is null, "Idle update unexpectedly blocked.");
            window._deckLoads = 1; Check(window.CardUpdateBlockReason() is not null, "Update raced a deck load."); window._deckLoads = 0;
            window._analysisTransition = true; Check(window.CardUpdateBlockReason() is not null, "Update raced analysis transition."); window._analysisTransition = false;
            window._libraryTransferBusy = true; Check(window.CardUpdateBlockReason() is not null, "Update raced library import."); window._libraryTransferBusy = false;
            window._cardDataBusy = true; window.RefreshAnalysisButton(); Check(!window.DiagnosticButton.IsEnabled, "Analysis enabled during update."); window._cardDataBusy = false;

            window._library = library;
            window._currentCardValues = new CurrentCardValues(CardDataSnapshot.Parse(json.ToJsonString()).Cards);
            var state = JsonSerializer.Serialize(window._library.Records);
            var item = window.CreateDeckItems([raw]).First(i => i.Deck?.Id == raw.Id);
            Check(item.Deck!.Cards.Single(c => c.Card.Id == card.Id).Card.Power == card.Power + 1, "Library result used stale values.");
            Check(JsonSerializer.Serialize(window._library.Records) == state, "Displaying current values edited the library.");
            window._confirmedOpponentDeck = raw;
            window._expandedWorkspace = true; window._workspaceZoom = 1.15;
            window.Left = 40; window.Top = 35; window.Width = 1350; window.Height = 900;
            var next = window.CreateCardDataReloadWindow(importing.Result!); windows.Add(next); next.Loaded -= next.OnLoaded;
            Check(next._expandedWorkspace && next._workspaceZoom == 1.15 && next.Left == 40 && next.Width == 1350 && next._page == UiPage.Settings,
                "Reload factory lost display mode/location or update result page.");
            Check(next._confirmedOpponentDeck?.Id == raw.Id && next._currentCardValues is not null && next.CardDataChanges.Text.Contains(card.Name),
                "Reload factory lost pinned reference or change report.");
            next.RefreshCardDataStatus(); next.CardDataChangesPanel.IsExpanded = true;
            next.HideLoadingShell();
            next.UpdateWorkspaceLayout(1350);
            DeckBuilderSmoke.Render(next, Path.Combine(directory, "settings-wide.png"), 1350, 900);
            next._expandedWorkspace = false; next.UpdateWorkspaceLayout(410);
            DeckBuilderSmoke.Render(next, Path.Combine(directory, "settings-compact.png"), 410, 760);
            Check(Contrast(next.CardDataChanges.Foreground, next.CardDataChanges.Background) >= 4.5, "Changes text is unreadable.");
            foreach (var pair in originals) Check(SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value), "Smoke changed user data: " + pair.Key);
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: actual isolated import modal, automatic reload request, reload window factory, update guards, current-value library results, compact/wide settings and popup contrast. Live catalogue/library/settings/drafts unchanged. No game capture or browser.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + error); return 1; }
        finally { foreach (var window in windows) window.Close(); }
    }
}
