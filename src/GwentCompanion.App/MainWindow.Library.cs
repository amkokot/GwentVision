using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private DeckLibrary _library = new();
    private bool _libraryReady;
    private DeckLibraryWindow? _libraryWindow;
    private DeckBuilderWindow? _builderWindow;
    private static string LibraryPath => Path.Combine(FindDataRoot(), "cache", "deck-library.json");

    private LibraryMergeResult MergeLibrary(IEnumerable<DeckDefinition> incoming)
    {
        if (!_libraryReady) throw new InvalidOperationException("The deck library did not load; its file has been preserved.");
        var result = _library.Merge(incoming);
        if (_reviewEvidencePath is null) _library.Save(LibraryPath);
        RefreshLibraryReferences();
        return result;
    }
    private void RefreshLibraryReferences()
    {
        _cachedDecks = _library.Decks.Select(CurrentDeck).ToArray();
        if (_selectedUserDeck is { } own) _selectedUserDeck = CurrentDeck(_library.Find(own.Id)?.Deck ?? own);
        if (_confirmedOpponentDeck is { } opponent) _confirmedOpponentDeck = CurrentDeck(_library.Find(opponent.Id)?.Deck ?? opponent);
        RefreshDeckList(); RebuildPointCatalog(); RenderLiveInference();
        if (_selectedUserDeck is { } selected) UserDeckStatusText.Text = $"My deck: {selected.Name} · {selected.Faction} · {selected.Leader}";
    }
    private void OpenLibrary_OnClick(object sender, RoutedEventArgs e) => OpenLibrary(null);
    private async void EditLibraryDeck_OnClick(object sender, RoutedEventArgs e)
    {
        if (_editingLibraryDeck) return;
        var selected = DeckList.SelectedItem as DeckListItem;
        if (selected is null) { LibraryActionStatus("Select a deck to edit."); return; }
        if (BuilderBlockReason() is { } blocked) { LibraryActionStatus(blocked); return; }
        _editingLibraryDeck = true; EditSelectedDeckButton.IsEnabled = false;
        LibraryActionStatus("Opening " + selected.Name + "…");
        try
        {
            if (selected.Deck is null && selected.IndexEntry is null)
            {
                var observed = ObservedEditorTemplate(selected);
                OpenBuilder(observed.Deck, observed.SourceKey);
                return;
            }
            var deck = await LoadSelectedIndexDeckAsync(selected);
            if (deck is null) LibraryActionStatus(DeckDataStatusText.Text);
            else OpenBuilder(deck);
        }
        catch (Exception error) { LibraryActionStatus("Could not edit this deck: " + error.Message); }
        finally { _editingLibraryDeck = false; EditSelectedDeckButton.IsEnabled = DeckList.SelectedItem is DeckListItem; }
    }
    private bool _editingLibraryDeck;
    private void DeleteLibraryDeck_OnClick(object sender, RoutedEventArgs e)
    {
        if (DeckList.SelectedItem is not DeckListItem { Deck: { } deck } || _library.Find(deck.Id) is not { } record)
        { LibraryActionStatus("Select an exact cached deck to delete."); return; }
        if (BuilderBlockReason() is { } blocked) { LibraryActionStatus(blocked); return; }
        if (_builderWindow is not null || _libraryWindow is not null)
        { LibraryActionStatus("Close the open deck editor before deleting a library deck."); return; }
        var history = (record.Deck.Occurrences?.Count ?? 0) + record.Sources.Length;
        var variationGroup = _library.VariationGroupFor(record.Deck.Id);
        var variation = variationGroup is { Members.Length: > 1 }
            ? $"version {Array.IndexOf(variationGroup.Members, record.Fingerprint) + 1} of {variationGroup.Members.Length}"
            : "deck";
        if (MessageBox.Show(this, $"Delete {variation} ‘{record.Deck.Name}’ from the library?\n\nThis removes only this exact card list" +
            (history > 0 ? $" and its {history} source/history reference(s)" : "") + ". Other versions remain. The current library will be retained as .bak.",
            variationGroup is { Members.Length: > 1 } ? "Delete deck version" : "Delete library deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var removed = _library.Remove(record.Deck.Id);
            _library.Save(LibraryPath);
            foreach (var group in _variationSelection.Where(item => item.Value == removed.Deck.Id).Select(item => item.Key).ToArray())
                _variationSelection.Remove(group);
            RefreshLibraryReferences();
            LibraryActionStatus($"Deleted {variation} ‘{removed.Deck.Name}’. Other versions were kept; the previous library is available as .bak.");
        }
        catch (Exception error) { LibraryActionStatus("Could not delete the deck: " + error.Message); }
    }
    private void LibraryActionStatus(string message) => FooterStatusText.Text = DeckDataStatusText.Text = message;
    private string? BuilderBlockReason() => _cardDataBusy || _cardReloadRequired ? "Finish reloading card data before editing." :
        _libraryTransferBusy ? "Finish the library transfer before editing." :
        _reviewEvidencePath is not null || !_libraryReady ? "Deck editing requires a loaded library outside offline review." :
        _analysisTransition || _diagnosticSession?.IsRunning == true ? "Stop live analysis (■) before editing a deck." : null;
    private void BuildDeck_OnClick(object sender, RoutedEventArgs e)
    {
        OpenBuilder(null);
    }
    private void OpenBuilder(DeckDefinition? template, string? observedSourceKey = null)
    {
        if (BuilderBlockReason() is { } blocked) { LibraryActionStatus(blocked); return; }
        if (_builderWindow is not null)
        {
            if (_builderWindow.WindowState == WindowState.Minimized) _builderWindow.WindowState = WindowState.Normal;
            _builderWindow.Activate();
            LibraryActionStatus(_builderWindow.OpenDraft(template, observedSourceKey: observedSourceKey) ? "Editing " + (template?.Name ?? "a new deck") + "." : "Kept the unsaved draft; deck switch cancelled.");
            return;
        }
        if (_libraryWindow is not null) { _libraryWindow.Activate(); LibraryActionStatus("Close the import window before editing a deck."); return; }
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"));
            _builderWindow = new DeckBuilderWindow(_library, LibraryPath, catalog, template, RefreshLibraryReferences, c => CardArt(c), observedSourceKey, SaveObservedDraft) { Owner = this };
            DiagnosticButton.IsEnabled = false; SyncDecksButton.IsEnabled = false;
            _builderWindow.Closed += (_, _) => { _builderWindow = null; RefreshAnalysisButton(); SyncDecksButton.IsEnabled = true; TryOpenPendingEncounterReview(); };
            _builderWindow.Show();
            LibraryActionStatus("Editing " + (template?.Name ?? "a new deck") + ".");
        }
        catch (Exception exception) { _builderWindow = null; RefreshAnalysisButton(); SyncDecksButton.IsEnabled = true; LibraryActionStatus("Could not open the deck builder: " + exception.Message); }
    }
    private async void ExportLibraryDeck_OnClick(object sender, RoutedEventArgs e)
    {
        if (_libraryTransferBusy) { DeckDataStatusText.Text = "Finish the library transfer first."; return; }
        if (_reviewEvidencePath is not null || !_libraryReady) { DeckDataStatusText.Text = "Export requires a loaded library outside offline review."; return; }
        if (_analysisTransition || _diagnosticSession?.IsRunning == true) { DeckDataStatusText.Text = "Stop analysis before exporting a deck."; return; }
        try
        {
            var deck = await LoadSelectedIndexDeckAsync();
            if (deck is null) return;
            var catalog = GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"));
            new DeckExportWindow(_library, LibraryPath, deck, catalog, RefreshLibraryReferences) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) { DeckDataStatusText.Text = "Could not export: " + exception.Message; }
    }
    private void OpenLibrary(DeckDefinition? deck)
    {
        if (_libraryTransferBusy) { DeckDataStatusText.Text = "Finish the library transfer first."; return; }
        if (_reviewEvidencePath is not null) { DeckDataStatusText.Text = "Library editing is disabled in offline review."; return; }
        if (!_libraryReady) { DeckDataStatusText.Text = "Library is unavailable. Check the load error before importing."; return; }
        if (_analysisTransition || _diagnosticSession?.IsRunning == true) { DeckDataStatusText.Text = "Stop analysis before importing or editing a deck."; return; }
        if (_libraryWindow is not null) { _libraryWindow.Activate(); return; }
        if (_builderWindow is not null) { _builderWindow.Activate(); return; }
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json"));
            _libraryWindow = new DeckLibraryWindow(_library, LibraryPath, ResolveDeckCacheDirectory(), catalog, deck,
                () => { _deckIndexEntries = _deckIndexEntries.Concat(_library.ImportedLinks).DistinctBy(entry => entry.SourceId + "|" + entry.DeckUri).ToArray(); RefreshLibraryReferences(); },
                Path.Combine(FindDataRoot(), "deck-scans"));
            _libraryWindow.Owner = this;
            if (_expandedWorkspace) _libraryWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DiagnosticButton.IsEnabled = false; SyncDecksButton.IsEnabled = false;
            _libraryWindow.Closed += (_, _) => { _libraryWindow = null; RefreshAnalysisButton(); SyncDecksButton.IsEnabled = true; TryOpenPendingEncounterReview(); };
            _libraryWindow.Show();
        }
        catch (Exception exception) { DeckDataStatusText.Text = "Could not open deck editor: " + exception.Message; }
    }
    private void DeckCardsFilter_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyDeckFilter();
}
