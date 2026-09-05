using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _autoEncounterBusy;
    private Task? _autoEncounterSave;
    private string? _autoEncounterKey;
    private DateTimeOffset _autoEncounterRetryAfter;
    private readonly HashSet<string> _encounterPrompts = [];
    private bool _restoringReviewPreference;
    private LearnedOpponentDeck? _pendingEncounterReview;
    private string? _autoEncounterError;
    private void ReviewNewDecks_OnChanged(object sender, RoutedEventArgs e)
    { if (_libraryReady && !_restoringReviewPreference) SaveUserSettings(); }

    private async void TryAutoCacheOpponent()
    {
        if (_reviewEvidencePath is not null || _autoEncounterBusy || !_libraryReady ||
            (_observedPostMatchMmr?.RatingAfter is null && _observedPostMatchRank is null) || _opponentTracker.DeckBuildingObservations.Count == 0 ||
            DateTimeOffset.UtcNow < _autoEncounterRetryAfter) return;
        var encounter = CurrentEncounter();
        var key = encounter.SessionId + "/" + (_observedPostMatchMmr?.ToString() ?? _observedPostMatchRank?.ToString());
        if (_autoEncounterKey == key) return;
        _autoEncounterBusy = true;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoEncounterSave = completed.Task;
        var library = _cachedDecks.ToArray();
        var suggestions = _projectionEncounter == encounter.SessionId
            ? _lastProjection?.Slots.Where(s => s.Card is not null).GroupBy(s => s.Card!.Id)
                .Select(g => new GwentCompanion.Core.Domain.DeckCard(g.First().Card!, g.Count())).ToArray() ?? [] : [];
        try
        {
            // Raw evidence first. A library-write failure can safely retry the same match ID.
            var captured = await Task.Run(() =>
            {
                var memory = OpponentDeckMemoryStore.Load(OpponentMemoryPath);
                var result = OpponentEncounterPipeline.Capture(memory, encounter, library);
                if (result.NewEncounter && result.Record.LibraryFingerprint is null)
                    result = result with { Record = memory.Suggest(result.Record.Id, suggestions) };
                memory.Save(OpponentMemoryPath);
                return (Memory: memory, Result: result);
            });
            _opponentMemory = captured.Memory; _memoryRenderKey = null;
            if (captured.Result.LibraryObservation is { } observation) MergeLibrary([observation]);
            _autoEncounterKey = key;
            _autoEncounterError = null;
            if (_windowClosing) return;
            RefreshDeckList(); RenderOpponentMemories();
            if (CurrentEncounterId != encounter.SessionId) return;
            var record = captured.Result.Record;
            FooterStatusText.Text = $"Opponent saved · {record.Name} · faced {record.EncounterCount}× · " +
                (record.LibraryFingerprint is null ? "incomplete; review available" : "strong library association (unseen cards unverified)");
            if (record.NeedsReview && ReviewNewDecksChoice.IsChecked == true && _encounterPrompts.Add(encounter.SessionId)) OpenEncounterReview(record);
        }
        catch (Exception error)
        {
            _autoEncounterError = error.Message;
            _autoEncounterRetryAfter = DateTimeOffset.UtcNow.AddSeconds(10);
            FooterStatusText.Text = "Opponent auto-save needs attention: " + error.Message + " · raw memory is preserved if already written.";
        }
        finally { _autoEncounterBusy = false; completed.TrySetResult(); TryOpenPendingEncounterReview(); }
    }

    private void EditLearnedMemory_OnClick(object sender, RoutedEventArgs e)
    {
        if (OpponentMemoryList.SelectedItem is LearnedDeckMatch match) OpenEncounterReview(match.Deck);
    }
    private void OpenEncounterReview(LearnedOpponentDeck record)
    {
        if (_candidateCatalog is null || _reviewEvidencePath is not null || _windowClosing) return;
        if (_builderWindow?.IsReviewingOpponent(record.Id) == true) { _builderWindow.Activate(); return; }
        _pendingEncounterReview = record;
        if (_builderWindow is not null)
        {
            _builderWindow.Activate();
            FooterStatusText.Text = "Opponent review is ready; close the current editor when finished. Unsaved edits are preserved.";
        }
        TryOpenPendingEncounterReview();
    }

    private void TryOpenPendingEncounterReview()
    {
        if (_pendingEncounterReview is not { } record || _candidateCatalog is null || !_libraryReady ||
            _windowClosing || _reviewEvidencePath is not null || _analysisTransition || _autoEncounterBusy ||
            _cardDataBusy || _cardReloadRequired || _libraryTransferBusy || _builderWindow is not null || _libraryWindow is not null) return;
        try
        {
            var window = new OpponentReviewWindow(record, _candidateCatalog, _library, LibraryPath, RefreshLibraryReferences, (draft, needsReview) =>
            {
                if (_autoEncounterBusy) throw new InvalidOperationException("Auto-save is finishing; try Save again in a moment.");
                var memory = OpponentDeckMemoryStore.Load(OpponentMemoryPath);
                var current = memory.Records.Single(r => r.Id == record.Id);
                memory.Review(record.Id, draft.Name, OpponentReviewDraft.Cards(current, draft), needsReview, OpponentReviewDraft.Header(draft, _candidateCatalog));
                memory.Save(OpponentMemoryPath); _opponentMemory = memory; _memoryRenderKey = null;
                _library.RemoveInferredEncounters(current.Encounters.Select(e => e.SessionId)); _library.Save(LibraryPath);
                _cachedDecks = _library.Decks.Select(CurrentDeck).ToArray();
                RefreshDeckList(); RenderOpponentMemories(); RenderDeckProjection();
            }, c => CardArt(c), SaveObservedDraft) { Owner = this };
            _builderWindow = window; _pendingEncounterReview = null;
            SyncDecksButton.IsEnabled = false; RefreshAnalysisButton();
            window.Closed += (_, _) =>
            {
                _builderWindow = null; RefreshAnalysisButton();
                SyncDecksButton.IsEnabled = _diagnosticSession?.IsRunning != true && !_analysisTransition;
                TryOpenPendingEncounterReview();
            };
            window.Show(); // Modeless and non-topmost; original auto-saved evidence survives closing.
        }
        catch (Exception error)
        {
            _builderWindow = null; RefreshAnalysisButton();
            FooterStatusText.Text = "Opponent review could not open: " + error.Message + ". Saved memory is intact.";
        }
    }
}
