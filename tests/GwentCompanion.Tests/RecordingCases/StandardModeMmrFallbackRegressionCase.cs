using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class StandardModeMmrFallbackRegressionCase : IRecordingValidationCase
{
    public string Id => "standard-mode-mmr-fallback";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        using var screenReader = new ScreenStateRecognizer();
        var mmrReader = new PostMatchMmrRecognizer();
        var gate = new PostMatchAutoStopGate();
        gate.TryRequest("retained-menu", new(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true),
            true, true, false);
        GwentVisualObservation? final = null;
        var stops = 0;
        for (var index = 0; index < 3; index++)
        {
            var pixels = definition.Load($"evidence-{index + 1:D2}.png");
            final = await mmrReader.ReadAsync(pixels, await screenReader.AnalyzeAsync(pixels),
                DateTimeOffset.UnixEpoch.AddMilliseconds(index * 1000), screenReader);
            if (gate.TryRequest("retained-menu", final, true, true, false)) stops++;
        }
        if (final is not { ScreenHeader: "STANDARD MODE", PostMatchExitCue: true,
            PostMatchMmr: { RatingAfter: 2434, SeasonPeak: 2441, IsFactionRating: true } } || stops != 1)
            throw new InvalidOperationException($"The retained Standard Mode menu did not recover 2434/2441 and stop the armed match exactly once: header={final?.ScreenHeader}, cue={final?.PostMatchExitCue}, current={final?.PostMatchMmr?.RatingAfter}, peak={final?.PostMatchMmr?.SeasonPeak}, stops={stops}.");

        var unarmed = new PostMatchAutoStopGate();
        if (unarmed.TryRequest("fresh-menu", final, true, true, false))
            throw new InvalidOperationException("Opening analysis on the main menu triggered a stale post-match stop.");
    }
}
