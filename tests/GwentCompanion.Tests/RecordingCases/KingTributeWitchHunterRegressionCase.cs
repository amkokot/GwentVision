using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class KingTributeWitchHunterRegressionCase : IRecordingValidationCase
{
    public string Id => "king-tribute-witch-hunter";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var king = catalog.Single(card => card.Id == "203100");
        var hunter = catalog.Single(card => card.Id == "202320");
        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var features = new FeatureCardRecognizer(references.Where(reference => reference.Card.Id == king.Id),
            Path.Combine(cache, "recognition-features"));
        var matcher = new CardArtMatcher(features.ArtReferences);
        var at = DateTimeOffset.UnixEpoch;
        var firstRegion = new NormalizedRegion(.4903952, .1726072, .5428952, .2996572);
        var secondRegion = new NormalizedRegion(.4858081, .1593891, .5458081, .3045891);
        var firstDistance = matcher.IdentityDistance(definition.Load("evidence-01.png"), firstRegion, king.Id);
        var secondDistance = matcher.IdentityDistance(definition.Load("evidence-02.png"), secondRegion, king.Id);
        if (firstDistance <= .75 || secondDistance <= .75)
            throw new InvalidOperationException($"The retained non-King board body became deceptively close to King of Beggars ({firstDistance:F3}/{secondDistance:F3}).");

        CardSighting FalseKing(NormalizedRegion region, double distance, string evidence) =>
            new(king, PlayerSide.Opponent, CardSightSource.Board, region, .40, 1,
                $"{evidence} {distance:F3} · bounded automatic-arrival board fallback; repetition and deck conservation still required · opponent candidate reference index",
                NeedsTemporalConfirmation: true);
        var firstKing = FalseKing(firstRegion, firstDistance,
            "Two geometrically agreeing anchors among five ratio-separated SIFT candidates; appearance distance");
        var persistedKing = FalseKing(secondRegion, .421,
            "Localized board appearance persisted in an independent scan at distance");
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 8, 5, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 15 };
        var kingLedger = new MatchVisionLedger();
        kingLedger.Observe(at, board with { OpponentDeckCount = 16 }, [], false);
        kingLedger.Observe(at.AddSeconds(1), board, [], false);
        if (kingLedger.Observe(at.AddSeconds(2), board, [firstKing], true).Count != 0 ||
            kingLedger.Observe(at.AddSeconds(24), board, [persistedKing], true).Count != 0)
            throw new InvalidOperationException("A Tribute-plausible, crop-persistent board body was relabeled as King of Beggars without reference-art identity.");

        var state = new GameStateTracker();
        state.Reset("false-king");
        var unconfirmed = state.Observe(new(at, board, [firstKing], [], true));
        if (unconfirmed.After.Cards.Any(card => card.Card.Id == king.Id))
            throw new InvalidOperationException("A quarantined automatic-arrival candidate leaked into visible board contacts before ledger confirmation.");
        var confirmedEvent = new VisionEvidenceEvent(at.AddSeconds(1), firstKing, "Reviewed automatic arrival.");
        var confirmed = state.Observe(new(at.AddSeconds(1), board, [firstKing], [confirmedEvent], true));
        if (!confirmed.After.Cards.Any(card => card.Card.Id == king.Id))
            throw new InvalidOperationException("A genuinely ledger-confirmed automatic arrival was hidden from board contacts.");

        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        var title1 = await pipeline.PrepareAsync(definition.Load("evidence-03.png"), at.AddSeconds(30));
        var title2 = await pipeline.PrepareAsync(definition.Load("evidence-04.png"), at.AddSeconds(30.2));
        if (title1.HoveredCard?.Id != hunter.Id || title2.HoveredCard?.Id != hunter.Id ||
            title1.Screen.TooltipRegion is null || title2.Screen.TooltipRegion is null)
            throw new InvalidOperationException("The retained exact Witch Hunter opponent-board tooltip title no longer resolves twice.");

        GwentVisualObservation HunterScreen(PreparedVisionFrame frame, int hand) => frame.Screen with
        {
            View = GwentViewKind.Board, MatchHudVisible = true, IsCardSelectionOverlay = false,
            ScreenHeader = null, OpponentHandCount = hand
        };
        var hunterLedger = new MatchVisionLedger();
        var handLedger = new HandCommitTracker();
        var settledEight = HunterScreen(title1, 8) with { HasCardTooltip = false, TooltipRegion = null };
        handLedger.Observe(at.AddSeconds(29), settledEight, hunterLedger.Observe(at.AddSeconds(29), settledEight, [], false));
        var firstTitleEvents = hunterLedger.Observe(at.AddSeconds(30), HunterScreen(title1, 8), [], false,
            title1.HoveredCard, artworkWasScanned: false);
        handLedger.Observe(at.AddSeconds(30), HunterScreen(title1, 8), firstTitleEvents);
        var secondTitleEvents = hunterLedger.Observe(at.AddSeconds(30.2), HunterScreen(title2, 8), [], false,
            title2.HoveredCard, artworkWasScanned: false);
        handLedger.Observe(at.AddSeconds(30.2), HunterScreen(title2, 8), secondTitleEvents);
        if (firstTitleEvents.Count != 0 || secondTitleEvents.Count != 0)
            throw new InvalidOperationException("Exact opponent-board title browsing committed Witch Hunter before a hand spend.");
        var paidScreen = settledEight with { OpponentHandCount = 7 };
        var paid = hunterLedger.Observe(at.AddSeconds(30.4), paidScreen, [], false, artworkWasScanned: false);
        if (paid.Count != 1 || paid[0].Sighting.Card.Id != hunter.Id ||
            paid[0].Sighting.Source != CardSightSource.PlayPreview)
            throw new InvalidOperationException("Repeated exact Witch Hunter board title plus the retained 8→7 opponent hand spend did not recover one play.");
        var originals = handLedger.Observe(at.AddSeconds(30.4), paidScreen, paid);
        var opponent = new LiveDeckTracker(PlayerSide.Opponent);
        if (originals.Count != 1 || !HandCommitTracker.Apply(originals, new(), new(), new(),
                new(PlayerSide.User), opponent, null) ||
            opponent.Observations.SingleOrDefault(item => item.Card.Id == hunter.Id)?.ObservedCopies != 1)
            throw new InvalidOperationException("Recovered Witch Hunter did not enter conservative starting-deck accounting as one paid hand copy.");

        var browsing = new MatchVisionLedger();
        browsing.Observe(at.AddSeconds(40), settledEight, [], false);
        browsing.Observe(at.AddSeconds(40.2), HunterScreen(title1, 8), [], false, hunter, false);
        browsing.Observe(at.AddSeconds(40.4), HunterScreen(title2, 8), [], false, hunter, false);
        if (browsing.Observe(at.AddSeconds(47), paidScreen, [], false, artworkWasScanned: false).Count != 0)
            throw new InvalidOperationException("A stale opponent-board tooltip title captured an unrelated later hand decrement.");
    }
}
