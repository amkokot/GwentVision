using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class StandardModeMmrLightweightRegressionCase : IRecordingValidationCase
{
    public string Id => "standard-mode-mmr-lightweight";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        using var pipeline = new CardVisionPipeline(VisionReferenceLibrary.Load(catalog, cache).Take(1), catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        PreparedVisionFrame? final = null;
        for (var index = 0; index < 3; index++)
        {
            final = await pipeline.PrepareResultsAsync(definition.Load($"evidence-{index + 1:D2}.png"),
                DateTimeOffset.UnixEpoch.AddMilliseconds(index * 300));
            if (final.Titles.Count != 0 || final.NeedsArtwork || final.HoveredCard is not null ||
                final.OpponentLeader is not null || final.DeckPlayChoices is not null)
                throw new InvalidOperationException("The numeric-only MMR path performed card/leader/choice work.");
        }
        if (final is not { Screen: { ScreenHeader: "STANDARD MODE", PostMatchExitCue: true,
                PostMatchMmr: { RatingAfter: 2393, SeasonPeak: 2400, IsFactionRating: true } } })
            throw new InvalidOperationException($"The retained Standard Mode panel did not confirm 2393/2400: " +
                $"header={final?.Screen.ScreenHeader}, cue={final?.Screen.PostMatchExitCue}, " +
                $"current={final?.Screen.PostMatchMmr?.RatingAfter}, peak={final?.Screen.PostMatchMmr?.SeasonPeak}.");
    }
}
