using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private DispatcherTimer? _libraryPreviewTimer;
    private int _libraryPreviewRequest;
    private bool _updatingLibraryItems;

    private void CancelLibraryPreview()
    { _libraryPreviewTimer?.Stop(); _libraryPreviewRequest++; }

    private static bool SameLibraryItem(DeckListItem a, DeckListItem b)
    {
        if (a.DraftSourceKey is not null && b.DraftSourceKey is not null)
            return a.DraftSourceKey == b.DraftSourceKey && (a.SavedDraft is null) == (b.SavedDraft is null);
        if (a.GroupId is not null && b.GroupId is not null) return a.GroupId == b.GroupId;
        if (a.Deck is not null && b.Deck is not null) return a.Deck.Id == b.Deck.Id;
        if (a.SourceUri is not null && b.SourceUri is not null) return a.SourceUri == b.SourceUri;
        return a.Name == b.Name && a.RecordedAt == b.RecordedAt;
    }

    private void LibraryDeckPreview_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeckListItem item }) return;
        if (ReferenceEquals(DeckList.SelectedItem, item)) RefreshLibraryPreview();
        else DeckList.SelectedItem = item;
    }

    private void LibrarySelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingLibraryItems) RefreshLibraryPreview();
    }

    private void RefreshLibraryPreview()
    {
        if (LibraryDeckCards is null) return;
        var palette = Controls.FactionPalette.For((DeckList.SelectedItem as DeckListItem)?.HeadingFaction);
        LibraryDetailSurface.Background = Controls.FactionPalette.Brush(palette.Surface);
        LibraryDetailSurface.BorderBrush = Controls.FactionPalette.Brush(palette.Accent);
        var selectedItem = DeckList.SelectedItem as DeckListItem;
        EditSelectedDeckButton.IsEnabled = !_editingLibraryDeck && selectedItem is not null;
        DeleteSelectedDeckButton.IsEnabled = !_editingLibraryDeck && _libraryReady && _reviewEvidencePath is null &&
            DeckList.SelectedItem is DeckListItem { Deck: { } selectedDeck } && _library.Find(selectedDeck.Id) is not null;
        var multipleVersions = selectedItem?.Deck is { } activeDeck && _library.VariationGroupFor(activeDeck.Id)?.Members.Length > 1;
        DeleteSelectedDeckButton.Content = multipleVersions ? "Delete version" : "Delete deck";
        DeleteSelectedDeckButton.ToolTip = multipleVersions ? "Delete only the exact version currently displayed." : "Delete this exact cached deck.";
        RefreshVariationNavigation();
        CancelLibraryPreview();
        if (_page != UiPage.Library) return;
        LibraryDeckCards.Rows = Array.Empty<DeckStripRow>();
        LibraryDeckMetadata.Visibility = Visibility.Collapsed;
        LibraryDeckMetadataText.Text = "";
        if (DeckList.SelectedItem is not DeckListItem item)
        { LibraryDeckDetailText.Text = "Select a deck to preview."; return; }
        if (item.Deck is { } cached) { ShowLibraryDeck(cached); return; }
        if (item.IndexEntry is null)
        {
            LibraryDeckDetailText.Text = item.Name + "\n" + item.FilterFaction + " · " + (item.FilterLeader ?? "Leader unknown") +
                (item.SavedDraft is not null ? $" · {item.PartialSlots?.Count ?? 0} proposed cards\nSaved draft · not match evidence." :
                    $" · Partial record · {item.PartialSlots?.Count ?? 0} recorded/proposed copies\nEdit as a draft; raw observations stay unchanged.");
            LibraryDeckCards.Rows = item.PartialSlots?.Select(slot =>
            {
                var row = Strip(slot) with { ValueBadge = null };
                if (item.SavedDraft is not null) return row with { Badge = "DRAFT", Detail = "Manual draft · not match evidence",
                    AccessibleName = $"Draft card {row.Name}, {row.Provision} provisions; not match evidence" };
                return slot.State == DeckSlotState.Selected ? row with { Detail = "Saved proposal · see tooltip" } : row;
            }).ToArray() ?? [];
            return;
        }
        if (_reviewEvidencePath is not null)
        { LibraryDeckDetailText.Text = item.Name + " · Not cached. Offline review does not fetch decks."; return; }
        LibraryDeckDetailText.Text = item.Name + " · Loading cards…";
        var request = _libraryPreviewRequest;
        // Debounce network work when moving quickly through uncached search results.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _libraryPreviewTimer = timer;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var deck = await LoadSelectedIndexDeckAsync(item);
            if (request != _libraryPreviewRequest || _page != UiPage.Library) return;
            if (deck is not null) ShowLibraryDeck(deck);
            else LibraryDeckDetailText.Text = item.Name + " · Cards unavailable; this link may have expired.";
        };
        timer.Start();
    }

    private void ShowLibraryDeck(DeckDefinition deck)
    {
        LibraryDeckDetailText.Text = $"{deck.Name}\n{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · {deck.ProvisionTotal}p";
        LibraryDeckMetadata.Visibility = Visibility.Visible;
        LibraryDeckMetadataText.Text = $"Stratagem: {deck.Stratagem?.Name ?? "unknown"} · {DeckPatchMetadata.Describe(deck.Patches)}" +
            (deck.Occurrences is { Count: > 0 } ? "\n" + DeckOccurrences.Describe(deck.Occurrences) : "") + VariationDifference(deck);
        LibraryDeckCards.Rows = OpponentDeckProjector.Reference(deck).Select(Strip).ToArray();
    }

    private IReadOnlyList<ProjectedDeckSlot> PartialLibrarySlots(IEnumerable<StoredObservedCard> observations)
    {
        _candidateCatalog ??= GwentOneCardCatalog.Load(System.IO.Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"));
        return ObservedDeckStore.StartingCards(observations, _candidateCatalog).Select(item => (Item: item, Card: _candidateCatalog.FirstOrDefault(card => card.Id == item.Id)))
            .Where(pair => pair.Card is not null).OrderBy(pair => pair.Card!, DeckBuilderOrder.Comparer)
            .SelectMany(pair => Enumerable.Range(1, Math.Max(1, pair.Item.ObservedCopies)).Select(copy =>
                new ProjectedDeckSlot(0, pair.Card, copy, DeckSlotState.Observed, null, null, false,
                    "Partial recorded evidence; origin: " + pair.Item.Provenance)))
            .Select((slot, index) => slot with { Position = index + 1 }).ToArray();
    }

    private void LibraryDeck_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Button { DataContext: DeckListItem item }) return;
        e.Handled = true;
        DeckList.SelectedItem = item;
        var uri = item.SourceUri is null ? null : DeckLinkFileReader.CanonicalUrl(item.SourceUri);
        if (uri is null) { FooterStatusText.Text = "This is a local deck; no PlayGWENT link is available."; return; }
        if (_reviewEvidencePath is not null) { FooterStatusText.Text = "Offline review: PlayGWENT link " + uri; return; }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) { FooterStatusText.Text = "Could not open PlayGWENT: " + exception.Message; }
    }
}
