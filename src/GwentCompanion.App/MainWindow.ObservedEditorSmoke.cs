using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    /// <summary>Offline regression using real observations read-only; all saves go to isolated diagnostics.</summary>
    internal static async Task<int> RunObservedEditorSmokeAsync()
    {
        var folder = Path.Combine(FindGameRoot(), "GwentCompanion/diagnostics/observed-editor-smoke");
        Directory.CreateDirectory(folder);
        var draftPath = Path.Combine(folder, "drafts-" + Guid.NewGuid().ToString("N") + ".json");
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        object? Call(object instance, string method, params object?[] args) => instance.GetType()
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
        DeckAutoFillResult Result(DeckBuilderWindow editor) => (DeckAutoFillResult)typeof(DeckBuilderWindow)
            .GetField("_result", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        async Task Settled(DeckBuilderWindow editor)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (editor.BuilderStatus.Text.StartsWith("Updating", StringComparison.Ordinal))
            { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(editor.BuilderStatus.Text); await Task.Delay(40); }
            Check(!editor.BuilderStatus.Text.StartsWith("Could not", StringComparison.Ordinal), editor.BuilderStatus.Text);
        }
        Dictionary<string, string> SourceHashes() => Directory.GetFiles(ResolveObservedDeckDirectory(), "*.json", SearchOption.AllDirectories)
            .Concat(new[] { LibraryPath, OpponentMemoryPath, ResolveSettingsPath(), ObservedDraftPath }).Where(File.Exists)
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        try
        {
            var before = SourceHashes();
            var window = new MainWindow(); window.Loaded -= window.OnLoaded;
            window._library = DeckLibrary.Load(LibraryPath); window._cachedDecks = window._library.Decks;
            window._candidateCatalog = GwentOneCardCatalog.Load(Path.Combine(FindGameRoot(), "GwentCompanion/cache/gwent-one-cards.json"));
            window._opponentMemory = OpponentDeckMemoryStore.Load(OpponentMemoryPath);
            var observations = window.CreateObservedDeckItems(); var learned = window.LearnedDeckItems().ToArray();
            Check(observations.Concat(learned).All(item => (item.PartialSlots ?? []).All(slot => slot.Card is null || StartingDeckRules.IsStartingCard(slot.Card))),
                "Generated token leaked into an existing saved-match preview.");
            Check(observations.All(item => !(item.ObservedNames ?? []).Contains("Woodland Spirit")), "Token remained in saved-match search/summary.");
            Check(observations.Length > 0 && learned.Length > 0, "Real observed and learned fixtures are required.");
            var original = observations.First(i => i.PartialSlots is { Count: > 1 and < 25 });
            var editor = new DeckBuilderWindow(window._library, Path.Combine(folder, "unused-library.json"), window._candidateCatalog,
                window._cachedDecks.First(), () => { }, saveObservedDraft: d => DeckEditorDraftStore.Save(draftPath, d));
            window._builderWindow = editor; window._libraryReady = true; window.ShowPage(UiPage.Library);
            foreach (var item in new[] { original, learned.First() })
            {
                window.SetDeckListItems([item]); window.DeckList.SelectedIndex = 0;
                window.EditLibraryDeck_OnClick(window.EditSelectedDeckButton, new RoutedEventArgs()); await Settled(editor);
                Check(editor.DeckName.Text == item.Name && window.FooterStatusText.Text.StartsWith("Editing"), "Edit did not open the selected partial record: " + window.FooterStatusText.Text);
                Check(editor.AutoFill.IsChecked == false && Result(editor).Cards.Sum(c => c.Count) == item.PartialSlots!.Count,
                    "Opening a record changed its card-copy count or auto-filled unknown slots.");
                Check(editor.SaveObservedDraftButton.Visibility == Visibility.Visible && editor.SaveObservedDraftButton.IsEnabled,
                    "Partial record cannot be saved as a draft.");
            }
            window.SetDeckListItems([original]); window.DeckList.SelectedIndex = 0;
            window.EditLibraryDeck_OnClick(window.EditSelectedDeckButton, new RoutedEventArgs()); await Settled(editor);
            var rows = ((IEnumerable)editor.DraftCards.Rows!).Cast<object>().ToArray();
            var selectedCard = (CardDefinition)rows[0].GetType().GetProperty("Card")!.GetValue(rows[0])!;
            var initialCopies = Result(editor).Cards.Sum(c => c.Count);
            Call(editor, "DraftActivated", null, rows[0]); Call(editor, "RemoveClicked", editor, new RoutedEventArgs()); await Settled(editor);
            editor.DeckName.Text = "Observed edit test";
            Check(Result(editor).Cards.Sum(c => c.Count) == initialCopies - 1 && !editor.SaveDeckButton.IsEnabled,
                "Partial editing failed or enabled a complete-library save.");
            Check(!editor.OpenDraft(window._cachedDecks.First(), () => false), "Partial edits were discarded without consent.");
            Call(editor, "SaveObservedDraftLocal");
            var saved = DeckEditorDraftStore.Load(draftPath).Single();
            Check(saved.SourceKey == original.DraftSourceKey && saved.Name == editor.DeckName.Text && saved.Cards.Sum(c => c.Count) == initialCopies - 1,
                "Draft persistence lost the source, name or edited composition.");
            editor.CardSearch.Text = selectedCard.Name;
            var collectionRow = ((IEnumerable)editor.Collection.Rows!).Cast<object>().First(r =>
                ((CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).Id == selectedCard.Id);
            Call(editor, "CollectionActivated", null, collectionRow); await Settled(editor);
            Call(editor, "SaveObservedDraftLocal"); saved = DeckEditorDraftStore.Load(draftPath).Single();
            Check(saved.Cards.Sum(c => c.Count) == initialCopies && File.Exists(draftPath + ".bak"), "Draft update duplicated the draft or lost a restored card.");
            DeckBuilderSmoke.Render(editor, Path.Combine(folder, "observed-editor-wide.png"), 1320, 900);
            DeckBuilderSmoke.Render(editor, Path.Combine(folder, "observed-editor-compact.png"), 728, 530);
            Check(editor.Collection.ActualHeight >= 70 && editor.DraftCards.ActualHeight >= 70, "Draft actions squeezed the compact card lists out.");
            var savedItem = window.DraftListItem(saved);
            editor.OpenDraft(window._cachedDecks.First()); await Settled(editor);
            Check(editor.SaveObservedDraftButton.Visibility == (editor.SaveDeckButton.IsEnabled ? Visibility.Collapsed : Visibility.Visible),
                "Ordinary template did not choose between legal library save and work-in-progress save.");
            window.SetDeckListItems([savedItem]); window.DeckList.SelectedIndex = 0;
            window.EditLibraryDeck_OnClick(window.EditSelectedDeckButton, new RoutedEventArgs()); await Settled(editor);
            Check(editor.DeckName.Text == saved.Name && Result(editor).Cards.Sum(c => c.Count) == initialCopies,
                "Saved draft did not reopen through Library Edit.");
            // Empty drafts and unknown headers are valid work in progress, but never complete inference lists.
            var unknown = saved with { SourceKey = "smoke:unknown", Name = "Unknown headers", Faction = null, LeaderId = null, StratagemId = null, Cards = [] };
            DeckEditorDraftStore.Save(draftPath, unknown);
            window.SetDeckListItems([window.DraftListItem(DeckEditorDraftStore.Load(draftPath).Single(d => d.SourceKey == unknown.SourceKey))]);
            window.DeckList.SelectedIndex = 0; window.EditLibraryDeck_OnClick(window.EditSelectedDeckButton, new RoutedEventArgs()); await Settled(editor);
            Check(editor.FactionChoice.SelectedIndex == 0 && Result(editor).Leader is null && Result(editor).Cards.Count == 0 &&
                editor.SaveObservedDraftButton.IsEnabled && !editor.SaveDeckButton.IsEnabled && !editor.ExportDeckButton.IsEnabled, "Unknown headers/empty draft cannot be reopened safely.");
            // Exercise the actual CheckBox handlers without persisting test choices to the user's settings.
            window._libraryReady = false;
            var full = window.CreateDeckItems([window._cachedDecks.First()]).Single();
            window.SetDeckListItems([original, learned.First(), savedItem, full]);
            window.DeckList.SelectedItem = original; window.HideLoadingShell();
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "library-observed-visible.png"), 394, 710);
            window.ShowObservedDecksChoice.IsChecked = false;
            Check(window.DeckList.Items.Count == 2 && window.DeckList.SelectedItem is null && !window.EditSelectedDeckButton.IsEnabled,
                "Hiding observations left automatic records or a stale selected preview.");
            Check(window.DeckList.Items.Cast<DeckListItem>().Any(i => i.SavedDraft is not null), "Visibility toggle hid the explicitly saved draft.");
            window.DeckList.SelectedItem = savedItem;
            Check(((IEnumerable)window.LibraryDeckCards.Rows!).Cast<DeckStripRow>().All(r => r.Badge == "DRAFT" && r.ValueBadge is null &&
                !r.Detail.Contains("click to remove")), "Saved-draft preview has live evidence labels or inactive removal instructions.");
            window.LibrarySearch.Query.Text = "Observed edit test";
            Check(window.DeckList.Items.Count == 1 && ((DeckListItem)window.DeckList.SelectedItem).SavedDraft is not null, "Search does not combine with the observation filter.");
            window.LibrarySearch.ClearAll();
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "library-observed-hidden.png"), 394, 710);
            window.ShowObservedDecksChoice.IsChecked = true;
            Check(window.DeckList.Items.Count == 4, "Showing observations failed to restore the records.");
            var preference = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(new UserSettings(null, ShowObservedDecks: false)));
            Check(preference?.ShowObservedDecks == false && JsonSerializer.Deserialize<UserSettings>("{}")!.ShowObservedDecks,
                "Visibility preference does not round-trip or old settings lost their default.");
            // The general builder keeps illegal work in the draft cache, never the prediction library.
            Check(editor.OpenDraft(null, () => true), "General builder could not start a work-in-progress draft."); await Settled(editor);
            editor.AutoFill.IsChecked = false; await Settled(editor); editor.DeckName.Text = "General incomplete draft";
            Check(editor.SaveObservedDraftButton.Visibility == Visibility.Visible && editor.SaveObservedDraftButton.IsEnabled &&
                !editor.SaveDeckButton.IsEnabled && !editor.ExportDeckButton.IsEnabled, "Incomplete general draft has no safe save action.");
            DeckBuilderSmoke.Render(editor, Path.Combine(folder, "general-work-in-progress.png"), 1008, 730);
            Call(editor, "SaveObservedDraftLocal");
            var incomplete = DeckEditorDraftStore.Load(draftPath).Single(d => d.Name == "General incomplete draft");
            Check(incomplete.SourceKey.StartsWith("builder-draft:") && incomplete.Cards.Count == 0,
                "General incomplete draft was not assigned a stable separate identity.");
            var overBudget = GwentOneCardCatalog.StartingLeaders(window._candidateCatalog).Select(leader => new
            {
                Leader = leader,
                Cards = window._candidateCatalog.Where(c => StartingDeckRules.IsStartingCard(c) && FactionCompatibility.IsPlayableBy(c, leader.Faction))
                    .OrderByDescending(c => c.Provision).ThenBy(c => c.Id).Take(25).Select(c => new DeckCard(c)).ToArray()
            }).Where(x => x.Cards.Length == 25).OrderByDescending(x => x.Cards.Sum(c => c.Card.Provision) - 150 - x.Leader.Provision).First();
            var illegal = new DeckDefinition("over-budget", "General over-budget draft", overBudget.Leader.Faction, overBudget.Leader.Name,
                overBudget.Leader.Provision, overBudget.Cards);
            Check(DeckBuildValidation.Errors(illegal).Any(e => e.StartsWith("Over the provision limit")), "Over-budget fixture is no longer invalid.");
            Check(editor.OpenDraft(illegal, () => true, "builder-draft:over-budget"), "General over-budget draft could not open."); await Settled(editor);
            Check(editor.SaveObservedDraftButton.IsEnabled && !editor.SaveDeckButton.IsEnabled && !editor.ExportDeckButton.IsEnabled,
                "Over-budget general draft was exportable or could not be preserved.");
            Call(editor, "SaveObservedDraftLocal");
            Check(DeckEditorDraftStore.Load(draftPath).Single(d => d.SourceKey == "builder-draft:over-budget").Cards.Sum(c => c.Count) == 25,
                "Over-budget general draft was rejected or lost cards.");
            var damaged = Path.Combine(folder, "damaged-drafts.json"); File.WriteAllText(damaged, "{ invalid cache");
            try { DeckEditorDraftStore.Save(damaged, saved); throw new InvalidOperationException("Damaged cache was overwritten."); }
            catch (JsonException) { Check(File.ReadAllText(damaged) == "{ invalid cache", "Damaged cache was not preserved."); }
            editor.Close(); window._builderWindow = null; window.Close();
            var after = SourceHashes(); Check(before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "Live evidence/library/settings changed during offline smoke.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: real observed ({observations.Length}) and learned ({learned.Length}) rows open via Edit; manual copies, add/remove, dirty cancellation, partial save/update/reopen, unknown headers/empty drafts; general incomplete and over-budget drafts saved separately while library/export stay gated; hidden observed filter plus saved-draft search and selection; preference serialization; damaged-cache preservation. Wide/compact UI rendered. All {before.Count} live source hashes unchanged; no capture or network.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
    }
}
