using System.Windows;
using System.Windows.Threading;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private CardVisionResult? _pendingFastHover;
    private int _hoverDispatchPending;
    private DispatcherTimer? _hoverExpiry;
    private string? _dismissedHoverId;

    private void QueueFastHover(CardVisionResult result)
    {
        if (!ReachEnabledInMainApp) return;
        // One pending UI notification, not one queue item per frame. This never commits match evidence.
        Interlocked.Exchange(ref _pendingFastHover, result);
        if (Interlocked.CompareExchange(ref _hoverDispatchPending, 1, 0) != 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _hoverDispatchPending, 0);
            var latest = Interlocked.Exchange(ref _pendingFastHover, null);
            if (latest is null || _windowClosing || _visionQueue is null || _diagnosticSession?.IsRunning != true ||
                latest.SampledAt < _latestHoverAt || DateTimeOffset.Now - latest.SampledAt > TimeSpan.FromSeconds(2)) return;
            if (latest.Sightings.Any(sight => sight.Source == CardSightSource.PlayPreview)) _waitingForThreatBoard = true;
            AcceptHover(latest);
            RefreshThreats(latest.SampledAt);
        }, DispatcherPriority.Background);
    }

    private void AcceptHover(CardVisionResult result)
    {
        if (result.SampledAt < _latestHoverAt) return; // Older artwork commits cannot rewind the active hover.
        _latestHoverAt = result.SampledAt; _latestHoverScreen = result.Screen;
        var prior = _liveHover?.Id;
        if (result.Screen.IsCardSelectionOverlay || result.Screen.View != GwentViewKind.Board || result.Screen.MatchHudVisible == false)
            _liveHover = null;
        else if (result.HoverInPlayerHand)
        {
            _liveHover = result.HoveredCard;
            if (_liveHover is not null) _lastHoverAt = result.SampledAt;
        }
        else if (result.SampledAt - _lastHoverAt > TimeSpan.FromMilliseconds(900)) _liveHover = null;
        if (_liveHover?.Id != prior) _dismissedHoverId = null;
        if (_hoverExpiry is null)
        {
            _hoverExpiry = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            {
                RefreshReachProgress();
                if (_reviewEvidencePath is null && DateTimeOffset.Now - _lastHoverAt > TimeSpan.FromSeconds(2))
                { if (_liveHover is not null) { _liveHover = null; RefreshThreats(DateTimeOffset.Now); } }
            }, Dispatcher);
        }
    }

    private bool HoverBoardCompatible()
    {
        if (_latestHoverScreen is not { } screen) return true;
        if (screen.IsCardSelectionOverlay || screen.View != GwentViewKind.Board || screen.MatchHudVisible == false) return false;
        // Fast text can be ahead of the authoritative board. Do not calculate on a board whose score changed.
        return _threatSnapshot is { } snapshot &&
            (screen.UserScore is null || screen.UserScore == snapshot.User.Score?.Value) &&
            (screen.OpponentScore is null || screen.OpponentScore == snapshot.Opponent.Score?.Value) &&
            (screen.UserHandCount is null || screen.UserHandCount == snapshot.User.HandCount?.Value) &&
            (screen.OpponentHandCount is null || screen.OpponentHandCount == snapshot.Opponent.HandCount?.Value);
    }

    private void RefreshHoverBanner()
    {
        if (HoverThreatBanner is null) return;
        if (!ReachEnabledInMainApp)
        {
            HoverThreatBanner.Visibility = Visibility.Collapsed;
            HoverThreatViewport.Visibility = Visibility.Collapsed;
            return;
        }
        RefreshReachProgress();
        var selected = ThreatCardChoice.SelectedItem as CardDefinition;
        var card = selected ?? _liveHover;
        var visible = ShowHoverThreats?.IsChecked == true && card is not null &&
            (selected is not null || card.Id != _dismissedHoverId) && _page is UiPage.Plays or UiPage.Deck or UiPage.Candidates or UiPage.Reference or UiPage.MyDeck or UiPage.Pinned;
        HoverThreatBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { ThreatDetailPanel.Visibility = Visibility.Collapsed; return; }
        var current = _reachTitle.StartsWith(card!.Name, StringComparison.Ordinal);
        HoverThreatTitle.Text = current ? _reachTitle : card.Name + " · reading current board…";
        HoverThreatSummary.Text = current ? string.Join(" · ", new[] { _reachGap, _reachSummary }.Where(value => value.Length > 0)) : "";
        HoverThreatWarning.Text = current ? _reachStatus : "Calculating…";
        HoverThreatWarning.Visibility = HoverThreatWarning.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ThreatDetailPanel.Visibility = DetailedReachChoice?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DismissHoverThreat_OnClick(object sender, RoutedEventArgs e)
    { _dismissedHoverId = _liveHover?.Id; RefreshHoverBanner(); }
    private void HoverThreatSetting_OnChanged(object sender, RoutedEventArgs e) => RefreshHoverBanner();
    private void DetailedReach_OnChanged(object sender, RoutedEventArgs e)
    {
        RefreshHoverBanner();
        if (_libraryReady && !_restoringReviewPreference) SaveUserSettings();
    }
}
