using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

internal static class CandidatePointEvaluatorTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static PositionCard Card(CardDefinition card, string id, int? power = null, bool? original = true,
        params CardStatus[] statuses) => new(id, card.Id, power ?? card.Power, card.Power, card.PrintedArmor ?? 0,
        statuses.ToImmutableHashSet(), 0, 0, original);

    private static GamePosition Put(GamePosition position, PlayerSide side, CardZone zone, BoardRow? row,
        params PositionCard[] cards) => position with
    {
        Zones = position.Zones.Select(item => item.Side == side && item.Zone == zone && item.Row == row
            ? item with { Cards = cards.ToImmutableArray(), Complete = true, TotalCount = cards.Length }
            : item).ToImmutableArray()
    };

    public static void Run()
    {
        var body = new CardDefinition("candidate-body", "Candidate body", "Monsters", CardKind.Unit, 8, 9,
            AbilityText: "Deploy: Resolve a deliberately unsupported test effect.", PrintedArmor: 0);
        var thrive = new CardDefinition("candidate-thrive", "Candidate Thrive", "Monsters", CardKind.Unit, 5, 4,
            AbilityText: "Thrive.", PrintedArmor: 0);
        var assimilate = new CardDefinition("candidate-assimilate", "Candidate Assimilate", "Nilfgaard", CardKind.Unit, 6, 4,
            AbilityText: "Assimilate 2.", PrintedArmor: 0);
        var intimidate = new CardDefinition("candidate-intimidate", "Candidate Intimidate", "Syndicate", CardKind.Unit, 6, 4,
            AbilityText: "Intimidate 2.", PrintedArmor: 0);
        var symbiosis = new CardDefinition("candidate-symbiosis", "Candidate Symbiosis", "Scoiatael", CardKind.Unit, 6, 4,
            AbilityText: "Symbiosis.", PrintedArmor: 0);
        var seductress = new CardDefinition("candidate-seductress", "Candidate Seductress", "Syndicate", CardKind.Unit, 5, 4,
            AbilityText: "Fee 3: Gain a Shield. Whenever your opponent plays a unit, boost self by 1. Bonded: Whenever your opponent plays a card, boost self by 1.", PrintedArmor: 0);
        var crime = new CardDefinition("candidate-crime", "Candidate Crime", "Syndicate", CardKind.Special, 5,
            CardCategories: new HashSet<string> { "Crime" }, AbilityText: "Perform an unsupported Crime effect.");
        var nature = new CardDefinition("candidate-nature", "Candidate Nature", "Scoiatael", CardKind.Special, 5,
            CardCategories: new HashSet<string> { "Nature" }, AbilityText: "Perform an unsupported Nature effect.");
        var crownsplitter = new CardDefinition("candidate-crownsplitter", "Candidate Crownsplitter", "Syndicate", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Crownsplitters" }, AbilityText: "", PrintedArmor: 0);
        var cleaver = new CardDefinition("202890", "Cleaver", "Syndicate", CardKind.Unit, 11, 6,
            AbilityText: "Intimidate. Deploy: Spawn and play Shakedown. Increase this card's Intimidate by 1 for each adjacent Crownsplitter. Fee 4: Spawn a Cleaver's Muscle on this row.", PrintedArmor: 0);
        var dryad = new CardDefinition("candidate-dryad", "Candidate Dryad", "Scoiatael", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Dryad" }, AbilityText: "", PrintedArmor: 0);
        var aucwenn = new CardDefinition("203149", "Aucwenn", "Scoiatael", CardKind.Unit, 12, 5,
            CardCategories: new HashSet<string> { "Dryad" }, AbilityText: "Symbiosis. Deploy: Infuse all your Naiads with the Nature category, wherever they are. Whenever you Spawn a Wandering Treant, give it Vitality equal to the number of Dryads you control.", PrintedArmor: 0);
        var koshchey = new CardDefinition("202832", "Koshchey", "Monsters", CardKind.Unit, 10, 4,
            CardCategories: new HashSet<string> { "Insectoid" }, AbilityText: "Thrive. Whenever this unit's Thrive is triggered, Spawn a Drone on this row. Adrenaline 4: Whenever this unit's Thrive is triggered, Spawn an Endrega Larva on this row instead.", PrintedArmor: 0);
        var queen = new CardDefinition("202435", "Kikimore Queen", "Monsters", CardKind.Unit, 10, 4,
            CardCategories: new HashSet<string> { "Insectoid" }, AbilityText: "Thrive. Whenever this unit's Thrive is triggered, boost all allied Insectoids on this row by 1. Whenever you play an Organic card, trigger own Thrive.", PrintedArmor: 0);
        var organic = new CardDefinition("candidate-organic", "Candidate Organic", "Monsters", CardKind.Special, 5,
            CardCategories: new HashSet<string> { "Organic" }, AbilityText: "Perform an unsupported Organic effect.");
        var insectoid = new CardDefinition("candidate-insectoid", "Candidate Insectoid", "Monsters", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Insectoid" }, AbilityText: "", PrintedArmor: 0);
        var tactic = new CardDefinition("candidate-tactic", "Candidate Tactic", "Nilfgaard", CardKind.Special, 5,
            CardCategories: new HashSet<string> { "Tactic" }, AbilityText: "Perform an unsupported Tactic effect.");
        var chargeEngine = new CardDefinition("candidate-charge-engine", "Candidate Charge Engine", "Nilfgaard", CardKind.Unit, 6, 4,
            AbilityText: "Order: Damage an enemy unit by 2. Whenever you play a Tactic card, gain 1 Charge.", PrintedArmor: 0);
        var crewMage = new CardDefinition("candidate-crew-mage", "Candidate Crew Mage", "Northern Realms", CardKind.Unit, 4, 3,
            CardCategories: new HashSet<string> { "Mage" }, AbilityText: "", PrintedArmor: 0);
        var crewSoldier = new CardDefinition("candidate-crew-soldier", "Candidate Crew Soldier", "Northern Realms", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Soldier" }, AbilityText: "", PrintedArmor: 0);
        var raffard = new CardDefinition("203050", "Raffard’s Vengeance", "Northern Realms", CardKind.Unit, 10, 4,
            CardCategories: new HashSet<string> { "Machine", "Mage", "Siege Engine" },
            AbilityText: "Order: Play a bronze unit from your hand, then draw a card. Cooldown: 5 Crew: Whenever you play a unit next to this card, damage a random enemy unit by 2. Mages contribute to this card's Crew ability.", PrintedArmor: 0);
        var elf = new CardDefinition("candidate-elf", "Candidate Elf", "Scoiatael", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Elf" }, AbilityText: "", PrintedArmor: 0);
        var swordmaster = new CardDefinition("200535", "Elven Swordmaster", "Scoiatael", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Elf", "Warrior" },
            AbilityText: "Order (Melee): Damage an enemy unit by 1. Cooldown: 2 Whenever you play an Elf, decrease the Cooldown by 1.", PrintedArmor: 0);
        var dwarf = new CardDefinition("candidate-dwarf", "Candidate Dwarf", "Scoiatael", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string> { "Dwarf" }, AbilityText: "", PrintedArmor: 0);
        var dwarfEngine = new CardDefinition("candidate-dwarf-engine", "Candidate Dwarf Engine", "Scoiatael", CardKind.Unit, 5, 5,
            AbilityText: "Whenever you play a Dwarf, boost self by 1.", PrintedArmor: 0);
        var specialEngine = new CardDefinition("candidate-special-engine", "Candidate Special Engine", "Neutral", CardKind.Unit, 5, 5,
            AbilityText: "Whenever you play a special card, damage a random enemy unit by 2.", PrintedArmor: 0);
        var graveBronze = new CardDefinition("candidate-grave-bronze", "Candidate Grave Bronze", "Skellige", CardKind.Unit, 4, 6,
            AbilityText: "", PrintedArmor: 0);
        var necromancy = new CardDefinition("200020", "Necromancy", "Neutral", CardKind.Special, 7,
            AbilityText: "Play a bronze unit from your graveyard and give it Doomed.");
        var portal = new CardDefinition("202201", "Portal", "Neutral", CardKind.Artifact, 10,
            AbilityText: "Deploy: Summon a random 4-provision cost unit from your deck to the left of this card. Timer 3: Summon a random 4-provision cost unit from your deck to the right of this card.");
        var whisperer = new CardDefinition("202916", "Whisperer of Dol Blathanna", "Scoiatael", CardKind.Unit, 7, 4,
            AbilityText: "Veil. When you play a special card, Spawn a base copy of self on this row and remove a Counter. Counter: 1", PrintedArmor: 0);
        var longship = new CardDefinition("candidate-longship", "Candidate Longship", "Skellige", CardKind.Unit, 5, 4,
            AbilityText: "Whenever your opponent plays a unit, damage it by 1.", PrintedArmor: 0);
        var crowmother = new CardDefinition("202514", "Crowmother", "Skellige", CardKind.Unit, 9, 4,
            CardCategories: new HashSet<string> { "Human", "Druid" },
            AbilityText: "Deploy: Spawn 2 Crows on this row. Whenever you play an Alchemy card, Summon Crowmother from your graveyard to a random allied row.", PrintedArmor: 0);
        var alchemy = new CardDefinition("candidate-alchemy", "Candidate Alchemy", "Skellige", CardKind.Special, 4,
            CardCategories: new HashSet<string> { "Alchemy" }, AbilityText: "Perform an unsupported Alchemy effect.");
        var riptide = new CardDefinition("203214", "Lord Riptide", "Monsters", CardKind.Unit, 9, 9,
            AbilityText: "Deploy (Melee): Clash with the highest-power enemy unit. Might: At the end of your turn, while in hand, gain 1 Armor.", PrintedArmor: 0);
        var warfare = new CardDefinition("candidate-warfare", "Candidate Warfare", "Northern Realms", CardKind.Special, 4,
            CardCategories: new HashSet<string> { "Warfare" }, AbilityText: "Perform an unsupported Warfare effect.");
        var resupply = new CardDefinition("candidate-resupply", "Candidate Resupply", "Northern Realms", CardKind.Unit, 5, 4,
            AbilityText: "Resupply. Order (Ranged): Damage an enemy unit by 2. Cooldown: 3", PrintedArmor: 0);
        var orderCandidate = new CardDefinition("candidate-order-card", "Candidate Order Card", "Northern Realms", CardKind.Unit, 4, 4,
            AbilityText: "Order: Boost an allied unit by 1.", PrintedArmor: 0);
        var arbalest = new CardDefinition("candidate-arbalest", "Candidate Arbalest", "Northern Realms", CardKind.Unit, 5, 4,
            AbilityText: "Order: Damage a unit by 1. Charge: 1 Whenever you play a card with an Order, gain 1 Charge .", PrintedArmor: 0);
        var frost = new CardDefinition("candidate-frost", "Candidate Frost", "Neutral", CardKind.Special, 4,
            AbilityText: "Spawn Frost on an enemy row for 3 turns.");
        var catalog = new[] { body, thrive, assimilate, intimidate, symbiosis, seductress, crime, nature, crownsplitter,
            cleaver, dryad, aucwenn, koshchey, queen, organic, insectoid, tactic, chargeEngine, crewMage, crewSoldier, raffard,
            elf, swordmaster, dwarf, dwarfEngine, specialEngine, graveBronze, necromancy, portal, whisperer, longship, crowmother, alchemy, riptide,
            warfare, resupply, orderCandidate, arbalest, frost };
        var evaluator = new CandidatePointEvaluator(catalog);

        var thriveBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee, Card(thrive, "thrive", 4));
        var bodyValue = evaluator.Evaluate(thriveBoard, body, PlayerSide.User);
        Check(bodyValue is { Quality: CandidateEvaluationQuality.BoardAwareEstimate, MinimumPoints: 10, MaximumPoints: 10 },
            $"Unsupported candidate lost readable Thrive: {bodyValue.Quality} {bodyValue.RangeText}");
        Check(bodyValue.BoardContributions.Any(value => value.Source == thrive.Name && value.Minimum == 1),
            "Thrive contribution was not separately auditable.");
        Check(thriveBoard.Zone(PlayerSide.User, CardZone.Hand).Cards.Length == 0,
            "Candidate evaluation mutated the supplied position.");

        var locked = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(thrive, "locked", 4, true, CardStatus.Locked));
        Check(evaluator.Evaluate(locked, body, PlayerSide.User).MaximumPoints == body.Power,
            "Locked Thrive contributed points.");

        var assimilateBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(assimilate, "assimilate"));
        var unknownOrigin = evaluator.Evaluate(assimilateBoard, body, PlayerSide.User, original: null);
        Check(unknownOrigin.MinimumPoints == body.Power && unknownOrigin.MaximumPoints == body.Power + 2,
            "Unknown candidate origin did not produce an Assimilate range.");
        Check(evaluator.Evaluate(assimilateBoard, body, PlayerSide.User, original: false).MaximumPoints == body.Power + 2,
            "Known generated candidate did not trigger Assimilate.");

        var crimeBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(intimidate, "intimidate"));
        var crimeValue = evaluator.Evaluate(crimeBoard, crime, PlayerSide.User);
        Check(crimeValue.MinimumPoints == 2 && crimeValue.MaximumPoints == 2 &&
            crimeValue.BoardContributions.Any(value => value.Source == intimidate.Name),
            "Unsupported Crime did not retain Intimidate value.");
        var cleaverBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(crownsplitter, "crownsplitter"), Card(cleaver, "cleaver"));
        Check(evaluator.Evaluate(cleaverBoard, crime, PlayerSide.User).MaximumPoints == 2,
            "Cleaver did not include adjacent Crownsplitter Intimidate scaling.");

        var natureBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(symbiosis, "symbiosis"));
        natureBoard = natureBoard with { User = natureBoard.User with { CurrentLeaderId = "200165" } };
        var natureValue = evaluator.Evaluate(natureBoard, nature, PlayerSide.User);
        Check(natureValue.MinimumPoints == 2 && natureValue.MaximumPoints == 2,
            "Nature candidate did not include unit plus leader Symbiosis.");
        var natureHorizons = new CardPointHorizonEvaluator(catalog).Evaluate(natureValue, new(3, []));
        Check(natureHorizons is { CardOnly.Maximum: 0, OneTurn.Minimum: 2, OneTurn.Maximum: 2, TwoTurns.Maximum: 2 },
            $"Symbiosis Treant belongs in Y, not X or a repeated Z tick: {natureHorizons?.Compact ?? "null"}.");
        var aucwennBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(dryad, "dryad"), Card(aucwenn, "aucwenn"));
        Check(evaluator.Evaluate(aucwennBoard, nature, PlayerSide.User).MaximumPoints == 2,
            "Aucwenn's first Vitality tick was not combined with the spawned Symbiosis body.");

        var koshcheyBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(koshchey, "koshchey"));
        Check(evaluator.Evaluate(koshcheyBoard, body, PlayerSide.User).MaximumPoints == body.Power + 2,
            "Koshchey did not combine its own Thrive boost and spawned body.");
        var queenBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(queen, "queen"), Card(insectoid, "insectoid"));
        var queenValue = evaluator.Evaluate(queenBoard, organic, PlayerSide.User);
        Check(queenValue.MaximumPoints == 2,
            $"Organic did not trigger Kikimore Queen's row-wide Insectoid payoff: {queenValue.RangeText}; " +
            string.Join(", ", queenValue.BoardContributions.Select(value => value.Source + "=" + value.RangeText)));

        var opponentReaction = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(seductress, "seductress"));
        Check(evaluator.Evaluate(opponentReaction, body, PlayerSide.User).MaximumPoints == body.Power - 1,
            "Opponent play reaction was not subtracted from candidate swing.");

        var opponentValues = evaluator.Evaluate(crimeBoard, new[] { body, crime }, PlayerSide.Opponent);
        Check(opponentValues.Count == 2 && opponentValues.All(value => value.Side == PlayerSide.Opponent),
            "Opponent likely-card batch did not preserve acting side.");

        var at = DateTimeOffset.UnixEpoch;
        var location = new StateFact<CardLocation>(new(PlayerSide.User, CardZone.Board, BoardRow.Melee, null), at, 1,
            EvidenceKind.Reviewed, "test");
        var contact = new GameCardInstance("snapshot-thrive", thrive, location, at, at, CardPresence.Visible);
        var rows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row =>
            new GameRowState(side, row, RowCoverage.Complete, at,
                side == PlayerSide.User && row == BoardRow.Melee ? [contact.InstanceId] : [], [], true))).ToImmutableArray();
        var snapshot = new GameStateSnapshot(1, "test", "candidate-snapshot", 1, at, GamePhase.Playing,
            new(2, at, 1, EvidenceKind.Reviewed, "test"), null,
            new(PlayerSide.User), new(PlayerSide.Opponent), [contact], rows, [], [], []);
        var snapshotValue = evaluator.Evaluate(snapshot, body, PlayerSide.User);
        Check(snapshotValue.MaximumPoints == body.Power + 1 && snapshotValue.BoardContributions.Any(),
            "GameStateSnapshot overload did not calculate a board-aware candidate value.");
        Check(snapshotValue.Assumptions.Any(value => value.Contains("printed defaults", StringComparison.OrdinalIgnoreCase)),
            "Snapshot defaulting was not disclosed.");

        var horizonEvaluator = new CardPointHorizonEvaluator(catalog);
        var weatherBoard = Put(thriveBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "weather-target"));
        var weatherValue = evaluator.Evaluate(weatherBoard, body, PlayerSide.User);
        var horizons = horizonEvaluator.Evaluate(weatherValue, new(1,
            [new(PlayerSide.Opponent, BoardRow.Melee, "Frost", 2)]));
        Check(horizons is { CardOnly.Minimum: 9, CardOnly.Maximum: 9, OneTurn.Minimum: 12, OneTurn.Maximum: 12,
                TwoTurns.Minimum: 14, TwoTurns.Maximum: 14, TwoTurnRelevant: false } && horizons.Compact == "9* | 12* | 14*",
            $"Three-horizon card/engine/weather value is wrong: {horizons?.Compact ?? "null"}.");
        var ownWeather = horizonEvaluator.Evaluate(weatherValue, new(3,
            [new(PlayerSide.User, BoardRow.Melee, "Frost", 2)]));
        Check(ownWeather is { OneTurn.Maximum: 10, TwoTurns.Maximum: 10 },
            $"Weather on the acting side incorrectly ticked again after the candidate was played: {ownWeather?.Compact ?? "null"}.");
        var createdWeatherBoard = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(thrive, "created-weather-target"));
        var createdWeather = evaluator.Evaluate(createdWeatherBoard, frost, PlayerSide.User);
        var createdHorizons = horizonEvaluator.Evaluate(createdWeather, new(3, []));
        Check(createdHorizons is { CardOnly.Maximum: 0, OneTurn.Maximum: 2, TwoTurns.Maximum: 4 },
            $"Candidate-created weather did not contribute its first Y tick and second Z tick: {createdHorizons?.Compact ?? "null"}.");
        var terminalWeather = horizonEvaluator.Evaluate(createdWeather, new(1, [], true, 0, false));
        Check(terminalWeather is { OneTurn.Maximum: 2, TwoTurns.Maximum: 2, TwoTurnRelevant: false },
            $"Empty hands should retain one alternating terminal weather/engine turn, but no second: {terminalWeather?.Compact ?? "null"}.");
        var passedWeather = horizonEvaluator.Evaluate(createdWeather, new(3, [], true, 3, true));
        Check(passedWeather is { OneTurn.Maximum: 0, TwoTurns.Maximum: 0 },
            $"A side that already passed received a future weather tick: {passedWeather?.Compact ?? "null"}.");

        var chargeBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(chargeEngine, "charge-engine") with { Charges = 0, Cooldown = 0 });
        chargeBoard = Put(chargeBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "charge-target"));
        var chargeValue = evaluator.Evaluate(chargeBoard, tactic, PlayerSide.User);
        var chargeHorizons = horizonEvaluator.Evaluate(chargeValue, new(3, []));
        Check(chargeValue.BoardContributions.Any(item => item.RequiresAction) &&
            chargeHorizons is { OneTurn.Maximum: 2, TwoTurns.Maximum: 2 } &&
            chargeHorizons.Notes.Any(note => note.Contains("not repeated", StringComparison.OrdinalIgnoreCase)),
            "Click-dependent charge reach incorrectly grew again in pass-safe Z.");

        var raffardBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(crewMage, "crew-mage"), Card(raffard, "raffard") with { Cooldown = 5, Charges = 1 });
        raffardBoard = Put(raffardBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "raffard-target"));
        var raffardValue = evaluator.Evaluate(raffardBoard, crewSoldier, PlayerSide.User);
        var raffardHorizons = horizonEvaluator.Evaluate(raffardValue, new(3, []));
        Check(raffardValue.BoardContributions.Any(item => item.Source == "Raffard's Vengeance" && item.Minimum == 2 && item.Maximum == 2) &&
            raffardHorizons is { CardOnly.Maximum: 4, OneTurn.Maximum: 6, TwoTurns.Maximum: 6 },
            $"Raffard Crew placement/cooldown reach is wrong: {raffardHorizons?.Compact ?? "null"}; {raffardValue.RangeText}.");

        var cooldownBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(swordmaster, "swordmaster") with { Cooldown = 1, Charges = 1 });
        cooldownBoard = Put(cooldownBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(body, "cooldown-target"));
        var cooldownValue = evaluator.Evaluate(cooldownBoard, elf, PlayerSide.User);
        var cooldownHorizons = horizonEvaluator.Evaluate(cooldownValue, new(3, []));
        Check(cooldownValue.BoardContributions.Any(item => item.Source == swordmaster.Name && item.RequiresAction && item.Maximum == 1) &&
            cooldownHorizons is { CardOnly.Maximum: 4, OneTurn.Maximum: 5, TwoTurns.Maximum: 5 },
            $"Cooldown-to-ready Order was not counted in Y or was incorrectly repeated in pass-safe Z: {cooldownHorizons?.Compact ?? "null"}.");

        var genericCategoryBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(dwarfEngine, "dwarf-engine"), Card(specialEngine, "special-engine"));
        genericCategoryBoard = Put(genericCategoryBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(body, "generic-target"));
        var dwarfValue = evaluator.Evaluate(genericCategoryBoard, dwarf, PlayerSide.User);
        var specialValue = evaluator.Evaluate(genericCategoryBoard, nature, PlayerSide.User);
        Check(dwarfValue.BoardContributions.Any(item => item.Source == dwarfEngine.Name && item.Maximum == 1) &&
            specialValue.BoardContributions.Any(item => item.Source == specialEngine.Name && item.Maximum == 2),
            "Generic category/special-card play events did not feed their literal boost/damage engines into Y.");

        var graveyard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Graveyard, null, Card(graveBronze, "grave-bronze"));
        var graveValue = evaluator.Evaluate(graveyard, necromancy, PlayerSide.User);
        Check(graveValue is { MinimumPoints: 6, MaximumPoints: 6 } && graveValue.Quality is CandidateEvaluationQuality.Simulated or CandidateEvaluationQuality.ConditionalSimulation &&
            graveValue.After?.Zones.Where(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).Any(card =>
                card.CardId == graveBronze.Id && card.Statuses!.Contains(CardStatus.Doomed)) == true,
            $"Known graveyard replay did not become an executable Doomed play: {graveValue.Quality} {graveValue.RangeText}.");

        var portalPosition = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Deck, null,
            Card(graveBronze, "portal-first"), Card(graveBronze, "portal-second"));
        var portalValue = evaluator.Evaluate(portalPosition, portal, PlayerSide.User);
        Check(portalValue is { MinimumPoints: 6, MaximumPoints: 6 } && portalValue.Quality is CandidateEvaluationQuality.Simulated or CandidateEvaluationQuality.ConditionalSimulation &&
            portalValue.After?.Zones.Where(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards)
                .First(card => card.CardId == portal.Id).Cooldown == 2,
            $"Portal did not summon from the known deck and advance Timer 3 exactly once: {portalValue.Quality} {portalValue.RangeText}.");
        var portalEngine = new TacticalPlayEngine(catalog);
        var portalTick2 = portalEngine.ResolveEndTurn(portalValue.After!, PlayerSide.User);
        var portalTick3 = portalEngine.ResolveEndTurn(portalTick2.After!, PlayerSide.User);
        Check(portalTick2.Points == 0 && portalTick3.Points == 6,
            "Portal Timer did not remain pass-safe at horizon two and summon exactly when Timer reached zero.");

        var whispererBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Melee,
            Card(whisperer, "whisperer") with { Charges = 1 });
        var whispererValue = evaluator.Evaluate(whispererBoard, nature, PlayerSide.User);
        Check(whispererValue.MaximumPoints == 4 && whispererValue.BoardContributions.Any(item => item.Source == whisperer.Name),
            "Whisperer's one-shot Counter did not add its base-copy reach to a special-card candidate.");

        var longshipBoard = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(longship, "longship"));
        Check(evaluator.Evaluate(longshipBoard, body, PlayerSide.User).MaximumPoints == body.Power - 1,
            "Opponent unit-play damage reaction was not subtracted from reach.");

        var crowmotherPosition = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Graveyard, null,
            Card(crowmother, "crowmother-grave"));
        var crowmotherValue = evaluator.Evaluate(crowmotherPosition, alchemy, PlayerSide.User);
        Check(crowmotherValue.MaximumPoints == crowmother.Power && crowmotherValue.BoardContributions.Any(item => item.Source == "Crowmother"),
            "Known graveyard Crowmother was not included in Alchemy reach.");

        var clashPosition = Put(GamePosition.EmptyKnown(), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(graveBronze, "clash-target", 12) with { Armor = 2 });
        var clashValue = evaluator.Evaluate(clashPosition, riptide, PlayerSide.User);
        Check(clashValue.MinimumPoints == 9 && clashValue.MaximumPoints == 9,
            $"Reach search should prefer Ranged body over an unfavorable Clash: {clashValue.RangeText}.");
        var stagedRiptide = clashValue.Position.Zone(PlayerSide.User, CardZone.Hand).Cards.Single(card => card.CardId == riptide.Id);
        var reviewedClash = new TacticalPlayEngine(catalog).ResolvePlay(clashValue.Position, stagedRiptide.InstanceId, PlayerSide.User,
            new Dictionary<string, PlaySelection> { [stagedRiptide.InstanceId] = new(Row: BoardRow.Melee) });
        Check(reviewedClash.Points == 7 && reviewedClash.After?.Zone(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee).Cards.Single().Power == 5,
            $"Reviewed Melee Clash did not apply simultaneous current-power damage through visible Armor: {reviewedClash.Points?.ToString() ?? "unresolved"}.");

        var supplyBoard = Put(GamePosition.EmptyKnown(), PlayerSide.User, CardZone.Board, BoardRow.Ranged,
            Card(resupply, "resupply") with { Charges = 1, Cooldown = 1 }, Card(arbalest, "arbalest") with { Charges = 0, Cooldown = 0 });
        supplyBoard = Put(supplyBoard, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(graveBronze, "supply-target"));
        var warfareValue = evaluator.Evaluate(supplyBoard, warfare, PlayerSide.User);
        var orderValue = evaluator.Evaluate(supplyBoard, orderCandidate, PlayerSide.User);
        Check(warfareValue.BoardContributions.Any(item => item.Source == resupply.Name && item.RequiresAction && item.Maximum == 2) &&
            orderValue.BoardContributions.Any(item => item.Source == arbalest.Name && item.RequiresAction && item.Maximum == 1),
            "Resupply or card-with-Order Charge conversion was not included in click-dependent Y reach. warfare=" +
            string.Join(',', warfareValue.BoardContributions.Select(item => item.Source + item.RangeText)) + " order=" +
            string.Join(',', orderValue.BoardContributions.Select(item => item.Source + item.RangeText)));

        var opponentHorizon = horizonEvaluator.Evaluate(evaluator.Evaluate(weatherBoard, body, PlayerSide.Opponent),
            new(3, [new(PlayerSide.User, BoardRow.Melee, "Frost", 2)]));
        Check(opponentHorizon is not null && opponentHorizon.TwoTurns.Maximum >= opponentHorizon.OneTurn.Maximum,
            "Opponent candidate did not use the same three-horizon/weather calculation.");

        Console.WriteLine("Candidate point evaluator passed: strict-first immutable staging, snapshot input, board-aware fallbacks, engine/leader/opponent reactions, X|Y|Z pass horizons and weather ranges.");
    }
}
