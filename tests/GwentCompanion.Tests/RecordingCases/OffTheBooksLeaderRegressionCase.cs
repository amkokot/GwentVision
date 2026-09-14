using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class OffTheBooksLeaderRegressionCase : IRecordingValidationCase
{
    public string Id => "off-the-books-leader";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        using var recognizer = new LeaderAbilityRecognizer(catalog);
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        var at = DateTimeOffset.UnixEpoch;
        LeaderAbilityReading? reading = null;
        var files = new[] { "evidence-01.png", "evidence-02.png", "evidence-03.png" };
        for (var index = 0; index < files.Length; index++)
            reading = recognizer.Observe(definition.Load(files[index]), board, at.AddMilliseconds(index * 500)) ?? reading;
        if (reading?.Card.Id != "202328")
            throw new InvalidOperationException($"Repeated fixed-HUD plaque should identify Off the Books; got {reading?.Card.Name ?? "nothing"}.");
        return Task.CompletedTask;
    }
}
