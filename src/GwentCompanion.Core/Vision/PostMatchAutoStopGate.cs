namespace GwentCompanion.Core.Vision;

/// <summary>Consumes confirmed recognizer output, never a typed value or an unconfirmed OCR candidate.</summary>
public sealed class PostMatchAutoStopGate
{
    private string? _requestedSession;
    public bool TryRequest(string sessionId, GwentVisualObservation screen, bool enabled, bool live, bool blocked)
    {
        if (!enabled || !live || blocked || string.IsNullOrWhiteSpace(sessionId) || sessionId == _requestedSession ||
            !PostMatchMmr.IsResultHeader(screen.ScreenHeader) ||
            (screen.PostMatchMmr is not { IsFactionRating: true, RatingAfter: >= 0 and <= 10000 } &&
             screen.PostMatchRank is null)) return false;
        _requestedSession = sessionId;
        return true;
    }
    public void Reset() => _requestedSession = null;
}
