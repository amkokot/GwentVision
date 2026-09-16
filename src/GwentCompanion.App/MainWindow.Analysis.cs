using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Windows;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private Task? _startupTask, _visionLoadTask;
    private bool _analysisTransition, _windowClosing;
    private readonly bool _analysisControlTest = Environment.GetCommandLineArgs().Contains("--analysis-control-test");
    private bool _testFailureInjected;
    private readonly PostMatchAutoStopGate _mmrStopGate = new();
    private readonly MatchCaptureLifecycle _matchLifecycle = new();
    private bool _waitingOnMenu, _closedAtNextGame;
    private Task? _autoStopTask;

    private void AutoStopOnMmr_OnChanged(object sender, RoutedEventArgs e)
    { if (_libraryReady && !_restoringReviewPreference) SaveUserSettings(); }

    private void TryAutoStopOnMmr(CardVisionResult result)
    {
        if (_mmrStopGate.TryRequest(CurrentEncounterId, result.Screen, AutoStopOnMmrChoice.IsChecked == true,
            _diagnosticSession?.IsRunning == true || _visionWorker is not null,
            _analysisTransition || _windowClosing || _reviewEvidencePath is not null, result.SampledAt))
            _autoStopTask = AutoStopAtMmrAsync();
    }

    private async Task AutoStopAtMmrAsync()
    {
        _analysisTransition = true; RefreshAnalysisButton();
        try { await StopAnalysisCoreAsync(_closedAtNextGame
            ? "Tracking stopped as the next game began · best available rating and previous match saved."
            : "Tracking stopped automatically after the post-match menu · best available rating and evidence retained.",
            discardVisionBacklog: true); }
        catch (Exception error) { ShowAnalysisFailure("Automatic stop needs attention; use Stop to retry.", error); }
        finally
        {
            _analysisTransition = false;
            if (!_windowClosing)
            {
                SyncDecksButton.IsEnabled = _diagnosticSession?.IsRunning != true && _builderWindow is null && _libraryWindow is null;
                RefreshAnalysisButton(); TryOpenPendingEncounterReview();
            }
        }
    }

    private async Task StopAnalysisCoreAsync(string status, bool discardVisionBacklog = false)
    {
        ShowAnalysisStatus("Stopping analysis and saving queued evidence…");
        var completedDiagnosticSession = _analysisControlTest ? null : _diagnosticSession?.CurrentSessionDirectory;
        // Automatic post-match shutdown has all required evidence. Cancel expensive
        // queued recognition immediately, before waiting for the capture journal to
        // close, so no redundant result/menu frames delay the visible stop.
        if (discardVisionBacklog) _visionCancellation?.Cancel();
        if (_diagnosticSession is not null) await _diagnosticSession.StopAsync();
        await StopVisionAsync(discardVisionBacklog);
        if (_autoEncounterSave is { } save) await save;
        PersistCurrentMatch(); RefreshDeckList();
        if (_projectionQueue is not null) await _projectionQueue.Idle;
        await FlushMatchAcquisitionAsync();
        // The last preview can arrive only a few seconds before Stop. Its journal
        // update is already drained above; also wait for the coalesced ledger writer
        // so the saved provision floor cannot trail the final event stream.
        if (_valueWriter is not null) await _valueWriter.Idle;
        ShowAnalysisStatus(status);
        MemoryStatusText.Text = _autoEncounterError is { } error ? "Encounter auto-save needs attention: " + error + ". Observation snapshot retained." :
            _observedPostMatchMmr?.RatingAfter is null && _observedPostMatchRank is null
            ? "Observation snapshot saved. No confirmed post-match rating result: encounter history was not auto-updated; review/save is available here."
            : "Observation snapshot saved. The post-match rating result triggered the separate encounter-history save; check its save status.";
        QueueDiagnosticRetention(completedDiagnosticSession);
    }

    private static void QueueDiagnosticRetention(string? protectedSessionDirectory = null)
    {
        // Retention runs after every writer has closed, but stays off the UI path so
        // automatic post-match shutdown remains immediate. Startup also invokes it
        // so an interrupted prior run cannot leave the rolling history over budget.
        _ = Task.Run(() => DiagnosticSessionRetention.Enforce(
            DiagnosticSessionDirectory, protectedSessionDirectory));
    }

    private async Task InitializeAnalysisAsync()
    {
        if (!_analysisControlTest && _reviewEvidencePath is null) QueueDiagnosticRetention();
        var identity = InitializeMatchIdentityAsync();
        SetLoadingStage("Indexing the deck library…");
        ShowAnalysisStatus("Loading deck library… You can click Play to start when ready.");
        await LoadDeckIndexesAsync(loadVision: false);
        await identity;
        if (_windowClosing) return;
        SetLoadingStage("Finishing startup…");
        if (!_windowClosing && !_analysisTransition)
            ShowAnalysisStatus("Ready · click Play to start analysis.");
    }

    private void SetLoadingStage(string message)
    {
        if (LoadingStatusText is not null) LoadingStatusText.Text = message;
    }

    private void HideLoadingShell()
    {
        if (LoadingShell is not null) LoadingShell.Visibility = Visibility.Collapsed;
    }

    private Task EnsureVisionReadyAsync() => _visionPipeline is not null ? Task.CompletedTask : ReloadVisionAsync();

    private async void BeginVisionWarmup()
    {
        // Capture and analysis remain inactive until Play; only immutable local
        // references are prepared here.
        var failOnceTest = _analysisControlTest && Environment.GetCommandLineArgs().Contains("--analysis-fail-once");
        if (failOnceTest || _reviewEvidencePath is not null && !_analysisControlTest || !_libraryReady || _windowClosing) return;
        if (_visionPipeline is not null || _visionLoadTask is { IsCompleted: false }) return;
        // Let the first visible interaction win the CPU/disk budget. If the user
        // opens Library or an editor immediately, leaving that surface schedules
        // another idle warm-up; Play can always request recognition directly.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_windowClosing || _page == UiPage.Library || _libraryWindow is not null ||
            _builderWindow is not null || _libraryTransferBusy) return;
        if (_visionPipeline is not null || _visionLoadTask is { IsCompleted: false }) return;
        BeginOcrWarmup();
        try { await ReloadVisionAsync(announce: false); }
        catch (Exception error) { _visionError = error.Message; } // Play retries and reports a foreground error.
    }

    private void RecordingChoice_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_diagnosticSession is not null) _diagnosticSession.RetainTrainingFrames = RecordTrainingChoice.IsChecked == true;
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
        if (_diagnosticSession?.IsRunning == true)
            ShowAnalysisStatus(RecordTrainingChoice.IsChecked == true
                ? $"Analyzing · training recording on at {SelectedRecordingFrameRate} FPS."
                : "Analyzing · training recording off; compact match evidence only.");
    }

    private int SelectedRecordingFrameRate => RecordingFrameRateChoice?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), out var framesPerSecond)
            ? TrainingRecordingFrameRate.Normalize(framesPerSecond)
            : TrainingRecordingFrameRate.Recommended;

    private void SelectRecordingFrameRate(int framesPerSecond)
    {
        var normalized = TrainingRecordingFrameRate.Normalize(framesPerSecond);
        foreach (var option in RecordingFrameRateChoice.Items.OfType<System.Windows.Controls.ComboBoxItem>())
            if (option.Tag?.ToString() == normalized.ToString())
            {
                RecordingFrameRateChoice.SelectedItem = option;
                return;
            }
        RecordingFrameRateChoice.SelectedIndex = 1;
    }

    private void RecordingFrameRate_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_diagnosticSession is not null) _diagnosticSession.RetainedFramesPerSecond = SelectedRecordingFrameRate;
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
        if (_diagnosticSession?.IsRunning == true && _diagnosticSession.RetainTrainingFrames)
            ShowAnalysisStatus($"Analyzing · training recording on at {SelectedRecordingFrameRate} FPS.");
    }

    private void ShowAnalysisStatus(string message)
    {
        if (_windowClosing) return;
        AnalysisStatusText.Text = message;
        AnalysisStatusText.Visibility = Visibility.Visible;
        DiagnosticStatusText.Text = message;
        FooterStatusText.Text = message;
    }

    private void ShowAnalysisFailure(string message, Exception exception)
    {
        ShowAnalysisStatus(message + " " + exception.Message);
        if (_reviewEvidencePath is not null) return;
        try
        {
            var path = Path.Combine(FindDataRoot(), "diagnostics", "analysis-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n{exception}\n\n");
        }
        catch (IOException) { /* The visible error must survive a log-write failure. */ }
        catch (UnauthorizedAccessException) { }
    }

    private void RefreshAnalysisButton()
    {
        if (_windowClosing) return;
        var running = _diagnosticSession?.IsRunning == true || _visionWorker is not null;
        DiagnosticButton.IsEnabled = !_cardDataBusy && !_cardReloadRequired && !_analysisTransition && !_libraryTransferBusy &&
            (_diagnosticSession?.IsRunning == true || _visionWorker is not null || _libraryWindow is null && _builderWindow is null) &&
            (_reviewEvidencePath is null || _analysisControlTest);
        if (_analysisTransition)
        {
            AutomationProperties.SetName(DiagnosticButton, "Preparing or stopping tracking");
            DiagnosticButton.ToolTip = "Please wait for the current start/stop operation.";
        }
        else
            SetAnalysisButtonState(_diagnosticSession?.IsRunning == true);
    }

    private bool TryAnalysisWindow(out GwentWindowSnapshot window)
    {
        if (!_analysisControlTest) return RequireGameWindow(out window);
        // Explicit smoke-test mode captures ONLY this companion window, never GWENT.
        var handle = new WindowInteropHelper(this).Handle;
        window = _windowService.Snapshot(handle);
        return true;
    }
}
