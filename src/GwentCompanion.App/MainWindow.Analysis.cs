using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
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
    private Task? _autoStopTask;

    private void ExperimentalAnalysis_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
        if (!ExperimentalOverviewEnabled && _page == UiPage.Plays) ShowPage(UiPage.Deck);
        else UpdateWorkspaceLayout();
        if (ExperimentalOverviewEnabled)
        {
            ShowAnalysisStatus("Experimental Overview layout enabled. Live analysis behavior is unchanged.");
        }
        else
            ShowAnalysisStatus("Standard Live layout: opponent deck, candidate cards, and snapshots.");
    }

    private void AutoStopOnMmr_OnChanged(object sender, RoutedEventArgs e)
    { if (_libraryReady && !_restoringReviewPreference) SaveUserSettings(); }

    private void TryAutoStopOnMmr(CardVisionResult result)
    {
        if (_mmrStopGate.TryRequest(CurrentEncounterId, result.Screen, AutoStopOnMmrChoice.IsChecked == true,
            _diagnosticSession?.IsRunning == true || _visionWorker is not null,
            _analysisTransition || _windowClosing || _reviewEvidencePath is not null))
            _autoStopTask = AutoStopAtMmrAsync();
    }

    private async Task AutoStopAtMmrAsync()
    {
        _analysisTransition = true; RefreshAnalysisButton();
        try { await StopAnalysisCoreAsync("Analysis stopped automatically after confirmed rating result · evidence retained."); }
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

    private async Task StopAnalysisCoreAsync(string status)
    {
        ShowAnalysisStatus("Stopping analysis and saving queued evidence…");
        if (_diagnosticSession is not null) await _diagnosticSession.StopAsync();
        await StopVisionAsync(); // Drain retained text/artwork before persisting the final state.
        if (_autoEncounterSave is { } save) await save;
        PersistCurrentMatch(); RefreshDeckList();
        // The last preview can arrive only a few seconds before Stop. Its journal
        // update is already drained above; also wait for the coalesced ledger writer
        // so the saved provision floor cannot trail the final event stream.
        if (_valueWriter is not null) await _valueWriter.Idle;
        OpenSessionButton.IsEnabled = _diagnosticSession?.CurrentSessionDirectory is not null;
        ShowAnalysisStatus(status);
        MemoryStatusText.Text = _autoEncounterError is { } error ? "Encounter auto-save needs attention: " + error + ". Observation snapshot retained." :
            _observedPostMatchMmr?.RatingAfter is null && _observedPostMatchRank is null
            ? "Observation snapshot saved. No confirmed post-match rating result: encounter history was not auto-updated; review/save is available here."
            : "Observation snapshot saved. The post-match rating result triggered the separate encounter-history save; check its save status.";
    }

    private async Task InitializeAnalysisAsync()
    {
        SetLoadingStage("Indexing the deck library…");
        ShowAnalysisStatus("Loading deck library… You can click Play to start when ready.");
        await LoadDeckIndexesAsync(loadVision: false);
        if (_windowClosing) return;
        SetLoadingStage("Finishing startup…");
        if (!_windowClosing && !_analysisTransition)
            ShowAnalysisStatus("Ready · click Play to load recognition and start analysis.");
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

    private void RecordingChoice_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_diagnosticSession is not null) _diagnosticSession.RetainTrainingFrames = RecordTrainingChoice.IsChecked == true;
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
        if (_diagnosticSession?.IsRunning == true)
            ShowAnalysisStatus(RecordTrainingChoice.IsChecked == true
                ? "Analyzing · training recording on."
                : "Analyzing · training recording off; compact match evidence only.");
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
            AutomationProperties.SetName(DiagnosticButton, "Preparing or stopping analysis");
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
