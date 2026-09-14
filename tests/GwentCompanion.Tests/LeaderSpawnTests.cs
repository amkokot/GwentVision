using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class LeaderSpawnTests
{
    private static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var cards = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var engine = new TacticalPlayEngine(cards);
        var leaders = cards.Where(card => card.Kind == CardKind.Leader && card.AbilityText?.Contains("Spawn") == true).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        var named = leaders.SelectMany(leader => LeaderSpawnCatalog.NamedUnits(leader, cards)).DistinctBy(card => card.Id).ToArray();
        Check(named.Length == 15, "Leader named-unit coverage changed; review the catalog and token references: " + string.Join(", ", named.Select(card => card.Name + "#" + card.Id)));
        Check(named.All(card => references.Any(item => item.Card.Id == card.Id)), "A leader-spawned card has no recognition reference.");
        var publicSources = leaders.Concat(cards.Where(card => card.Kind == CardKind.Stratagem &&
            card.AbilityText?.Contains("Spawn", StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        var publicRoutes = publicSources.SelectMany(source => cards.Where(target =>
                LeaderSpawnCatalog.NamesSpawnedCard(source, target)).Select(target => (Source: source, Target: target)))
            .ToArray();
        Check(publicRoutes.Any(route => route.Source.Name == "Uprising" && route.Target.Name == "Lyrian Scytheman"),
            "Uprising's collectible Lyrian Scytheman output was not parsed.");
        Check(publicRoutes.Any(route => route.Source.Name == "Pirate's Cove" && route.Target.Name == "Sea Jackal"),
            "Pirate's Cove's collectible Sea Jackal output was not parsed.");
        Check(publicRoutes.Any(route => route.Source.Name == "Aen Seidhe Saber" && route.Target.Name == "Scoia'tael Neophyte"),
            "Aen Seidhe Saber's collectible Neophyte output was not parsed.");
        Check(publicRoutes.Any(route => route.Source.Name == "Toussaintois Hospitality" && route.Target.Name == "Buhurt" &&
                route.Target.Kind == CardKind.Special && route.Target.CanBeInStartingDeck),
            "Toussaintois Hospitality's collectible Buhurt special output was not parsed.");
        foreach (var route in publicRoutes)
        {
            var resolver = new PlayProvenanceResolver();
            if (route.Source.Kind == CardKind.Leader) resolver.ObserveCurrentLeader(PlayerSide.Opponent, route.Source);
            else resolver.ObserveOpeningStratagem(PlayerSide.Opponent, route.Source);
            var eventAt = new DateTimeOffset(2026, 9, 11, 9, 38, 13, TimeSpan.FromHours(-4));
            var sighting = new CardSighting(route.Target, PlayerSide.Opponent, CardSightSource.PlayPreview,
                new(.80, .11, .93, .43), .08, .45, "public-source fixture");
            var evidence = new VisionEvidenceEvent(eventAt, sighting, "public-source fixture");
            var origin = resolver.Observe(evidence);
            Check(origin.Provenance == CardProvenance.Spawned && (!route.Target.CanBeInStartingDeck ||
                    origin.Reason.Contains(route.Source.Name, StringComparison.Ordinal)),
                $"{route.Source.Name} -> {route.Target.Name} was charged to the starting deck.");
        }

        // A natural copy of a leader output can still be in hand. The persistent
        // public route starts conservatively, but an independently measured hand
        // decrement must supersede it even if the initial art match was stronger.
        var uprising = cards.Single(card => card.Name == "Uprising");
        var scytheman = cards.Single(card => card.Name == "Lyrian Scytheman");
        var handResolver = new PlayProvenanceResolver(); handResolver.ObserveCurrentLeader(PlayerSide.Opponent, uprising);
        var handAt = new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.FromHours(-4));
        var handSighting = new CardSighting(scytheman, PlayerSide.Opponent, CardSightSource.PlayPreview,
            new(.80, .11, .93, .43), .01, .45, "natural-copy fixture");
        var handEvent = new VisionEvidenceEvent(handAt, handSighting, "natural-copy fixture");
        var conservative = handResolver.Observe(handEvent);
        var handOpponent = new LiveDeckTracker(PlayerSide.Opponent);
        handOpponent.ConsiderDirectPlay(scytheman, .99, handAt, conservative.Reason, conservative.Provenance);
        HandCommitTracker.Apply([handEvent], handResolver, new DeckMutationLedger(), new ThinningCopyTracker(),
            new LiveDeckTracker(PlayerSide.User), handOpponent, null);
        Check(handOpponent.Observations.Single(item => item.Card.Id == scytheman.Id).Provenance == CardProvenance.ProbableStartingDeck,
            "Independent hand cost could not restore a natural Lyrian Scytheman copy after conservative leader routing.");

        var hospitality = cards.Single(card => card.Name == "Toussaintois Hospitality");
        var buhurt = cards.Single(card => card.Name == "Buhurt");
        var specialResolver = new PlayProvenanceResolver(); specialResolver.ObserveCurrentLeader(PlayerSide.Opponent, hospitality);
        var specialSighting = handSighting with { Card = buhurt };
        var specialEvent = new VisionEvidenceEvent(handAt, specialSighting, "leader-played collectible special fixture");
        var generatedSpecial = specialResolver.Observe(specialEvent);
        Check(generatedSpecial.Provenance == CardProvenance.Spawned && generatedSpecial.Reason.Contains(hospitality.Name, StringComparison.Ordinal),
            "Toussaintois Hospitality's generated Buhurt was charged as a natural special.");
        var specialOpponent = new LiveDeckTracker(PlayerSide.Opponent);
        specialOpponent.ConsiderDirectPlay(buhurt, .99, handAt, generatedSpecial.Reason, generatedSpecial.Provenance);
        HandCommitTracker.Apply([specialEvent], specialResolver, new DeckMutationLedger(), new ThinningCopyTracker(),
            new LiveDeckTracker(PlayerSide.User), specialOpponent, null);
        Check(specialOpponent.Observations.Single(item => item.Card.Id == buhurt.Id).Provenance == CardProvenance.ProbableStartingDeck,
            "Independent hand cost could not restore a natural Buhurt after conservative leader routing.");

        var precision = cards.Single(card => card.Name == "Precision Strike");
        var sentinel = cards.Single(card => card.Name == "Brokilon Sentinel");
        var precisionAssumption = LeaderSpawnCatalog.StartingDeckAssumptions(precision, cards).Single();
        Check(precisionAssumption.Card.Id == sentinel.Id && precisionAssumption.Copies == 2,
            "Precision Strike did not infer the two natural Brokilon Sentinels that its generated body summons.");
        Check(leaders.Where(leader => leader.Id != precision.Id).SelectMany(leader =>
                LeaderSpawnCatalog.StartingDeckAssumptions(leader, cards)).Count() == 0,
            "The strong two-copy prior spread to a leader without the exact fixed-spawn/self-summon package.");
        var precisionDeck = new LiveDeckTracker(PlayerSide.Opponent); precisionDeck.SetFactionPrior("Scoia'tael");
        precisionDeck.ConsiderDirectPlay(sentinel,.84,handAt,precisionAssumption.Reason,CardProvenance.ProbableStartingDeck);
        precisionDeck.SetObservedCopyLowerBound(sentinel.Id,precisionAssumption.Copies,"Precision Strike package");
        precisionDeck.ConsiderDirectPlay(sentinel,.99,handAt.AddSeconds(1),"Observed leader-created body",CardProvenance.Spawned);
        var retainedSentinels=precisionDeck.DeckBuildingObservations.Single(item=>item.Card.Id==sentinel.Id);
        Check(retainedSentinels.ObservedCopies==2 && retainedSentinels.Provenance==CardProvenance.ProbableStartingDeck,
            "Recognizing Precision Strike's spawned Sentinel erased or triple-counted the two starting copies.");
        var deckSource = cards.First(source => DeckPlayResolutionTracker.IsGenericDeckPlaySource(source) &&
            DeckPlayResolutionTracker.CanSelectFromDeck(source, sentinel));
        var deckResolver = new PlayProvenanceResolver(); deckResolver.ObserveCurrentLeader(PlayerSide.Opponent, precision);
        var sourceSighting = handSighting with { Card = deckSource };
        deckResolver.Observe(new(handAt, sourceSighting, "mandatory deck-play source"));
        var fromDeck = deckResolver.Observe(new(handAt.AddSeconds(1), handSighting with { Card = sentinel }, "matched deck play"));
        Check(fromDeck.Provenance == CardProvenance.ProbableStartingDeck && fromDeck.Reason.Contains("deck origin established", StringComparison.Ordinal),
            "A definite card-driven deck play was overridden by a merely available leader Spawn route.");
        var audit = leaders.Select(leader => new { leader.Id, leader.Name, leader.Faction,
            CalculationSupported = engine.Leader(leader.Id) is not null, VariableCopy = LeaderSpawnCatalog.VariableCopy(leader),
            Units = LeaderSpawnCatalog.NamedUnits(leader, cards).Select(card => new { card.Id, card.Name, card.CanBeInStartingDeck,
                References = references.Count(item => item.Card.Id == card.Id),
                ObservedVariants = references.Count(item => item.Card.Id == card.Id && item.Path.Contains("observed-art")),
                PremiumVariants = references.Count(item => item.Card.Id == card.Id && item.Path.Contains("premium-frames")) }) }).ToArray();
        foreach (var (id, points, charges, name) in new[] { ("202117", 1, 1, "Fruit"), ("202115", 9, 3, "Deadeyes"),
            ("201743", 5, 5, "Drones"), ("132103", 9, 1, "Woodland Spirit"), ("202188", 7, 1, "Dana"), ("202347", 9, 3, "Zealots") })
        {
            var p = GamePosition.EmptyKnown() with { Opponent = GamePosition.EmptyKnown().Opponent with { CurrentLeaderId = id, LeaderCharges = charges } };
            var result = engine.LeaderContribution(p, PlayerSide.Opponent);
            Check(result.Estimate.MaximumPoints == points, $"{name}: expected {points}, got {result.Estimate.MaximumPoints}: {string.Join(';', result.Estimate.Missing)}");
            Check(result.Estimate.After!.Zones.Where(zone => zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).All(card => card.Original == false), "Leader-created body counted as an original card.");
            if (id == "202347") Check(result.Estimate.After.Opponent.Coins == 3, "Congregate coins were lost after reach valuation.");
            Check(engine.LeaderContribution(p with { Opponent = p.Opponent with { LeaderCharges = 0 } }, PlayerSide.Opponent).Estimate.MaximumPoints == 0,
                "Exhausted leader spawned future bodies again.");
            Console.WriteLine($"Leader {name}: separate +{points}; {charges} charge(s); generated origins retained.");
        }
        var foe = cards.Single(card => card.Name == "Fiend");
        var blood = GamePosition.EmptyKnown() with { Opponent = GamePosition.EmptyKnown().Opponent with { CurrentLeaderId = "202185", LeaderCharges = 3 } };
        blood = blood with { Zones = blood.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee
            ? zone with { Cards = [new("enemy", foe.Id, 8, 8, 0, ImmutableHashSet<CardStatus>.Empty, 0, 0)], TotalCount = 1 } : zone).ToImmutableArray() };
        var scent = engine.LeaderContribution(blood, PlayerSide.Opponent);
        Check(scent.Estimate.MaximumPoints == 3 && scent.Estimate.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single().Power == 8,
            "Blood Scent omitted Ekimmara or counted future enemy Bleeding as immediate points.");
        Check(LeaderSpawnCatalog.VariableCopy(cards.Single(card => card.Name == "Imposter")), "Variable copied bodies were omitted from audit.");
        var output = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-leader-spawn-audit.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { NamedUnitIdentities = named.Length,
            Note = "Reference availability and supplied-state rules checks, not proven live recognition for every leader/token. Named references do not prove spawn occurrence or original ownership.", Leaders = audit }, GameStateJournal.Json));
        Console.WriteLine("Leader token audit passed: 15 named identities; variable copies/weather/specials audited separately. " + output);
    }
}
