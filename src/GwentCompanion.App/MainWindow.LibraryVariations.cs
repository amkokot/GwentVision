using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using Microsoft.Win32;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _variationSelection = new(StringComparer.Ordinal);
    private bool _libraryTransferBusy;

    private DeckListItem[] GroupLibraryItems(DeckListItem[] matches)
    {
        var groups = _library.VariationGroups.ToDictionary(g => g.Id);
        var membership = _library.VariationGroups.SelectMany(g => _library.GroupVariants(g).Select(r => (r.Deck.Id, Group: g.Id)))
            .ToDictionary(p => p.Id, p => p.Group, StringComparer.Ordinal);
        return matches.GroupBy(item => item.Deck is { } deck && membership.TryGetValue(deck.Id, out var id) ? id :
            "ungrouped-" + Array.IndexOf(matches, item)).Select(bucket =>
        {
            var variants = bucket.ToArray();
            if (!groups.TryGetValue(bucket.Key, out var group)) return variants[0];
            var active = variants.FirstOrDefault(v => v.Deck?.Id == _variationSelection.GetValueOrDefault(group.Id)) ?? variants[0];
            _variationSelection[group.Id] = active.Deck!.Id;
            var index = Array.IndexOf(group.Members, DeckLibrary.Fingerprint(active.Deck)) + 1;
            return active with { Name = group.Members.Length > 1 ? $"{group.Name} · {group.Members.Length} variations" : active.Name,
                Context = group.Members.Length > 1 ? $"Variation {index}/{group.Members.Length}: {active.Deck.Name}\n" + active.Context : active.Context,
                GroupId = group.Id, Variations = variants };
        }).ToArray();
    }
    private void RefreshVariationNavigation()
    {
        if (VariationNavigation is null) return;
        var item = DeckList.SelectedItem as DeckListItem;
        var group = item?.GroupId is { } id ? _library.VariationGroups.FirstOrDefault(g => g.Id == id) : null;
        VariationNavigation.Visibility = group?.Members.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousVariationButton.IsEnabled = NextVariationButton.IsEnabled = item?.Variations?.Length > 1;
        VariationNavigationText.Text = item?.Deck is { } deck && group is not null
            ? $"Variation {Array.IndexOf(group.Members, DeckLibrary.Fingerprint(deck)) + 1} / {group.Members.Length}" +
                (item.Variations?.Length < group.Members.Length ? $" · {item.Variations.Length} match filters" : "") : "";
        VariationNavigation.ToolTip = item?.Deck is null ? null : "Edit, pin and export use this exact list.";
    }
    private void PreviousVariation_OnClick(object sender, RoutedEventArgs e) => CycleLibraryVariation(-1);
    private void NextVariation_OnClick(object sender, RoutedEventArgs e) => CycleLibraryVariation(1);
    private void CycleLibraryVariation(int step)
    {
        if (DeckList.SelectedItem is not DeckListItem { GroupId: { } id, Variations: { Length: > 1 } variants } item) return;
        var index = Array.FindIndex(variants, v => v.Deck?.Id == item.Deck?.Id);
        _variationSelection[id] = variants[(index + step + variants.Length) % variants.Length].Deck!.Id;
        ApplyDeckFilter();
    }
    private string VariationDifference(DeckDefinition deck)
    {
        var group = _library.VariationGroupFor(deck.Id);
        if (group is null || group.Members.Length < 2) return "";
        var baseline = _library.GroupVariants(group)[0].Deck;
        if (DeckLibrary.Fingerprint(baseline) == DeckLibrary.Fingerprint(deck)) return "\nGroup reference variation; each list retains its own history.";
        var metric = new DeckVariationSimilarity(_library.VariationPolicy!.Prices);
        string Only(DeckDefinition a, DeckDefinition b) => string.Join(", ", a.Cards.Where(c => c.Count > b.CountOf(c.Card.Id))
            .Select(c => $"{c.Card.Name} ×{c.Count - b.CountOf(c.Card.Id)}"));
        return $"\n{metric.Compare(deck, baseline).Ratio:P1} provision overlap with group reference. Added: {Only(deck, baseline)}. Removed: {Only(baseline, deck)}.";
    }
    private bool CanTransferLibrary()
    {
        if (!_libraryReady || _reviewEvidencePath is not null || _analysisTransition || _diagnosticSession?.IsRunning == true ||
            _autoEncounterBusy || _builderWindow is not null || _libraryWindow is not null || _libraryTransferBusy || !SyncDecksButton.IsEnabled)
        { DeckDataStatusText.Text = "Stop analysis and finish other library operations before transferring the library."; return false; }
        return true;
    }
    private void TransferBusy(bool value)
    {
        _libraryTransferBusy = value; ExportLibraryFileButton.IsEnabled = ImportLibraryFileButton.IsEnabled = SyncDecksButton.IsEnabled = !value;
        RefreshAnalysisButton();
    }
    private async void ExportLibraryFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanTransferLibrary()) return;
        var dialog = new SaveFileDialog { Title = "Export grouped Gwent Vision library", FileName = "GwentVision.gwent-library.json",
            Filter = "Gwent Vision library (*.gwent-library.json)|*.gwent-library.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        if (new[] { LibraryPath, LibraryPath + ".bak" }.Any(p => Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
        { DeckDataStatusText.Text = "Choose a transfer file, not the app's live deck-library.json."; return; }
        try
        {
            TransferBusy(true); await Task.Run(() => _library.ExportTransfer(dialog.FileName));
            DeckDataStatusText.Text = $"Exported {_library.Records.Count} exact lists in {_library.VariationGroups.Count} headings, including notes and patch/observation listings. No account sessions or website-import flags exported.";
        }
        catch (Exception error) { DeckDataStatusText.Text = "Library export failed: " + error.Message; }
        finally { TransferBusy(false); }
    }
    private async void ImportLibraryFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanTransferLibrary()) return;
        var dialog = new OpenFileDialog { Title = "Import grouped Gwent Vision library",
            Filter = "Gwent Vision library (*.gwent-library.json)|*.gwent-library.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            TransferBusy(true); var preview = await Task.Run(() => _library.PreviewTransfer(dialog.FileName));
            if (MessageBox.Show(this, $"Import {preview.Added} new exact lists and merge {preview.Merged} existing lists from {preview.Groups} headings?\n\n" +
                $"Variation groups and source histories are preserved. Same-deck, same-patch library copies count as one observation. Existing custom local names and {preview.KeptLocalDetails} conflicting note sets are kept. No existing list is deleted; no website request will be sent.",
                "Confirm library import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await Task.Run(() => preview.Library.Save(LibraryPath));
            _library = preview.Library; _deckIndexEntries = _deckIndexEntries.Concat(_library.ImportedLinks).DistinctBy(e => e.SourceId + "|" + e.DeckUri).ToArray();
            RefreshLibraryReferences(); DeckDataStatusText.Text = $"Library imported: {preview.Added} added, {preview.Merged} merged. Previous cache retained as .bak.";
        }
        catch (Exception error) { DeckDataStatusText.Text = "Library import stopped; existing library retained: " + error.Message; }
        finally { TransferBusy(false); }
    }
}
