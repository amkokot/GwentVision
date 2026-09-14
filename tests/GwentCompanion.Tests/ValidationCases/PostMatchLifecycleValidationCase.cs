using GwentCompanion.Core.Vision;

internal sealed class PostMatchLifecycleValidationCase : IContributorValidationCase
{
    public string Id => "post-match-mmr-linger";
    public string Kind => "integration";
    public string Summary => "Keep capture alive through the result-overlay fade so the menu MMR can still be recovered.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool condition, string message) => ContributorValidationContext.Check(condition, message);
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var session = "result-fade";
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
            MatchHudVisible: true);
        var victory = board with { MatchHudVisible = false, ScreenHeader = "VICTORY",
            IsCardSelectionOverlay = true };
        var fadedBoard = board with { ScreenHeader = null, IsCardSelectionOverlay = false };
        var candidate = new PostMatchMmr(2434, null, true, "Standard Mode fallback", 2441,
            Confirmed: false, ReadCount: 2);
        var menu = board with { MatchHudVisible = false, ScreenHeader = "STANDARD MODE",
            PostMatchExitCue = true, PostMatchMmrCandidate = candidate };

        var lifecycle = new MatchCaptureLifecycle();
        var gate = new PostMatchAutoStopGate();
        lifecycle.Observe(board, at);
        Check(!gate.TryRequest(session, board, true, true, false), "An active match requested automatic stop.");
        lifecycle.Observe(victory, at.AddSeconds(1));
        Check(!gate.TryRequest(session, victory, true, true, false),
            "A result without a rating stopped before the menu fallback.");

        // Reproduces the latest live session: the HUD detector briefly sees the
        // completed board 0.3 seconds after VICTORY disappears.
        lifecycle.Observe(fadedBoard, at.AddSeconds(1.3));
        Check(!lifecycle.NewGameStarted,
            "The fading completed board was mistaken for the next game before MMR recovery.");
        Check(!gate.TryRequest(session, fadedBoard with { PostMatchCaptureEnded = lifecycle.NewGameStarted },
                true, true, false), "The result-overlay fade stopped capture.");

        lifecycle.Observe(menu, at.AddSeconds(2));
        Check(lifecycle.BestRating == candidate, "The menu fallback rating was not retained.");
        Check(!gate.TryRequest(session, menu, true, true, false),
            "An unconfirmed fallback stopped before it could gather another vote.");

        lifecycle.Observe(board, at.AddSeconds(3));
        Check(lifecycle.NewGameStarted && lifecycle.BestRating == candidate,
            "A board after the authenticated menu did not close the previous match with its best read.");
        var boundary = victory with { ScreenHeader = "PROGRESSION", PostMatchMmr = lifecycle.BestRating,
            PostMatchMmrCandidate = null, PostMatchCaptureEnded = true };
        Check(gate.TryRequest(session, boundary, false, true, false),
            "The next game did not close the previous capture with its retained rating.");
        return Task.CompletedTask;
    }
}
