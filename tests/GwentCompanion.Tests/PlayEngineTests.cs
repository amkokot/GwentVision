using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Capture;
using System.Windows.Media.Imaging;

internal static class PlayEngineTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static PositionCard Card(CardDefinition card, string id, int? power = null, int armor = 0, bool? original = true, params CardStatus[] status) =>
        new(id, card.Id, power ?? card.Power, card.Power, armor, PlayRules.Compile(card).PrintedStatuses!.Union(status), 0, 0, original);
    private static GamePosition Put(GamePosition p, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] cards) => p with
    { Zones = p.Zones.Select(item => item.Side == side && item.Zone == zone && item.Row == row ? item with { Cards = cards.ToImmutableArray(), Complete = true, TotalCount = cards.Length } : item).ToImmutableArray() };
    public static void Run(string root)
    {
        var watch = Stopwatch.StartNew();
        var actual = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var body = new CardDefinition("test-body", "Test body", "Monsters", CardKind.Unit, 4, 5, AbilityText: "", PrintedArmor: 0);
        var big = body with { Id = "test-big", Name = "Test expensive body", Power = 9, Provision = 10, IsGold = true };
        var dwarf = body with { Id = "test-dwarf", Name = "Test Dwarf", Faction = "Scoia'tael", Categories = new HashSet<string> { "Dwarf" } };
        var elf = body with { Id = "test-elf", Name = "Test Elf", Faction = "Scoia'tael", Categories = new HashSet<string> { "Elf" } };
        var dryad = body with { Id = "test-dryad", Name = "Test Dryad", Faction = "Scoia'tael", Categories = new HashSet<string> { "Dryad" } };
        var treant = body with { Id = "test-treant", Name = "Test Treant", Faction = "Scoia'tael", Categories = new HashSet<string> { "Treant" } };
        var soldier = body with { Id = "test-soldier", Name = "Test Soldier", Faction = "Northern Realms", Categories = new HashSet<string> { "Soldier" } };
        var tactic = new CardDefinition("test-tactic", "Test Tactic", "Nilfgaard", CardKind.Special, 0, 5,
            CardCategories: new HashSet<string> { "Tactic" }, AbilityText: "Damage a unit by 5.");
        var filaSpecial = new CardDefinition("test-fila-special", "Test Filavandrel special", "Scoia'tael", CardKind.Special, 4,
            AbilityText: "");
        var blindeye = body with { Id = "test-blindeye", Name = "Test Blindeye", Faction = "Syndicate", Categories = new HashSet<string> { "Blindeyes" } };
        var artifact = new CardDefinition("test-artifact", "Test artifact", "Neutral", CardKind.Artifact, 4, AbilityText: "");
        var agentWarrior = body with { Id = "test-agent-warrior", Name = "Test agent Warrior", Power = 4, Provision = 4,
            Faction = "Skellige", Categories = new HashSet<string> { "Warrior" } };
        var agentSpy = body with { Id = "test-agent-spy", Name = "Test agent Disloyal", Power = 1, Provision = 4,
            Faction = "Nilfgaard", IsGold = false, AbilityText = "Disloyal. Deploy: Damage an enemy unit by 3." };
        var agentAlchemy = new CardDefinition("test-agent-alchemy", "Test agent Alchemy", "Skellige", CardKind.Special, 4,
            CardCategories: new HashSet<string> { "Alchemy" }, AbilityText: "Boost an allied unit by 3.");
        var agentTactic = new CardDefinition("test-agent-tactic", "Test agent Tactic", "Nilfgaard", CardKind.Special, 4,
            CardCategories: new HashSet<string> { "Tactic" }, AbilityText: "Damage an enemy unit by 5.");
        var agentBomb = new CardDefinition("test-agent-bomb", "Test agent Bomb", "Neutral", CardKind.Special, 4,
            CardCategories: new HashSet<string> { "Bomb" }, AbilityText: "No ability.");
        var catalog = actual.Concat([body, big, dwarf, elf, dryad, treant, soldier, tactic, filaSpecial, blindeye, artifact,
            agentWarrior, agentSpy, agentAlchemy, agentTactic, agentBomb]).ToArray();
        CardDefinition Get(string name) => catalog.Single(card => card.Name == name);
        var engine = new TacticalPlayEngine(catalog);
        GamePosition Hand(string name) => Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null, Card(Get(name), "play"));
        PlaySearchResult Max(GamePosition p) => engine.Maximum(p, "play", PlayerSide.User);
        void Points(GamePosition p, int value, string label)
        {
            var result = Max(p);
            Check(result.MaximumPoints == value, $"{label}: expected {value}; got {result.MaximumPoints}/{result.BestModeledPoints}: {string.Join("; ", result.Missing)} · {string.Join(" | ", result.Line)}");
        }
        var thunder = Put(Hand("Alzur's Thunder"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 8, 2));
        Points(thunder, 3, "Armor absorbs damage");
        Points(Put(thunder, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 8, 2, true, CardStatus.Shield)), 0, "Shield blocks hit");
        Points(Put(thunder, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 2)), 2, "Overkill capped");
        var protectedRow = Put(thunder, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(big, "enemy", 20), Card(body, "defender", 1, 0, true, CardStatus.Defender));
        Points(protectedRow, 1, "Defender target gate");
        Points(Put(Hand("Geralt of Rivia"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy", 17, 9, true, CardStatus.Shield)), 20, "Destroy bypasses armor/Shield");
        Points(Put(Hand("Swallow"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy")), -6, "Enemy boost is negative swing");
        var rowDamage = Put(Hand("Lacerate"), PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, Card(body, "a", 1), Card(body, "b", 8, 1), Card(body, "c", 5));
        Points(rowDamage, 4, "AoE values per target");
        Points(Put(Hand("Mardroeme"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "a", 2)), -2, "Dead Mardroeme target cannot receive boost");
        Points(Put(Hand("Mardroeme"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "a", 5, 3)), 9, "Mardroeme armor synergy");

        var rider = Get("Wild Hunt Rider");
        var riders = Put(Hand("Wild Hunt Rider"), PlayerSide.User, CardZone.Deck, null, Card(rider, "copy"));
        Points(riders, rider.Power, "Entering Rider does not supply its own Dominance");
        riders = Put(riders, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "dominance"));
        Points(riders, 2 * rider.Power, "Dominance thins second Rider");
        Check(Max(riders).After!.Zone(PlayerSide.User, CardZone.Deck).Cards.Length == 0, "Summoned copy remained in draw pile");
        var volunteers = Put(Hand("Mahakam Volunteers"), PlayerSide.User, CardZone.Deck, null, Card(Get("Mahakam Volunteers"), "copy"));
        Points(volunteers, Get("Mahakam Volunteers").Power, "Entering Volunteer not its own precondition");
        Points(Put(volunteers, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(dwarf, "dwarf")), 2 * Get("Mahakam Volunteers").Power, "Dwarf enables pair");
        var roach = Put(Hand(big.Name), PlayerSide.User, CardZone.Deck, null, Card(Get("Roach"), "roach"));
        Points(roach, big.Power + Get("Roach").Power, "Gold pulls Roach");
        Points(Put(Hand(big.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Roach"), "roach")), big.Power, "Roach already on board not counted again");
        var thrive = Put(Hand(big.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Phooca"), "thrive"));
        Points(thrive, big.Power + 2, "Thrive reaction");
        Points(Put(thrive, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Phooca"), "thrive", status: [CardStatus.Locked])), big.Power, "Lock suppresses Thrive");
        Points(Hand("Nekker"), 2 * Get("Nekker").Power, "Spawn copy without Deploy recursion");
        var assimilate = Put(Hand("Swallow"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Glynnis aep Loernach"), "engine"), Card(body, "ally"));
        Points(assimilate, 6, "Original spell does not Assimilate");
        assimilate = Put(assimilate, PlayerSide.User, CardZone.Hand, null, Card(Get("Swallow"), "play", original: false));
        Points(assimilate, 8, "Generated spell triggers Assimilate");
        var nature = Put(Hand("Tempering"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Young Dryad"), "dryad"), Card(dwarf, "ally"));
        nature = nature with { User = nature.User with { CurrentLeaderId = "200165", LeaderCharges = 0 } };
        Points(nature, 7, "Nature's Gift adds one Symbiosis");
        var seductress = Put(Hand(body.Name), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(Get("Sly Seductress"), "seductress"));
        Points(seductress, body.Power - 1, "Opponent reacts to played unit");

        var egg = Get("Harpy Egg"); var toad = Get("Giant Toad");
        var toadPosition = Put(Hand("Harpy Egg"), PlayerSide.User, CardZone.Graveyard, null, Card(toad, "toad"));
        Points(toadPosition, egg.Power + toad.Power + Get("Harpy").Power, "Toad ranged summon consumes played Deathwish");
        var toadResult = Max(toadPosition);
        Check(toadResult.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Any(card => card.CardId == toad.Id && card.Statuses!.Contains(CardStatus.Doomed)), "Toad not on ranged row / Doomed missing");
        var consumed = Put(Hand("Giant Toad"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(egg, "egg"));
        Points(consumed, toad.Power + Get("Harpy").Power, "Consume does not double-count old board power");
        Points(Put(consumed, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(egg, "egg", status: [CardStatus.Doomed])), toad.Power + Get("Harpy").Power, "Destroyed Doomed still triggers Deathwish");
        var banish = Put(Hand("Korathi Heatwave"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(egg, "egg"));
        Points(banish, egg.Power, "Direct banishment bypasses Deathwish");
        var tome = Put(toadPosition, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Necromancer's Tome"), "tome"));
        tome = Put(tome, PlayerSide.User, CardZone.Graveyard, null, Card(toad, "toad"), Card(egg, "egg-copy"));
        Points(tome, egg.Power * 2 + toad.Power + Get("Harpy").Power, "Tome board reaction precedes Toad graveyard reaction");
        var ozzrel = Put(Hand("Ozzrel"), PlayerSide.Opponent, CardZone.Graveyard, null, Card(big, "meal"));
        Points(ozzrel, Get("Ozzrel").Power + big.Power, "Ozzrel chooses opponent graveyard/melee");
        Check(Max(ozzrel).After!.Zone(PlayerSide.Opponent, CardZone.Banished).Cards.Length == 1, "Graveyard consumption must banish");
        var rite = Put(Hand("Sigrdrifa's Rite"), PlayerSide.User, CardZone.Graveyard, null, Card(rider, "rider"));
        rite = Put(rite, PlayerSide.User, CardZone.Deck, null, Card(rider, "copy"));
        Points(rite, rider.Power, "Resurrection summon does not run Deploy");
        var renew = Put(Hand("Renew"), PlayerSide.User, CardZone.Graveyard, null,
            Card(body, "renew-bronze"), Card(big, "renew-too-expensive"));
        Points(renew, body.Power, "Renew uses the known own-graveyard provision filter");
        var blueDream = Put(Hand("Hanmarvyn's Blue Dream"), PlayerSide.Opponent, CardZone.Graveyard, null,
            Card(body, "dream-target"), Card(big, "dream-too-expensive"));
        Points(blueDream, body.Power, "Blue Dream uses the opponent graveyard and provision ceiling");
        var remedy = Put(Hand("Experimental Remedy"), PlayerSide.Opponent, CardZone.Graveyard, null,
            Card(body, "remedy-bronze"), Card(big, "remedy-gold"));
        Points(remedy, body.Power, "Experimental Remedy excludes gold opponent-graveyard units");
        var hillock = Put(Hand("Whispering Hillock"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "ordinary-unit"), Card(egg, "deathwish-unit"));
        Points(hillock, egg.Power, "Deathwish tutor filters the present deck inventory");
        var decree = Put(Hand("Royal Decree"), PlayerSide.User, CardZone.Deck, null, Card(big, "big"), Card(Get("Roach"), "roach"));
        Points(decree, big.Power + Get("Roach").Power, "Tutor plus offboard summon chain");
        var elves = Enumerable.Range(0, 5).Select(i => Card(elf, "elf" + i)).ToArray();
        var aelirenn = Put(Put(Hand(body.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee, elves), PlayerSide.User, CardZone.Deck, null, Card(Get("Aelirenn"), "aelirenn"));
        Points(aelirenn, body.Power + Get("Aelirenn").Power, "End-turn Aelirenn");
        var full = Put(Put(toadPosition, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Enumerable.Range(0, 8).Select(i => Card(body, "full" + i)).ToArray()),
            PlayerSide.User, CardZone.Board, BoardRow.Melee, Enumerable.Range(0, 9).Select(i => Card(body, "other" + i)).ToArray());
        Points(full, egg.Power, "Toad cannot summon into ninth-slot row before Consume");

        var leaderPosition = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "ally"));
        leaderPosition = leaderPosition with { Opponent = leaderPosition.Opponent with { CurrentLeaderId = "131101", LeaderCharges = 3 } };
        var leaderResult = engine.MaximumLeader(leaderPosition, PlayerSide.Opponent);
        Check(leaderResult.Estimate.MaximumPoints == 9 && leaderResult.ChargesUsed == 3, "Carapace maximum/charges wrong");
        Check(engine.MaximumLeader(leaderPosition, PlayerSide.Opponent, 5).ChargesUsed == 2, "Did not choose least sufficient leader charges");
        leaderPosition = leaderPosition with { Opponent = leaderPosition.Opponent with { CurrentLeaderId = "200165" } };
        Check(engine.MaximumLeader(leaderPosition, PlayerSide.Opponent).Estimate.MaximumPoints == 1, "Stacked Vitality should tick once, not six immediate points");
        leaderPosition = Put(leaderPosition, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "one"), Card(body, "two"), Card(body, "three"));
        Check(engine.MaximumLeader(leaderPosition, PlayerSide.Opponent).Estimate.MaximumPoints == 3, "Vitality target optimization");
        Check(engine.LeaderContribution(leaderPosition, PlayerSide.Opponent).Estimate.MaximumPoints == 3, "Obvious new Vitality ticks missing from separate leader addition");
        var existingVitality = Put(leaderPosition, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "already-vital") with { Statuses = ImmutableHashSet.Create(CardStatus.Vitality), StatusTurns = ImmutableDictionary<CardStatus, int>.Empty.Add(CardStatus.Vitality, 2) });
        existingVitality = existingVitality with { Opponent = existingVitality.Opponent with { CurrentLeaderId = "131101" } };
        Check(engine.LeaderContribution(existingVitality, PlayerSide.Opponent).Estimate.MaximumPoints == 9,
            "Ordinary Vitality was double-counted in card plus standalone leader addition");
        var coins = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Graveyard, null, Card(Get("The Flying Redanian"), "ship"));
        coins = coins with { Opponent = coins.Opponent with { CurrentLeaderId = "202577", LeaderCharges = 1, Coins = 4 } };
        Check(engine.MaximumLeader(coins, PlayerSide.Opponent).Estimate.MaximumPoints == 3 + Get("The Flying Redanian").Power,
            "Hidden Cache coin reach/hoard reduction/resurrection");
        Check(engine.MaximumLeader(coins with { Zones = GamePosition.EmptyKnown().Zones }, PlayerSide.Opponent).Estimate.MaximumPoints == 3,
            "New Syndicate Coins were not valued at one point of reach each");

        Points(Hand("Pickpocket"), 8, "Pure Profit is immediate reach");
        var pontar = Put(Hand("Dip in the Pontar"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "coin-enemy"));
        Points(pontar, 6, "Profit plus direct damage");
        var blacksmith = Put(Hand("Coerced Blacksmith"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "coin-ally"));
        blacksmith = blacksmith with { User = blacksmith.User with { Coins = 4 } };
        Points(blacksmith, Get("Coerced Blacksmith").Power + 6, "Profit and repeatable one-to-one Fee conversion");
        var guard = Put(Hand("Oxenfurt Guard"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "guard-left"), Card(body, "guard-right"));
        guard = guard with { User = guard.User with { Coins = 4 } };
        Points(guard, Get("Oxenfurt Guard").Power + 6, "Oxenfurt Guard empty-pouch multiplier and optimal adjacency");
        var jackal = Hand("Sea Jackal") with { User = GamePosition.EmptyKnown().User with { Coins = 7 } };
        Points(jackal, Get("Sea Jackal").Power + 7, "Sea Jackal Hoard conversion");
        var drill = Put(Hand("Tunnel Drill"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "drill-target", 12));
        drill = drill with { User = drill.User with { Coins = 2 } };
        Points(drill, Get("Tunnel Drill").Power + 3, "Tunnel Drill Profit and one immediate Cooldown-limited Fee");
        var renewable = engine.Leader("202117");
        Check(renewable?.Commitment.Contains("Renewable") == true, "Renewable leader resource-cost annotation missing");
        Check(engine.MaximumLeader(leaderPosition with { Opponent = leaderPosition.Opponent with { LeaderCharges = 0 } }, PlayerSide.Opponent).ChargesUsed == 0, "Exhausted leader reused");

        var unmodeled = Put(thunder, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Ravanen Kimbolt"), "kimbolt"));
        Check(Max(unmodeled).MaximumPoints is null && Max(unmodeled).Missing.Count > 0, "Unmodeled board listener silently ignored");
        Check(Max(thunder with { Zones = thunder.Zones.Select(zone => zone.Zone == CardZone.Board ? zone with { Cards = zone.Cards.Select(card => card with { Power = null }).ToImmutableArray() } : zone).ToImmutableArray() }).MaximumPoints is null, "Unknown power treated as zero");
        var before = PositionNotation.Write(toadPosition); Max(toadPosition);
        Check(before == PositionNotation.Write(toadPosition), "Search mutated observed state");
        Check(PositionNotation.Write(PositionNotation.Read(PositionNotation.Write(leaderResult.Estimate.After!))) == PositionNotation.Write(leaderResult.Estimate.After!), "Extended notation round trip");
        var limited = engine.MaximumLeader(leaderPosition, PlayerSide.Opponent, branchLimit: 32);
        Check(!limited.Estimate.SearchComplete && limited.Estimate.MaximumPoints is null, "Truncated search claimed maximum");

        var reply6 = body with { Id = "reply6", Name = "Cheap reply", Power = 6, Provision = 4 };
        var reply9 = big with { Id = "reply9", Name = "Expensive reply" };
        var responseCatalog = catalog.Concat([reply6, reply9]).ToArray();
        var report = new ThreatAnalyzer(responseCatalog).Analyze(new(Hand(body.Name), "play", []), 1,
            [new(reply9, .7, "Hypothesized"), new(reply6, .8, "Hypothesized")]);
        Check(report.GapAfterPlay == 6 && report.Replies.Count == 2 && report.Replies[0].Candidate.Card.Id == reply6.Id, "Catch-up equality/provision sort");
        Check(report.Summary is { } summary && summary.MinimumAheadProvisions == reply9.Provision && summary.MaximumCardSwing == reply9.Power && summary.EvaluatedCards == 2,
            "Threat summary confused tying with getting ahead, or combined leader points with card maximum.");
        var shortList = ThreatAnalyzer.LikelyOptions(report, 1);
        Check(shortList.Count == 1 && shortList[0].Candidate.Card.Id == reply9.Id, "Shortlist omitted cheapest actual way ahead in favor of a tie.");
        Check(ThreatAnalyzer.LikelyOptions(report, 0).Count == 0, "Zero-length shortlist not empty.");
        var withLeader = Hand(body.Name) with { Opponent = GamePosition.EmptyKnown().Opponent with { CurrentLeaderId = "131101", LeaderCharges = 3 } };
        withLeader = Put(withLeader, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "leader-target"));
        var separated = new ThreatAnalyzer(responseCatalog).Analyze(new(withLeader, "play", []), 1, [new(reply6, .8, "Hypothesized")]);
        Check(separated.Replies.Single() is { UsesLeader: true, LeaderCharges: > 0 } && separated.Summary?.MinimumAheadProvisions == reply6.Provision &&
            separated.Summary?.MaximumCardSwing == 6, "Leader-assisted catch-up was omitted or leaked into the card-only maximum.");
        var handChance = OpponentReachModel.ConditionalHandChance(3, 7, 2);
        Check(handChance is > .53 and < .54, "Conditional remaining-copy hand availability is incorrect.");
        var evidenceCandidate = new ThreatCandidate(reply6, .8, "Working deck", 2, handChance);
        var assessed = OpponentReachModel.Assess(6, [new(evidenceCandidate, 6, false, 0, true, [], "test")], [evidenceCandidate],
            new(4, 6, 1, 1), [], ReachValidationProfile.ReviewedFootage);
        Check(assessed is { Ease: "tie only", Reliability: "high", ExactModels: 1, AnswerCopies: 2, LeadAnswerCopies: 0 } && assessed.Detail.Contains("10/10 reviewed chosen actions exact"),
            "Opponent reach ease/reliability did not combine availability, model coverage and footage calibration.");
        var leadAssessed = OpponentReachModel.Assess(5, [new(evidenceCandidate, 6, false, 0, true, [], "test")], [evidenceCandidate],
            new(4, 6, 1, 1), [], ReachValidationProfile.ReviewedFootage);
        Check(leadAssessed is { Ease: "reachable", LeadAnswerIdentities: 1, LeadAnswerCopies: 2 },
            "Strict lead-taking reach was not separated from tie-only reach.");
        var evolvedPosition = Hand(body.Name) with { Round = 3 };
        var evolvedReport = new ThreatAnalyzer(catalog).Analyze(new(evolvedPosition, "play", []), -4,
            [new(Get("Auberon: King"), .8, "Hypothesized starting-deck card")]);
        Check(evolvedReport.Replies.Any(reply => reply.Candidate.Card.Name == "Auberon: Conqueror" &&
                reply.Candidate.Availability.Contains("round 3 evolving form")),
            "Opponent reach did not replace a starting evolving identity with its current-round form.");
        Check(new ThreatAnalyzer(responseCatalog).Analyze(new(Hand(body.Name), "play", []), -10, [new(reply9, .7, "Hypothesized")]).Replies.Count == 0, "Behind should show gap, not misleading catch-up list");
        var estimatedReply = big with { Id = "estimated-reply", Name = "Estimated reply", Power = 8,
            AbilityText = "At the end of your next turn, boost self by 20." };
        var estimatedReport = new ThreatAnalyzer(responseCatalog.Append(estimatedReply)).Analyze(new(Hand(body.Name), "play", []), 1,
            [new(estimatedReply, .9, "Hypothesized")]);
        Check(estimatedReport.Replies.Single() is { Points: 8, Conditional: true } estimatedThreat &&
            estimatedThreat.Note.StartsWith("Estimated board-aware range") && estimatedReport.Summary is { EstimatedCards: 1 },
            "Unsupported candidate did not fall back to a clearly conditional direct-body estimate");
        var unsupportedHover = body with { Id = "unsupported-hover", Name = "Unsupported hover", AbilityText = "At the end of your next turn, boost self by 20." };
        var hoverPosition = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null, Card(unsupportedHover, "unsupported-play"));
        var hoverReport = new ThreatAnalyzer(responseCatalog.Append(unsupportedHover)).Analyze(new(hoverPosition, "unsupported-play", []), 2, []);
        Check(hoverReport.PlayerPlay.MaximumPoints is null && hoverReport.PlayerPlay.Approximation is { Maximum: 5, Quality: PointEstimateQuality.BaselineOnly } &&
            hoverReport.EstimatedGapAfterPlay == 7 && hoverReport.Replies.Count == 0,
            "Unsupported hovered card did not expose a body-only range without fabricating a post-play state");
        var fallbackReport = new ThreatAnalyzer(responseCatalog.Append(estimatedReply).Append(unsupportedHover)).Analyze(
            new(hoverPosition, "unsupported-play", ["partial board fixture"]), 2, [new(estimatedReply, .9, "Hypothesized")]);
        Check(fallbackReport.ReplyGap == 7 && fallbackReport.Replies.Single() is { Points: 8, Conditional: true } &&
            fallbackReport.Summary is { MinimumAheadProvisions: 10, EstimatedCards: 1 } &&
            fallbackReport.Assumptions.Any(note => note.Contains("bounded one-card estimates")),
            "Partial-board hover fallback did not retain approximate opponent reach");
        var ownDeck = new DeckDefinition("own", "Own", "Monsters", "", 0, [new(body, 2)]);
        var built = ThreatPositionBuilder.Build(GamePosition.EmptyKnown(), catalog, body, ownDeck, [], []);
        Check(built.Position.Zone(PlayerSide.User, CardZone.Deck).Cards.Length == 1 && built.Position.Zone(PlayerSide.User, CardZone.Hand).Cards.Length == 1, "Hovered copy double-counted");
        var spentAt = DateTimeOffset.UnixEpoch;
        var spent = new ObservedCard(reply6, CardProvenance.ProbableStartingDeck, 1, spentAt, "recorded plays", 2);
        var graveEvidence = new ZoneHypothesis(reply6.Id, PlayerSide.Opponent, CardZone.Graveyard, 1, true, spentAt, "reviewed graveyard");
        var exhausted = ThreatPositionBuilder.Build(GamePosition.EmptyKnown(), responseCatalog, body, ownDeck,
            [new DeckCard(reply6, 2)], [graveEvidence], opponentPlayed: [spent]);
        Check(exhausted.Position.Zone(PlayerSide.Opponent, CardZone.Graveyard).Cards.Count(card => card.CardId == reply6.Id) == 1 &&
            exhausted.Position.Zone(PlayerSide.Opponent, CardZone.Deck).Cards.All(card => card.CardId != reply6.Id),
            "Reviewed graveyard plus played-copy history did not exhaust the opponent's two-copy candidate.");
        Check(HoverTitleReader.Read("ALZUR'S THUNDER\nDamage a unit by 5.", catalog)?.Name == "Alzur's Thunder", "Hover title parse");
        Check(HoverTitleReader.Read("Deploy: Spawn a Harpy Egg on this row.", catalog) is null, "Ability text mistaken for hovered identity");
        Check(actual.All(card => PlayRules.Compile(card) is not null), "Catalog compilation failed");
        Points(Put(Hand("Field Medic"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "left"), Card(body, "right")),
            Get("Field Medic").Power + 4, "Search chooses adjacency pocket");
        Points(Hand("Griffin"), 0, "Griffin destroys itself without a target");
        var ghoulGraveyard = Put(Hand("Ghoul"), PlayerSide.User, CardZone.Graveyard, null,
            Card(Get("Fiend"), "grave-fiend"), Card(Get("Griffin"), "grave-griffin"), Card(Get("Golyat"), "grave-gold"));
        Points(ghoulGraveyard, Get("Ghoul").Power + Get("Griffin").Power,
            "Ghoul must consume the highest-power bronze unit in the graveyard and exclude golds");
        var ghoulPartial = ghoulGraveyard with { Zones = ghoulGraveyard.Zones.Select(zone =>
            zone.Side == PlayerSide.User && zone.Zone == CardZone.Graveyard ? zone with { Complete = false } : zone).ToImmutableArray() };
        Check(new ApproximatePointModel(catalog).Estimate(Get("Ghoul"), ghoulPartial, PlayerSide.User) is
            { Maximum: 11, Quality: PointEstimateQuality.Bounded } ghoulEstimate &&
            ghoulEstimate.Notes.Any(note => note.Contains("highest-power eligible known graveyard unit")),
            "Ghoul fallback ignored the largest known bronze graveyard target");
        Points(Put(Hand("Fiend"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "enemy")), Get("Fiend").Power,
            "Fiend chooses unoccupied opposite row");
        var schirru = Put(Hand("Schirrú"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "target", Get("Schirrú").Power));
        Points(schirru, 2 * Get("Schirrú").Power, "Zeal Schirru order");
        Check(engine.ImmediateMaximum(schirru, "play", PlayerSide.User).MaximumPoints == 2 * Get("Schirrú").Power,
            "Live one-card estimate lost the played card's own ready Order.");
        var nenneke = Put(Hand("Nenneke"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "nenneke-target"));
        Points(nenneke, Get("Nenneke").Power + 4, "Generic row-bound Zeal Order spends all printed charges");
        Points(Hand("Tuirseach Invader") with { Round = 2 }, Get("Tuirseach Invader").Power + 1,
            "Veteran adjusts the played base body from current round state");
        var livingArmor = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Living Armor"), "play", armor: Get("Living Armor").PrintedArmor ?? 0));
        Points(livingArmor, Get("Living Armor").PrintedArmor ?? 0, "Living Armor enters at current Armor rather than printed power");
        var immortal = Put(Hand("Alzur's Thunder"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("Olgierd: Immortal"), "immortal"));
        Points(immortal, 0, "Olgierd Immortal ignores power-changing damage");
        var wolfsbane = Put(Hand("Wolfsbane"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "set-target", 8));
        Points(wolfsbane, 7, "Set-power effect values current board power");
        var mandrake = Put(Hand("Mandrake"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "reset-target", 9));
        Points(mandrake, 4, "Reset effect uses the target's current and base powers");
        var existingOrder = Put(Hand(body.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Miner"), "existing-order") with { Charges = 1, Cooldown = 0 });
        Check(engine.ImmediateMaximum(existingOrder, "play", PlayerSide.User).MaximumPoints == body.Power &&
            engine.Maximum(existingOrder, "play", PlayerSide.User).MaximumPoints == body.Power + 2,
            "Live one-card estimate searched unrelated board Orders, or full engine lost them.");
        var moreReplies = Enumerable.Range(0, 36).Select(i => new ThreatCandidate(reply9 with { Id = "candidate-" + i, Name = "Candidate " + i }, .8, "Hypothesized")).ToArray();
        var cheapCheck = new ThreatCandidate(reply6, .05, "Hypothesized");
        var tallCheck = new ThreatCandidate(Get("Korathi Heatwave"), .06, "Hypothesized");
        var shortlistAnalyzer = new ThreatAnalyzer(responseCatalog.Concat(moreReplies.Select(item => item.Card)));
        var liveCandidates = shortlistAnalyzer.LiveCandidates(moreReplies.Concat([cheapCheck, tallCheck]));
        Check(liveCandidates.Count == 30 && liveCandidates.Contains(cheapCheck) && liveCandidates.Contains(tallCheck), "Candidate cap dropped a cheap reality check or tall punish.");
        var longChain = Put(Hand("Oneiromancy"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Oneiromancy"), "second-tutor"), Card(body, "tutor-body"));
        var immediateChain = engine.ImmediateMaximum(longChain, "play", PlayerSide.User);
        Check(immediateChain.MaximumPoints is null && immediateChain.Missing.Any(message => message.Contains("Long play chain")),
            "Live long chain was explored or a truncated line became a maximum.");
        Check(engine.Maximum(longChain, "play", PlayerSide.User).MaximumPoints == body.Power, "Live limit leaked into full replay engine.");
        var hound = Put(Hand(big.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Wild Hunt Hound"), "hound"));
        Points(hound, big.Power + 1, "Dominance end-turn subscription");
        var cleanup = Put(GamePosition.EmptyKnown() with { Round = 1 }, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(egg, "egg"), Card(body, "retained", 9, 2, true, CardStatus.Resilience), Card(body, "doomed", status: [CardStatus.Doomed]));
        cleanup = cleanup with { User = cleanup.User with { RoundsWon = 0 }, Opponent = cleanup.Opponent with { RoundsWon = 0, Coins = 7, CurrentLeaderId = "202577", LeaderCharges = 0 } };
        var nextRound = RoundTransitions.Advance(cleanup, catalog, PlayerSide.Opponent).Position;
        Check(nextRound.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Single().CardId == egg.Id &&
            nextRound.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single() is { Power: 5, Armor: 0 } &&
            nextRound.Zone(PlayerSide.User, CardZone.Banished).Cards.Length == 1 && nextRound.Opponent is { RoundsWon: 1, Coins: 3, LeaderCharges: 1 },
            "Round cleanup/resilience/coins/round result; must not trigger Harpy Egg Deathwish");
        var phoenixGrave = Put(GamePosition.EmptyKnown() with { Round = 1 }, PlayerSide.User, CardZone.Graveyard, null,
            Card(Get("Phoenix"), "round-phoenix"));
        var phoenixRound = RoundTransitions.Advance(phoenixGrave, catalog);
        Check(phoenixRound.Position.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Length == 0 &&
            phoenixRound.Position.Zone(PlayerSide.User, CardZone.Banished).Cards.Single().CardId == Get("Phoenix").Id &&
            phoenixRound.Position.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single() is
                { CardId: "202114", Charges: 1, Cooldown: 0 } hatchling && hatchling.Statuses!.Contains(CardStatus.Doomed) &&
            phoenixRound.Unresolved.Any(note => note.Contains("random row")),
            "Round-start Phoenix did not banish itself and create a ready Doomed Hatchling without inventing a random row");
        var echo = Put(GamePosition.EmptyKnown() with { Round = 1 }, PlayerSide.User, CardZone.Graveyard, null, Card(Get("Oneiromancy"), "echo"));
        var echoed = RoundTransitions.Advance(echo, catalog).Position.Zone(PlayerSide.User, CardZone.Deck).Cards.Single();
        Check(echoed.Statuses!.Contains(CardStatus.Doomed), "Echo return to deck lost Doomed");
        var scoreState = new GameStateTracker().Current with { At = DateTimeOffset.UnixEpoch, Phase = GamePhase.Playing,
            User = new(PlayerSide.User, Score: new(15, DateTimeOffset.UnixEpoch, 1, EvidenceKind.Reviewed, "test")),
            Opponent = new(PlayerSide.Opponent, Score: new(8, DateTimeOffset.UnixEpoch, 1, EvidenceKind.Reviewed, "test")) };
        Check(ThreatAnalyzer.UserLead(scoreState) == 7, "Scoreboard debt/lead sign inverted in app bridge");
        UiShellTests.Navigation(root); PlaysSectionTests.Run(root);
        var sergeant = Hand("Nauzicaa Sergeant");
        Points(sergeant with { Round = 1 }, Get("Nauzicaa Sergeant").Power, "Round-one gate skips Sergeant special");
        Check(Max(sergeant).MaximumPoints is null, "Unknown round result assumed to satisfy Sergeant");
        Points(sergeant with { Opponent = sergeant.Opponent with { RoundsWon = 1 } }, Get("Nauzicaa Sergeant").Power + 6, "Round-loss gate enables Battle Preparation on Soldier");
        var brigade = Put(sergeant with { Round = 1 }, PlayerSide.User, CardZone.Deck, null, Card(Get("Nauzicaa Brigade"), "brigade"));
        Points(brigade, Get("Nauzicaa Sergeant").Power, "Round-one gate skips Brigade");
        Points(brigade with { Round = 2, Opponent = brigade.Opponent with { RoundsWon = 1 } },
            Get("Nauzicaa Sergeant").Power + 6 + Get("Nauzicaa Brigade").Power, "Soldier plus lost-round Brigade");
        var idleDeck = Put(Hand(body.Name), PlayerSide.User, CardZone.Deck, null, Enumerable.Range(0, 50).Select(i => Card(body, "idle" + i)).ToArray());
        Check(Max(idleDeck).Branches == Max(Hand(body.Name)).Branches, "Irrelevant draw-pile cards consumed trigger-search branches");
        var idleToad = Put(Hand(body.Name), PlayerSide.User, CardZone.Graveyard, null, Card(toad, "idle-toad"));
        Check(Max(idleToad).Branches == Max(Hand(body.Name)).Branches && !Max(idleToad).Line.Any(line => line.Contains("Giant Toad")), "Non-Deathwish play evaluated irrelevant Toad trigger");
        var approximate = new ApproximatePointModel(catalog);
        var approximateBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "estimate-ally"));
        approximateBoard = Put(approximateBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "estimate-enemy", 8, 2));
        Check(approximate.Estimate(Get("Alzur's Thunder"), approximateBoard, PlayerSide.User) is { Maximum: 3, Quality: PointEstimateQuality.StateAware },
            "Direct estimator did not respect armor");
        Check(approximate.Estimate(Get("Geralt of Rivia"), Put(approximateBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "estimate-tall", 12)), PlayerSide.User)?.Maximum == Get("Geralt of Rivia").Power + 12,
            "Direct estimator missed visible tall removal");
        Check(approximate.Estimate(Get("Nekker"), approximateBoard, PlayerSide.User)?.Maximum == 2 * Get("Nekker").Power,
            "Direct estimator double-counted or missed the spawned self copy");
        var approximateGraveyard = Put(approximateBoard, PlayerSide.Opponent, CardZone.Graveyard, null,
            Card(Get("Fiend"), "opponent-bronze"), Card(Get("Golyat"), "opponent-gold"));
        Check(approximate.Estimate(Get("Experimental Remedy"), approximateGraveyard, PlayerSide.User) is { Maximum: 8, Quality: PointEstimateQuality.StateAware },
            "Fallback did not read the opponent graveyard or apply the bronze filter");
        var damagedImmortal = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("Olgierd: Immortal"), "approximate-immortal"));
        Check(approximate.Estimate(Get("Alzur's Thunder"), damagedImmortal, PlayerSide.User)?.Maximum == 0,
            "Fallback damage changed Olgierd: Immortal");
        var veteranFallback = approximate.Estimate(Get("Tuirseach Invader"), GamePosition.EmptyKnown() with { Round = 3 }, PlayerSide.User);
        Check(veteranFallback?.Maximum == Get("Tuirseach Invader").Power + 2, "Fallback body ignored Veteran round state");
        var armorFallback = approximate.Estimate(Get("Living Armor"), GamePosition.EmptyKnown(), PlayerSide.User);
        Check(armorFallback?.Maximum == Get("Living Armor").PrintedArmor, "Fallback body ignored Living Armor's power invariant");
        var frostBoth = ImmutableArray.Create(
            new PositionRowEffect(PlayerSide.Opponent, BoardRow.Melee, "Frost", 2),
            new PositionRowEffect(PlayerSide.Opponent, BoardRow.Ranged, "Frost", 2));
        var queen = Put(Hand(body.Name), PlayerSide.User, CardZone.Deck, null, Card(Get("Winter Queen"), "winter-queen")) with
        { RowEffectsKnownInactive = false, RowEffects = frostBoth };
        Points(queen, body.Power + Get("Winter Queen").Power, "Both-row Frost summons Winter Queen at end of turn");
        var rainBoth = ImmutableArray.Create(
            new PositionRowEffect(PlayerSide.Opponent, BoardRow.Melee, "Rain", 2),
            new PositionRowEffect(PlayerSide.Opponent, BoardRow.Ranged, "Storm", 2));
        var fish = Put(Hand(body.Name), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Anglerfish"), "anglerfish-a"), Card(Get("Anglerfish"), "anglerfish-b")) with
        { RowEffectsKnownInactive = false, RowEffects = rainBoth };
        Points(fish, body.Power + 2 * Get("Anglerfish").Power, "Both-row Rain/Storm summons every Anglerfish copy");
        Points(Hand("Anglerfish"), 0, "Played Anglerfish returns to the deck when neither enemy row has Rain or Storm");
        var roche = Put(Hand("Vernon Roche"), PlayerSide.User, CardZone.Deck, null, Card(body, "roche-top-a"), Card(body, "roche-top-b"));
        Points(roche, 2 * body.Power - Get("Vernon Roche").Power, "Vernon plays two ordered top cards and places its Disloyal body opposite");
        var joachim = Put(Hand("Joachim de Wett"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Vernon Roche"), "joachim-skip"), Card(body, "joachim-target"));
        Points(joachim, body.Power + 8 - Get("Joachim de Wett").Power, "Joachim skips the top Disloyal unit and boosts the fetched body");
        var simlas = Put(Hand("Simlas Finn aep Dabairr"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Alzur's Thunder"), "simlas-a"), Card(Get("Alzur's Thunder"), "simlas-b"));
        simlas = Put(simlas, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "simlas-target"));
        Points(simlas, Get("Simlas Finn aep Dabairr").Power + big.Power, "Simlas plays every known copy of the selected bronze special");
        var tempest = Put(Hand("Tempest"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Biting Frost"), "tempest-a"), Card(Get("Biting Frost"), "tempest-b"), Card(Get("Winter Queen"), "tempest-queen")) with
        { RowEffects = [], RowEffectsKnownInactive = true };
        Points(tempest, Get("Winter Queen").Power, "Tempest can split two Frosts across rows and trigger Winter Queen");
        var goldenNekker = Put(Hand("Golden Nekker"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "gn-unit"), Card(Get("Alzur's Thunder"), "gn-special"), Card(artifact, "gn-artifact"));
        goldenNekker = Put(goldenNekker, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "gn-target"));
        goldenNekker = goldenNekker with { User = goldenNekker.User with { StartingDeckIds =
            ImmutableHashSet.Create(Get("Golden Nekker").Id, body.Id, Get("Alzur's Thunder").Id, artifact.Id) } };
        Points(goldenNekker, 2 * body.Power, "Golden Nekker resolves top unit, special, and artifact under the known starting-deck condition");
        var phoenixPlay = Max(Hand("Phoenix"));
        Check(phoenixPlay.MaximumPoints == Get("Phoenix").Power + 1 && phoenixPlay.After is not null &&
            engine.ResolveEndTurn(phoenixPlay.After, PlayerSide.User).Points == 1,
            "Phoenix did not separate its body, first Vitality tick, and second pass-safe Vitality tick");
        Points(Hand("Phoenix Hatchling"), Get("Phoenix").Power, "Phoenix Hatchling's Zeal Order transforms it into Phoenix without inventing Vitality");
        var berserkerPosition = Put(Hand("Drummond Berserker"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "berserker-target"));
        var berserkerPlay = Max(berserkerPosition);
        Check(berserkerPlay.MaximumPoints == Get("Drummond Berserker").Power && berserkerPlay.After is not null &&
            engine.ResolveEndTurn(berserkerPlay.After, PlayerSide.User).Points == 3 &&
            engine.ResolveEndTurn(berserkerPlay.After, PlayerSide.User).After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards
                .Single(card => card.InstanceId == "play").CardId == Get("Bear Abomination").Id,
            "Drummond Berserker's second automatic window did not damage, cross Berserk 3, and transform into a 6-power Bear");
        var hen = Put(Hand("Hen Gaidth Sword"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "hen-target", 8));
        hen = hen with { CardValues = [new(PlayerSide.User, Get("Hen Gaidth Sword").Id, "stored-soul", StoredCardId: body.Id)] };
        Points(hen, 5 + body.Power, "Hen Gaidth adds the previously stored base copy after its 5 damage");
        var henDeathblow = Put(Hand("Hen Gaidth Sword"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "hen-deathblow", 3));
        var henCapture = Max(henDeathblow);
        Check(henCapture.MaximumPoints == 3 && henCapture.After?.Zone(PlayerSide.Opponent, CardZone.Banished).Cards.Single().CardId == body.Id &&
            henCapture.After.CardValues?.Single(value => value.Kind == "stored-soul").StoredCardId == body.Id,
            "Hen Gaidth Deathblow did not banish and persist the captured identity for its next play");
        Check(PositionNotation.Write(PositionNotation.Read(PositionNotation.Write(henCapture.After!))) == PositionNotation.Write(henCapture.After!),
            "Stored cross-zone card values were lost by compact position notation");
        var frogs = Put(Hand("Frog Mating Season"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(body, "frog-target-a"), Card(body, "frog-target-b"));
        var frogResult = Max(frogs);
        Check(frogResult.MaximumPoints == 10 && frogResult.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Count(card => card.CardId == "203153") == 4 &&
            frogResult.After.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Where(card => card.InstanceId.StartsWith("frog-target"))
                .All(card => card.StatusTurns?.GetValueOrDefault(CardStatus.Vitality) == 1),
            "Frog Mating Season did not insert four adjacent Frogs and consume two extra Vitality triggers per selected unit");
        var frogValue = new CandidatePointEvaluator(catalog).Evaluate(frogs, Get("Frog Mating Season"), PlayerSide.User, "play");
        var frogHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(frogValue, new(3, []));
        Check(frogHorizons is { CardOnly.Maximum: 4, OneTurn.Maximum: 10 },
            "Frog X included Vitality or Frog triggers, or Y omitted the first automatic adjacency window");
        var aerondight = Put(Hand("Aerondight"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "aerondight-ally"));
        aerondight = Put(aerondight, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "aerondight-target", 2));
        aerondight = aerondight with { CardValues = [new(PlayerSide.User, "203102", "damage", 5, 5)] };
        var aerondightResult = Max(aerondight);
        Check(aerondightResult.MaximumPoints == 5 && aerondightResult.After?.CardValues?.Single(value => value.CardId == "203102").Minimum == 6,
            "Aerondight did not transfer overkill and grow after ending the turn ahead");
        var afterPass = aerondightResult.After! with { User = aerondightResult.After!.User with { Passed = true } };
        Check(engine.ResolveEndTurn(afterPass, PlayerSide.User).After?.CardValues?.Single(value => value.CardId == "203102").Minimum == 7,
            "Aerondight did not grow when a pass ended the turn while ahead");
        var knickers = Put(Hand(body.Name), PlayerSide.User, CardZone.Deck, null, Card(Get("Knickers"), "knickers"));
        var knickersValue = new CandidatePointEvaluator(catalog).Evaluate(knickers, body, PlayerSide.User, "play");
        var knickersHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(knickersValue, new(7, []));
        Check(knickersHorizons is { OneTurn.Minimum: 5, OneTurn.Maximum: 5, TwoTurns.Minimum: 5, TwoTurns.Maximum: 5 } &&
            knickersHorizons.Notes.All(note => !note.Contains("Knickers", StringComparison.OrdinalIgnoreCase)),
            $"Knickers should not contribute to reach horizons: {knickersHorizons}");

        var stefan = Hand("Stefan Skellen") with
        {
            CardValues = [new(PlayerSide.User, "starting-deck", "starting-tactic-count", 6, 6)]
        };
        stefan = Put(stefan, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "stefan-target", 20));
        Points(stefan, Get("Stefan Skellen").Power + 4, "Stefan repeats one Ace per three starting-deck Tactics");

        var imlerith = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Imlerith"), "play"), Card(big, "imlerith-discard", 11));
        Points(imlerith, Get("Imlerith").Power + 11, "Imlerith uses the current power of a known unit discarded from hand");
        Check(Max(imlerith).After!.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Any(card => card.InstanceId == "imlerith-discard"),
            "Imlerith did not move the selected hand card to the graveyard");

        var equinox = Put(Hand("Spring Equinox"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "equinox-target"));
        equinox = equinox with { CardValues =
        [
            new(PlayerSide.User, "starting-deck", "starting-nature-count", 10, 10),
            new(PlayerSide.User, Get("Spring Equinox").Id, "starting-copy-count", 2, 2)
        ] };
        Points(equinox, 8, "Spring Equinox uses inferred starting-deck Nature count excluding its own copies");

        var falseCiri = Put(Hand("False Ciri"), PlayerSide.User, CardZone.Graveyard, null, Card(tactic, "false-ciri-tactic"));
        falseCiri = Put(falseCiri, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "false-ciri-target", 20));
        Points(falseCiri, 5 - Get("False Ciri").Power, "False Ciri replays a known bronze graveyard Tactic as Doomed");
        Check(Max(falseCiri).After!.Zone(PlayerSide.User, CardZone.Banished).Cards.Any(card => card.InstanceId == "false-ciri-tactic"),
            "False Ciri's replayed Tactic did not leave play as Doomed");

        var grace = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("False Ciri"), "grace-ciri", 7, status: [CardStatus.Spying]));
        var graceResult = engine.ResolveEndTurn(grace, PlayerSide.Opponent);
        Check(graceResult.Points == -15 && graceResult.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single() is
            { Power: 8 } movedCiri && movedCiri.Statuses!.Count == 0,
            $"False Ciri end-turn Grace did not move across the board and Purify: {graceResult.Points}; {string.Join("; ", graceResult.Missing)}");

        var filavandrel = Get("Filavandrel aén Fidháil");
        var filaChoice = filaSpecial;
        var filaPower = filaChoice.Provision;
        var filaPosition = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(filavandrel, "play", filaPower));
        var filaReplay = engine.ResolvePlay(filaPosition, "play", PlayerSide.User,
            new Dictionary<string, PlaySelection> { ["play"] = new(Row: BoardRow.Melee, Slot: 0, CreatedCardId: filaChoice.Id) });
        Check(filaReplay.Points is not null && filaReplay.Line.Any(line => line.Contains(filaChoice.Name, StringComparison.Ordinal)) &&
            filaReplay.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single(card => card.CardId == filavandrel.Id).Power == filaPower,
            $"Filavandrel did not constrain recursive Create by current power: {filaChoice.Name}/{filaPower}; {string.Join("; ", filaReplay.Missing)}");

        var vypper = Put(GamePosition.EmptyKnown() with { Round = 1 }, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Vypper"), "vypper"));
        var vypperRound = RoundTransitions.Advance(vypper, catalog).Position;
        Check(vypperRound.Zone(PlayerSide.Opponent, CardZone.Graveyard).Cards.Single().InstanceId == "vypper",
            "Vypper did not move to the opponent graveyard at round end");
        var vypperReturn = engine.ResolveEndTurn(vypperRound, PlayerSide.Opponent);
        var returnedVypper = vypperReturn.After is null ? null : Enum.GetValues<BoardRow>()
            .SelectMany(row => vypperReturn.After.Zone(PlayerSide.User, CardZone.Board, row).Cards).SingleOrDefault(card => card.InstanceId == "vypper");
        Check(returnedVypper?.Statuses!.IsSupersetOf([CardStatus.Doomed, CardStatus.Spying]) == true,
            $"Vypper did not return to its original side as Doomed/Spying carryover: {string.Join("; ", vypperReturn.Missing)}");

        var kaer = Max(Hand("Kaer Seren"));
        Check(kaer.MaximumPoints is not null && kaer.MaximumPoints >= Get("Kaer Seren").Power &&
            kaer.Line.Any(line => line.Contains("chooses Griffin Witcher", StringComparison.Ordinal)),
            $"Kaer Seren did not recursively resolve its generated Witcher choice: {kaer.MaximumPoints}; {string.Join("; ", kaer.Missing)}");

        var shaping = Put(Hand("Shaping Nature"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "shaping-target"));
        Points(shaping, 9, "Shaping Nature selects its strongest board-aware mode");
        var ida = Put(Hand("Ida Emean aep Sivney"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "ida-target"));
        Points(ida, Get("Ida Emean aep Sivney").Power + 1, "Ida ranged Vitality contributes its first automatic tick");

        var caravan = Put(Hand("Caravan Vanguard"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Caravan Vanguard"), "caravan-copy"));
        Points(caravan, Get("Caravan Vanguard").Power * 2 + 3, "Caravan Vanguard Bonded combines melee and ranged deploys");

        var fledgling = Put(Hand("Tempering"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(body, "fledgling-left"), Card(Get("Naiad Fledgling"), "fledgling"), Card(Get("Young Dryad"), "fledgling-symbiosis"));
        Points(fledgling, 7, "Naiad Fledgling converts a Symbiosis trigger into adjacent Vitality Y");

        var mantrap = Put(Hand("Orchard Mantrap"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "mantrap-food"));
        Points(mantrap, Get("Orchard Mantrap").Power, "Orchard Mantrap Consume preserves the eaten body's points and adds only its own body");

        var maraal = Put(Hand("Maraal"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(big, "maraal-target", status: [CardStatus.Poison]));
        Points(maraal, Get("Maraal").Power + big.Power, "Maraal deploy applies the lethal second Poison");
        var mutant = Put(Hand("Maraal"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("Mutant"), "mutant") with { Charges = 1 });
        Points(mutant, Get("Maraal").Power - Get("Mutant").Power, "Mutant's first Poison Counter spawns an opposing base copy");

        var cat = Put(Hand("Cat Witcher"), PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, Card(body, "cat-target", 8));
        Points(cat, Get("Cat Witcher").Power + 2, "Cat Witcher end-turn move and Adrenaline damage");

        Points(Hand("Tuirseach Veteran") with { Round = 1 }, Get("Tuirseach Veteran").Power - 3,
            "Tuirseach Veteran does not invent its conditional Berserk heal above power 3");
        var passiflora = Hand("One Night at the Passiflora") with { User = GamePosition.EmptyKnown().User with { Coins = 0 } };
        var passifloraResult = Max(passiflora);
        Check(passifloraResult.MaximumPoints == Get("Sly Seductress").Power, "Passiflora Prologue did not spawn its first engine");
        var blindeyePlay = Put(passifloraResult.After!, PlayerSide.User, CardZone.Hand, null, Card(blindeye, "chapter-play"));
        var chapter = engine.Maximum(blindeyePlay, "chapter-play", PlayerSide.User);
        Check(chapter.MaximumPoints == blindeye.Power + Get("Passiflora Peaches").Power &&
            chapter.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Any(card => card.CardId == Get("Passiflora Peaches").Id) == true,
            $"First qualifying card did not keep its own X while adding Passiflora Chapter 1 as a board interaction: {chapter.MaximumPoints}/{chapter.BestModeledPoints}; {string.Join("; ", chapter.Missing)}; {string.Join("; ", chapter.Line)}");

        var koshchey = Put(Hand(big.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Koshchey"), "koshchey"));
        Points(koshchey, big.Power + 1 + Get("Endrega Larva").Power, "Koshchey Adrenaline Thrive spawns one Larva without its Deploy copy");
        var longHand = Put(koshchey, PlayerSide.User, CardZone.Hand, null,
            Card(big, "play"), Card(body, "kh1"), Card(body, "kh2"), Card(body, "kh3"), Card(body, "kh4"), Card(body, "kh5"));
        Points(longHand, big.Power + 2, "Koshchey above Adrenaline spawns a Drone and gains its Thrive point");

        var raffardCrew = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Raffard’s Vengeance"), "play"), Card(soldier, "raffard-soldier"));
        Check(PlayRules.Compile(Get("Raffard’s Vengeance")) is { Reaction: "raffard-crew", Order: "raffard-hand" }, "Raffard rule did not compile");
        raffardCrew = Put(raffardCrew, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(soldier, "crew-left"), Card(soldier, "crew-right"));
        raffardCrew = Put(raffardCrew, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "crew-target", 12));
        Points(raffardCrew, Get("Raffard’s Vengeance").Power,
            "Crew does not grant Raffard Zeal; an unzealed Order remains outside immediate reach");
        var raffardUnzealed = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Raffard’s Vengeance"), "play"), Card(body, "raffard-unready-unit"));
        Points(raffardUnzealed, Get("Raffard’s Vengeance").Power, "Uncrewed Raffard does not spend its unzealed Order");
        var raffardEngine = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null, Card(soldier, "play"));
        raffardEngine = Put(raffardEngine, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(soldier, "crew-left"), Card(Get("Raffard’s Vengeance"), "raffard") with { Charges = 1, Cooldown = 1 }, Card(soldier, "crew-right"));
        raffardEngine = Put(raffardEngine, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "crew-target", 12));
        Points(raffardEngine, soldier.Power + 2, "Crew is a separate Y engine when a later adjacent Soldier preserves Crew");
        var zealRaffard = raffardEngine with { User = raffardEngine.User with { CurrentLeaderId = "200168", LeaderCharges = 1 } };
        zealRaffard = Put(zealRaffard, PlayerSide.User, CardZone.Hand, null, Card(soldier, "raffard-zeal-unit"));
        Check(engine.LeaderContribution(zealRaffard, PlayerSide.User).Estimate.MaximumPoints == 2 + soldier.Power + 2,
            "Inspired Zeal did not boost Raffard, unlock its known-hand Order and preserve the Crew ping");

        var formation = Put(Hand("Reinforced Ballista"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "formation-target", 12));
        Points(formation, Get("Reinforced Ballista").Power + 1, "Formation chooses Zeal damage or ranged boost without double-counting both");
        var onager = Put(formation, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Card(Get("Onager"), "onager"));
        Points(onager, Get("Reinforced Ballista").Power + 2, "Onager adds one random damage after the Formation Order resolves");
        var helge = Put(Hand(tactic.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Hefty Helge"), "helge") with { Charges = 1, Cooldown = 0 });
        helge = Put(helge, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "helge-target", 20));
        Points(helge, 9, "Tactic grants Helge a Charge before both known Order pings are spent");

        var caranthir = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Caranthir Ar-Feiniel"), "play"), Card(Get("Golyat"), "caranthir-copy"));
        Points(caranthir, Get("Caranthir Ar-Feiniel").Power + 1, "Caranthir spawns a 1-power base hand copy without triggering Deploy");
        var informant = Put(Hand("Duchess' Informant"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "informant-copy"));
        Points(informant, body.Power - Get("Duchess' Informant").Power, "Duchess Informant copies and plays a known enemy bronze against its Disloyal body");
        var reinforcement = Put(Hand("Reinforcements"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "reinforcement-copy"));
        Points(reinforcement, body.Power, "Reinforcements spawns and plays a base copy of a known allied bronze");
        var saer = Put(Hand("Saer Qu'an"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "saer-ally"));
        saer = Put(saer, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "saer-target", 12));
        Points(saer, Get("Saer Qu'an").Power + 2, "Saer Qu'an counts itself among controlled units");
        var rebuke = Put(Hand("Nature's Rebuke"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(treant, "rebuke-treant"));
        rebuke = Put(rebuke, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "rebuke-target", 3));
        Points(rebuke, 5, "Nature's Rebuke Deathblow adds the random Treant boost");
        var barnabas = Put(Hand("Barnabas Beckenbauer"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(elf, "barnabas-elf"), Card(dwarf, "barnabas-dwarf"), Card(dryad, "barnabas-dryad"));
        Points(barnabas, Get("Barnabas Beckenbauer").Power + 9, "Barnabas resolves each visible category target");
        var truffle = Hand("The Mushy Truffle") with { User = GamePosition.EmptyKnown().User with { StartingDeckIds =
            ImmutableHashSet.Create(Get("Sly Seductress").Id) } };
        Points(truffle, Get("Sly Seductress").Power, "Mushy Truffle creates and plays a known starting-deck Bonded unit");

        var manor = Put(Hand("Nekker"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("The Manor’s Dark Secret"), "manor") with { Charges = 1, Cooldown = 0 });
        var manorNekker = Max(manor);
        Check(manorNekker.MaximumPoints == 5 && manorNekker.Line.Any(line => line.StartsWith("[approx]", StringComparison.Ordinal)) &&
            manorNekker.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Where(card => card.CardId == Get("Nekker").Id)
                .Select(card => card.Power).Order().SequenceEqual([2, 3]) == true,
            $"Manor/Nekker sequencing should be 3 + 2 with an asterisk: {manorNekker.MaximumPoints}; {string.Join("; ", manorNekker.Line)}");

        var calveit = Put(Hand("Jan Calveit"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "calveit-low"), Card(big, "calveit-high"));
        var calveitResult = Max(calveit);
        Check(calveitResult.MaximumPoints == Get("Jan Calveit").Power &&
            calveitResult.After?.Zone(PlayerSide.User, CardZone.Deck).Cards.Select(card => card.InstanceId)
                .SequenceEqual(["calveit-high", "calveit-low"]) == true,
            "Jan Calveit did not preserve body-only reach while ordering inferred deck identities by provision.");

        var prism = Put(Hand("Shaping Nature"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Prism Pendant"), "prism"), Card(body, "prism-target"));
        var prismResult = Max(prism);
        Check(prismResult.MaximumPoints == 10 && prismResult.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards
                .Single(card => card.InstanceId == "prism-target").StatusTurns?.GetValueOrDefault(CardStatus.Vitality) == Get("Shaping Nature").Provision - 1,
            $"Prism did not add special-provision Vitality before the first normal tick: {prismResult.MaximumPoints}; {string.Join("; ", prismResult.Missing)}");

        var mammuna = Put(Hand("Mammuna"), PlayerSide.User, CardZone.Graveyard, null, Card(body, "mammuna-grave"));
        mammuna = Put(mammuna, PlayerSide.User, CardZone.Deck, null, Card(body, "mammuna-deck"));
        Points(mammuna, Get("Mammuna").Power + 2 * body.Power, "Mammuna is body plus grave boost plus matching deck body");

        var fucusya = Put(Hand("Fucusya"), PlayerSide.User, CardZone.Graveyard, null, Card(body, "fucusya-grave"));
        fucusya = Put(fucusya, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "fucusya-rain-target", 20));
        var fucusyaValue = new CandidatePointEvaluator(catalog).Evaluate(fucusya, Get("Fucusya"), PlayerSide.User, "play");
        var fucusyaHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(fucusyaValue,
            new(3, [], true, 2, false));
        var fucusyaBase = Get("Fucusya").Power + body.Power;
        Check(fucusyaHorizons is not null && fucusyaHorizons.CardOnly.Maximum == fucusyaBase &&
            fucusyaHorizons.OneTurn.Maximum == fucusyaBase + 2 && fucusyaHorizons.TwoTurns.Maximum == fucusyaBase + 4 &&
            fucusyaHorizons.OneTurn.Approximate && fucusyaHorizons.TwoTurns.Approximate,
            $"Fucusya should be body+res | +2 Rain | +4 total Rain*: {fucusyaHorizons?.Compact ?? "null"}");

        var artaud = Hand("Artaud Terranova") with
        {
            CardValues = [new(PlayerSide.User, body.Id, "spying-granted", StoredCardId: body.Id)]
        };
        Points(artaud, Get("Artaud Terranova").Power + body.Power + 1, "Artaud replays remembered Spying identity and triggers own Assimilate");

        var emhyr = Hand("Emhyr var Emreis");
        emhyr = emhyr with { Zones = emhyr.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand
            ? zone with { Complete = false, TotalCount = 8 } : zone).ToImmutableArray() };
        var emhyrResult = Max(emhyr);
        Check(emhyrResult.MaximumPoints == Get("Emhyr var Emreis").Power + Get("Impera Enforcers").Power &&
            emhyrResult.Line.Any(line => line.StartsWith("[approx]", StringComparison.Ordinal)),
            "Emhyr hidden-hand reach did not use the bounded Impera Enforcers default.");
        var intoEmhyr = Put(Hand(body.Name), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("Emhyr var Emreis"), "enemy-emhyr") with { Charges = 1, Cooldown = 1 },
            Card(Get("Impera Enforcers"), "enemy-enforcer") with { Charges = 1, Cooldown = 0 });
        var emhyrReaction = Max(intoEmhyr);
        Check(emhyrReaction.After?.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Single(card => card.InstanceId == "play")
                .Statuses?.Contains(CardStatus.Spying) == true &&
            emhyrReaction.After.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single(card => card.InstanceId == "enemy-enforcer").Charges == 2 &&
            emhyrReaction.After.CardValues?.Any(value => value.Side == PlayerSide.Opponent && value.CardId == body.Id && value.Kind == "spying-granted") == true,
            "Emhyr did not persist the granted-Spying identity and feed Impera Enforcers.");

        var stations = Put(Hand("Battle Stations!"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "stations-low"), Card(dwarf, "stations-high"));
        Points(stations, body.Power + dwarf.Power, "Battle Stations plays the inferred bronze floor/ceiling through normal listeners");

        var lippy = Put(Hand("Lippy Gudmund"), PlayerSide.User, CardZone.Graveyard, null,
            Card(Get("Roach"), "lippy-roach"), Card(Get("Knickers"), "lippy-knickers"), Card(body, "lippy-ignored"));
        Points(lippy, Get("Lippy Gudmund").Power + Get("Roach").Power + Get("Knickers").Power,
            "Lippy reach only returns cached Roach/Knickers graveyard bodies");

        var erland = Put(Hand("Erland of Larvik"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "erland-one"), Card(soldier, "erland-two"));
        Points(erland, Get("Erland of Larvik").Power + 2, "Erland banks one point for each inferred deck unit");

        var greatsword = Put(Hand("Alzur's Thunder"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("An Craite Greatsword"), "greatsword", 5));
        greatsword = Put(greatsword, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(big, "greatsword-target", 12));
        Points(greatsword, 6, "Enemy damage heals Greatsword by one up to its base-power cap");

        var bloodEagle = Put(Hand("Blood Eagle"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("An Craite Raiders"), "eagle-warrior"));
        bloodEagle = Put(bloodEagle, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "eagle-target", 2));
        Points(bloodEagle, 2 + Get("An Craite Raiders").Power, "Blood Eagle Deathblow opens the full inferred Warrior tutor pool");

        var sihil = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Sihil"), "play"), Card(body, "sihil-bronze"));
        sihil = Put(sihil, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "sihil-target", 1));
        var sihilResult = Max(sihil);
        Check(sihilResult.MaximumPoints == 1 + body.Power && sihilResult.After?.CardValues?.Any(value =>
                value.Side == PlayerSide.User && value.CardId == Get("Sihil").Id && value.Kind == "damage" && value.Maximum == 2) == true,
            "Sihil first Deathblow did not play a bronze and persist damage 2 for the next cast");

        var bounty = Hand("The Brute") with { CardValues =
            [new(PlayerSide.User, "bounty", "bounty-placements", 3, 3),
             new(PlayerSide.User, "bounty", "bounty-max-base-power", 8, 8)] };
        Points(bounty, Get("The Brute").Power + 3 + 8, "The Brute consumes reviewed placement and maximum-base-power history");
        var ignatius = Hand("Ignatius Hale") with { CardValues =
            [new(PlayerSide.User, "bounty", "bounty-total-base-power", 10, 10)] };
        Points(ignatius, 11, "Ignatius uses one base plus reviewed destroyed-Bounty base power");

        var justice = Put(Hand("Novigradian Justice"), PlayerSide.User, CardZone.Deck, null, Card(dwarf, "justice-target"));
        justice = Put(justice, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Card(dwarf, "justice-enabler"));
        Points(justice, dwarf.Power + Get("Cleaver's Muscle").Power,
            "Novigradian Justice tutors the inferred body and conditionally spawns Muscle");

        var blindFury = Put(Hand("Philippa: Blind Fury"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(big, "blind-fury-target", 20));
        Points(blindFury, Get("Philippa: Blind Fury").Power + 10,
            "Philippa Blind Fury resolves its selected four and sequential random three-two-one packets");

        var aard = Put(Hand("Geralt: Aard"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "aard-one"), Card(body, "aard-two"), Card(body, "aard-three"));
        Points(aard, Get("Geralt: Aard").Power + 6, "Geralt Aard damages three before moving survivors");

        // Greedy internal-player recipes execute child cards through the ordinary resolver. These
        // fixtures deliberately include listeners and nested plays so a flat body/text estimate fails.
        var braathens = Put(Hand("Braathens"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "braathens-target", 3));
        braathens = braathens with { User = braathens.User with { StartingDeckIds = [agentSpy.Id] } };
        Points(braathens, Get("Braathens").Power + 3,
            "Braathens agent creates Disloyal, resolves damage and triggers own Assimilate");

        var vivaldi = Put(Hand("Vivaldi Bank"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "bank-top"), Card(big, "bank-second"));
        Points(vivaldi, big.Power + 3, "Vivaldi agent spends one of four Profit Coins for the stronger second card");
        var expensiveBank = Put(Hand("Vivaldi Bank"), PlayerSide.User, CardZone.Deck, null,
            Card(body, "bank-free", 8), Card(body, "bank-pay-one", 1), Card(body, "bank-pay-two", 1),
            Card(body, "bank-pay-three", 1), Card(big, "bank-pay-four", 10));
        Points(expensiveBank, 12, "Bank agent values the four remaining Profit Coins instead of greedily overpaying for a ten-point body");

        var war = Put(Hand("War of Clans"), PlayerSide.User, CardZone.Graveyard, null,
            Card(agentWarrior, "war-warrior"));
        war = Put(war, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "war-target", 2));
        Points(war, 2 + agentWarrior.Power, "War of Clans agent uses Deathblow and replays the graveyard Warrior as Doomed");

        var bride = Put(Hand("Bride of the Sea"), PlayerSide.User, CardZone.Graveyard, null,
            Card(agentAlchemy, "bride-alchemy"));
        Points(bride, Get("Bride of the Sea").Power + 3,
            "Bride agent replays the best eligible graveyard Alchemy through normal targeting");

        var crow = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Crow Messenger"), "play"), Card(agentAlchemy, "crow-alchemy"));
        crow = Put(crow, PlayerSide.User, CardZone.Graveyard, null, Card(Get("Crow Messenger"), "crow-grave"));
        crow = Put(crow, PlayerSide.User, CardZone.Deck, null,
            Card(Get("Crow Messenger"), "crow-deck-one"), Card(Get("Crow Messenger"), "crow-deck-two"));
        Points(crow, 4 * Get("Crow Messenger").Power,
            "Crow Messenger agent reads hand plus graveyard/deck inventories for the 16-point line");
        Points(Put(crow, PlayerSide.User, CardZone.Deck, null, Card(Get("Crow Messenger"), "crow-deck-one")),
            3 * Get("Crow Messenger").Power, "One fewer available copy changes the Crow Messenger projection to twelve");

        var magne = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Magne Division"), "play"), Card(agentTactic, "magne-tactic"));
        magne = Put(magne, PlayerSide.User, CardZone.Deck, null, Card(body, "magne-draw"));
        magne = Put(magne, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "magne-target"));
        Points(magne, Get("Magne Division").Power + 5,
            "Magne agent plays the damage Tactic and performs the actual top-deck draw");

        var sapper = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null,
            Card(Get("Sapper"), "play", armor: Get("Sapper").PrintedArmor ?? 0), Card(agentBomb, "sapper-bomb"));
        sapper = Put(sapper, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "sapper-target"));
        Points(sapper, Get("Sapper").Power + 1,
            "Sapper agent plays a Bomb and its newly established Barricade listener deals one");

        var sentinel = Put(Hand("Brokilon Sentinel"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Brokilon Sentinel"), "sentinel-copy"));
        sentinel = Put(sentinel, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "sentinel-target", 2));
        Points(sentinel, 2 * Get("Brokilon Sentinel").Power + 2,
            "Brokilon Sentinel agent chooses a Deathblow target and summons its inferred copy");

        var mistress = Put(Hand("Bloody Mistress"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(big, "mistress-sabbath-one", 9), Card(big, "mistress-sabbath-two", 9));
        Points(mistress, Get("Bloody Mistress").Power + 2,
            "Bloody Mistress reaches Sabbath after entry and produces the caption-discussed nine-point projection");

        var arachasQueen = Put(Hand("Arachas Queen"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Harpy Egg"), "queen-egg"), Card(Get("Barbegazi"), "queen-consumer") with { Charges = 1, Cooldown = 0 });
        var queenPlay = engine.ResolvePlay(arachasQueen, "play", PlayerSide.User, new Dictionary<string, PlaySelection>
        { ["play"] = new(Row: BoardRow.Melee, Slot: 0, TargetId: "queen-egg") });
        Check(queenPlay.Points == Get("Arachas Queen").Power + Get("Harpy").Power,
            "Arachas Queen Deploy failed to consume and trigger Harpy Egg");
        var queenDeath = engine.ResolveOrder(queenPlay.After!, "queen-consumer", PlayerSide.User, "play");
        Check(queenDeath.Points == Get("Harpy Egg").Power && queenDeath.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee)
            .Cards.Any(card => card.CardId == Get("Harpy Egg").Id), "Arachas Queen lost its consumed-card memory before Deathwish");

        var eggs3 = Put(Hand("Barbegazi"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(Get("Endrega Eggs"), "drone-eggs"));
        var eggsOrder = engine.ResolveOrder(eggs3 with { Zones = eggs3.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee
            ? zone with { Cards = zone.Cards.Add(Card(Get("Barbegazi"), "egg-consumer") with { Charges = 1, Cooldown = 0 }) } : zone).ToImmutableArray() },
            "egg-consumer", PlayerSide.User, "drone-eggs");
        Check(eggsOrder.Points == 3 * Get("Drone").Power, "Endrega Eggs Deathwish did not spawn three non-Deploy Drones");

        var addict = Put(Hand(body.Name), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Wretched Addict"), "addict", status: [CardStatus.Poison]));
        Points(addict, body.Power + 2, "Poisoned Wretched Addict automatic growth");

        var dimBomb = Put(Hand("Dimeritium Bomb"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "veil-target", 8));
        Points(dimBomb, 4, "Dimeritium Bomb damage resolves before Veil");
        Check(Max(dimBomb).After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single().Statuses!.Contains(CardStatus.Veil),
            "Dimeritium Bomb failed to grant Veil to the survivor");

        var healer = Put(Hand("Hawker Healer"), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(body, "healer-target", 1));
        Points(healer, Get("Hawker Healer").Power + 4, "Greedy row selection prefers four healing over two boosting");
        var elite = Put(Hand("Blue Mountain Elite"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "elite-target", 8));
        Points(elite, Get("Blue Mountain Elite").Power + 3, "Blue Mountain Elite finds the isolated target row");
        Points(Put(elite, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "elite-target", 8), Card(body, "elite-blocker")),
            Get("Blue Mountain Elite").Power, "Blue Mountain Elite rejects a populated row");

        var tears = Put(Hand("Tears of Siren"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "rain-one"), Card(body, "rain-two"));
        Points(tears, Get("Deafening Siren").Power, "Tears of Siren spawns its body; weather remains in Y/Z");
        var tearsValue = new CandidatePointEvaluator(catalog).Evaluate(tears, Get("Tears of Siren"), PlayerSide.User, "play");
        var tearsHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(tearsValue, new(2, [], true, 1, false));
        Check(tearsHorizons?.OneTurn.Maximum == Get("Deafening Siren").Power + 2 &&
            tearsHorizons.TwoTurns.Maximum == Get("Deafening Siren").Power + 4,
            $"Tears of Siren should add two Rain ticks cumulatively: {tearsHorizons?.Compact}");

        // An explicit recorded choice must not be replaced by a greedier tutor endpoint.
        var recordedBank = engine.ResolvePlay(vivaldi, "play", PlayerSide.User, new Dictionary<string, PlaySelection>
        { ["play"] = new(TutorId: "bank-top"), ["bank-top"] = new(Row: BoardRow.Melee, Slot: 0) });
        Check(recordedBank.Points == body.Power + 4 && recordedBank.After!.Zone(PlayerSide.User, CardZone.Deck).Cards.Any(card => card.InstanceId == "bank-second"),
            "Greedy agent overrode the recorded Vivaldi Bank choice");
        var greedyValue = new CandidatePointEvaluator(catalog).Evaluate(vivaldi, Get("Vivaldi Bank"), PlayerSide.User, "play");
        Check(greedyValue.Quality == CandidateEvaluationQuality.ConditionalSimulation &&
            new CardPointHorizonEvaluator(catalog).Evaluate(greedyValue, new(2, [], true, 1, false))?.OneTurn.Approximate == true,
            "Greedy policy was presented as an unqualified full-turn optimum");
        var healerValue = new CandidatePointEvaluator(catalog).Evaluate(healer, Get("Hawker Healer"), PlayerSide.User, "play");
        Check(healerValue.MinimumPoints == healerValue.MaximumPoints && healerValue.MaximumPoints == 7,
            "A deliberately inferior legal row was exposed as uncertainty instead of optimizing the player's choice");
        var uncertainCrow = Put(crow, PlayerSide.User, CardZone.Hand, null, Card(Get("Crow Messenger"), "play"));
        uncertainCrow = uncertainCrow with { Zones = uncertainCrow.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand
            ? zone with { Complete = false, TotalCount = 2 } : zone).ToImmutableArray() };
        var uncertainCrowValue = new CandidatePointEvaluator(catalog).Evaluate(uncertainCrow, Get("Crow Messenger"), PlayerSide.User, "play");
        Check(uncertainCrowValue.MinimumPoints == 8 && uncertainCrowValue.MaximumPoints == 16,
            $"Crow Messenger hidden-Alchemy branch should remain 8–16, got {uncertainCrowValue.RangeText}");
        var mirroredCrow = crow with
        {
            Zones = crow.Zones.Select(zone => zone with { Side = zone.Side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User }).ToImmutableArray(),
            User = crow.Opponent with { Side = PlayerSide.User }, Opponent = crow.User with { Side = PlayerSide.Opponent }
        };
        Check(engine.Maximum(mirroredCrow, "play", PlayerSide.Opponent).MaximumPoints == 16,
            "Greedy card recipes diverged when evaluating opponent reach");
        var snowMagne = Put(magne, PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Snowdrop"), "magne-snowdrop") with { Charges = 0, Cooldown = 0 });
        Points(snowMagne, Get("Magne Division").Power + 5 + 2, "Magne actual draw triggers Snowdrop once");
        Points(Put(snowMagne, PlayerSide.User, CardZone.Deck, null), Get("Magne Division").Power + 5,
            "Attempting to draw from an empty deck does not invent a Snowdrop trigger");
        var lydia = Put(Hand("Lydia van Bredevoort"), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(Get("Glynnis aep Loernach"), "lydia-assimilate"));
        lydia = Put(lydia, PlayerSide.Opponent, CardZone.Graveyard, null, Card(agentBomb, "lydia-borrowed", original: true));
        var lydiaReplay = engine.ResolvePlay(lydia, "play", PlayerSide.User, new Dictionary<string, PlaySelection>
        { ["play"] = new(Row: BoardRow.Ranged, Slot: 0, TutorId: "lydia-borrowed") });
        Check(lydiaReplay.Points == Get("Lydia van Bredevoort").Power + 2 &&
            lydiaReplay.After!.Zone(PlayerSide.Opponent, CardZone.Graveyard).Cards.Length == 0,
            "Lydia must play the borrowed graveyard card under the acting side and trigger its Assimilate");

        var futureOnly = body with { Id = "future-only", Name = "Future-only engine", AbilityText = "At the end of your next turn, boost self by 20." };
        Check(new ApproximatePointModel(catalog.Append(futureOnly)).Estimate(futureOnly, approximateBoard, PlayerSide.User) is
            { Minimum: 5, Maximum: 5, Quality: PointEstimateQuality.BaselineOnly }, "Future engine was presented as immediate tempo");
        var aucwennRule = PlayRules.Compile(Get("Aucwenn"));
        Check(aucwennRule is { Effect: PlayEffect.Aucwenn, Symbiosis: 1, Unmodeled: null, Partial: null },
            "Aucwenn's unpunctuated catalogue Symbiosis keyword did not compile into its executable rule.");
        var coverage = actual.Where(card => card.CanBeInStartingDeck).Select(PlayRules.Compile).ToArray();
        var leaders = actual.Where(card => card.Kind == CardKind.Leader && engine.Leader(card.Id) is not null).ToArray();
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-rules-coverage.json"), JsonSerializer.Serialize(new
        {
            Compiled = coverage.Count(rule => rule.Unmodeled is null && rule.UnmodeledDeploy is null && rule.Partial is null), Total = coverage.Length,
            Supported = coverage.Where(rule => rule.Unmodeled is null && rule.UnmodeledDeploy is null && rule.Partial is null).Select(rule => new { rule.Card.Id, rule.Card.Name }),
            Leaders = leaders.Select(card => card.Name), Conditions = PlayConditions.Table,
            Note = "Executable handler coverage, not end-to-end recognition accuracy or a complete Gwent engine."
        }, new JsonSerializerOptions { WriteIndented = true }));
        var directCoverage = actual.Where(card => card.CanBeInStartingDeck).Select(card => new
        {
            card.Id, card.Name, card.Kind,
            Estimate = approximate.Estimate(card, approximateBoard, PlayerSide.User)
        }).ToArray();
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.34-point-coverage.json"), JsonSerializer.Serialize(new
        {
            Exact = coverage.Count(rule => rule.Unmodeled is null && rule.UnmodeledDeploy is null && rule.Partial is null),
            DirectReadout = directCoverage.Count(item => item.Estimate is not null), Total = directCoverage.Length,
            StateAware = directCoverage.Count(item => item.Estimate?.Quality == PointEstimateQuality.StateAware),
            Bounded = directCoverage.Count(item => item.Estimate?.Quality == PointEstimateQuality.Bounded),
            BodyOnly = directCoverage.Count(item => item.Estimate?.Quality == PointEstimateQuality.BaselineOnly),
            Unsupported = directCoverage.Where(item => item.Estimate is null).Select(item => new { item.Id, item.Name, item.Kind }),
            Note = "Exact is executable whole-card coverage. DirectReadout is a labeled immediate range/body fallback, not a complete card engine or safe-to-pass proof."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Play engine tests passed: {coverage.Count(rule => rule.Unmodeled is null && rule.UnmodeledDeploy is null && rule.Partial is null)}/{coverage.Length} starting card definitions, {leaders.Length} leader profiles; {watch.ElapsedMilliseconds} ms.");
        var fixture = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Hand, null, Card(Get("Fiend"), "hover"));
        fixture = Put(fixture, PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(egg, "egg"));
        fixture = Put(fixture, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(Get("Ice Giant"), "enemy"));
        fixture = Put(fixture, PlayerSide.Opponent, CardZone.Hand, null, Card(Get("Old Speartip"), "reply1"), Card(egg, "reply2"), Card(Get("Ghoul"), "reply3"));
        fixture = fixture with { Opponent = fixture.Opponent with { CurrentLeaderId = "131101", LeaderCharges = 3 } };
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-threat-fixture.gvn"), PositionNotation.Write(fixture));
        LatestReplay(root, actual);
    }

    public static async Task HoverPixelsAsync(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer();
        foreach (var (file, expected) in new[] { ("frame-000466-184934801.jpg", "Fiend"), ("frame-000474-184935597.jpg", "Fiend"), ("frame-000402-184928407.jpg", (string?)null) })
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions/20260827-184848", file));
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var screen = await reader.AnalyzeAsync(frame);
            Check(screen.HasCardTooltip && screen.TooltipRegion is not null, "Tooltip not found in inspected frame " + file);
            var region = screen.TooltipRegion!.Value;
            var crop = new NormalizedRegion(Math.Max(0, region.Left - .02), Math.Max(0, region.Top - .12), Math.Min(1, region.Right + .02), Math.Min(1, region.Bottom + .02));
            var text = await reader.ReadAsync(frame, crop);
            var card = HoverTitleReader.Read(text, catalog);
            Console.WriteLine($"Hover pixels {file}: {card?.Name ?? "(no card)"} · {text.Replace('\n', ' ')}");
            Check(card?.Name == expected, "Hover OCR identity mismatch");
        }
        Check(HoverTitleReader.Read("", catalog) is null, "Blank hover text produced a card");
        Console.WriteLine("Hover pixel regression passed on latest match; pointer location cannot be recovered from these older captures.");
    }

    private static void LatestReplay(string root, IReadOnlyList<CardDefinition> catalog)
    {
        var tracker = new GameStateTracker(); var inventory = new ZoneInventoryTracker();
        tracker.Reset("latest"); var frames = 0; var reviews = 0; var peak = 0;
        foreach (var line in File.ReadLines(Path.Combine(root, "GwentCompanion/sessions/20260827-184848/vision-observations.jsonl")))
        {
            var frame = JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!;
            var update = GameStateVisionAdapter.Apply(tracker, frame); inventory.Observe(update);
            foreach (var action in frame.Events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
                inventory.ObserveSpecial(action.Sighting.Card, action.Sighting.Side, action.ObservedAt);
            Check(inventory.Entries.All(item => item.Copies >= 0 && item.Copies <= 10), "Replay produced unbounded copies");
            peak = Math.Max(peak, inventory.Entries.Count); frames++;
            if (frame.GraveyardInspection is not null) reviews++;
        }
        Check(frames > 2000 && inventory.Entries.All(entry => !entry.Reviewed), "Replay promoted inference/side-unknown OCR to reviewed facts");
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-inventory-replay.json"), JsonSerializer.Serialize(new
        { Frames = frames, Inspections = reviews, Peak = peak, Entries = inventory.Entries,
            Note = "Only latest recording. Inference/bookkeeping regression, not ground-truth graveyard or all-card accuracy." }, GameStateJournal.Json));
        Console.WriteLine($"Latest replay: {frames} frames; {peak} peak inferred zone identities. No early training-AI recordings used.");
    }
}
