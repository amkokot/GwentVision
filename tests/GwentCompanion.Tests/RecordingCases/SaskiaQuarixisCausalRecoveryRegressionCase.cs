using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class SaskiaQuarixisCausalRecoveryRegressionCase : IRecordingValidationCase
{
    public string Id => "saskia-quarixis-causal-recovery";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var saskia = catalog.Single(card => card.Id == "203090");
        var abandoned = catalog.Single(card => card.Id == "202681");
        var quarixis = catalog.Single(card => card.Id == "203196");
        var miner = catalog.Single(card => card.Name == "Miner");
        var pool = CompanionCardRules.RecurringSummonPool(saskia, catalog);
        if (!pool.Contains(abandoned.Id))
            throw new InvalidOperationException("Saskia's generalized legal-target pool lost Abandoned Girl.");

        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
            scope: VisionReferenceScope.CandidateDecks);
        features.ObserveOpponentSummonCandidates(pool);
        features.ObserveOpponentCards([saskia.Id, abandoned.Id]);
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 14 };
        var boardSightings = features.Recognize(definition.Load("evidence-02.png"), board, includeBoard: true);
        if (boardSightings.Any(item => item.Card.Id == miner.Id))
            throw new InvalidOperationException("The retained Abandoned Girl body was still accepted as Miner after appearance verification.");
        var appearance = new CardArtMatcher(features.ArtReferences).RankAligned(definition.Load("evidence-02.png"),
            new(.50, .134, .568, .270), 3);
        if (appearance.Count == 0 || appearance[0].Card.Id != abandoned.Id || appearance[0].Distance > .55 ||
            appearance.Count > 1 && appearance[1].Distance - appearance[0].Distance < .12)
            throw new InvalidOperationException("Appearance competition no longer identifies the far-right Saskia arrival as Abandoned Girl.");

        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards(pool.Append(saskia.Id).Append(quarixis.Id));
        var at = DateTimeOffset.UnixEpoch;
        using (var missedSourcePipeline = new CardVisionPipeline(references, catalog,
                   Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks))
        {
            missedSourcePipeline.SetKnownPlayerDeck([]);
            missedSourcePipeline.SetLikelyOpponentCards([]);
            var sourcePopup = board with { HasCardTooltip = true, TooltipRegion = new(.542, .286, .708, .643),
                IsCardSelectionOverlay = false, ScreenHeader = null };
            var firstSource = missedSourcePipeline.Commit(new CardVisionResult(at, sourcePopup, [], [], false, false,
                HoveredCard: saskia));
            var sourceOnlyReferences = missedSourcePipeline.CachedReferenceImages + missedSourcePipeline.ComputedReferenceImages;
            var secondSource = missedSourcePipeline.Commit(new CardVisionResult(at.AddSeconds(.3), sourcePopup, [], [], false, false,
                HoveredCard: saskia));
            if (firstSource.Events.Count != 0 || secondSource.Events is not [{ Sighting.Source: CardSightSource.Board }] ||
                secondSource.Events[0].Sighting.Card.Id != saskia.Id)
                throw new InvalidOperationException("The production pipeline did not recover missed-preview Saskia as board presence.");
            if (missedSourcePipeline.CachedReferenceImages + missedSourcePipeline.ComputedReferenceImages <= sourceOnlyReferences)
                throw new InvalidOperationException("Recovered Saskia did not expand the production recognizer to its legal summon pool.");
            missedSourcePipeline.Commit(new CardVisionResult(at.AddSeconds(1),
                board with { OpponentHandCount = 8, OpponentDeckCount = 13 }, [], [], false, false));
            var targetPopup = board with { OpponentHandCount = 8, OpponentDeckCount = 13, HasCardTooltip = true,
                TooltipRegion = new(.583, .286, .750, .500), IsCardSelectionOverlay = false, ScreenHeader = null };
            var firstTarget = missedSourcePipeline.Commit(new CardVisionResult(at.AddSeconds(1.2), targetPopup, [], [], false, false,
                HoveredCard: abandoned));
            if (firstTarget.Events.Count != 0)
                throw new InvalidOperationException("One target title bypassed confirmation after production recovered Saskia.");
            var recoveredTarget = missedSourcePipeline.Commit(new CardVisionResult(at.AddSeconds(1.5), targetPopup, [], [], false, false,
                HoveredCard: abandoned));
            if (recoveredTarget.Events is not [{ Sighting.Source: CardSightSource.Board, ResolvedDeckCopies: 1 }] ||
                recoveredTarget.Events[0].Sighting.Card.Id != abandoned.Id)
                throw new InvalidOperationException("Production did not join recovered Saskia to its confirmed deck summon.");
            var laterHandDrop = missedSourcePipeline.Commit(new CardVisionResult(at.AddSeconds(2),
                board with { OpponentHandCount = 7, OpponentDeckCount = 13 }, [], [], false, false));
            if (laterHandDrop.Events.Any(item => item.Sighting.Card.Id == abandoned.Id &&
                    item.Sighting.Source == CardSightSource.PlayPreview))
                throw new InvalidOperationException("A confirmed Saskia summon was later reclassified as a hand play.");
        }
        var abandonedPopup = await pipeline.PrepareAsync(definition.Load("evidence-04.png"), at.AddSeconds(30));
        if (abandonedPopup.HoveredCard?.Id != abandoned.Id)
            throw new InvalidOperationException("The retained exact Abandoned Girl board tooltip no longer resolves.");

        var summonLedger = new MatchVisionLedger();
        var source = new CardSighting(saskia, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.815, .136, .915, .399), .08, 1, "Exact visible preview title");
        summonLedger.Observe(at, board with { OpponentDeckCount = 15 }, [source], boardWasScanned: false);
        summonLedger.Observe(at.AddSeconds(1), board, [], boardWasScanned: false);
        if (summonLedger.Observe(at.AddSeconds(12), board, boardSightings, true).Count != 0 ||
            summonLedger.Observe(at.AddSeconds(12.3), board, boardSightings, true).Count != 0)
            throw new InvalidOperationException("Provisional Saskia targets became generic board evidence before causal resolution.");
        var popupScreen = abandonedPopup.Screen with { View = GwentViewKind.Board, MatchHudVisible = true,
            IsCardSelectionOverlay = false, OpponentHandCount = 8, OpponentDeckCount = 14, ScreenHeader = null,
            HasCardTooltip = true, TooltipRegion = abandonedPopup.Screen.TooltipRegion ?? new(.583, .286, .75, .50) };
        if (summonLedger.Observe(at.AddSeconds(30), popupScreen, [], false, abandoned, false).Count != 0)
            throw new InvalidOperationException("One Saskia-target title bypassed temporal confirmation.");
        var arrival = summonLedger.Observe(at.AddSeconds(30.3), popupScreen, [], false, abandoned, false);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != abandoned.Id || arrival[0].ResolvedDeckCopies != 1)
            throw new InvalidOperationException("Saskia plus the 15→14 conserved pile move and repeated exact target title did not recover one Abandoned Girl.");

        var q1 = await pipeline.PrepareAsync(definition.Load("evidence-05.png"), at.AddSeconds(100));
        var q2 = await pipeline.PrepareAsync(definition.Load("evidence-06.png"), at.AddSeconds(100.2));
        var q3 = await pipeline.PrepareAsync(definition.Load("evidence-07.png"), at.AddSeconds(101));
        if (q1.HoveredCard is not null || q2.HoveredCard?.Id != quarixis.Id || q3.HoveredCard?.Id != quarixis.Id)
            throw new InvalidOperationException("QUARIHIS did not remain quarantined for one frame and then resolve as Quarixis on recurrence.");
        var playLedger = new MatchVisionLedger();
        GwentVisualObservation QScreen(PreparedVisionFrame prepared, int? hand) => prepared.Screen with
        {
            View = GwentViewKind.Board, MatchHudVisible = true, IsCardSelectionOverlay = false,
            OpponentHandCount = hand, ScreenHeader = null, HasCardTooltip = true,
            TooltipRegion = prepared.Screen.TooltipRegion ?? new(.667, .214, .833, .429)
        };
        playLedger.Observe(at.AddSeconds(100), QScreen(q1, 3), [], false, q1.HoveredCard, false);
        playLedger.Observe(at.AddSeconds(100.2), QScreen(q2, 3), [], false, q2.HoveredCard, false);
        playLedger.Observe(at.AddSeconds(101), QScreen(q3, 3), [], false, q3.HoveredCard, false);
        var paid = playLedger.Observe(at.AddSeconds(101.3), QScreen(q3, 2) with
            { HasCardTooltip = false, TooltipRegion = null }, [], false, null, false);
        if (paid.Count != 1 || paid[0].Sighting.Card.Id != quarixis.Id ||
            paid[0].Sighting.Source != CardSightSource.PlayPreview)
            throw new InvalidOperationException("Repeated Quarixis popup plus the 3→2 opponent hand payment did not recover exactly one play.");
    }
}
