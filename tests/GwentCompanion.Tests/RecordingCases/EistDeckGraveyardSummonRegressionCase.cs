using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class EistDeckGraveyardSummonRegressionCase : IRecordingValidationCase
{
    public string Id => "eist-deck-graveyard-summon";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var eist = catalog.Single(card => card.Id == "202883");
        var evolvedTyr = catalog.Single(card => card.Id == "203205");
        var baseTyr = catalog.Single(card => card.Id == "203194");
        var anglerfish = catalog.Single(card => card.Id == "203217");

        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var pipeline = new CardVisionPipeline(references, catalog,
            Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck([]);
        pipeline.SetLikelyOpponentCards([eist.Id, evolvedTyr.Id, baseTyr.Id, anglerfish.Id]);

        var at = DateTimeOffset.UnixEpoch;
        var tyrTitle1 = await pipeline.PrepareAsync(definition.Load("evidence-01.png"), at.AddSeconds(20));
        var tyrTitle2 = await pipeline.PrepareAsync(definition.Load("evidence-02.png"), at.AddSeconds(20.3));
        if (tyrTitle1.HoveredCard?.Id != evolvedTyr.Id || tyrTitle2.HoveredCard?.Id != evolvedTyr.Id)
            throw new InvalidOperationException("The retained exact Tyr: Master of An Skellig board titles no longer resolve.");

        GwentVisualObservation TooltipScreen(PreparedVisionFrame prepared) => prepared.Screen with
        {
            View = GwentViewKind.Board,
            MatchHudVisible = true,
            IsCardSelectionOverlay = false,
            HasCardTooltip = true,
            OpponentHandCount = 5,
            OpponentDeckCount = 4,
            TooltipRegion = prepared.Screen.TooltipRegion ??
                throw new InvalidOperationException("Retained Tyr tooltip geometry was not found.")
        };
        var before = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 5, OpponentDeckCount = 5 };
        var after = before with { OpponentDeckCount = 4 };
        var eistPreview = new CardSighting(eist, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), 0, 1, "Exact Eist play preview", NeedsTemporalConfirmation: false);

        var ledger = new MatchVisionLedger();
        var eistEvents = ledger.Observe(at, before, [eistPreview]);
        if (eistEvents.Count != 1 || eistEvents[0].Sighting.Card.Id != eist.Id)
            throw new InvalidOperationException("The Eist source event did not arm the deck-to-graveyard summon route.");
        ledger.Observe(at.AddSeconds(10), before, [], boardWasScanned: false);
        ledger.Observe(at.AddSeconds(12), after, [], boardWasScanned: false);
        if (ledger.Observe(at.AddSeconds(20), TooltipScreen(tyrTitle1), [], boardWasScanned: false,
                confirmedHover: evolvedTyr, artworkWasScanned: false).Count != 0)
            throw new InvalidOperationException("One Tyr board title committed without temporal title repetition.");
        var arrival = ledger.Observe(at.AddSeconds(20.3), TooltipScreen(tyrTitle2), [], boardWasScanned: false,
            confirmedHover: evolvedTyr, artworkWasScanned: false);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != evolvedTyr.Id ||
            arrival[0].Sighting.Source != CardSightSource.Board ||
            !arrival[0].Description.Contains("deck-to-graveyard-to-board summon", StringComparison.Ordinal))
            throw new InvalidOperationException("Eist plus the conserved deck departure and repeated exact Tyr title did not recover the summon.");
        var origin = new OpponentKnowledge().BoardOrigin(arrival[0], "Skellige") ??
            throw new InvalidOperationException("The recovered Eist target did not receive a starting-deck origin.");
        var starting = EvolvingCardCatalog.StartingIdentity(arrival[0].Sighting, origin, catalog, "Skellige");
        if (starting.Card.Id != baseTyr.Id || starting.Origin.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Evolved Tyr was not accounted as the 14-provision starting Tyr identity.");

        using var anglerFeatures = new FeatureCardRecognizer(references.Where(reference => reference.Card.Id == anglerfish.Id),
            Path.Combine(cache, "recognition-features"));
        var anglerMatcher = new CardArtMatcher(anglerFeatures.ArtReferences);
        var falseRegion1 = new NormalizedRegion(.546, .255, .606, .400);
        var falseRegion2 = new NormalizedRegion(.509, .314, .569, .459);
        var falseDistance1 = anglerMatcher.IdentityDistance(definition.Load("evidence-03.png"), falseRegion1, anglerfish.Id);
        var falseDistance2 = anglerMatcher.IdentityDistance(definition.Load("evidence-04.png"), falseRegion2, anglerfish.Id);
        if (falseDistance1 <= .60 || falseDistance2 <= .60)
            throw new InvalidOperationException($"A retained Anglerfish-negative crop became visually plausible ({falseDistance1:F3}/{falseDistance2:F3}).");
        CardSighting FalseAngler(NormalizedRegion region, double appearance) =>
            new(anglerfish, PlayerSide.Opponent, CardSightSource.Board, region, .40, 1,
                $"One geometrically bounded anchor; appearance distance {appearance:F3} · bounded automatic-arrival board fallback; repetition and deck conservation still required",
                NeedsTemporalConfirmation: true);

        // Even if the conserved departure remains available, two noisy one-anchor
        // candidates that jump between different board bodies cannot claim it.
        var falseLedger = new MatchVisionLedger();
        falseLedger.Observe(at, before, [], boardWasScanned: false);
        falseLedger.Observe(at.AddSeconds(2), after, [], boardWasScanned: false);
        if (falseLedger.Observe(at.AddSeconds(3), after, [FalseAngler(falseRegion1, falseDistance1)], true).Count != 0 ||
            falseLedger.Observe(at.AddSeconds(4.5), after, [FalseAngler(falseRegion2, falseDistance2)], true).Count != 0)
            throw new InvalidOperationException("Spatially inconsistent weak Anglerfish fallbacks reused an unrelated deck departure.");

        var noEist = new MatchVisionLedger();
        noEist.Observe(at, before, [], false);
        noEist.Observe(at.AddSeconds(2), after, [], false);
        noEist.Observe(at.AddSeconds(3), TooltipScreen(tyrTitle1), [], false, evolvedTyr, false);
        if (noEist.Observe(at.AddSeconds(3.3), TooltipScreen(tyrTitle2), [], false, evolvedTyr, false).Count != 0)
            throw new InvalidOperationException("Repeated board title plus deck conservation manufactured an Eist summon without Eist.");
    }
}
