using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using Microsoft.Win32;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _cardDataBusy, _cardReloadRequired;
    private int _deckLoads;
    private CurrentCardValues? _currentCardValues;
    private static string CardDataPath => Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json");
    private CurrentCardValues CurrentValues => _currentCardValues ??= new(GwentOneCardCatalog.Load(CardDataPath));
    private DeckDefinition CurrentDeck(DeckDefinition deck) => _reviewEvidencePath is null ? CurrentValues.Deck(deck) : deck;
    private CardDefinition CurrentCard(CardDefinition card) => _reviewEvidencePath is null ? CurrentValues.Card(card) : card;

    private void RefreshCardDataStatus()
    {
        try
        {
            var updater = new CardDataUpdater(CardDataPath); var current = updater.Current();
            CardDataStatus.Text = current is null ? "No public catalogue installed." : $"{current.Source} · {current.Version} · {current.Cards.Count:N0} cards & abilities";
            RestoreCardDataButton.IsEnabled = File.Exists(updater.BackupPath);
            var changes = CardBalanceChanges.Load(CardDataPath);
            if (changes.Available)
            {
                CardDataChanges.Text = string.Join(Environment.NewLine, changes.Cards.Select(c => c.Summary));
                CardDataChangesPanel.Visibility = Visibility.Visible;
                CardDataChangesPanel.Header = $"{changes.Cards.Count} changes · {changes.FromVersion} → {changes.ToVersion}";
            }
        }
        catch (Exception error) { CardDataStatus.Text = "Catalogue unavailable: " + error.Message; }
    }

    private string? CardUpdateBlockReason() => _reviewEvidencePath is not null ? "Card updates are disabled in offline review." :
        _cardDataBusy || _windowClosing ? "Wait for the current card-data operation." :
        _startupTask is { IsCompleted: false } || _visionLoadTask is { IsCompleted: false } ? "Wait for startup to finish." :
        _analysisTransition || _diagnosticSession?.IsRunning == true || _visionWorker is not null || _flushInProgress ? "Stop live analysis (■) before updating card data." :
        _libraryWindow is not null || _builderWindow is not null ? "Save your work and close the editor before updating card data." :
        _libraryTransferBusy || _autoEncounterBusy || _autoEncounterSave is { IsCompleted: false } || _editingLibraryDeck ||
        _deckLoads > 0 || !SyncDecksButton.IsEnabled || !UseUserDeckButton.IsEnabled ? "Wait for the deck operation to finish." : null;

    private async void CardData_OnClick(object sender, RoutedEventArgs e)
    {
        if (CardUpdateBlockReason() is { } blocked) { CardDataStatus.Text = blocked; return; }
        var operation = (sender as FrameworkElement)?.Tag as string ?? "Update";
        string? importPath = null;
        if (operation == "Import")
        {
            var picker = new OpenFileDialog { Title = "Import full English gwent.one card data", Filter = "gwent.one JSON (*.json)|*.json" };
            if (picker.ShowDialog(this) != true) return;
            importPath = picker.FileName;
        }
        if (operation == "Restore" && MessageBox.Show(this, "Restore the previous card catalogue and reload Gwent Vision? Saved decks and history will not change.",
                "Restore card data", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Modal ownership stops new actions; the guards above also exclude work already in flight.
        _cardDataBusy = true; CancelLibraryPreview(); RefreshAnalysisButton();
        try
        {
            var dialog = new CardDataWindow(new CardDataUpdater(CardDataPath), operation, importPath) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Result is { } result && (result.Changed || result.ComparisonAdded || _cardReloadRequired))
            {
                _cardReloadRequired = true;
                IsEnabled = false;
                await ReloadAfterCardUpdateAsync(result);
            }
            else if (dialog.Result is { } unchanged) CardDataStatus.Text = $"Checked current sources · {unchanged.Version} · {unchanged.Source} · already up to date.";
        }
        catch (Exception error)
        { CardDataStatus.Text = (_cardReloadRequired ? "Data installed. Close and reopen Gwent Vision to finish reloading. " : "Card update unavailable. ") + error.Message; }
        finally { _cardDataBusy = false; if (!_windowClosing) IsEnabled = true; RefreshAnalysisButton(); }
    }

    private async Task ReloadAfterCardUpdateAsync(CardDataUpdateResult result)
    {
        // Finish the stopped session with its original in-memory values before creating fresh consumers.
        // No GWENT process interaction, external executable, or user-managed restart is required.
        if (_diagnosticSession is not null)
        {
            await _diagnosticSession.DisposeAsync(); await StopVisionAsync();
            PersistCurrentMatch(); _diagnosticSession = null;
        }
        if (_valueWriter is not null) await _valueWriter.Idle;
        var next = CreateCardDataReloadWindow(result);
        Application.Current.MainWindow = next;
        next.Show(); next.Activate();
        next.RefreshCardDataStatus(); next.CardDataStatus.Text += " · updated";
        _cardDataBusy = false;
        Close();
    }

    private MainWindow CreateCardDataReloadWindow(CardDataUpdateResult result)
    {
        var next = new MainWindow
        {
            Left = Left, Top = Top, Width = Width, Height = Height, WindowState = WindowState,
            WindowStyle = WindowStyle, ResizeMode = ResizeMode, Topmost = Topmost,
            _expandedWorkspace = _expandedWorkspace, _fullScreen = _fullScreen, _workspaceZoom = _workspaceZoom,
            _compactBounds = _compactBounds, _compactWindowState = _compactWindowState,
            _windowedBounds = _windowedBounds, _windowedState = _windowedState,
        };
        next.TopmostCheckBox.IsChecked = Topmost;
        next.ShowHoverThreats.IsChecked = ShowHoverThreats.IsChecked;
        if (_confirmedOpponentDeck is { } pinned) next._confirmedOpponentDeck = next.CurrentDeck(pinned);
        next.UpdateWorkspaceLayout(); next.ShowPage(UiPage.Settings);
        next.CardDataChanges.Text = string.Join(Environment.NewLine, result.Changes);
        next.CardDataChangesPanel.Visibility = result.Changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        next.CardDataChangesPanel.Header = $"{result.Changes.Count:N0} changed cards / abilities";
        return next;
    }
}
