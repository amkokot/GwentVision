using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class OpponentPopupHandCostRegressionCase : IRecordingValidationCase
{
    public string Id => "opponent-popup-hand-cost";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var prince = catalog.Single(card => card.Id == "202251");
        // Preserve the production matcher competition. A one-identity fixture has
        // no cross-card ratio-test distractor and is not representative of this
        // difficult preview path.
        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards([prince.Id, "203224", "202414", "122318", "202551", "122304", "122216",
            "203184", "122314", "203109", "202472", "122209", "203025", "202481", "203185", "202918",
            "203014", "203038", "202401", "122103"]);

        var at = DateTimeOffset.UnixEpoch;
        var first = await pipeline.PrepareAsync(definition.Load("evidence-01.png"), at);
        var second = await pipeline.PrepareAsync(definition.Load("evidence-02.png"), at.AddMilliseconds(400));
        var paid = await pipeline.PrepareAsync(definition.Load("evidence-03.png"), at.AddSeconds(3.3));
        if (first.HoveredCard?.Id != prince.Id || second.HoveredCard?.Id != prince.Id)
            throw new InvalidOperationException("The retained exact Prince Anséis popup title no longer repeats.");

        var popupScreen = first.Screen with { View = GwentViewKind.Board, MatchHudVisible = true,
            IsCardSelectionOverlay = false, OpponentHandCount = 9, ScreenHeader = null,
            HasCardTooltip = true, TooltipRegion = new(.583333, .285714, .75, .5) };
        var secondScreen = second.Screen with { View = GwentViewKind.Board, MatchHudVisible = true,
            IsCardSelectionOverlay = false, OpponentHandCount = null, ScreenHeader = null,
            HasCardTooltip = true, TooltipRegion = new(.583333, .285714, .75, .5) };
        pipeline.Commit(first.TextResult with { Screen = popupScreen });
        // Exercise both independent signals from this retained frame. The exact
        // popup title is carried as HoveredCard while the title sighting is removed
        // so it cannot replace the intentionally difficult artwork candidate in
        // MergePreviewEvidence.
        var artwork = pipeline.RecognizePrepared(second with { Screen = secondScreen, Titles = [] }, includeBoard: false);
        var candidate = artwork.Sightings.SingleOrDefault(sighting => sighting.Side == PlayerSide.Opponent &&
            sighting.Source == CardSightSource.PlayPreview && sighting.Card.Id == prince.Id);
        if (candidate is null || candidate.Distance > .30 || candidate.Margin < .70)
            throw new InvalidOperationException("The retained difficult Prince Anséis artwork no longer supplies the bounded preview candidate. Sightings: " +
                string.Join(" | ", artwork.Sightings.Select(item => $"{item.Card.Id}/{item.Side}/{item.Source}/d={item.Distance:F3}/m={item.Margin:F3}/{item.Evidence}")));
        if (pipeline.Commit(artwork).Events.Count != 0)
            throw new InvalidOperationException("The one-frame candidate committed before its opponent hand cost was observed.");

        var paidScreen = paid.Screen with { View = GwentViewKind.Board, MatchHudVisible = true,
            IsCardSelectionOverlay = false, OpponentHandCount = 8, ScreenHeader = null,
            HasCardTooltip = false, TooltipRegion = null };
        var recovered = pipeline.Commit(paid.TextResult with { Screen = paidScreen }).Events;
        if (recovered.Count != 1 || recovered[0].Sighting.Card.Id != prince.Id ||
            recovered[0].Sighting.Source != CardSightSource.PlayPreview)
            throw new InvalidOperationException("Repeated exact popup plus 9→8 opponent hand payment did not recover exactly one Prince Anséis play.");
    }
}
