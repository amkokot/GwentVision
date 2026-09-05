using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private LineState ActiveScenarioPlayListeners(LineState state, PositionCard played, PlayerSide side, GamePosition before)
    {
        var definition = Rule(played.CardId)?.Card;
        if (definition?.Kind != CardKind.Unit) return state;
        foreach (var scenario in Board(before, side).Where(card => card.Charges is >= 1 &&
                     card.Statuses?.Contains(CardStatus.Locked) != true))
        {
            if (scenario.CardId == "203080")
            {
                var triggersThrive = Board(before, side).Any(card => card.Statuses?.Contains(CardStatus.Locked) != true &&
                    Rule(card.CardId) is { Thrive: > 0 } && (played.Power ?? definition.Power) > card.Power);
                if (!triggersThrive)
                {
                    state = Boost(Approximate(state, "Manor Chapter 1 applies the flat +2 before play reactions"), played.InstanceId, 2);
                    // Deploy-spawned Thrive bodies exist before the original card's played event resolves. Nekker is
                    // the important case: original 1 -> 3, then its spawned base copy Thrives 1 -> 2.
                    var priorIds = Board(before, side).Select(card => card.InstanceId).ToHashSet(StringComparer.Ordinal);
                    var playedPower = Find(state.Position, played.InstanceId)?.Power ?? played.Power;
                    foreach (var spawnedThrive in Board(state.Position, side).Where(card => card.InstanceId != played.InstanceId &&
                                 !priorIds.Contains(card.InstanceId) && Rule(card.CardId) is { Thrive: > 0 } spawnedRule &&
                                 playedPower > card.Power).ToArray())
                        state = Boost(state.Note("Deploy-spawned Thrive reacts after Manor boost"), spawnedThrive.InstanceId,
                            Rule(spawnedThrive.CardId)!.Thrive + spawnedThrive.ExtraThrive);
                }
            }
            if (scenario.CardId == "203071" && definition.Faction != "Neutral")
            {
                var categories = definition.Categories.Count;
                if (categories > 0) state = Boost(state.Note($"Mysteries Chapter 1 boosts for {categories} categories"), played.InstanceId, categories);
            }
        }
        return state;
    }

    private LineState ActiveScenarioEndTurn(LineState state, PlayerSide side)
    {
        if (!Board(state.Position, side).Any(card => card.CardId == "203084" && card.Charges is >= 1 &&
                card.Statuses?.Contains(CardStatus.Locked) != true)) return state;
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.Coins is null) return state.Unknown("Forgotten Treasures Chapter 1 Coin count unread");
        var hoard = Math.Max(0, 8 - (resources.CurrentLeaderId == "202577" ? 2 : 0));
        return resources.Coins < hoard ? state : GainCoins(state.Note("Forgotten Treasures fills the pouch at end of turn"), side, 9 - resources.Coins.Value);
    }

    private IEnumerable<LineState> ScenarioPrologue(LineState state, PositionCard scenario, PlayerSide side, BoardRow row, int depth)
    {
        var initialized = state with { Position = Change(state.Position, scenario with { Charges = 0, Cooldown = 0 }) };
        return ScenarioAction(initialized.Note("Scenario Prologue: " + Rule(scenario.CardId)?.Card.Name), scenario.CardId, 0, side, row, depth);
    }

    private IEnumerable<LineState> ScenarioChapter(LineState state, string scenarioId, PlayerSide side, int depth)
    {
        var scenario = Find(state.Position, scenarioId); var zone = Locate(state.Position, scenarioId);
        if (scenario is null || zone?.Zone != CardZone.Board || scenario.Statuses?.Contains(CardStatus.Locked) == true) return [state];
        if (scenario.Charges is null) return [state.Unknown("Scenario chapter counter unread: " + Rule(scenario.CardId)?.Card.Name)];
        if (scenario.Charges >= 2) return [state];
        var chapter = scenario.Charges.Value + 1;
        var advanced = state with { Position = Change(state.Position, scenario with { Charges = chapter }) };
        return ScenarioAction(advanced.Note($"{Rule(scenario.CardId)?.Card.Name} Chapter {chapter}"), scenario.CardId,
            chapter, side, zone.Row!.Value, depth);
    }

    private IEnumerable<LineState> ScenarioAction(LineState state, string cardId, int chapter, PlayerSide side, BoardRow row, int depth)
    {
        return (cardId, chapter) switch
        {
            ("113101", 0) => ScenarioWeatherBoth(state, side, "Cataclysm", 2),
            ("113101", 1) => ScenarioDamage(state, side, 5, depth),
            ("113101", 2) => SpawnAndPlay(state, "Clear Skies", side, depth),

            ("202504", 0) => Spawn(state, _names.GetValueOrDefault("Reinforced Trebuchet"), side, BoardRow.Ranged, depth),
            ("202504", 1) => Spawn(state, _names.GetValueOrDefault("Battering Ram"), side, BoardRow.Ranged, depth),
            ("202504", 2) => SpawnAndPlay(state, "Bombardment", side, depth),

            ("202513", 0) => Spawn(state, _names.GetValueOrDefault("Crow Clan Preacher"), side, row, depth),
            ("202513", 1) => SpawnAndPlay(state, "Crow's-eye Rhizome", side, depth),
            ("202513", 2) => SpawnAndPlay(state, "Mardroeme", side, depth),

            ("202526", 0) => Spawn(state, _names.GetValueOrDefault("Desert Banshee"), side, row, depth),
            ("202526", 1) => SpawnAndPlay(state, "Barghest", side, depth),
            ("202526", 2) => SpawnAndPlay(state, "Nightwraith", side, depth),

            ("202536", 0) => Spawn(state, _names.GetValueOrDefault("Vernossiel's Commando"), side, row, depth),
            ("202536", 1) => SpawnMany(state, "Elven Deadeye", 2, side, row, depth),
            ("202536", 2) => SpawnAndPlay(state, "Waylay", side, depth),

            ("202544", 0) => Spawn(state, _names.GetValueOrDefault("Thirsty Dame"), side, row, depth),
            ("202544", 1 or 2) => SpawnAndPlay(state, "Fangs of the Empire", side, depth),

            ("202554", 0) => Spawn(state, _names.GetValueOrDefault("Sly Seductress"), side, row, depth),
            ("202554", 1) => Spawn(state, _names.GetValueOrDefault("Passiflora Peaches"), side, row, depth),
            ("202554", 2) => [GainCoins(state.Note("Passiflora gains 6 Coins"), side, 6)],

            ("203063", 0) => Spawn(state, _names.GetValueOrDefault("Eternal Eclipse Initiate"), side, row, depth),
            ("203063", 1) => [state.Unknown("Eternal Eclipse Chapter 1 Cultist infusion values require per-instance infused counters")],
            ("203063", 2) => SpawnAndPlay(state, "Eternal Eclipse Deacon", side, depth),

            ("203067", 0) => Spawn(state, _names.GetValueOrDefault("Bjorn's Drakkar"), side, row, depth),
            ("203067", 1) => [state.Unknown("Endless Voyage Chapter 1 enemy Deathwish infusion is active but its per-unit row payload is unresolved")],
            ("203067", 2) => EndlessVoyageFinal(state, side, row, depth),

            ("203071", 0) => MysteriesPrologue(state, side, row, depth),
            ("203071", 1) => [state.Note("Mysteries Chapter 1 category boost listener activated")],
            ("203071", 2) => SpawnAndPlay(state, "Loc Feainn: Convergence", side, depth),

            ("203075", 0) => SummonBronzeCategory(state, side, "Knight", row, depth),
            ("203075", 1) => [state.Unknown("Damsel in Distress Chapter 1 Grace-adjacency listener requires the later Grace event")],
            ("203075", 2) => SpawnAndPlay(state, "Mad Charge", side, depth),

            ("203080", 0) => Spawn(state, _names.GetValueOrDefault("Cursed Damsel"), side, row, depth, power: 6),
            ("203080", 1) => [state.Note("Manor Chapter 1 non-Thrive boost listener activated")],
            ("203080", 2) => PlayHighestBronzeNonNeutral(state, side, depth),

            ("203084", 0) => Spawn(state, _names.GetValueOrDefault("Gudrun Bjornsdottir"), side, row, depth),
            ("203084", 1) => [state.Note("Forgotten Treasures Chapter 1 Hoard listener activated")],
            ("203084", 2) => ScenarioHighestDamageByCoins(state, side, depth),
            _ => [state.Unknown("Unsupported scenario chapter: " + (Rule(cardId)?.Card.Name ?? cardId) + " " + chapter)]
        };
    }

    private IEnumerable<LineState> SpawnMany(LineState state, string name, int count, PlayerSide side, BoardRow row, int depth)
    {
        IEnumerable<LineState> states = [state];
        for (var i = 0; i < count; i++) states = states.SelectMany(line => Spawn(line, _names.GetValueOrDefault(name), side, row, depth)).ToArray();
        return states;
    }

    private IEnumerable<LineState> ScenarioWeatherBoth(LineState state, PlayerSide side, string name, int turns)
    {
        if (state.Position.RowEffects is null) return [state.Unknown("Scenario weather needs known row-effect state")];
        var effects = state.Position.RowEffects.Value;
        var affected = Other(side);
        foreach (var row in Enum.GetValues<BoardRow>())
        {
            var existing = effects.FirstOrDefault(effect => effect.AffectedSide == affected && effect.Row == row);
            if (existing is not null && !existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return [state.Unknown("Scenario weather replaces a different row effect")];
            effects = existing is null ? effects.Add(new(affected, row, name, turns)) :
                effects.Replace(existing, existing with { RemainingTurns = existing.RemainingTurns is { } current ? current + turns : null });
        }
        return [state with { Position = state.Position with { RowEffects = effects, RowEffectsKnownInactive = false } }];
    }

    private IEnumerable<LineState> ScenarioDamage(LineState state, PlayerSide side, int amount, int depth)
    {
        var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
        return enemies.Length == 0 ? [state] : enemies.SelectMany(card => Damage(state, card.InstanceId, amount, depth));
    }

    private IEnumerable<LineState> ScenarioHighestDamageByCoins(LineState state, PlayerSide side, int depth)
    {
        var coins = (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Coins;
        if (coins is null) return [state.Unknown("Forgotten Treasures Chapter 2 Coin count unread")];
        var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
        if (enemies.Length == 0) return [state];
        var highest = enemies.Max(card => card.Power);
        return enemies.Where(card => card.Power == highest).SelectMany(card => Damage(state, card.InstanceId, coins.Value, depth));
    }

    private IEnumerable<LineState> SummonBronzeCategory(LineState state, PlayerSide side, string category, BoardRow row, int depth)
    {
        var deck = state.Position.Zone(side, CardZone.Deck);
        var choices = deck.Cards.Where(card => Rule(card.CardId) is { Card.IsGold: false } rule && rule.Card.HasCategory(category)).ToArray();
        if (choices.Length == 0) return [deck.Complete ? state.Note("No eligible scenario summon") : state.Unknown("Scenario deck target unread")];
        return choices.SelectMany(card => Summon(state, card.InstanceId, side, row, depth));
    }

    private IEnumerable<LineState> PlayHighestBronzeNonNeutral(LineState state, PlayerSide side, int depth)
    {
        var deck = state.Position.Zone(side, CardZone.Deck);
        var choices = deck.Cards.Where(card => Rule(card.CardId) is { Card.Kind: CardKind.Unit, Card.IsGold: false } rule && rule.Card.Faction != "Neutral").ToArray();
        if (choices.Length == 0) return [deck.Complete ? state.Note("No Manor Chapter 2 target") : state.Unknown("Manor Chapter 2 deck inventory unread")];
        var highest = choices.Max(card => card.Power);
        return choices.Where(card => card.Power == highest).SelectMany(card => Play(state, card.InstanceId, side, depth, explicitSequenceStep: true));
    }

    private IEnumerable<LineState> MysteriesPrologue(LineState state, PlayerSide side, BoardRow row, int depth)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.StartingDeckIds is null) return [state.Unknown("Mysteries Prologue needs starting-deck categories")];
        var categories = resources.StartingDeckIds.Select(id => Rule(id)?.Card).Where(card => card is { Faction: not "Neutral" })
            .Select(card => card!.Categories.FirstOrDefault()).Where(category => category is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return Spawn(state, _names.GetValueOrDefault("Lake Guardian: Dawn Aspect"), side, row, depth, categories);
    }

    private IEnumerable<LineState> EndlessVoyageFinal(LineState state, PlayerSide side, BoardRow row, int depth)
    {
        var drakkar = Board(state.Position, side).FirstOrDefault(card => Rule(card.CardId)?.Card.Name == "Bjorn's Drakkar");
        if (drakkar is null) return Spawn(state, _names.GetValueOrDefault("Bjorn's Drakkar"), side, row, depth);
        var healed = Heal(state, drakkar.InstanceId, int.MaxValue);
        drakkar = Find(healed.Position, drakkar.InstanceId)!;
        return [healed with { Position = Change(healed.Position, drakkar with
        {
            BasePower = drakkar.BasePower + 5, Power = drakkar.Power + 5, Charges = (drakkar.Charges ?? 0) + 1,
            Statuses = drakkar.Statuses!.Where(status => status is CardStatus.Doomed or CardStatus.Resilience).ToImmutableHashSet()
        }) }];
    }
}
