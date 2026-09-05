using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Windows;
using Microsoft.Win32;

namespace GwentCompanion.App;

public partial class DeckLibraryWindow : Window
{
    private readonly DeckLibrary _library;
    private readonly string _libraryPath, _cache, _scans;
    private readonly IReadOnlyList<CardDefinition> _catalog;
    private readonly Action _changed;
    private readonly DeckScanDraft _draft = new();
    private DeckDefinition? _editing;
    private CancellationTokenSource? _busy;
    private Task? _work;
    private bool _scanning, _closing;
    private string? _lastScanDirectory;
    private string _saveOccurrenceId = Guid.NewGuid().ToString("N");
    private bool _selectingHeader = true;
    private LeaderChoice[] _leaderChoices = [];
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    public DeckLibraryWindow(DeckLibrary library, string libraryPath, string cache, IReadOnlyList<CardDefinition> catalog,
        DeckDefinition? editing, Action changed, string scans)
    {
        _library = library; _libraryPath = libraryPath; _cache = cache; _catalog = catalog; _changed = changed; _scans = scans;
        if (editing is not null) editing = new CurrentCardValues(catalog).Deck(editing);
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(660, area.Width - 30); Height = Math.Min(730, area.Height - 40);
        Left = Math.Max(area.Left, area.Right - Width - 15); Top = area.Top + 20;
        _leaderChoices = GwentOneCardCatalog.StartingLeaders(catalog).Select(card => new LeaderChoice(card)).ToArray();
        FactionBox.ItemsSource = new[] { new FactionChoice(null) }.Concat(_leaderChoices.Select(item => item.Card.Faction)
            .Distinct().Select(faction => new FactionChoice(faction))).ToArray();
        FactionBox.SelectedIndex = 0;
        LeaderBox.ItemsSource = _leaderChoices;
        StratagemBox.ItemsSource = new[] { new StratagemChoice(null) }.Concat(catalog.Where(card => card.Kind == CardKind.Stratagem)
            .OrderBy(card => card.Name).Select(card => new StratagemChoice(card))).ToArray();
        StratagemBox.SelectedIndex = 0;
        _editing = editing;
        if (editing is not null)
        {
            NameBox.Text = editing.Name; _draft.Load(editing.Cards);
            SelectLeader(_leaderChoices.FirstOrDefault(item =>
                DeckSearchCatalog.Normalize(item.Card.Name) == DeckSearchCatalog.Normalize(editing.Leader))?.Card);
            _draft.SetLeader((LeaderBox.SelectedItem as LeaderChoice)?.Card);
            if (editing.Stratagem is not null)
            {
                _draft.SetStratagem(editing.Stratagem);
                StratagemBox.SelectedItem = StratagemBox.Items.Cast<StratagemChoice>().FirstOrDefault(item => item.Card?.Id == editing.Stratagem.Id);
            }
            var entry = library.Find(editing.Id);
            SaveStatus.Text = $"{entry?.Aliases.Length ?? 1} source identities merged. Editing cards saves a new variant; Rename only keeps the list.\n" +
                string.Join("\n", entry?.Sources ?? []);
        }
        RenameButton.IsEnabled = editing is not null;
        _selectingHeader = false;
        Render(); Closing += WindowClosing;
    }

