using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private LineState Approximate(LineState state, string note) => state.Note("[approx] " + note);

    private LineState GrantSpying(LineState state, string targetId, PlayerSide giver)
    {
        var target = Find(state.Position, targetId);
        if (target is null) return state;
        var changed = Status(state, targetId, CardStatus.Spying);
        if (Find(changed.Position, targetId)?.Statuses?.Contains(CardStatus.Spying) != true) return changed;
        changed = changed with { Position = SetCardValue(changed.Position,
            new(giver, target.CardId, "spying-granted", StoredCardId: target.CardId)) };
        foreach (var enforcer in Board(changed.Position, giver).Where(card => Rule(card.CardId)?.Reaction == "spying-charge" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).ToArray())
            changed = changed with { Position = Change(changed.Position, enforcer with { Charges = (enforcer.Charges ?? 0) + 1 }) };
        return changed.Note("Give Spying to " + (Rule(target.CardId)?.Card.Name ?? target.CardId));
    }

    private LineState TargetedBySpecial(LineState state, PositionCard source, PlayerSide actor, string targetId)
    {
        if (Rule(source.CardId)?.Card.Kind != CardKind.Special || Locate(state.Position, targetId)?.Zone != CardZone.Board)
            return state;
        var pendants = Board(state.Position, actor).Count(card => Rule(card.CardId)?.Reaction == "prism" &&
            card.Statuses?.Contains(CardStatus.Locked) != true);
        if (pendants == 0) return state;
        var allied = Locate(state.Position, targetId)!.Side == actor;
        var provision = Rule(source.CardId)!.Card.Provision;
        for (var index = 0; index < pendants; index++)
            state = Duration(state, targetId, allied ? CardStatus.Vitality : CardStatus.Bleeding, provision);
        return state.Note($"Prism Pendant: {(allied ? "Vitality" : "Bleeding")} {provision} x{pendants}");
    }

    private LineState AucwennTreant(LineState state, string treantId, PlayerSide side)
    {
        if (!Board(state.Position, side).Any(card => Rule(card.CardId)?.Reaction == "aucwenn" &&
                card.Statuses?.Contains(CardStatus.Locked) != true)) return state;
        var dryads = Board(state.Position, side).Count(card => Rule(card.CardId)?.Card.HasCategory("Dryad") == true);
        return dryads == 0 ? state : Duration(state.Note($"Aucwenn gives Wandering Treant Vitality {dryads}"),
            treantId, CardStatus.Vitality, dryads);
    }

    private IEnumerable<LineState> StrategicCardEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Effect == PlayEffect.OrderDeckByProvision)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            var ordered = deck.Cards.OrderByDescending(card => Rule(card.CardId)?.Card.Provision ?? -1).ToImmutableArray();
            return [(state with { Position = Set(state.Position, deck, ordered) }).Note(
                deck.Complete ? "Jan Calveit orders the deck by provisions" : "Jan Calveit orders the known/inferred deck inventory by provisions")];
        }

        if (rule.Effect == PlayEffect.GiveSpying)
        {
            var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return targets.Length == 0 ? [state] : SelectOptions(played.InstanceId, "Spying target", targets,
                card => card.InstanceId, Selection(played.InstanceId)?.TargetId).Select(card => GrantSpying(state, card.InstanceId, side));
        }

        if (rule.Effect == PlayEffect.Fucusya)
        {
            var grave = state.Position.Zone(side, CardZone.Graveyard);
            var choices = grave.Cards.Where(card => Rule(card.CardId)?.Card is { Kind: CardKind.Unit, Provision: <= 10 } definition &&
                definition.Faction != "Neutral").ToArray();
            if (choices.Length == 0) return [grave.Complete ? state.Note("Fucusya: no eligible graveyard unit") :
                Approximate(state, "Fucusya graveyard identity unavailable; resurrection body omitted")];
            return Bound(SelectOptions(played.InstanceId, "Fucusya graveyard unit", choices, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target =>
                Play(state.Note("Fucusya replays " + Rule(target.CardId)?.Card.Name), target.InstanceId, side, depth, explicitSequenceStep: true)
                    .Select(line =>
                    {
                        if (Locate(line.Position, target.InstanceId)?.Zone == CardZone.Board)
                            line = Status(line, target.InstanceId, CardStatus.Doomed);
                        var effects = line.Position.RowEffects ?? [];
                        var affected = Other(side);
                        var existing = effects.FirstOrDefault(effect => effect.AffectedSide == affected && effect.Row == row);
                        if (existing is null) effects = effects.Add(new(affected, row, "Rain", 2));
                        else if (existing.Name.Equals("Rain", StringComparison.OrdinalIgnoreCase) && existing.RemainingTurns is { } turns)
                            effects = effects.Replace(existing, existing with { RemainingTurns = turns + 2 });
                        else return Approximate(line, "Fucusya assumes two Rain turns; a conflicting/unread row effect may replace the result");
                        return Approximate(line with { Position = line.Position with { RowEffects = effects, RowEffectsKnownInactive = false } },
                            "Fucusya reach assumes two Rain turns (+2 in Y, +4 total in Z when turn windows remain)");
                    })));
        }

        if (rule.Effect == PlayEffect.Artaud)
        {
            var choices = (state.Position.CardValues ?? []).Where(value => value.Side == side &&
                    value.Kind.Equals("spying-granted", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.StoredCardId ?? value.CardId).Distinct().Select(Rule)
                .Where(candidate => candidate is { Card.Kind: CardKind.Unit } && !candidate.Disloyal && candidate.Card.Id != played.CardId)
                .ToArray();
            if (choices.Length == 0) return [state.Note("Artaud: no remembered eligible Spying target")];
            var torres = (state.Position.CardValues ?? []).Any(value => value.Side == side &&
                value.Kind.Equals("torres-played", StringComparison.OrdinalIgnoreCase));
            return Bound(SelectOptions(played.InstanceId, "Artaud copied unit", choices, candidate => candidate!.Card.Id,
                Selection(played.InstanceId)?.CreatedCardId).SelectMany(candidate =>
                SpawnAndPlay(torres ? Approximate(state, "Artaud memory excludes Torres-created deck Spying identities") : state,
                    candidate!.Card.Name, side, depth, recursiveGeneratedChoice: true)));
        }

        if (rule.Effect == PlayEffect.TorresFounder)
        {
            var eligible = state.Position.Zone(Other(side), CardZone.Deck).Cards.Select(card => Rule(card.CardId))
                .Where(candidate => candidate is { Card.Kind: CardKind.Unit, Card.Provision: <= 10 }).DistinctBy(candidate => candidate!.Card.Id).ToArray();
            var deficits = eligible.Select(candidate => Math.Max(0, 10 - candidate!.Card.Provision)).Order().ToArray();
            var low = deficits.Take(3).Sum(); var high = deficits.OrderDescending().Take(3).Sum();
            if (eligible.Length < 3) high = Math.Max(high, 18); // ordinary bronze-unit floor: 4p, three selections
            var marked = state with { Position = SetCardValue(state.Position, new(side, played.CardId, "torres-played", 1, 1)) };
            return Enumerable.Range(low, Math.Max(0, high - low) + 1).Where(value => value == low || value == high)
                .Select(value => Boost(Approximate(marked, $"Torres Founder deck choices bounded at +{low}..+{high}; copied identities are not fed into Artaud"),
                    played.InstanceId, value));
        }

        if (rule.Effect == PlayEffect.TorresPriest)
        {
            var observedFaction = state.Position.Zones.Where(zone => zone.Side == Other(side)).SelectMany(zone => zone.Cards)
                .Select(card => Rule(card.CardId)?.Card.Faction).FirstOrDefault(faction => faction is not null and not "Neutral");
            var pool = _rules.Values.Where(candidate => candidate.Card is { Kind: CardKind.Unit, IsGold: false } && !candidate.Disloyal &&
                    (observedFaction is null || candidate.Card.Faction == observedFaction || candidate.Card.SecondaryFactions.Contains(observedFaction)))
                .DistinctBy(candidate => candidate.Card.Id).OrderBy(candidate => candidate.Card.Power).ToArray();
            if (pool.Length == 0) return [Approximate(state, "Torres Highest Priest Create pool unavailable")];
            var endpoints = new[] { pool.First(), pool.Last() }.DistinctBy(candidate => candidate.Card.Id);
            return Bound(endpoints.SelectMany(candidate => SpawnAndPlay(Approximate(state,
                "Torres Highest Priest uses the fallback Create branch; returned generated-unit hand lines are omitted"),
                candidate.Card.Name, side, depth, basePower: candidate.Card.Power + 2, recursiveGeneratedChoice: true)));
        }

        if (rule.Effect == PlayEffect.Emhyr)
        {
            var hand = state.Position.Zone(side, CardZone.Hand);
            var choices = hand.Cards.Where(card => card.InstanceId != played.InstanceId && Rule(card.CardId)?.Card is { Kind: CardKind.Unit, IsGold: false } definition &&
                (definition.HasCategory("Soldier") || definition.HasCategory("Aristocrat"))).ToArray();
            if (choices.Length > 0)
                return Bound(SelectOptions(played.InstanceId, "Emhyr hand unit", choices, card => card.InstanceId,
                    Selection(played.InstanceId)?.TutorId).SelectMany(card => Play(state.Note("Emhyr plays " + Rule(card.CardId)?.Card.Name),
                        card.InstanceId, side, depth, explicitSequenceStep: true)));
            return hand.Complete ? [state.Note("Emhyr: no eligible bronze hand unit")] :
                SpawnAndPlay(Approximate(state, "Emhyr hidden-hand line assumes the usual Impera Enforcers target"),
                    "Impera Enforcers", side, depth, original: true);
        }

        if (rule.Effect == PlayEffect.Aucwenn)
        {
            var values = state.Position.CardValues ?? [];
            var naiadIds = state.Position.Zones.Where(zone => zone.Side == side).SelectMany(zone => zone.Cards)
                .Where(card => Rule(card.CardId)?.Card.HasCategory("Naiad") == true).Select(card => card.CardId)
                .Concat((side == PlayerSide.User ? state.Position.User : state.Position.Opponent).StartingDeckIds ?? [])
                .Where(id => Rule(id)?.Card.HasCategory("Naiad") == true).Distinct();
            foreach (var id in naiadIds)
                state = state with { Position = SetCardValue(state.Position, new(side, id, "aucwenn-nature", 1, 1)) };
            return [state.Note("Aucwenn infuses known/inferred Naiads with Nature")];
        }

        return [state.Unknown("Unsupported strategic card effect")];
    }
}
