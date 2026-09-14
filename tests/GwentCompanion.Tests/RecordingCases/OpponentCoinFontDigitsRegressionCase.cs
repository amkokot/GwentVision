using GwentCompanion.Platform.Windows.Vision;

internal sealed class OpponentCoinFontDigitsRegressionCase : IRecordingValidationCase
{
    public string Id => "opponent-coin-font-digits";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        using var reader = new ScreenStateRecognizer();
        int[] expected = [1, 2, 3, 4, 5, 6, 9];
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = await OpponentHudRecognizer.ReadCoinCandidateAsync(
                definition.Load($"evidence-{index + 1:00}.png"),
                OpponentHudRecognizer.OpponentCoinRegion, reader).ConfigureAwait(false);
            if (actual != expected[index])
                throw new InvalidOperationException(
                    $"Opponent coin glyph {expected[index]} was read as {actual?.ToString() ?? "unknown"}.");
        }
    }
}
