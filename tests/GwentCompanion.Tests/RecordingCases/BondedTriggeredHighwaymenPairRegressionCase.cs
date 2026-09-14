using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class BondedTriggeredHighwaymenPairRegressionCase : IRecordingValidationCase
{
    public string Id => "bonded-triggered-highwaymen-pair";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var vanguard = catalog.Single(card => card.Id == "202484");
        var highwaymen = catalog.Single(card => card.Id == "202415");
        var ordinary = catalog.Single(card => card.Id == "142202");
        if (!CompanionCardRules.TriggeredAutomaticPairTargets(vanguard).Contains(highwaymen.Id) ||
            CompanionCardRules.TriggeredAutomaticPairTargets(ordinary).Count != 0)
            throw new InvalidOperationException("The Bonded-play to Highwaymen pair-target boundary changed.");

        var references = VisionReferenceLibrary.Load(catalog, cache)
            .Where(reference => reference.Card.Id == highwaymen.Id).ToArray();
        using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"));
        var matcher = new CardArtMatcher(features.ArtReferences);
        var leftBody = new NormalizedRegion(.435, .279, .499, .428);
        var rightBody = new NormalizedRegion(.499, .279, .563, .428);
        var pairDistances = new[]
        {
            matcher.IdentityDistance(definition.Load("evidence-03.png"), leftBody, highwaymen.Id),
            matcher.IdentityDistance(definition.Load("evidence-03.png"), rightBody, highwaymen.Id),
            matcher.IdentityDistance(definition.Load("evidence-04.png"), leftBody, highwaymen.Id),
            matcher.IdentityDistance(definition.Load("evidence-04.png"), rightBody, highwaymen.Id),
        };
        if (pairDistances.Any(distance => distance > .65))
            throw new InvalidOperationException("The retained frames no longer contain two adjacent Highwaymen-like bodies: " +
                string.Join('/', pairDistances.Select(distance => distance.ToString("F3"))));

        var at = DateTimeOffset.UnixEpoch;
        var preview = new CardSighting(vanguard, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .13, .90, .40), 0, 1, "Exact Caravan Vanguard play preview");
        var play = new VisionEvidenceEvent(at, preview, "Exact Caravan Vanguard play preview");
        var before = new GwentVisualObservation(GwentViewKind.Board, false, 9, 1, null)
            { MatchHudVisible = true, OpponentHandCount = 8, OpponentDeckCount = 14 };
        var afterPlay = before with { OpponentHandCount = 7 };
        var afterPair = afterPlay with { OpponentDeckCount = 12 };
        var tooltip = afterPair with
        {
            HasCardTooltip = true,
            TooltipRegion = new(.5416666667, .4285714286, .75, .6428571429)
        };
        var ledger = new MatchVisionLedger();
        if (ledger.Observe(at, before, [preview]).Single().Sighting.Card.Id != vanguard.Id)
            throw new InvalidOperationException("The exact Bonded trigger did not commit.");
        ledger.Observe(at.AddSeconds(1), afterPlay, [], boardWasScanned: false);
        ledger.Observe(at.AddSeconds(2), afterPair, [], boardWasScanned: false);
        if (ledger.Observe(at.AddSeconds(10), tooltip, [], false, highwaymen, false).Count != 0)
            throw new InvalidOperationException("One Highwaymen title claimed a triggered pair.");
        var arrival = ledger.Observe(at.AddSeconds(10.3), tooltip, [], false, highwaymen, false);
        if (arrival.Count != 1 || arrival[0].Sighting.Card.Id != highwaymen.Id || arrival[0].ResolvedDeckCopies != 2 ||
            !arrival[0].Description.Contains("exact two-card deck departure", StringComparison.Ordinal))
            throw new InvalidOperationException("Bonded trigger, 14→12 conservation and repeated exact Highwaymen title did not resolve two copies.");
        var origin = new OpponentKnowledge().BoardOrigin(arrival[0], "Neutral") ??
            throw new InvalidOperationException("The triggered Highwaymen arrival did not receive starting-deck provenance.");
        var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        var copies = new ThinningCopyTracker();
        tracker.ConsiderDirectPlay(highwaymen, 1, arrival[0].ObservedAt, arrival[0].Description, origin.Provenance);
        copies.ObserveEvent(arrival[0], origin.Provenance, tracker);
        var observed = tracker.DeckBuildingObservations.SingleOrDefault(card => card.Card.Id == highwaymen.Id);
        if (observed?.ObservedCopies != 2 || observed.Provenance != CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("The resolved Highwaymen event did not establish two probable starting copies.");

        MatchVisionLedger Setup(bool includeTrigger, int deckAfter)
        {
            var candidate = new MatchVisionLedger();
            candidate.Observe(at, before, includeTrigger ? [preview] : [], boardWasScanned: false);
            candidate.Observe(at.AddSeconds(1), afterPlay, [], false);
            candidate.Observe(at.AddSeconds(2), afterPlay with { OpponentDeckCount = deckAfter }, [], false);
            candidate.Observe(at.AddSeconds(10), tooltip with { OpponentDeckCount = deckAfter }, [], false, highwaymen, false);
            return candidate;
        }
        if (Setup(false, 12).Observe(at.AddSeconds(10.3), tooltip, [], false, highwaymen, false).Count != 0)
            throw new InvalidOperationException("Two-card conservation and title manufactured Highwaymen without a Bonded trigger.");
        if (Setup(true, 13).Observe(at.AddSeconds(10.3), tooltip with { OpponentDeckCount = 13 }, [], false, highwaymen, false).Count != 0)
            throw new InvalidOperationException("A one-card departure was promoted to two Highwaymen copies.");
        return Task.CompletedTask;
    }
}
