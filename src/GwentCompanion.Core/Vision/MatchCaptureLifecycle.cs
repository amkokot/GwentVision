namespace GwentCompanion.Core.Vision;

/// <summary>Keep menu readings out of a new match and close the previous match before accepting the next board.</summary>
public sealed class MatchCaptureLifecycle
{
    private bool _started, _resultSeen;
    private bool _menuSeenAfterResult;
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
        var explicitOpening = header is "ROUND 1" or "REDRAW";
        var game = !result && !menu && (screen.MatchHudVisible == true || explicitOpening);
        // The completed board can reappear briefly while the result overlay fades.
        // It is the same match, so keep reading until a real menu transition or an
        // explicit next-game opening has authenticated the boundary.
        if (_resultSeen && (explicitOpening || _menuSeenAfterResult && game))
        { NewGameStarted = true; return; }
        if (game || result) _started = true;
        if (!_started) return;
        if (result || menu) _resultSeen = true;
        if (_resultSeen && menu) _menuSeenAfterResult = true;
        if (!_resultSeen) return;
        var reading = screen.PostMatchMmr ?? screen.PostMatchMmrCandidate;
        if (reading is not { IsFactionRating: true } ||
            reading.RatingAfter is < 0 or > 10000 || reading.Change is < -1000 or > 1000 ||
            reading.SeasonPeak is < 0 or > 10000 ||
            reading.RatingAfter is null && reading.Change is null && reading.SeasonPeak is null) return;
        if (BestRating is null || reading.Confirmed && !BestRating.Confirmed ||
            reading.Confirmed == BestRating.Confirmed && (reading.ReadCount > BestRating.ReadCount ||
                reading.ReadCount == BestRating.ReadCount && Completeness(reading) >= Completeness(BestRating)))
            BestRating = reading;
    }

    private static int Completeness(PostMatchMmr reading) =>
        (reading.RatingAfter.HasValue ? 1 : 0) + (reading.Change.HasValue ? 1 : 0) + (reading.SeasonPeak.HasValue ? 1 : 0);

    public void Reset()
    { _started = false; _resultSeen = false; _menuSeenAfterResult = false; _lastAt = default; NewGameStarted = false; BestRating = null; }
}
