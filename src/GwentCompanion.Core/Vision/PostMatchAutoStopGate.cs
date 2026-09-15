namespace GwentCompanion.Core.Vision;

/// <summary>Stop after the completed match reaches its authenticated menu, or when the next game starts.</summary>
public sealed class PostMatchAutoStopGate
{
    private static readonly TimeSpan MenuRatingGrace = TimeSpan.FromMilliseconds(1500);
    private string? _requestedSession;
    private string? _armedSession;
    private string? _confirmedRankSession;
    private string? _menuSession;
    private DateTimeOffset _menuSeenAt;
    public bool TryRequest(string sessionId, GwentVisualObservation screen, bool enabled, bool live, bool blocked,
        DateTimeOffset? observedAt = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && live && screen.View == GwentViewKind.Board &&
            screen.MatchHudVisible == true && !screen.IsCardSelectionOverlay)
        {
            if (_armedSession != sessionId) { _menuSession = null; _menuSeenAt = default; }
            _armedSession = sessionId;
        }
        if (PostMatchMmr.IsResultHeader(screen.ScreenHeader) && screen.PostMatchRank?.Rank is >= 0 and <= 30)
            _confirmedRankSession = sessionId;
        // A user can start analysis while already sitting on the main menu. Only a
        // menu observed after this same session showed the authenticated match HUD
        // is allowed to stop it. A faction-MMR match first waits for the menu's own
        // rating read; a standard-rank match has no MMR to recover and can stop as
        // soon as the menu itself is authenticated.
        var authenticatedMenu = screen.PostMatchExitCue && _armedSession == sessionId;
        var at = observedAt ?? DateTimeOffset.UtcNow;
        if (authenticatedMenu && _menuSession != sessionId)
        {
            _menuSession = sessionId;
            _menuSeenAt = at;
        }
        var ratingRecovered = screen.PostMatchMmr is
            { Confirmed: true, IsFactionRating: true, RatingAfter: >= 0 and <= 10000 };
        // A changed or unreadable menu number must not leave capture running forever.
        // The recognizer normally confirms the current/peak pair quickly; retain a
        // short bounded grace period for alternate OCR and intermittent reads, then close with
        // the lifecycle's best candidate (or an explicitly missing rating).
        var recoveredMenu = authenticatedMenu &&
            (ratingRecovered || _confirmedRankSession == sessionId || at - _menuSeenAt >= MenuRatingGrace);
        // The lifecycle has already authenticated a previous result and a new-game
        // boundary. This also covers tracking begun on the previous result screen.
        var nextGame = screen.PostMatchCaptureEnded;
        if ((!enabled && !nextGame) || !live || blocked || string.IsNullOrWhiteSpace(sessionId) || sessionId == _requestedSession ||
            (!recoveredMenu && !nextGame)) return false;
        _requestedSession = sessionId;
        return true;
    }
    public void Reset()
    {
        _requestedSession = null; _armedSession = null; _confirmedRankSession = null;
        _menuSession = null; _menuSeenAt = default;
    }
}
