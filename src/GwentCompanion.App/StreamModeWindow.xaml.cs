using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Platform.Windows.Vision;
using Microsoft.Win32;

namespace GwentCompanion.App;

public partial class StreamModeWindow : Window
{
    private readonly string _dataRoot;
    private readonly string _streamCache;
    private readonly string _streamGames;
    private readonly string _streamValidation;
    private CancellationTokenSource? _scanCancellation;
    public ObservableCollection<StreamJob> Jobs { get; } = [];

    public StreamModeWindow(string dataRoot, string? localStorageRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        var local = localStorageRoot is null ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GwentVision") : Path.GetFullPath(localStorageRoot);
        _streamCache = Path.Combine(local, "stream-cache");
        _streamGames = Path.Combine(local, "stream-games");
        _streamValidation = Path.Combine(local, "stream-validation");
        InitializeComponent(); DataContext = this;
        LoadQueue(); RefreshSummary();
    }

    public static int RunSmoke()
    {
        var root = Path.Combine(Path.GetTempPath(), "gv-stream-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var window = new StreamModeWindow(AppContext.BaseDirectory, root);
            window.SourceText.Text = "https://youtu.be/dQw4w9WgXcQ";
            window.AddLink_OnClick(window, new RoutedEventArgs());
            window.Show();
            window.JobList.UpdateLayout();
            if (window.Jobs.Count != 1 || window.ScanButton.IsEnabled != true ||
                window.JobList.Items.Count != 1) throw new InvalidOperationException(
                    $"Stream-mode controls did not bind correctly (jobs={window.Jobs.Count}, items={window.JobList.Items.Count}, scan={window.ScanButton.IsEnabled}).");
            window.Close();
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    protected override void OnClosed(EventArgs e) { _scanCancellation?.Cancel(); base.OnClosed(e); }

    private void AddLink_OnClick(object sender, RoutedEventArgs e)
    {
        var sources = StreamSourceIdentity.FromText(SourceText.Text);
        if (sources.Count == 0) { ScanStatus.Text = "That field does not contain a supported HTTP video link."; return; }
        AddSources(sources.Select(source => new StreamLinkEntry(source, "Manual", "", 0, "", SourceText.Text)));
        SourceText.Clear();
    }

    private void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import stream links", Filter =
            "Link sheets (*.xlsx;*.csv;*.tsv;*.txt)|*.xlsx;*.csv;*.tsv;*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var entries = StreamLinkImportReader.Read(dialog.FileName);
            AddSources(entries);
            ScanStatus.Text = entries.Count == 0 ? "No HTTP video links were found in that file." :
                $"Imported {entries.Count} unique link(s) from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception error) { ScanStatus.Text = "Import failed: " + error.Message; }
    }

    private void AddSources(IEnumerable<StreamLinkEntry> entries)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            if (Jobs.Any(job => job.Source.Key == entry.Source.Key)) continue;
            Jobs.Add(new(entry.Source, entry.Label, entry.ImportFile, entry.Sheet, entry.Row)); added++;
        }
        SaveQueue(); RefreshSummary();
        if (added == 0) ScanStatus.Text = "Those streams are already in the queue.";
    }

    private async void Scan_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null || Jobs.Count == 0) return;
        var executable = StreamMediaResolver.FindExecutable(_dataRoot);
        if (executable is null)
        {
            ScanStatus.Text = "Stream resolver unavailable. Place yt-dlp.exe in the GWENT .tools\\yt-dlp folder."; return;
        }
        _scanCancellation = new(); ScanButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try
        {
            ScanStatus.Text = "Preparing the shared card detector…";
            var detector = await Task.Run(() =>
            {
                var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(_dataRoot, "cache", "gwent-one-cards.json"))).ToArray();
                var references = VisionReferenceLibrary.Load(cards, Path.Combine(_dataRoot, "cache"));
                return (Scanner: new StreamArchiveScanner(cards, references,
                        Path.Combine(_dataRoot, "cache", "recognition-features"),
                        typeof(StreamModeWindow).Assembly.GetName().Version?.ToString() ?? "unknown"), Cards: cards);
            }, _scanCancellation.Token);
            var pending = Jobs.Where(job => job.State is not StreamJobState.Complete).ToArray();
            foreach (var (job, index) in pending.Select((job, index) => (job, index)))
            {
                _scanCancellation.Token.ThrowIfCancellationRequested();
                job.Set(StreamJobState.Resolving, "Resolving a low-bandwidth media stream…");
                var media = await StreamMediaResolver.ResolveAsync(executable, job.Source, _scanCancellation.Token);
                job.DisplayName = media.Title ?? job.DisplayName;
                StreamDeckEvidence? sourceDeck = null;
                if (media.DeckUris.Length > 0)
                {
                    job.Set(StreamJobState.Resolving, "Caching the public deck linked by this stream…");
                    var entries = DeckLinkFileReader.FromText(
                        string.Join('\n', media.DeckUris.Select(uri => uri.AbsoluteUri)), "Stream description");
                    var deckResult = await new PlayGwentDeckCacheService().SyncAsync(entries,
                        Path.Combine(_streamCache, "decks"), maximumDecks: 3,
                        cancellationToken: _scanCancellation.Token);
                    var linked = deckResult.Decks.OrderByDescending(deck => deck.CardCount).FirstOrDefault();
                    if (linked is not null) sourceDeck = StreamDeckEvidence.FromDeck(linked);
                }
                var itemProgress = new Progress<StreamScanProgress>(update =>
                {
                    ScanProgress.Value = (index + update.Fraction) / Math.Max(1, pending.Length);
                    ScanStatus.Text = $"{job.DisplayName} · {update.Message}";
                    job.Set(StreamJobState.Scanning, update.Message);
                });
                try
                {
                    var scannedAtUtc = DateTimeOffset.UtcNow;
                    var result = await Task.Run(() => detector.Scanner.ScanAsync(media.MediaUrl, job.Source, media.Title, media.Channel,
                        _streamGames, _streamValidation, sourceDeck, itemProgress, _scanCancellation.Token,
                        media.PublishedAtUtc, scannedAtUtc), _scanCancellation.Token);
                    job.Set(StreamJobState.Complete, result.GamesSaved == 0 ?
                        string.Join(" ", result.Warnings.DefaultIfEmpty("No game records saved.")) :
                        $"Complete · {result.GamesSaved} game(s), {result.ValidationFramesSaved} bounded review frame(s)");
                    SaveSourceStatus(job, result, scannedAtUtc, media.PublishedAtUtc);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { job.Set(StreamJobState.Failed, "Failed · " + error.Message); }
                SaveQueue();
            }
            ScanProgress.Value = 1; ScanStatus.Text = "Queue complete. Compact stream games are ready; source videos were not retained.";
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Scan cancelled. Completed compact game records were kept."; }
        catch (Exception error) { ScanStatus.Text = "Stream scan failed: " + error.Message; }
        finally { _scanCancellation.Dispose(); _scanCancellation = null; ScanButton.IsEnabled = Jobs.Count > 0; CancelButton.IsEnabled = false; SaveQueue(); }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void OpenLink_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StreamJob job) return;
        try { Process.Start(new ProcessStartInfo(job.Source.CanonicalUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ScanStatus.Text = "Could not open the stream: " + error.Message; }
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null || (sender as FrameworkElement)?.Tag is not StreamJob job) return;
        Jobs.Remove(job); SaveQueue(); RefreshSummary();
    }

    private string QueuePath => Path.Combine(_streamCache, "queue.json");

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            var rows = JsonSerializer.Deserialize<StreamQueueRow[]>(File.ReadAllBytes(QueuePath)) ?? [];
            foreach (var row in rows)
                if (StreamSourceIdentity.TryCreate(row.Uri, out var source) && source is not null &&
                    !Jobs.Any(job => job.Source.Key == source.Key))
                    Jobs.Add(new(source, row.Name, row.ImportFile, row.Sheet, row.Row,
                        row.Complete ? StreamJobState.Complete : StreamJobState.Queued,
                        row.Complete ? "Previously scanned" : "Queued"));
        }
        catch (Exception error) { ScanStatus.Text = "The prior stream queue could not be read: " + error.Message; }
    }

    private void SaveQueue()
    {
        try
        {
            Directory.CreateDirectory(_streamCache);
            var rows = Jobs.Select(job => new StreamQueueRow(job.Source.CanonicalUri.AbsoluteUri, job.DisplayName,
                job.ImportFile, job.Sheet, job.Row, job.State == StreamJobState.Complete)).ToArray();
            File.WriteAllBytes(QueuePath, JsonSerializer.SerializeToUtf8Bytes(rows));
        }
        catch (Exception error) { ScanStatus.Text = "Queue save failed: " + error.Message; }
    }

    private void SaveSourceStatus(StreamJob job, StreamScanResult result, DateTimeOffset scannedAtUtc,
        DateTimeOffset? sourcePublishedAtUtc)
    {
        var directory = Path.Combine(_streamCache, "sources", job.Source.SafeKey); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "source.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2, job.Source.Key, Uri = job.Source.CanonicalUri, job.DisplayName,
            ScannedAtUtc = scannedAtUtc.ToUniversalTime(),
            SourcePublishedAtUtc = sourcePublishedAtUtc?.ToUniversalTime(),
            result.DurationSeconds, result.GamesFound, result.GamesSaved,
            result.ValidationFramesSaved, result.Warnings,
        }));
    }

    private void RefreshSummary() { QueueSummary.Text = Jobs.Count == 0 ? "No streams queued" : $"{Jobs.Count} unique stream(s) queued"; ScanButton.IsEnabled = Jobs.Count > 0; }
    private sealed record StreamQueueRow(string Uri, string? Name, string? ImportFile, string? Sheet, int Row, bool Complete);
}

public enum StreamJobState { Queued, Resolving, Scanning, Complete, Failed }

public sealed class StreamJob : INotifyPropertyChanged
{
    private string _displayName;
    private string _status; private StreamJobState _state;
    public StreamJob(StreamSourceIdentity source, string? name, string? importFile, string? sheet, int row,
        StreamJobState state = StreamJobState.Queued, string status = "Queued")
    {
        Source = source; ImportFile = importFile; Sheet = sheet; Row = row; _state = state; _status = status;
        _displayName = string.IsNullOrWhiteSpace(name) || Uri.TryCreate(name, UriKind.Absolute, out _)
            ? source.CanonicalUri.AbsoluteUri : name;
    }
    public StreamSourceIdentity Source { get; }
    public string Provider => Source.Kind.ToString().ToUpperInvariant();
    public string? ImportFile { get; } public string? Sheet { get; } public int Row { get; }
    public string DisplayName { get => _displayName; set { _displayName = value; Changed(); } }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public StreamJobState State { get => _state; private set { _state = value; Changed(); } }
    public void Set(StreamJobState state, string status) { State = state; Status = status; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
