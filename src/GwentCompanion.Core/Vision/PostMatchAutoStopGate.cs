namespace GwentCompanion.Core.Vision;

/// <summary>Stop after the completed match reaches its authenticated menu, or when the next game starts.</summary>
public sealed class PostMatchAutoStopGate
{
    private string? _requestedSession;
    private string? _armedSession;
    private string? _confirmedRankSession;
    public bool TryRequest(string sessionId, GwentVisualObservation screen, bool enabled, bool live, bool blocked)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && live && screen.View == GwentViewKind.Board &&
            screen.MatchHudVisible == true && !screen.IsCardSelectionOverlay)
            _armedSession = sessionId;
        if (PostMatchMmr.IsResultHeader(screen.ScreenHeader) && screen.PostMatchRank?.Rank is >= 0 and <= 30)
            _confirmedRankSession = sessionId;
        // A user can start analysis while already sitting on the main menu. Only a
        // menu observed after this same session showed the authenticated match HUD
        // is allowed to stop it. A faction-MMR match waits for the menu's own
        // confirmed current/peak pair; a standard-rank match has no MMR to recover
        // and can stop as soon as the menu itself is authenticated.
        var recoveredMenu = screen.PostMatchExitCue && _armedSession == sessionId &&
            (screen.PostMatchMmr is { Confirmed: true, IsFactionRating: true, RatingAfter: >= 0 and <= 10000 } ||
             _confirmedRankSession == sessionId);
        // The lifecycle has already authenticated a previous result and a new-game
        // boundary. This also covers tracking begun on the previous result screen.
        var nextGame = screen.PostMatchCaptureEnded;
        if ((!enabled && !nextGame) || !live || blocked || string.IsNullOrWhiteSpace(sessionId) || sessionId == _requestedSession ||
            (!recoveredMenu && !nextGame)) return false;
        _requestedSession = sessionId;
        return true;
    }
    public void Reset() { _requestedSession = null; _armedSession = null; _confirmedRankSession = null; }
}
