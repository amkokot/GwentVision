using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class CardDataWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _working, _committing;
    public CardDataUpdateResult? Result { get; private set; }

    public CardDataWindow(CardDataUpdater updater, string operation, string? importPath = null, bool run = true)
    {
        InitializeComponent();
        if (run) Loaded += async (_, _) => await RunAsync(updater, operation, importPath);
        Closing += ClosingUpdate;
        Closed += (_, _) => _cancel.Dispose();
    }

    private async Task RunAsync(CardDataUpdater updater, string operation, string? importPath)
    {
        _working = true;
        try
        {
            CardDataSnapshot? candidate = null;
            if (operation != "Restore")
            {
                StatusText.Text = operation == "Import" ? "Validating the imported catalogue…" : "Checking gwent.one and current PlayGWENT values…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("GwentVision/0.2.47");
                candidate = await Task.Run(() => operation == "Import"
                    ? CardDataUpdater.ImportAsync(importPath!, timeout.Token)
                    : CardDataUpdater.DownloadAsync(client, timeout.Token));
            }
            _cancel.Token.ThrowIfCancellationRequested();
            StatusText.Text = "Validating and saving a recovery copy…";
            _committing = true; CloseButton.IsEnabled = false;
            Result = await Task.Run(() => operation == "Restore" ? updater.RestorePrevious() : updater.Install(candidate!));
            if (operation == "Update")
            {
                StatusText.Text = "Checking previous-patch changes…";
                _committing = false; CloseButton.IsEnabled = true;
                // Optional history cannot turn a successfully installed catalogue into an apparent failure.
                try
                {
                    using var historyTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                    historyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    using var historyClient = new HttpClient();
                    var added = await Task.Run(() => CardBalanceChanges.EnsureBaselineAsync(updater.Path, historyClient, historyTimeout.Token));
                    if (added) Result = Result with { ComparisonAdded = true, Changes = CardBalanceChanges.Load(updater.Path).Cards.Select(c => c.Summary).ToArray() };
                }
                catch (Exception) { /* Offline history is optional; next Update retries, and current data remains valid. */ }
            }
        }
        catch (OperationCanceledException)
        { StatusText.Text = _cancel.IsCancellationRequested ? "Cancelled. Current data was kept." : "The request timed out. Current data was kept; try again later or import a catalogue."; }
        catch (Exception error)
        { StatusText.Text = "Update stopped. Current data was kept.\n" + error.Message; }
        finally
        {
            _working = _committing = false;
            Progress.Visibility = Visibility.Collapsed; CloseButton.IsEnabled = true; CloseButton.Content = "Close";
        }
        if (Result is { Changed: true } or { ComparisonAdded: true }) { DialogResult = true; return; }
        if (Result is not null) StatusText.Text = $"Already up to date · {Result.Version} · {Result.Source}. No reload needed.";
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    { if (_working) { _cancel.Cancel(); CloseButton.IsEnabled = false; } else Close(); }
    private void ClosingUpdate(object? sender, CancelEventArgs e)
    { if (_working) { e.Cancel = true; if (!_committing) _cancel.Cancel(); } }
}
