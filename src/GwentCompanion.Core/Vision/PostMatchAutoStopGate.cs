namespace GwentCompanion.Core.Vision;

/// <summary>Stop on confirmed ratings when enabled, or close a completed match when the next game starts.</summary>
public sealed class PostMatchAutoStopGate
{
    private string? _requestedSession;
    private string? _armedSession;
    public bool TryRequest(string sessionId, GwentVisualObservation screen, bool enabled, bool live, bool blocked)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && live && screen.View == GwentViewKind.Board &&
            screen.MatchHudVisible == true && !screen.IsCardSelectionOverlay)
            _armedSession = sessionId;
        var directResult = PostMatchMmr.IsResultHeader(screen.ScreenHeader) &&
            (screen.PostMatchMmr is { Confirmed: true, IsFactionRating: true, RatingAfter: >= 0 and <= 10000 } ||
             screen.PostMatchRank?.Rank is >= 0 and <= 30);
        // A user can start analysis while already sitting on the main menu. Only a
        // menu observed after this same session showed the authenticated match HUD
        // is allowed to act as the missed-result fallback.
        var recoveredMenu = screen.PostMatchExitCue && _armedSession == sessionId &&
            screen.PostMatchMmr is { Confirmed: true, IsFactionRating: true, RatingAfter: >= 0 and <= 10000 };
        // The lifecycle has already authenticated a previous result and a new-game
        // boundary. This also covers tracking begun on the previous result screen.
        var nextGame = screen.PostMatchCaptureEnded;
        if ((!enabled && !nextGame) || !live || blocked || string.IsNullOrWhiteSpace(sessionId) || sessionId == _requestedSession ||
            (!directResult && !recoveredMenu && !nextGame)) return false;
        _requestedSession = sessionId;
        return true;
    }
    public void Reset() { _requestedSession = null; _armedSession = null; }
}
