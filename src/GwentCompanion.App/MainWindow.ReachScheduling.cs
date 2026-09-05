using System.Windows;
using System.Windows.Threading;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Simulation;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private sealed record ReachDisplayProgress(int Completed, int Total, ThreatReport? Report = null);
    private ReachDisplayProgress? _reachProgress;
    private ReachNotification? _pendingReachProgress;
    private int _reachDispatchPending;
    private sealed record ReachNotification(string Key, CardDefinition Card, ThreatProgress Progress, CancellationToken Cancellation);

    private bool CurrentReachRequest(string key, string cardId, CancellationToken cancellation) =>
        ReachEnabledInMainApp && !cancellation.IsCancellationRequested && !_windowClosing && !_suspendReach && !_waitingForThreatBoard &&
        _threatKey == key && HoverBoardCompatible() &&
        (ThreatCardChoice.SelectedItem as CardDefinition ?? _liveHover)?.Id == cardId;

    private void QueueReachProgress(string key, CardDefinition card, ThreatProgress progress, CancellationToken cancellation)
    {
        Interlocked.Exchange(ref _pendingReachProgress, new(key, card, progress, cancellation));
        if (Interlocked.CompareExchange(ref _reachDispatchPending, 1, 0) != 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _reachDispatchPending, 0);
            var next = Interlocked.Exchange(ref _pendingReachProgress, null);
            if (next is null || !CurrentReachRequest(next.Key, next.Card.Id, next.Cancellation) ||
                _reachProgress?.Report?.Complete == true) return;
            _reachProgress = new(next.Progress.Completed, next.Progress.Total, next.Progress.Report);
            RenderThreatReport(next.Card, next.Progress.Report);
        }, DispatcherPriority.ContextIdle);
    }

    private void RefreshReachProgress()
    {
        if (ReachProgressPanel is null) return;
        ReachProgressPanel.Visibility = _reachProgress is null ? Visibility.Collapsed : Visibility.Visible;
        if (_reachProgress is not { } progress) return;
        ReachProgressBar.Maximum = Math.Max(1, progress.Total);
        ReachProgressBar.Value = progress.Completed;
        var done = progress.Report?.Complete == true;
        ReachProgressText.Text = done ? $"Scheduled evaluation complete · {Math.Max(0, progress.Total - 1)} opponent candidates" :
            (_gameplayPriority.PauseReason is { } reason ? reason + " · " : "Evaluating Reach · ") +
            $"{progress.Completed}/{progress.Total} · wait for more detail";
        ReachProgressPanel.ToolTip = "Cards evaluated, not confidence. Card tracking takes priority.";
    }
}