    private void Render()
    {
        if (DraftList is null) return;
        var selected = (DraftList.SelectedItem as CardRow)?.Card.Id;
        DraftList.ItemsSource = _draft.Cards.Select(item => new CardRow(item.Card, item.Count)).ToArray();
        DraftList.SelectedItem = DraftList.Items.Cast<CardRow>().FirstOrDefault(item => item.Card.Id == selected);
        var bonus = (LeaderBox.SelectedItem as LeaderChoice)?.Card.Provision;
        DraftStatus.Text = $"{_draft.CardCount} cards · {_draft.Cards.Sum(item => item.Count * item.Card.Provision)} / {(bonus is null ? "?" : (150 + bonus).ToString())} provisions · " +
            $"{_draft.Cards.Where(item => item.Card.Kind == CardKind.Unit).Sum(item => item.Count)} units";
        SearchCards();
    }
    private void SearchCards()
    {
        if (CardResults is null) return;
        var terms = DeckSearchCatalog.Terms(CardSearch.Text);
        var faction = (LeaderBox.SelectedItem as LeaderChoice)?.Card.Faction;
        CardResults.ItemsSource = _catalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(card => faction is null || FactionCompatibility.IsPlayableBy(card, faction))
            .Where(card => DeckSearchCatalog.Matches(DeckSearchCatalog.Normalize(card.Name), terms))
            .OrderBy(card => card.Name).Take(100).Select(card => new CardRow(card, 0)).ToArray();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => SearchCards();
    private void FilterLeaders(CardDefinition? keep)
    {
        var faction = (FactionBox.SelectedItem as FactionChoice)?.Faction;
        LeaderBox.ItemsSource = _leaderChoices.Where(item => faction is null || item.Card.Faction == faction).ToArray();
        LeaderBox.SelectedItem = LeaderBox.Items.Cast<LeaderChoice>().FirstOrDefault(item => item.Card.Id == keep?.Id);
    }
    private void SelectLeader(CardDefinition? leader)
    {
        // Caller suppresses selection events; a detected/saved leader selects its matching faction too.
        if (leader is not null && (LeaderBox.SelectedItem as LeaderChoice)?.Card.Id == leader.Id &&
            (FactionBox.SelectedItem as FactionChoice)?.Faction == leader.Faction) return;
        if (leader is not null)
            FactionBox.SelectedItem = FactionBox.Items.Cast<FactionChoice>().FirstOrDefault(item => item.Faction == leader.Faction);
        FilterLeaders(leader);
    }
    private void FactionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingHeader) return;
        var previous = (LeaderBox.SelectedItem as LeaderChoice)?.Card;
        _selectingHeader = true;
        try { FilterLeaders(previous); }
        finally { _selectingHeader = false; }
        if (previous is not null && LeaderBox.SelectedItem is null) _draft.ClearLeader();
        Render();
    }
    private void LeaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingHeader) _draft.SetLeader((LeaderBox.SelectedItem as LeaderChoice)?.Card);
        Render();
    }
    private void StratagemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingHeader) _draft.SetStratagem((StratagemBox.SelectedItem as StratagemChoice)?.Card);
    }
    private void AddClicked(object sender, RoutedEventArgs e) { if (CardResults.SelectedItem is CardRow row) ChangeCount(row.Card, 1); }
    private void CopyClicked(object sender, RoutedEventArgs e) { if (DraftList.SelectedItem is CardRow row) ChangeCount(row.Card, 1); }
    private void RemoveClicked(object sender, RoutedEventArgs e) { if (DraftList.SelectedItem is CardRow row) ChangeCount(row.Card, -1); }
    private void ChangeCount(CardDefinition card, int delta)
    {
        var count = _draft.Cards.FirstOrDefault(item => item.Card.Id == card.Id)?.Count ?? 0;
        if (count + delta > (card.IsGold ? 1 : 2)) { SaveStatus.Text = "Copy limit: one gold, two bronze."; return; }
        _draft.SetCount(card, Math.Max(0, count + delta)); Render();
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.CardCount > 0 && MessageBox.Show(this, "Clear this unsaved draft? Cached decks will not be removed.", "New draft", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _editing = null; _lastScanDirectory = null; _saveOccurrenceId = Guid.NewGuid().ToString("N"); _draft.Reset(); NameBox.Text = "My imported deck"; RenameButton.IsEnabled = false; SaveStatus.Text = "New draft; no cached decks changed."; Render();
        _selectingHeader = true; FactionBox.SelectedIndex = 0; FilterLeaders(null); StratagemBox.SelectedIndex = 0; _selectingHeader = false;
        Render();
    }
    private void RenameClicked(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        try { _editing = _library.Rename(_editing.Id, NameBox.Text); _library.Save(_libraryPath); _changed(); SaveStatus.Text = "Renamed. All source links retained."; }
        catch (Exception exception) { SaveStatus.Text = exception.Message; }
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LeaderBox.SelectedItem is not LeaderChoice leader) throw new InvalidDataException("Choose the starting leader ability first.");
            var at = DateTimeOffset.Now;
            var deck = DeckOccurrences.Record(_draft.Build(NameBox.Text, leader.Card),
                "library-" + (_lastScanDirectory ?? _saveOccurrenceId) + "-" + DeckPatchMetadata.Current(at).Label,
                "LibrarySave", at, _lastScanDirectory is null ? "Reviewed library save" : "Confirmed deck-builder scan");
            var warnings = new List<string>();
            if (deck.ProvisionTotal > 150 + deck.LeaderProvisionBonus) warnings.Add("Over provision allowance");
            if (deck.UnitCount < 13) warnings.Add("Fewer than 13 units");
            if (deck.Cards.Any(item => !FactionCompatibility.IsPlayableBy(item.Card, deck.Faction))) warnings.Add("Off-faction cards present");
            if (deck.Stratagem is not null && !FactionCompatibility.IsPlayableBy(deck.Stratagem, deck.Faction)) warnings.Add("Off-faction stratagem selected");
            if (warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", warnings) + "\nSave this reviewed list anyway?", "Check deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var existing = _library.Records.FirstOrDefault(item => item.Fingerprint == DeckLibrary.Fingerprint(deck));
            if (existing is not null && existing.Deck.Name != deck.Name && MessageBox.Show(this,
                $"This exact deck already exists as '{existing.Deck.Name}'. Merge and use the name '{deck.Name}'?", "Duplicate deck", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var result = _library.Merge([deck]);
            var saved = _library.Find(deck.Id)!.Deck;
            _editing = _library.Rename(saved.Id, deck.Name); _library.Save(_libraryPath); _changed();
            RenameButton.IsEnabled = true;
            SaveStatus.Text = (result.Added > 0 ? "Saved to the cache. " : "Duplicate merged; history retained. ") + DeckOccurrences.Describe(_editing.Occurrences);
            if (_lastScanDirectory is { } scan && Directory.Exists(scan))
            {
                try
                {
                    File.WriteAllText(Path.Combine(scan, "confirmed-deck.json"), JsonSerializer.Serialize(new
                    { Label = "User-confirmed composition, not per-frame card labels", SavedAt = DateTimeOffset.Now, _editing.Id, _editing.Name,
                        _editing.Faction, _editing.Leader, Stratagem = _editing.Stratagem is null ? null : new { _editing.Stratagem.Id, _editing.Stratagem.Name },
                        Cards = _editing.Cards.Select(item => new { item.Card.Id, item.Card.Name, item.Count }) }));
                }
                catch (IOException exception) { SaveStatus.Text += " Training reference could not be saved: " + exception.Message; }
            }
        }
        catch (Exception exception) { SaveStatus.Text = "Not saved: " + exception.Message; }
    }

    private void FileClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Deck spreadsheets / text|*.xlsx;*.csv;*.tsv;*.txt", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(false);
        _work = ReadAndImportAsync(picker.FileName, _busy!.Token);
    }
    private async Task ReadAndImportAsync(string path, CancellationToken token)
    {
        try
        {
            ImportStatus.Text = "Reading spreadsheet…";
            var links = await Task.Run(() => DeckLinkFileReader.Read(path), token);
            token.ThrowIfCancellationRequested();
            if (links.Count == 0) { ImportStatus.Text = "No public PlayGWENT deck or guide links found."; EndBusy(); return; }
            await ImportAsync(links, token);
        }
        catch (OperationCanceledException) { EndBusy(); ImportStatus.Text = "Canceled before import."; }
        catch (Exception exception) { EndBusy(); ImportStatus.Text = exception.Message; }
    }
    private void LinksClicked(object sender, RoutedEventArgs e) => StartImport(DeckLinkFileReader.FromText(LinksBox.Text));
    private void RetryClicked(object sender, RoutedEventArgs e) => StartImport(_library.ImportedLinks.ToArray());
    private void CancelClicked(object sender, RoutedEventArgs e) => _busy?.Cancel();
    private void StartImport(IReadOnlyList<DeckIndexEntry> links)
    {
        if (_busy is not null) return;
        if (links.Count == 0) { ImportStatus.Text = "No public PlayGWENT deck or guide links found. Export Google Sheets as .xlsx."; return; }
        SetBusy(false); _work = ImportAsync(links, _busy!.Token);
    }
    private async Task ImportAsync(IReadOnlyList<DeckIndexEntry> links, CancellationToken token)
    {
        var service = new PlayGwentDeckCacheService();
        try
        {
            _library.AddLinks(links); _library.Save(_libraryPath);
            var progress = new Progress<DeckCacheProgress>(value => ImportStatus.Text = $"{value.Completed}/{value.Requested} checked · {value.Loaded} valid · {value.Expired} unavailable · {value.Failed} failed");
            var result = await service.SyncAsync(links, _cache, int.MaxValue, progress, token);
            var merge = _library.Merge(result.Decks); _library.Save(_libraryPath); _changed();
            ImportStatus.Text = $"{merge.Added} new decks · {merge.Merged} duplicate imports merged · {result.Expired} unavailable · {result.Errors.Count} failed";
            ImportLog.Text = string.Join("\n", result.Errors.Concat(links.Where(entry => result.Decks.All(deck => deck.SourceUri != entry.DeckUri))
                .Select(entry => "Unavailable / not imported: " + entry.DeckUri)));
        }
        catch (OperationCanceledException)
        {
            // Completed payloads are atomic files, so cancellation can safely recover successes.
            try
            {
                var merge = _library.Merge(service.LoadCached(links, _cache, int.MaxValue)); _library.Save(_libraryPath); _changed();
                ImportStatus.Text = $"Canceled. Retained {merge.Added} new decks; {merge.Merged} duplicates merged. Retry saved imports to continue.";
            }
            catch (Exception exception) { ImportStatus.Text = "Canceled; payload files retained, but library update failed: " + exception.Message; }
        }
        catch (Exception exception) { ImportStatus.Text = "Import stopped: " + exception.Message; }
        finally { EndBusy(); }
    }
    private void SetBusy(bool scanning)
    {
        _busy = new(); _scanning = scanning;
        FileButton.IsEnabled = LinkButton.IsEnabled = RetryButton.IsEnabled = SaveButton.IsEnabled = RenameButton.IsEnabled = ResetButton.IsEnabled = PanelBox.IsEnabled = false;
        ScanButton.IsEnabled = scanning; ScanButton.Content = scanning ? "Stop scan / review" : "Start deck-builder scan"; CancelButton.IsEnabled = !scanning;
    }
    private void EndBusy()
    {
        _busy?.Dispose(); _busy = null; _scanning = false;
        FileButton.IsEnabled = LinkButton.IsEnabled = RetryButton.IsEnabled = SaveButton.IsEnabled = ResetButton.IsEnabled = PanelBox.IsEnabled = ScanButton.IsEnabled = true;
        RenameButton.IsEnabled = _editing is not null; ScanButton.Content = "Start deck-builder scan"; CancelButton.IsEnabled = false;
    }
    private async void ScanClicked(object sender, RoutedEventArgs e)
    {
        if (_scanning) { _busy?.Cancel(); if (_work is not null) await _work; return; }
        if (_busy is not null) return;
        SetBusy(true); _draft.BreakSequence();
        var right = PanelBox.SelectedIndex == 1;
        _work = ScanAsync(right, _busy!.Token); await _work;
    }
    private async Task ScanAsync(bool right, CancellationToken token)
    {
        var directory = Path.Combine(_scans, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        _lastScanDirectory = directory;
        try
        {
            await Task.Run(async () =>
            {
                using var capture = new Win32FrameCapture(); using var scanner = new DeckBuilderScanner(_catalog);
                var windows = new GwentWindowService(); var saved = 0; string previous = "";
                var region = right ? DeckBuilderScanner.RightPanel : DeckBuilderScanner.LeftPanel;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(650));
                while (await timer.WaitForNextTickAsync(token))
                {
                    var window = windows.Find();
                    if (window is null || window.IsMinimized || GetForegroundWindow() != window.Handle)
                    {
                        await Dispatcher.InvokeAsync(() => { _draft.BreakSequence(); ScanStatus.Text = "Scan waiting: switch to GWENT with your deck open. Other windows are not captured."; });
                        continue;
                    }
                    var bitmap = capture.Capture(window);
                    var page = await scanner.ReadAsync(BitmapFrameAdapter.ToPixelFrame(bitmap), region);
                    token.ThrowIfCancellationRequested();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var added = _draft.Observe(page.Cards); if (added > 0) Render();
                        _draft.ObserveHeader(page.Leader, page.Stratagem);
                        _selectingHeader = true;
                        if (_draft.Leader is { } leader) SelectLeader(leader);
                        if (_draft.Stratagem is { } stratagem) StratagemBox.SelectedItem = StratagemBox.Items.Cast<StratagemChoice>().FirstOrDefault(item => item.Card?.Id == stratagem.Id);
                        _selectingHeader = false;
                        ScanStatus.Text = $"{page.Cards.Count} card rows on this page · {_draft.CardCount} draft cards. Pause briefly after scrolling; review when finished." +
                            (page.NameCorrections?.Count > 0 ? $" Name correction to verify: {string.Join(", ", page.NameCorrections.Select(item => item.MatchedName))}." : "") +
                            (page.UnresolvedRows?.Count > 0 ? $" {page.UnresolvedRows.Count} unreadable name row(s); pause or check card search. Hover this message for the text." : "");
                        var unresolved = page.UnresolvedRows?.Select(line => line.Text).ToArray() ?? [];
                        ScanStatus.ToolTip = unresolved.Length == 0 ? null : string.Join(" · ", unresolved.Take(2)) + (unresolved.Length > 2 ? $" · +{unresolved.Length - 2} more" : "");
                        System.Windows.Automation.AutomationProperties.SetHelpText(ScanStatus, string.Join("\n", unresolved));
                    });
                    // Keep changed unreadable rows too, not only pages that already recognized new cards.
                    var signature = string.Join(';', page.Cards.Select(item => item.Card.Id + ":" + item.Count).Order()) + "|" +
                        string.Join(';', page.Lines.Select(line => line.Text + ":" + Math.Round(line.Region.Top, 2)));
                    if (page.Lines.Count > 0 && signature != previous && saved < 100)
                    {
                        Directory.CreateDirectory(directory); saved++;
                        var crop = new CroppedBitmap(bitmap, new Int32Rect(region.PixelLeft(bitmap.PixelWidth), region.PixelTop(bitmap.PixelHeight),
                            region.PixelRight(bitmap.PixelWidth) - region.PixelLeft(bitmap.PixelWidth), region.PixelBottom(bitmap.PixelHeight) - region.PixelTop(bitmap.PixelHeight)));
                        var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; encoder.Frames.Add(BitmapFrame.Create(crop));
                        using (var stream = File.Create(Path.Combine(directory, $"page-{saved:000}.jpg"))) encoder.Save(stream);
                        await File.WriteAllTextAsync(Path.Combine(directory, $"page-{saved:000}.json"), JsonSerializer.Serialize(new
                            { Region = region, SourceWidth = bitmap.PixelWidth, SourceHeight = bitmap.PixelHeight,
                                Cards = page.Cards.Select(item => new { item.Card.Id, item.Card.Name, item.Count }), page.Lines, page.Quantities, page.NameCorrections, page.UnresolvedRows,
                                Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name,
                                Label = "Unverified OCR draft; not training ground truth" }), token);
                    }
                    previous = signature;
                }
            }, token);
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Scan stopped. Review names, quantities and leader; add missing cards, then Confirm / save. Draft has not been cached."; }
        catch (Exception exception) { ScanStatus.Text = "Scan stopped: " + exception.Message; }
        finally { EndBusy(); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busy is null) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _busy.Cancel();
        if (_work is not null) await _work;
        _closing = false; Close();
    }
    private sealed record LeaderChoice(CardDefinition Card) { public string Label => $"{Card.Name} (+{Card.Provision}p)"; }
    private sealed record FactionChoice(string? Faction) { public string Label => Faction ?? "All factions"; }
    private sealed record StratagemChoice(CardDefinition? Card) { public string Label => Card?.Name ?? "Unknown / not recorded"; }
    private sealed record CardRow(CardDefinition Card, int Count) { public string Label => $"{Card.Provision}p · {Card.Name}" + (Count > 0 ? $" ×{Count}" : ""); }
}
