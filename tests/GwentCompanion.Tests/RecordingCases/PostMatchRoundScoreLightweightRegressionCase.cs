using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class PostMatchRoundScoreLightweightRegressionCase : IRecordingValidationCase
{
    public string Id => "post-match-round-score-lightweight";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        using var pipeline = new CardVisionPipeline(VisionReferenceLibrary.Load(catalog, cache).Take(1), catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        PreparedVisionFrame? final = null;
        for (var index = 0; index < 2; index++)
        {
            final = await pipeline.PrepareResultsAsync(definition.Load($"evidence-{index + 1:D2}.png"),
                DateTimeOffset.UnixEpoch.AddMilliseconds(index * 500));
            if (final.Titles.Count != 0 || final.NeedsArtwork || final.HoveredCard is not null ||
                final.OpponentLeader is not null || final.DeckPlayChoices is not null)
                throw new InvalidOperationException("The numeric-only result path performed card/leader/choice work.");
        }
        var rows = final?.Screen.PostMatchRoundScores;
        if (rows is null || rows.Length != 3 || rows[0].UserScore != 2 || rows[0].OpponentScore != 15 ||
            rows[1].UserScore != 28 || rows[1].OpponentScore != 13 || rows[2].UserScore != 79 || rows[2].OpponentScore != 78)
            throw new InvalidOperationException("The retained result panel did not confirm the 2–15, 28–13, 79–78 round table.");
    }
}
