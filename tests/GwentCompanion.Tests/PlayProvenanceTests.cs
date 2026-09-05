using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

internal static class PlayProvenanceTests
{
    public static void Run(string gameRoot)
    {
        var catalog = GwentOneCardCatalog.Load(System.IO.Path.Combine(gameRoot, "GwentCompanion/cache/gwent-one-cards.json"))
            .ToDictionary(card => card.Id);
        var resolver = new PlayProvenanceResolver();
        var at = DateTimeOffset.Parse("2026-08-27T10:22:25-04:00");
        var opponent = new LiveDeckTracker(PlayerSide.Opponent);
        var user = new LiveDeckTracker(PlayerSide.User);
        var observations = new[]
        {
            Event("203071", -55), // Scenario is starting-deck evidence.
            Event("203127", -50, source: CardSightSource.Board), // Its spawned Dawn Aspect.
            Event("203169", 0), // Generated special with 15 printed provisions, not a second 15-provision card.
            Event("202915", 6), // Bronze creation candidate: retain identity, not starting-deck claim.
            Event("122102", 90, PlayerSide.User), // User's spy.
            Event("122102", 92, source: CardSightSource.Board), // Same spy, opponent controller.
        };
        foreach (var evidence in observations)
        {
            var origin = resolver.Observe(evidence);
            var tracker = evidence.Sighting.Side == PlayerSide.User ? user : opponent;
            tracker.ConsiderDirectPlay(evidence.Sighting.Card, .9, evidence.ObservedAt, origin.Reason.Length == 0 ? "Preview" : origin.Reason, origin.Provenance);
        }
        Check(StartingDeckRules.ProbableProvisionLowerBound(opponent.DeckBuildingObservations) == catalog["203071"].Provision,
            "Generated cards and the user's spy must not inflate opponent provisions.");
        Check(opponent.Observations.Single(item => item.Card.Id == "202915").Provenance == CardProvenance.Unknown,
            "A plausible creation remains visible but unresolved.");
        Check(user.Observations.Single().Provenance == CardProvenance.ProbableStartingDeck &&
              opponent.Observations.Single(item => item.Card.Id == "122102").Provenance == CardProvenance.Unknown,
            "User and opponent caches must retain independent origins for a Disloyal card.");

        resolver.Reset();
        Check(resolver.Observe(Event("202915", 6)).Provenance == CardProvenance.ProbableStartingDeck, "Reset clears creation hints.");
        resolver.Observe(Event("203169", 10));
        Check(resolver.Observe(Event("202915", 41)).Provenance == CardProvenance.ProbableStartingDeck, "Old creation hints expire.");
        resolver.Observe(Event("203169", 50));
        resolver.Observe(Event("122102", 51, PlayerSide.User));
        Check(resolver.Observe(Event("202915", 56)).Provenance == CardProvenance.Unknown, "An interleaved other-side reading must not erase the pending source.");
        resolver.Reset();
        resolver.Observe(Event("203169", 60));
        Check(resolver.Observe(Event("202676", 65)).Provenance == CardProvenance.ProbableStartingDeck, "A gold Dunca cannot be this bronze creation.");
        Check(resolver.Observe(Event("202915", 67)).Provenance == CardProvenance.ProbableStartingDeck, "Any next preview consumes the hint.");
        resolver.Observe(Event("203071", 70));
        Check(resolver.Observe(Event("202915", 75)).Provenance == CardProvenance.ProbableStartingDeck, "Shared scenario artwork must not start a creation hint.");
        resolver.Observe(Event("203169", 80));
        resolver.Observe(Event("202676", 82, source: CardSightSource.Board));
        Check(resolver.Observe(Event("202915", 86)).Provenance == CardProvenance.Unknown, "Background board evidence does not consume a play hint.");

        VisionEvidenceEvent Event(string id, int seconds, PlayerSide side = PlayerSide.Opponent, CardSightSource source = CardSightSource.PlayPreview)
            => new(at.AddSeconds(seconds), new CardSighting(catalog[id], side, source, new NormalizedRegion(.8, .1, .9, .4), .1, 1, "Fixture"), "Fixture");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
