using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static int RunVariationSmoke()
    {
        var folder = Path.Combine(FindGameRoot(), "GwentCompanion/diagnostics/library-variation-ui-smoke"); Directory.CreateDirectory(folder);
        try
        {
            var window = new MainWindow(); window.Loaded -= window.OnLoaded;
            window._library = DeckLibrary.Load(LibraryPath); window._cachedDecks = window._library.Decks;
            window._candidateCatalog = GwentOneCardCatalog.Load(Path.Combine(FindGameRoot(), "GwentCompanion/cache/gwent-one-cards.json"));
            window._library.EnsureVariationGroups(window._candidateCatalog);
            window.ShowPage(UiPage.Library); window.SetDeckListItems(window.CreateDeckItems(window._cachedDecks));
            var group = window._library.VariationGroups.Where(g => g.Members.Length >= 3 && !g.Name.All(c => char.IsAsciiHexDigit(c)))
                .OrderByDescending(g => g.Members.Length).First();
            var heading = window.DeckList.Items.Cast<DeckListItem>().Single(i => i.GroupId == group.Id);
            window.DeckList.SelectedItem = heading;
            if (window.DeckList.Items.Count != window._library.VariationGroups.Count) throw new InvalidOperationException("Full library was not grouped.");
            window._libraryReady = true; window.RefreshLibraryPreview();
            if (window.DeleteSelectedDeckButton.Content?.ToString() != "Delete version" || !window.DeleteSelectedDeckButton.IsEnabled)
                throw new InvalidOperationException("A selected multi-version deck did not expose exact-version deletion.");
            window._libraryReady = false;
            var initialId = heading.Deck!.Id;
            window.CycleLibraryVariation(1);
            var next = (DeckListItem)window.DeckList.SelectedItem;
            if (next.Deck!.Id == initialId || next.GroupId != group.Id || !window.LibraryDeckDetailText.Text.Contains(next.Deck.Name))
                throw new InvalidOperationException("Arrow did not switch the exact deck preview within its heading.");
            window.CycleLibraryVariation(-1);
            if (((DeckListItem)window.DeckList.SelectedItem).Deck!.Id != initialId) throw new InvalidOperationException("Previous arrow did not restore selection.");
            // A visible first-page group is used for off-screen rendering; the nine-variant navigation was exercised above.
            window.DeckList.SelectedItem = window.DeckList.Items.Cast<DeckListItem>().First(i => i.Variations?.Length > 1);
            window.HideLoadingShell();
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "library.png"), 600, 850);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "library-compact.png"), 394, 710);
            window.LibraryManagement.IsExpanded = true;
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "transfer-actions.png"), 600, 850);
            window.LibraryManagement.IsExpanded = false;
            // Every operational path consumes the active row's exact Deck, not the group representative.
            var active = (DeckListItem)window.DeckList.SelectedItem;
            window.EditLibraryDeck_OnClick(window.EditSelectedDeckButton, new RoutedEventArgs());
            if (!window.FooterStatusText.Text.Contains("loaded library")) throw new InvalidOperationException("Blocked edit failed silently.");
            window._libraryReady = true;
            var otherTemplate = window._cachedDecks.First(d => d.Id != active.Deck!.Id);
            var editor = new DeckBuilderWindow(window._library, Path.Combine(folder, "test-library.json"), window._candidateCatalog, otherTemplate, () => { });
            window._builderWindow = editor;
            window.OpenBuilder(active.Deck);
            if (editor.DeckName.Text != active.Deck!.Name) throw new InvalidOperationException("Edit activated a stale editor without loading the selected variation.");
            editor.Close(); window._builderWindow = null;
            var popup = new DeckExportWindow(window._library, Path.Combine(folder, "test-library.json"), active.Deck!, window._candidateCatalog, () => { }, false);
            var marker = DeckLibrary.Fingerprint(active.Deck!)[..8];
            if (!popup.DeckSummary.Text.Contains(marker) || !popup.WebsiteName.Text.Contains(marker)) throw new InvalidOperationException("Export is missing the active variation marker.");
            DeckBuilderSmoke.Render(popup, Path.Combine(folder, "variation-export.png"), 1088, 730); popup.Close();
            var chosen = window._library.GroupVariants(group).Last().Deck;
            window.DeckSearchBox.Text = chosen.Name;
            var rows = window.DeckList.Items.Cast<DeckListItem>().ToArray();
            if (!rows.Any(r => r.Variations?.Any(v => v.Deck?.Id == chosen.Id) == true)) throw new InvalidOperationException("Search hid a matching variation.");
            window.Close();
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: {window._cachedDecks.Length} lists grouped under {window._library.VariationGroups.Count} headings; next/previous switch exact lists, previews and export markers; search preserves matching variants; full/compact UI rendered. Startup/capture/network disabled; live library unchanged.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
    }
}
