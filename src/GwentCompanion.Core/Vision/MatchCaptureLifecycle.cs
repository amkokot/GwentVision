namespace GwentCompanion.Core.Vision;

/// <summary>Keep menu readings out of a new match and close the previous match before accepting the next board.</summary>
public sealed class MatchCaptureLifecycle
{
    private bool _started, _resultSeen;
    private DateTimeOffset _lastAt;
    public bool WaitingForGame => !_started;
    public bool NewGameStarted { get; private set; }
    public PostMatchMmr? BestRating { get; private set; }

    public void Observe(GwentVisualObservation screen, DateTimeOffset at)
    {
        if (!screen.FrameGeometrySupported || at <= _lastAt || NewGameStarted) return;
        _lastAt = at;
        var header = screen.ScreenHeader?.Trim().ToUpperInvariant();
        var result = PostMatchMmr.IsResultHeader(header) || header == "GAME OVER";
        var menu = header == "STANDARD MODE" || screen.PostMatchExitCue;
        var game = !result && !menu && (screen.MatchHudVisible == true || header is "ROUND 1" or "REDRAW");
        if (_resultSeen && game) { NewGameStarted = true; return; }
        if (game || result) _started = true;
        if (!_started) return;
        if (result || menu) _resultSeen = true;
        if (!_resultSeen) return;
        var reading = screen.PostMatchMmr ?? screen.PostMatchMmrCandidate;
        if (reading is not { IsFactionRating: true, RatingAfter: >= 0 and <= 10000 }) return;
        if (BestRating is null || reading.Confirmed && !BestRating.Confirmed ||
            reading.Confirmed == BestRating.Confirmed && reading.ReadCount >= BestRating.ReadCount)
            BestRating = reading;
    }

    public void Reset()
    { _started = false; _resultSeen = false; _lastAt = default; NewGameStarted = false; BestRating = null; }
}
