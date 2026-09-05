using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class ReachProjectionTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static PositionCard Unit(CardDefinition card, string id, int? power = null, int armor = 0, params CardStatus[] statuses) =>
        new(id, card.Id, power ?? card.Power, card.Power, armor, statuses.ToImmutableHashSet(), 0, 0, true);
    private static GamePosition Put(GamePosition p, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] cards) =>
        p with { Zones = p.Zones.Select(z => z.Side == side && z.Zone == zone && z.Row == row ?
            z with { Cards = cards.ToImmutableArray(), Complete = true, TotalCount = cards.Length } : z).ToImmutableArray() };

    internal static void Run(string root)
    {
        var companion = Path.Combine(root, "GwentCompanion");
        var catalog = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(companion, "cache", "gwent-one-cards.json"))).ToArray();
        var cold = Stopwatch.StartNew();
        CardDefinition Named(string name) => catalog.First(card => card.Name == name);
        var evaluator = new CandidatePointEvaluator(catalog);
        var horizons = new CardPointHorizonEvaluator(catalog);
        var body = Named("Elder Bear");
        var position = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(body, "ally", 12));
        position = Put(position, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Unit(body, "enemy", 20));
        position = Put(position, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, Unit(body, "other-enemy", 20));
        CandidatePointEvaluation Evaluate(string name, GamePosition? input = null) => evaluator.Evaluate(input ?? position, Named(name), PlayerSide.User);
        void Points(string name, int expected, GamePosition? input = null)
        {
            var result = Evaluate(name, input);
            Check(result.MaximumPoints == expected, $"{name}: expected {expected}, got {result.RangeText}; {string.Join(" / ", result.Assumptions)}");
            Check(result.Simulation.MaximumPoints is null || !result.Simulation.UsedProjection, "Approximation advertised as strict maximum");
        }
        Points("Crow's Eye", 4);
        var firstProjectionMilliseconds = cold.ElapsedMilliseconds;
        Points("Arachas Nest", 4);
        Points("Imlerith's Wrath", 12);
        Points("Curse of Corruption", 20);
        Points("Predatory Dive", 8);
        // Visually reviewed Shinmiri Q55Cl2IXukM, 888.048s -> 890.048s: Carroballista
        // 5 power/1 Armor -> 2 power/0 Armor; scores 37:30 -> 37:27. A later Order ping
        // is deliberately outside this component fixture, as are the unreconstructed board listeners.
        var joustCard = Named("Tourney Joust"); var carro = Named("Carroballista");
        var joustBoard = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged, Unit(carro, "carro", 5, 1));
        joustBoard = Put(joustBoard, PlayerSide.User, CardZone.Hand, null, Unit(joustCard, "joust"));
        var projectedReplay = new TacticalPlayEngine(new PlayRuleBook(catalog), allowReachProjection: true);
        var joustReplay = projectedReplay.ResolvePlay(joustBoard, "joust", PlayerSide.User,
            new Dictionary<string, PlaySelection> { ["joust"] = new(TargetId: "carro") });
        Check(joustReplay.Points == 3 && joustReplay.After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged).Cards.Single() is { Power: 2, Armor: 0 },
            $"Shinmiri isolated Joust damage mismatch: {joustReplay.Points}");
        var surrender = Evaluate("Surrender", Put(position, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Unit(body, "armored", 10, 3), Unit(body, "bare", 10)));
        Check(surrender.MaximumPoints == 3 && surrender.After!.Zones.SelectMany(z => z.Cards).All(card => card.Armor >= 0),
            $"Surrender armor removal incorrect: {surrender.RangeText}");
        var frost = Evaluate("Ard Gaeth");
        var weather = horizons.Evaluate(frost, new(3, [], true, 3, false));
        Check(weather is { CardOnly.Maximum: 0, OneTurn.Maximum: 4, TwoTurns.Maximum: 8 }, $"Both-row weather: {weather?.Compact}");
        var terminal = horizons.Evaluate(frost, new(1, [], true, 0, false));
        Check(terminal is { OneTurn.Maximum: 4, TwoTurns.Maximum: 4, TwoTurnRelevant: false }, "Terminal weather window duplicated");
        var passed = horizons.Evaluate(frost, new(2, [], true, 3, true));
        Check(passed is { OneTurn.Maximum: 0, TwoTurns.Maximum: 0 }, "Weather continued after opposing pass");

        var tutorBoard = Put(position, PlayerSide.User, CardZone.Deck, null, Unit(Named("Crow's Eye"), "tutored"));
        var tutor = Evaluate("Land of a Thousand Fables", tutorBoard);
        Check(tutor.MaximumPoints == Named("Land of a Thousand Fables").Power + 4 &&
            tutor.After!.Zone(PlayerSide.User, CardZone.Graveyard).Cards.Any(card => card.InstanceId == "tutored"),
            $"Recursive tutor did not execute a projected child: {tutor.RangeText}");
        var tutorX = horizons.Evaluate(tutor, new(1, [], true, 0, false));
        Check(tutorX!.CardOnly.Maximum == tutor.MaximumPoints, $"Fetched special omitted from X: {tutorX.Compact}");
        var hideout = Evaluate("Salamandra Hideout");
        Check(hideout.Simulation.UsedProjection && hideout.MaximumPoints > 0 && hideout.After!.Zones.Any(zone =>
            zone.Zone == CardZone.Board && zone.Cards.Any(card => card.CardId == Named("Salamandra Hideout").Id)),
            $"Location did not recursively play its possible children: {hideout.RangeText}");

        var trigger = new CardDefinition("projection-trigger", "Projection listener", "Neutral", CardKind.Unit, 4, 3,
            AbilityText: "Whenever you play a special card, boost self by 2. At the end of your turn, boost self by 1.");
        var candidate = new CardDefinition("projection-child", "Projection child", "Neutral", CardKind.Special, 4,
            AbilityText: "Damage an enemy unit by 3. Future imaginary clause remains unknown.");
        var order = new CardDefinition("projection-order", "Projection order", "Neutral", CardKind.Unit, 5, 4,
            AbilityText: "Deploy: Boost self by 1. Order: Damage an enemy unit by 3. At the end of your turn, boost self by 1.");
        var unknown = new CardDefinition("projection-unknown", "Unknown ability", "Neutral", CardKind.Special, 4, AbilityText: "Uninterpretable clause.");
        var conditionalZeal = new CardDefinition("conditional-zeal", "Conditional Zeal", "Neutral", CardKind.Unit, 5, 4,
            AbilityText: "Deploy: If an unknown condition holds, gain Zeal. Order: Damage an enemy unit by 3.");
        var custom = catalog.Concat(new[] { trigger, candidate, order, unknown, conditionalZeal }).ToArray();
        var customEvaluator = new CandidatePointEvaluator(custom); var customHorizons = new CardPointHorizonEvaluator(custom);
        var withEngine = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(trigger, "listener"));
        var played = customEvaluator.Evaluate(withEngine, candidate, PlayerSide.User);
        var split = customHorizons.Evaluate(played, new(1, [], true, 0, false));
        Check(played is { MaximumPoints: 6 } && split is { CardOnly.Maximum: 3, OneTurn.Maximum: 6 },
            $"Direct action, play listener, and automatic tick were not separated: {split?.Compact}");
        Check(played.Assumptions.Any(note => note.Contains("imaginary")) && played.Simulation.MaximumPoints is null,
            "Omitted clause disappeared from projected result");
        var locked = Put(withEngine, PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(trigger, "listener", null, 0, CardStatus.Locked));
        Check(customEvaluator.Evaluate(locked, candidate, PlayerSide.User).MaximumPoints == 3, "Locked projection engine still triggered");
        var blank = customEvaluator.Evaluate(position, unknown, PlayerSide.User);
        Check(blank is { HasValue: true, Estimate.Quality: PointEstimateQuality.BaselineOnly } &&
            blank.Estimate.Notes.Any(note => note.Contains("NOT zero")), "Unknown value disguised as zero-valued implemented ability");
        var ordered = customEvaluator.Evaluate(position, order, PlayerSide.User);
        Check(customEvaluator.Evaluate(position, conditionalZeal, PlayerSide.User).MaximumPoints == 4,
            "An unparsed conditional Zeal clause made an Order unconditionally ready on deploy");
        var orderHorizons = customHorizons.Evaluate(ordered, new(3, [], true, 3, false));
        Check(orderHorizons is { CardOnly.Maximum: 5, OneTurn.Maximum: 9, TwoTurns.Maximum: 10 },
            $"Ready projected Order should click once in Y, with automatic-only Z: {orderHorizons?.Compact}");
        var opposingPassed = customHorizons.Evaluate(ordered, new(3, [], true, 3, true));
        Check(opposingPassed is { OneTurn.Maximum: 9, TwoTurns.Maximum: 10 },
            $"An opposing pass incorrectly stopped the acting player's remaining automatic turns: {opposingPassed?.Compact}");
        var berserker = Named("Dwarf Berserker");
        var nakedBerserker = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(berserker, "berserker", null, 0));
        var armoredBerserker = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(berserker, "berserker", null, 1));
        var naked = customEvaluator.Evaluate(nakedBerserker, candidate, PlayerSide.User);
        var armored = customEvaluator.Evaluate(armoredBerserker, candidate, PlayerSide.User);
        Check(naked.MaximumPoints == 3 && armored.MaximumPoints == 4,
            $"Barricade was separated from its automatic trigger: naked {naked.RangeText}, armored {armored.RangeText}");
        var full = position;
        foreach (var row in Enum.GetValues<BoardRow>()) full = Put(full, PlayerSide.User, CardZone.Board, row,
            Enumerable.Range(0, 9).Select(index => Unit(body, $"full-{row}-{index}")).ToArray());
        Check(evaluator.Evaluate(full, body, PlayerSide.User).MaximumPoints == 0, "Full rows still projected an unplayable body");
        var ownCandidate = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(body, "large-ally", 30));
        ownCandidate = Put(ownCandidate, PlayerSide.Opponent, CardZone.Board, BoardRow.Ranged);
        ownCandidate = Put(ownCandidate, PlayerSide.User, CardZone.Hand, null, Unit(candidate, "hover"));
        var threat = new ThreatAnalyzer(custom).Analyze(new(ownCandidate, "hover", [], UserHorizon: new(1, [], true, 1, false),
            OpponentHorizon: new(1, [], true, 0, false)), 0, [new(Named("Imlerith's Wrath"), 1, "inferred")]);
        Check(threat.PlayerPlay.UsedProjection && threat.EstimatedGapAfterPlay == 3 && threat.Summary?.MaximumCardSwing == 17,
            "Hover path overwrote greedy opponent projection or discarded projected player state");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var cancelled = false;
            try { customEvaluator.Evaluate(position, candidate, PlayerSide.User, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Reach did not yield to detection cancellation");
        }
        AdditionalRegressions(catalog, evaluator);
        var mirror = position with
        {
            Zones = position.Zones.Select(zone => zone with { Side = zone.Side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User }).ToImmutableArray(),
            User = position.Opponent with { Side = PlayerSide.User }, Opponent = position.User with { Side = PlayerSide.Opponent },
        };
        Check(evaluator.Evaluate(mirror, Named("Crow's Eye"), PlayerSide.Opponent).MaximumPoints == Evaluate("Crow's Eye").MaximumPoints,
            "Opponent projection was not side-symmetric");

        // Catalog-wide output coverage is distinct from strict ability coverage. A known subtotal is
        // useful, but it must never inflate the exact-transition or video-validated denominators.
        var ruleBook = new PlayRuleBook(catalog); var projectedEngine = new TacticalPlayEngine(ruleBook, allowReachProjection: true);
        var watch = Stopwatch.StartNew();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long evaluationBytes = 0, maximumCandidateBytes = 0;
        var report = new List<object>(); var simulated = 0; var projected = 0; var baseline = 0;
        var bodyOnly = 0;
        var originalPosition = PositionNotation.Write(position);
        foreach (var card in catalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact))
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var evaluation = evaluator.Evaluate(position, card, PlayerSide.User);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            evaluationBytes += bytes; maximumCandidateBytes = Math.Max(maximumCandidateBytes, bytes);
            Check(evaluation.HasValue && evaluation.MinimumPoints <= evaluation.MaximumPoints, "No ordered readout for " + card.Name);
            Check(PositionNotation.Write(position) == originalPosition, "Candidate changed input inventory");
            var plan = projectedEngine.Rule(card.Id)?.Projection;
            if (evaluation.Simulation.MaximumPoints is not null) simulated++;
            else if (evaluation.Simulation.UsedProjection) projected++;
            else baseline++;
            if (evaluation.Estimate?.Quality == PointEstimateQuality.BaselineOnly) bodyOnly++;
            report.Add(new { card.Id, card.Name, evaluation.MinimumPoints, evaluation.MaximumPoints, evaluation.Quality,
                EstimateQuality = evaluation.Estimate?.Quality.ToString(),
                Projected = evaluation.Simulation.UsedProjection, ExtractedActions = plan?.HasActions ?? false,
                Omitted = plan?.Omitted.ToArray() ?? [], evaluation.Unresolved });
        }
        var output = Path.Combine(companion, "diagnostics", "reach-projection-coverage.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Total = report.Count,
            Simulated = simulated, Projected = projected, OtherEstimates = baseline, BodyOnlySubtotals = bodyOnly,
            WarmSweepAllocatedBytesIncludingAudit = GC.GetAllocatedBytesForCurrentThread() - allocationStart,
            EvaluationAllocatedBytes = evaluationBytes, MaximumCandidateAllocatedBytes = maximumCandidateBytes,
            ManagedHeapBytes = GC.GetTotalMemory(false),
            FirstProjectionMilliseconds = firstProjectionMilliseconds, WarmSweepMilliseconds = watch.ElapsedMilliseconds, Cards = report },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS projection regressions; all {report.Count} candidates have readouts: simulated {simulated}, projected {projected}, other estimates {baseline} ({bodyOnly} body-only); {watch.ElapsedMilliseconds}ms warm, {firstProjectionMilliseconds}ms cold. {output}");
    }

    private static void AdditionalRegressions(CardDefinition[] catalog, CandidatePointEvaluator evaluator)
    {
        CardDefinition Named(string name) => catalog.First(card => card.Name == name);
        var bear = Named("Elder Bear");
        var position = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, Unit(bear, "ally", 6));
        position = Put(position, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Unit(bear, "enemy", 20));
        var marine = Named("Kerack Marine");
        Check(evaluator.Evaluate(position, marine, PlayerSide.User).MaximumPoints == marine.Power + 4,
            "Devotion replacement should boost by four, not two plus four");
        Check(evaluator.Evaluate(position with { User = position.User with { Devotion = false } }, marine, PlayerSide.User).MaximumPoints == marine.Power + 2,
            "Non-Devotion replacement should preserve the base action");
        var striga = Named("Adda: Striga"); var lamp = Named("Lamp Djinn");
        // Direct video component: K1DMr2GA8KA 626.205 -> 628.205. 8-power Striga eats
        // an allied 3-power Lamp Djinn, becomes 14; own total 17 -> 20 before end-turn.
        var strigaBoard = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Ranged,
            Unit(striga, "striga", 8) with { Charges = 1 }, Unit(lamp, "lamp", 3, 0, CardStatus.Doomed));
        var engine = new TacticalPlayEngine(catalog);
        var consumed = engine.ResolveOrder(strigaBoard, "striga", PlayerSide.User, "lamp");
        Check(consumed.Points == 3 && consumed.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single().Power == 14,
            "Shinmiri Striga component should be net +3, not +6");
        var forbidden = Put(strigaBoard, PlayerSide.User, CardZone.Board, BoardRow.Ranged,
            Unit(striga, "striga", 8) with { Charges = 1 }, Unit(lamp, "lamp", 8));
        Check(engine.ResolveOrder(forbidden, "striga", PlayerSide.User, "lamp").Points is null, "Predator allowed equal-power target");

        var poison = new CardDefinition("audit-poison", "Poison test", "Neutral", CardKind.Special, 4, AbilityText: "Poison a unit.");
        var ping = new CardDefinition("audit-ping", "Damage test", "Neutral", CardKind.Special, 4, AbilityText: "Damage a unit by 1.");
        var extra = catalog.Concat(new[] { poison, ping }).ToArray(); var replay = new TacticalPlayEngine(extra);
        var roland = Named("Roland Bleinheim");
        var poisonBoard = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Unit(roland, "roland"));
        poisonBoard = Put(poisonBoard, PlayerSide.User, CardZone.Hand, null, Unit(poison, "poison"));
        var first = replay.ResolvePlay(poisonBoard, "poison", PlayerSide.User, new Dictionary<string, PlaySelection> { ["poison"] = new(TargetId: "enemy") });
        Check(first.After?.User.Coins == 2, "First Poison missed Roland");
        var secondBoard = Put(first.After!, PlayerSide.User, CardZone.Hand, null, Unit(poison, "poison-2"));
        var second = replay.ResolvePlay(secondBoard, "poison-2", PlayerSide.User, new Dictionary<string, PlaySelection> { ["poison-2"] = new(TargetId: "enemy") });
        Check(second.After?.User.Coins == 4 && second.After.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Length == 0,
            "Lethal Poison should still supply the Poison event");
        var veiled = Put(poisonBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Unit(bear, "enemy", 20, 0, CardStatus.Veil));
        Check(replay.ResolvePlay(veiled, "poison", PlayerSide.User, new Dictionary<string, PlaySelection> { ["poison"] = new(TargetId: "enemy") }).After?.User.Coins == 0,
            "Veil-blocked Poison generated Coins");

        var maiden = Named("Drummond Shieldmaiden");
        var maidenBoard = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Unit(maiden, "maiden"));
        maidenBoard = Put(maidenBoard, PlayerSide.User, CardZone.Deck, null, Unit(maiden, "deck-maiden"));
        maidenBoard = Put(maidenBoard, PlayerSide.User, CardZone.Hand, null, Unit(ping, "ping"));
        var summoned = replay.ResolvePlay(maidenBoard, "ping", PlayerSide.User, new Dictionary<string, PlaySelection> { ["ping"] = new(TargetId: "maiden") });
        Check(summoned.Points == maiden.Power - 1 && summoned.After?.Zone(PlayerSide.User, CardZone.Deck).Cards.Length == 0,
            "Damage did not summon known Shieldmaiden copies");
        var lockedMaiden = Put(maidenBoard, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Unit(maiden, "maiden", null, 0, CardStatus.Locked));
        Check(replay.ResolvePlay(lockedMaiden, "ping", PlayerSide.User, new Dictionary<string, PlaySelection> { ["ping"] = new(TargetId: "maiden") }).Points == -1,
            "Locked Shieldmaiden summoned copies");
        Check(evaluator.Evaluate(position, Named("Sandstorm"), PlayerSide.User).MaximumPoints == 3,
            "Single-unit row was damaged twice as both endpoints");
        var armorSwap = evaluator.Evaluate(position, Named("Vlodimir von Everec"), PlayerSide.User);
        Check(armorSwap.After!.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Length == 0,
            "Swapping zero Armor into power left a zero-power unit alive");
        Check(evaluator.Evaluate(position, Named("Stolen Mutagens"), PlayerSide.User).MaximumPoints == 5,
            "Single mutagen did not greedily select Coins");
        var salamandra = Named("Salamandra Lackey");
        var gang = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Ranged,
            Unit(salamandra, "gang-1", null, 0, CardStatus.Locked), Unit(salamandra, "gang-2", null, 0, CardStatus.Locked));
        Check(evaluator.Evaluate(gang, Named("Stolen Mutagens"), PlayerSide.User).MaximumPoints == 9,
            "Two Salamandra should enable two distinct mutagens");
        var copies = evaluator.Evaluate(position, Named("Obsidian Mirror"), PlayerSide.User);
        Check(copies.MaximumPoints == 1, "Mirror copied a lone eligible enemy more than once");

        var scope = evaluator.Evaluate(position, Named("Megascope"), PlayerSide.User);
        var scopeHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(scope, new(3, [], true, 3, false));
        Check(scope.After is not null && scopeHorizons?.TwoTurns.Maximum == bear.Power && scopeHorizons.OneTurn.Maximum == 0,
            $"Megascope must wait until its second timer tick: {scopeHorizons?.Compact}");
        var timerEngine = new TacticalPlayEngine(new PlayRuleBook(catalog), allowReachProjection: true);
        var tick = timerEngine.AutomaticReach(scope.After!, PlayerSide.User);
        Check(tick.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Melee).Cards.Count(card => card.CardId == bear.Id) +
            tick.After.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Count(card => card.CardId == bear.Id) == 2,
            "Megascope failed to spawn its stored base-copy target");
        var again = timerEngine.AutomaticReach(tick.After, PlayerSide.User);
        Check(again.Points == 0, "Completed timer fired repeatedly");
        var townsfolk = Named("Townsfolk"); var dame = Named("Thirsty Dame");
        var listeners = Put(poisonBoard, PlayerSide.User, CardZone.Board, BoardRow.Ranged,
            Unit(roland, "roland"), Unit(townsfolk, "townsfolk"), Unit(dame, "dame"));
        var chain = replay.ResolvePlay(listeners, "poison", PlayerSide.User,
            new Dictionary<string, PlaySelection> { ["poison"] = new(TargetId: "enemy") });
        Check(chain.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single(card => card.InstanceId == "townsfolk").Power == townsfolk.Power + 1 &&
            chain.After.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single(card => card.InstanceId == "dame").Power == dame.Power + 1,
            "Poison -> Roland Coins -> Townsfolk / Thirsty Dame chain was not resolved");
        var chainProjection = new CandidatePointEvaluator(extra).Evaluate(listeners, poison, PlayerSide.User);
        var chainHorizons = new CardPointHorizonEvaluator(extra).Evaluate(chainProjection, new(3, [], true, 3, false));
        Check(chainHorizons is { CardOnly.Maximum: 0, OneTurn.Maximum: 4 },
            $"Reactive Poison engines leaked into the card's individual X: {chainHorizons?.Compact}");
        var shieldBody = new CardDefinition("audit-shield", "Shield entry", "Neutral", CardKind.Unit, 4, 6, AbilityText: "Shield. ");
        var spyBody = new CardDefinition("audit-spy", "Spy entry", "Neutral", CardKind.Unit, 4, 2,
            AbilityText: "Disloyal. Deploy: Damage an enemy unit by 0.");
        var statusCatalog = catalog.Concat(new[] { shieldBody, spyBody }).ToArray();
        var statusBoard = Put(position, PlayerSide.User, CardZone.Board, BoardRow.Ranged, Unit(dame, "dame"));
        var shieldEntry = new CandidatePointEvaluator(statusCatalog).Evaluate(statusBoard, shieldBody, PlayerSide.Opponent);
        Check(shieldEntry.After!.Zone(PlayerSide.User, CardZone.Board, BoardRow.Ranged).Cards.Single().Power == dame.Power + 1,
            "A status-bearing opponent entry did not trigger Thirsty Dame");
        var spyEntry = new CandidatePointEvaluator(statusCatalog).Evaluate(statusBoard, spyBody, PlayerSide.User);
        Check(spyEntry.After!.Zones.Where(zone => zone.Side == PlayerSide.Opponent && zone.Zone == CardZone.Board)
                .SelectMany(zone => zone.Cards).Any(card => card.CardId == spyBody.Id && card.Statuses!.Contains(CardStatus.Spying)),
            "Disloyal entry did not acquire Spying");
        var stolenDeck = Put(statusBoard, PlayerSide.Opponent, CardZone.Deck, null, Unit(bear, "stolen-top"));
        var cantarella = evaluator.Evaluate(stolenDeck, Named("Cantarella"), PlayerSide.User);
        Check(cantarella.After?.Zone(PlayerSide.Opponent, CardZone.Deck).Cards.Length == 0 &&
            cantarella.After.Zones.Where(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board)
                .SelectMany(zone => zone.Cards).Any(card => card.InstanceId == "stolen-top"),
            "Cantarella did not play the inferred opposing top card for the acting player");
    }
}
