using System.IO;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class CurrentMatchAuditTests
{
    private static void Check(bool okay, string message)
    { if (!okay) throw new InvalidOperationException(message); }

    private static PixelFrame Load(string path)
    {
        using var file = File.OpenRead(path);
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0]);
    }

    public static void Run(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        CardDefinition Card(string id) => catalog.Single(card => card.Id == id);
        var yennefer = Card("201780"); var gascon = Card("202099");
        var warCouncil = Card("202447"); var battlePreparation = Card("202454");
        var at = new DateTimeOffset(2026, 9, 1, 10, 30, 11, TimeSpan.FromHours(-4));
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null) { MatchHudVisible = true };
        CardSighting Preview(CardDefinition card, PlayerSide side = PlayerSide.Opponent, bool weak = false) =>
            new(card, side, CardSightSource.PlayPreview, new(.80, .11, .93, .43), weak ? .34 : .08, .45,
                "fixture", weak);

        var ledger = new MatchVisionLedger();
        var source = ledger.Observe(at, screen, [Preview(yennefer)], artworkWasScanned: true);
        Check(source.Count == 1 && source[0].Sighting.Source == CardSightSource.PlayPreview,
            "Yennefer source play was not retained.");
        Check(ledger.Observe(at.AddSeconds(2), screen, [Preview(gascon, weak: true)], artworkWasScanned: true).Count == 0,
            "One weak reveal frame was accepted without corroboration.");
        var revealed = ledger.Observe(at.AddSeconds(2.4), screen, [Preview(gascon, weak: true)], artworkWasScanned: true);
        Check(revealed.Count == 1 && revealed[0].Sighting.Card.Id == gascon.Id &&
              revealed[0].Sighting.Source == CardSightSource.DeckReveal &&
              revealed[0].Sighting.Side == PlayerSide.Opponent,
            "Yennefer's revealed Gascon became a play or lost opponent-deck ownership.");
        var hands = new HandCommitTracker();
        hands.Observe(at.AddSeconds(2.4), screen with { OpponentHandCount = 5 }, revealed);
        Check(hands.Observe(at.AddSeconds(3), screen with { OpponentHandCount = 4 }, []).Count == 0,
            "Deck reveal opened an opponent hand-commit route.");

        // Opponent-deck wording must change identity ownership even though the
        // animation still occupies the revealer's visual lane.
        var alchemist = Card("202798"); var userUnit = Card("202099");
        ledger.Reset(); ledger.Observe(at, screen, [Preview(alchemist)], artworkWasScanned: true);
        var stolenView = ledger.Observe(at.AddSeconds(2), screen, [Preview(userUnit)], artworkWasScanned: true);
        Check(stolenView.Single().Sighting.Source == CardSightSource.DeckReveal &&
              stolenView[0].Sighting.Side == PlayerSide.User,
            "Opponent-deck reveal did not assign the revealed identity to the user's deck.");
        foreach (var route in new[]
        {
            (Source: Card("112106"), Target: gascon, Owner: PlayerSide.Opponent), // Triss
            (Source: Card("162108"), Target: gascon, Owner: PlayerSide.Opponent), // Xarthisius unit branch
            (Source: Card("132407"), Target: gascon, Owner: PlayerSide.User), // Imperial Golem
            (Source: Card("200124"), Target: battlePreparation, Owner: PlayerSide.User), // Kingslayer banish
            (Source: Card("202798"), Target: gascon, Owner: PlayerSide.User), // Viper Alchemist
        })
        {
            var routeLedger = new MatchVisionLedger();
            routeLedger.Observe(at, screen, [Preview(route.Source)], artworkWasScanned: true);
            var routeEvent = routeLedger.Observe(at.AddSeconds(2), screen, [Preview(route.Target)], artworkWasScanned: true).Single();
            Check(routeEvent.Sighting.Source == CardSightSource.DeckReveal && routeEvent.Sighting.Side == route.Owner,
                $"Catalog reveal route failed for {route.Source.Name}.");
        }
        var xarthisius = new MatchVisionLedger();
        xarthisius.Observe(at, screen, [Preview(Card("162108"))], artworkWasScanned: true);
        Check(xarthisius.Observe(at.AddSeconds(2), screen, [Preview(battlePreparation)], artworkWasScanned: true)
                .Single().Sighting.Source == CardSightSource.PlayPreview,
            "Xarthisius' played non-unit branch was mislabeled as reveal-only evidence.");

        var origins = new PlayProvenanceResolver();
        var councilEvent = new VisionEvidenceEvent(at, Preview(warCouncil), "fixture");
        var prepEvent = new VisionEvidenceEvent(at.AddMinutes(2), Preview(battlePreparation), "fixture");
        Check(origins.Observe(councilEvent).Provenance == CardProvenance.ProbableStartingDeck,
            "War Council itself lost ordinary original-deck provenance.");
        var generated = origins.Observe(prepEvent);
        Check(generated.Provenance == CardProvenance.Spawned && generated.Reason.Contains("named Spawn", StringComparison.Ordinal),
            "Persistent named Location Order did not mark Battle Preparation as spawned.");
        Check(origins.Observe(prepEvent with { ObservedAt = at.AddMinutes(3) }).Provenance == CardProvenance.ProbableStartingDeck,
            "A consumed one-use Order permanently tainted an independent later copy.");

        // Fresh full-catalog art: this clear, moving reveal was missed by the old
        // fixed crop. The geometry locator plus ordinary identity gates must recover it.
        var framePath = Path.Combine(root,
            "GwentCompanion/sessions/20260901-102039/frame-005753-103015122.jpg");
        using var features = new FeatureCardRecognizer(VisionReferenceLibrary.Load(catalog, cache),
            Path.Combine(cache, "recognition-features"));
        features.SetLikelyOpponentCards(new[] { "202099", "202401", "202121", "202484", "202447", "201780", "162108",
            "202409", "202408", "203242", "200154", "203040", "202217", "200018" });
        var revealFrame = Load(framePath);
        var featureSightings = features.Recognize(revealFrame, screen, includeBoard: false);
        var fallback = new CardFrameRecognizer(new CardArtMatcher(features.ArtReferences));
        var fallbackSightings = fallback.Recognize(revealFrame, screen, false, featureSightings);
        var sightings = featureSightings.Concat(fallbackSightings).ToArray();
        Check(sightings.Any(sighting => sighting.Card.Id == gascon.Id &&
                                      sighting.Source == CardSightSource.DeckRevealCandidate &&
                                      sighting.Side == PlayerSide.Opponent),
            "Fresh full-catalog reveal pixels did not recover Gascon in the opponent visual lane.");
        var pixelLedger = new MatchVisionLedger();
        pixelLedger.Observe(at, screen, [Preview(yennefer)], artworkWasScanned: true);
        var pixelReveal = pixelLedger.Observe(at.AddSeconds(2), screen, sightings, artworkWasScanned: true);
        Check(pixelReveal.Count == 1 && pixelReveal[0].Sighting.Card.Id == gascon.Id &&
              pixelReveal[0].Sighting.Source == CardSightSource.DeckReveal,
            "Fresh context-gated reveal pixels did not confirm Gascon as deck evidence.");

        Console.WriteLine("PASS current audit: Gascon reveal pixels/provenance, opponent-deck ownership, hand isolation, and persistent one-use named Order generation.");
    }
}
