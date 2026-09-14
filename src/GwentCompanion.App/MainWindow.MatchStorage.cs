using System.Diagnostics;
using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Platform.Windows.Data;
using GwentCompanion.Platform.Windows.Security;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private static readonly Uri PublicMmrSite = new("https://amkokot.github.io/GwentVision/");
    private MatchAcquisition? _matchAcquisition;
    private MatchAnalysisWindow? _matchAnalysisWindow;
    private InstallationIdentity? _matchIdentity;
    private InstallationSigningIdentity? _matchSigningIdentity;
    private Task _matchStorageWrite = Task.CompletedTask;
    private DateTimeOffset _lastMatchCheckpoint;
    private int _newMatchRoundVotes;
    private string? _matchStorageError;
    private bool _matchCaptureStopped;
    private Guid? _completedUserHypothesisMatch;
    private string? _matchStorageDirectoryOverride;
    private DataContributionPreferences _dataContributionPreferences = DataContributionPreferences.Initial;
    private DataContributionSyncState _dataContributionSyncState = DataContributionSyncState.Empty;
    private DataServiceConfiguration _dataServiceConfiguration = DataServiceConfiguration.Disabled;
    private bool _restoringDataContributionPreferences;
    private bool _dataUploadRunning;
    private string? _automaticShareDuePeriod;
    private static string PersistentUserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GwentVision");
    private string MatchStorageDirectory => _matchStorageDirectoryOverride ?? Path.Combine(PersistentUserDataDirectory, "match-data");
    private static string DataContributionPreferencesPath => Path.Combine(PersistentUserDataDirectory, "data-contribution.json");
    private static string DataContributionSyncStatePath => Path.Combine(PersistentUserDataDirectory, "data-contribution-state.json");
    private static string DataContributionServerStatePath => Path.Combine(PersistentUserDataDirectory, "data-service-state.json");
    private static string DataServiceOverridePath => Path.Combine(PersistentUserDataDirectory, "data-service.json");

    private async Task InitializeMatchIdentityAsync()
    {
        if (_reviewEvidencePath is not null || _analysisControlTest) return;
        try
        {
            var path = Path.Combine(PersistentUserDataDirectory, "installation.json");
            _matchIdentity = await Task.Run(() => InstallationIdentity.LoadOrCreate(path));
            _dataContributionPreferences = await Task.Run(() => DataContributionPreferences.Load(DataContributionPreferencesPath));
            _dataContributionSyncState = await Task.Run(() => DataContributionSyncState.Load(DataContributionSyncStatePath));
            _dataServiceConfiguration = await Task.Run(() => DataServiceConfiguration.Load(
                File.Exists(DataServiceOverridePath)
                    ? DataServiceOverridePath
                    : Path.Combine(FindDataRoot(), "cache", "data-service.json")));
            ApplyDataContributionPreferences();
            _matchSigningIdentity = await Task.Run(() => InstallationSigningIdentity.LoadOrCreate(
                Path.Combine(PersistentUserDataDirectory, "upload-identity.json"), _matchIdentity.Id));
            RefreshDataContributionButton();
        }
        catch (Exception error)
        {
            _dataContributionPreferences = DataContributionPreferences.Initial with { AutomaticMonthlyUpload = false };
            ApplyDataContributionPreferences();
            MatchStorageStatus.Text = "Match storage or sharing preferences unavailable: " + error.Message;
        }
    }

    private void BeginMatchAcquisition()
    {
        // Review and fixture runs must never contribute to the live dataset.
        if (_reviewEvidencePath is not null || _analysisControlTest) { _matchAcquisition = null; return; }
        if (_matchIdentity is null) { _matchAcquisition = null; return; }
        _matchAcquisition = new(typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown", _matchIdentity.Id, _selectedUserDeck);
        _matchCaptureStopped = false; _completedUserHypothesisMatch = null; _lastMatchCheckpoint = default; _newMatchRoundVotes = 0;
    }

    private void PrepareMatchAcquisition(CardVisionResult result)
    {
        if (_matchAcquisition?.GameDateUtc is null || !result.Screen.FrameGeometrySupported ||
            _gameState.Current.At is { } previousAt && result.SampledAt <= previousAt) return;
        // A repeated opening heading after a later round/result establishes another game
        // during continuous capture. Do not split on a transient menu or a single OCR read.
        var opening = result.Screen.ScreenHeader?.Trim().Equals("ROUND 1", StringComparison.OrdinalIgnoreCase) == true;
        var previousEnded = _gameState.Current.Phase == GwentCompanion.Core.GameState.GamePhase.Ended ||
            _observedPostMatchMmr is not null || _observedPostMatchRank is not null;
        if (!opening || !previousEnded && _gameState.Current.Round?.Value is not > 1) { _newMatchRoundVotes = 0; return; }
        if (++_newMatchRoundVotes < 2) return;
        QueueMatchCheckpoint(force: true, stopped: true);
        _projectionQueue?.Dispose(); _projectionQueue = null; _projectionEncounter = null;
        _userTracker.Reset(); _opponentTracker.Reset(); ResetOpponentKnowledge();
        _opponentEdits.Clear(); _lastProjection = null;
        _playOrigins.Reset(); _gameState.Reset(_manualEncounterId, _selectedUserDeck);
        _lastGameStateUpdate = null;
        BeginMatchAcquisition();
        _matchLifecycle.Reset(); _matchLifecycle.Observe(result.Screen, result.SampledAt);
        _mmrStopGate.Reset();
    }

    private void ObserveMatchAcquisition(CardVisionResult result)
    {
        if (_matchAcquisition is null || _lastGameStateUpdate is null) return;
        var hadStarted = _matchAcquisition.GameDateUtc is not null;
        var trustedSelectedDeck = !_matchAcquisition.UserReferenceRejected;
        _matchAcquisition.Observe(_lastGameStateUpdate, result.Screen,
            MatchAcquisition.Observations(_userTracker.Observations),
            MatchAcquisition.Observations(_opponentTracker.Observations));
        if (trustedSelectedDeck && _matchAcquisition.UserReferenceRejected)
        {
            // Remove the stale player-deck search restriction without adding a new
            // scan. Opponent matchers continue to run first; player recognition uses
            // the same accepted frames and only fills the saved player observation list.
            _visionPipeline?.SetKnownPlayerDeck([]);
            ShowAnalysisStatus("Selected player deck differs from detected cards · saving detected player cards for this match.");
        }
        if (!hadStarted && _matchAcquisition.GameDateUtc is not null)
            ObserveMonthlyDataShareWindow(DateOnly.FromDateTime(result.SampledAt.ToLocalTime().DateTime));
        QueueMatchCheckpoint();
    }

    private void ShowDataContributionConsentIfNeeded()
    {
        if (_dataContributionPreferences.Consent != DataContributionConsent.Undecided || _windowClosing) return;
        var dialog = new DataContributionConsentWindow { Owner = this };
        var accepted = dialog.ShowDialog() == true;
        _dataContributionPreferences = _dataContributionPreferences with
        {
            PublishAnonymousCurve = dialog.PublishAnonymousCurve,
            AutomaticMonthlyUpload = accepted && dialog.AutomaticMonthlyUpload,
        };
        _dataContributionPreferences = _dataContributionPreferences.Decide(accepted, DateTimeOffset.UtcNow);
        SaveDataContributionPreferences();
        ApplyDataContributionPreferences();
        if (_dataContributionPreferences.CanShare && _dataServiceConfiguration.Enabled)
            _ = UpdateCurveVisibilityAsync();
    }

    private void ApplyDataContributionPreferences()
    {
        _restoringDataContributionPreferences = true;
        try
        {
            PublishAnonymousCurveChoice.IsChecked = _dataContributionPreferences.PublishAnonymousCurve;
            AutomaticMonthlyUploadChoice.IsChecked = _dataContributionPreferences.CanShare &&
                _dataContributionPreferences.AutomaticMonthlyUpload;
        }
        finally { _restoringDataContributionPreferences = false; }
        DataContributionConsentStatus.Text = _dataContributionPreferences.Consent switch
        {
            DataContributionConsent.Accepted when _dataServiceConfiguration.Enabled =>
                "Data contribution is allowed. Uploads occur only when you click Push or on the monthly schedule you selected.",
            DataContributionConsent.Accepted => "Data contribution is allowed. This build has no database address, so nothing leaves this computer.",
            DataContributionConsent.Declined => "Data stays local unless you enable monthly sharing or later use Push to database.",
            _ => "Choose whether to contribute when prompted. Nothing has been uploaded.",
        };
        RefreshDataContributionButton();
    }

    private void DataContributionChoice_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringDataContributionPreferences || !IsInitialized ||
            PublishAnonymousCurveChoice is null || AutomaticMonthlyUploadChoice is null) return;
        var automatic = AutomaticMonthlyUploadChoice.IsChecked == true;
        var consent = automatic ? DataContributionConsent.Accepted : _dataContributionPreferences.Consent;
        DateTimeOffset? decidedAt = consent == DataContributionConsent.Undecided ? null :
            _dataContributionPreferences.DecidedAtUtc ?? DateTimeOffset.UtcNow;
        _dataContributionPreferences = _dataContributionPreferences with
        {
            Consent = consent,
            PublishAnonymousCurve = PublishAnonymousCurveChoice.IsChecked == true,
            AutomaticMonthlyUpload = automatic,
            DecidedAtUtc = decidedAt,
        };
        SaveDataContributionPreferences();
        ApplyDataContributionPreferences();
        if (_dataContributionPreferences.CanShare && _dataServiceConfiguration.Enabled)
            _ = UpdateCurveVisibilityAsync();
    }

    private void SaveDataContributionPreferences()
    {
        try { DataContributionPreferences.Save(DataContributionPreferencesPath, _dataContributionPreferences); }
        catch (Exception error)
        {
            _dataContributionPreferences = _dataContributionPreferences with
            {
                Consent = DataContributionConsent.Undecided,
                AutomaticMonthlyUpload = false,
                DecidedAtUtc = null,
            };
            MatchStorageStatus.Text = "Sharing preference could not be saved; automatic sharing remains off: " + error.Message;
        }
    }

    private void ObserveMonthlyDataShareWindow(DateOnly localGameDate)
    {
        if (!DataContributionSchedule.IsDue(_dataContributionPreferences, localGameDate,
            _dataContributionSyncState.LastSuccessfulAutomaticPeriod)) return;
        var period = DataContributionSchedule.Period(localGameDate);
        if (_automaticShareDuePeriod == period) return;
        _automaticShareDuePeriod = period;
        if (!_dataServiceConfiguration.Enabled)
        {
            MatchStorageStatus.Text = "Monthly data sharing is due for " + period +
                ". This build has no database address, so the match remains local and no receipt was recorded.";
            return;
        }
        _ = UploadDataAsync(automatic: true, period);
    }

    private void RecordSuccessfulAutomaticShare(string period, Guid receiptId, DateTimeOffset receivedAtUtc)
    {
        // Called only after the server has committed the idempotent batch.
        var next = _dataContributionSyncState.RecordSuccess(period, receiptId, receivedAtUtc);
        DataContributionSyncState.Save(DataContributionSyncStatePath, next);
        _dataContributionSyncState = next;
        _automaticShareDuePeriod = null;
    }

    private void QueueMatchCheckpoint(bool force = false, bool stopped = false)
    {
        if (_matchAcquisition is null) return;
        if (!force && (!_matchStorageWrite.IsCompleted || DateTimeOffset.UtcNow - _lastMatchCheckpoint < TimeSpan.FromSeconds(5))) return;
        _matchCaptureStopped |= stopped;
        // Keep guesses in their own collection, including explicitly selected/pinned guesses.
        // Never promote a projected slot merely because it also exists in a reference list.
        var guesses = (_lastProjection?.Slots ?? []).Where(s => s.Card is not null && s.State != DeckSlotState.Observed)
            .GroupBy(s => (s.Card!.Id, Manual: s.State is DeckSlotState.Pinned or DeckSlotState.Reference or DeckSlotState.Selected))
            .Select(g => new MatchCard(g.Key.Id, g.Count(),
                g.Key.Manual ? MatchCardEvidence.ManualHypothesis : MatchCardEvidence.Inferred,
                GwentCompanion.Core.Domain.CardProvenance.Unknown,
                MatchAcquisition.Confidence(g.Select(s => s.ModelShare).FirstOrDefault()))).ToArray();
        _matchAcquisition.SetHypothesis(guesses);
        if (_matchCaptureStopped && _matchAcquisition.UserReferenceRejected &&
            _completedUserHypothesisMatch != _matchAcquisition.MatchId)
        {
            try { _matchAcquisition.SetUserHypothesis(BuildUserDeckHypothesis()); }
            catch { _matchAcquisition.SetUserHypothesis([]); } // Completion must never prevent the final local save.
            _completedUserHypothesisMatch = _matchAcquisition.MatchId;
        }
        var snapshot = _matchAcquisition.Snapshot(_matchCaptureStopped);
        if (snapshot is null) return;
        _lastMatchCheckpoint = DateTimeOffset.UtcNow;
        var store = new LocalMatchStore(MatchStorageDirectory);
        // Checkpoints are immutable. Serialize final saves even when a prior match is still
        // writing; slow disks coalesce ordinary checkpoints without dropping match boundaries.
        _matchStorageWrite = _matchStorageWrite.ContinueWith(previousWrite =>
        {
            try
            {
                store.Save(snapshot); _matchStorageError = null;
                _ = Dispatcher.InvokeAsync(() => MatchStorageStatus.Text = "Match data saved locally. Nothing is uploaded.");
            }
            catch (Exception error)
            {
                _matchStorageError = error.Message;
                _ = Dispatcher.InvokeAsync(() => MatchStorageStatus.Text = "Match data save failed: " + error.Message);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private MatchCard[] BuildUserDeckHypothesis()
    {
        var evidence = _userTracker.DeckBuildingObservations.ToArray();
        if (evidence.Length == 0) return [];
        var faction = evidence.Select(card => card.Card.Faction)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Equals("Neutral", StringComparison.OrdinalIgnoreCase))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault() ?? _selectedUserDeck?.Faction;
        var projection = new OpponentDeckProjector().Build(_cachedDecks, evidence, faction,
            constraints: StartingDeckRules.EvaluateObservedDeck(evidence), catalog: _candidateCatalog);
        return projection.Slots.Where(slot => slot.Card is not null && slot.State != DeckSlotState.Observed)
            .GroupBy(slot => slot.Card!.Id)
            .Select(group => new MatchCard(group.Key, group.Count(), MatchCardEvidence.Inferred,
                CardProvenance.Unknown, MatchAcquisition.Confidence(group.Select(slot => slot.ModelShare).FirstOrDefault())))
            .ToArray();
    }

    private async Task FlushMatchAcquisitionAsync()
    {
        QueueMatchCheckpoint(force: true, stopped: true);
        await _matchStorageWrite;
        if (_matchStorageError is { } error) MatchStorageStatus.Text = "Match data save failed: " + error;
    }

    private async void OpenMatchData_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            QueueMatchCheckpoint(force: true); await _matchStorageWrite;
            if (_matchAnalysisWindow is { } existing)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate(); await existing.RefreshAsync(); return;
            }
            var window = new MatchAnalysisWindow(MatchStorageDirectory, FindDataRoot()) { Owner = this };
            _matchAnalysisWindow = window;
            window.Closed += (_, _) => _matchAnalysisWindow = null;
            window.Show();
        }
        catch (Exception error) { MatchStorageStatus.Text = "Cannot open Match Data: " + error.Message; }
    }

    private async void PushToDatabase_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_dataContributionPreferences.CanShare)
        {
            _dataContributionPreferences = _dataContributionPreferences with
            {
                Consent = DataContributionConsent.Accepted,
                DecidedAtUtc = DateTimeOffset.UtcNow,
            };
            SaveDataContributionPreferences();
            ApplyDataContributionPreferences();
        }
        await UploadDataAsync(automatic: false, period: null);
    }

    private async Task UploadDataAsync(bool automatic, string? period)
    {
        if (_dataUploadRunning || !_dataServiceConfiguration.Enabled || _matchIdentity is null ||
            _matchSigningIdentity is null || !_dataContributionPreferences.CanShare) return;
        _dataUploadRunning = true; RefreshDataContributionButton();
        try
        {
            QueueMatchCheckpoint(force: true);
            await _matchStorageWrite;
            MatchStorageStatus.Text = automatic ? "Sharing this month's completed matches…" : "Sending completed matches…";
            using var client = new DataContributionClient(_dataServiceConfiguration, _matchSigningIdentity,
                DataContributionServerStatePath);
            var result = await client.UploadCurrentSeasonAsync(MatchStorageDirectory, _matchIdentity.Id,
                typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
                DataContributionPreferences.CurrentNoticeVersion,
                _dataContributionPreferences.PublishAnonymousCurve);
            if (automatic && period is not null && result.ReceiptId is { } receipt && result.ReceiptAtUtc is { } received)
                RecordSuccessfulAutomaticShare(period, receipt, received);
            else if (automatic && result.ReceiptId is null) _automaticShareDuePeriod = null;
            if (!string.IsNullOrWhiteSpace(result.SeasonCode))
            {
                SeasonCodeText.Text = result.SeasonCode;
                SeasonCodePanel.Visibility = Visibility.Visible;
            }
            MatchStorageStatus.Text = result.ReceiptId is null
                ? $"No completed {result.Season} matches are ready to upload."
                : $"Database receipt saved · {result.Uploaded} updated · {result.Unchanged} unchanged" +
                  (result.Skipped == 0 ? "." : $" · {result.Skipped} damaged local file(s) skipped.");
        }
        catch (Exception error)
        {
            if (automatic) _automaticShareDuePeriod = null;
            MatchStorageStatus.Text = "Data upload did not complete; local data is unchanged: " + error.Message;
        }
        finally { _dataUploadRunning = false; RefreshDataContributionButton(); }
    }

    private void RefreshDataContributionButton()
    {
        if (PushToDatabaseButton is null) return;
        var ready = _dataServiceConfiguration.Enabled && _matchIdentity is not null && _matchSigningIdentity is not null;
        PushToDatabaseButton.IsEnabled = ready && !_dataUploadRunning;
        PushToDatabaseButton.Opacity = ready ? 1 : 0.5;
        PushToDatabaseButton.ToolTip = ready
            ? "Register this installation automatically and send completed matches from the active patch."
            : "This build has no configured database address yet.";
        System.Windows.Automation.AutomationProperties.SetName(PushToDatabaseButton,
            ready ? "Push match data to database" : "Push to database (service not configured)");
    }

    private void CopySeasonCode_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SeasonCodeText.Text)) Clipboard.SetText(SeasonCodeText.Text);
    }

    private void OpenPublicMmrSite_OnClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(PublicMmrSite.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { MatchStorageStatus.Text = "Cannot open the public MMR curves: " + error.Message; }
    }

    private async Task UpdateCurveVisibilityAsync()
    {
        if (_dataUploadRunning || _matchSigningIdentity is null) return;
        try
        {
            using var client = new DataContributionClient(_dataServiceConfiguration, _matchSigningIdentity,
                DataContributionServerStatePath);
            if (await client.UpdateCurrentSeasonVisibilityAsync(_dataContributionPreferences.PublishAnonymousCurve))
                MatchStorageStatus.Text = _dataContributionPreferences.PublishAnonymousCurve
                    ? "Your anonymous MMR curve is visible for the current patch."
                    : "Your MMR curve is hidden for the current patch.";
        }
        catch (Exception error) { MatchStorageStatus.Text = "Curve visibility will be retried: " + error.Message; }
    }
}
