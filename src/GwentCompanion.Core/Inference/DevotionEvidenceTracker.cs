using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

/// <summary>Distinctive visual consequences, never just the presence of Devotion text.</summary>
public sealed class DevotionEvidenceTracker
{
    private DateTimeOffset? _conqueror;
    private DateTimeOffset _lastVote;
    private int _survivalVotes;
    private bool _previouslyOnBoard;
    private DateTimeOffset? _marinePlayed;
    private string? _effectSource;
    private DateTimeOffset _effectPlayedAt;
    public string? ObserveEvent(VisionEvidenceEvent evidence, string? faction, CardProvenance origin = CardProvenance.Unknown)
    {
        var sight = evidence.Sighting;
        if (sight.Source == CardSightSource.PlayPreview)
        {
            if (sight.Side == PlayerSide.Opponent && _effectSource == "131102" && InEffectWindow(evidence.ObservedAt) &&
                sight.Card.Kind != CardKind.Special && sight.Card.HasCategory("Wild Hunt"))
            {
                _effectSource = null;
                return "Ge'els was followed by an opponent non-Special Wild Hunt card play. Its non-Devotion Deploy can only play a Wild Hunt Special from deck; the wider Devotion choice visibly resolved.";
            }
            if (sight.Side == PlayerSide.Opponent) _effectSource = null;
            _conqueror = sight.Side == PlayerSide.Opponent && sight.Card.Id == "202614" && !_previouslyOnBoard ? evidence.ObservedAt : null;
            if (sight.Side == PlayerSide.Opponent && sight.Card.Id == "202652") _marinePlayed = evidence.ObservedAt;
            if (sight.Side == PlayerSide.Opponent && DevotionInteractionCatalog.All.Any(item => item.CardId == sight.Card.Id))
            { _effectSource = sight.Card.Id; _effectPlayedAt = evidence.ObservedAt; }
            _survivalVotes = 0; _lastVote = default;
        }
        if (origin is CardProvenance.Created or CardProvenance.Copied or CardProvenance.Stolen or CardProvenance.Transformed) return null;
        if (sight.Side != PlayerSide.Opponent || string.IsNullOrWhiteSpace(faction) ||
            !FactionCompatibility.IsPlayableBy(sight.Card, faction) || !EvolvingCardCatalog.IsFinal(sight.Card.Id)) return null;
        return $"Final evolved form {sight.Card.Name} recognized for the opponent's faction. Normal evolution requires Devotion; copied/stolen forms or mistaken identity remain possible.";
    }
    public string? ObserveFrame(DateTimeOffset at, GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings, bool boardScanned)
    {
        if (screen.IsCardSelectionOverlay || screen.View != GwentViewKind.Board) { _conqueror = null; return null; }
        if (!boardScanned) return null;
        var visible = sightings.Any(s => s.Side == PlayerSide.Opponent && s.Source == CardSightSource.Board && s.Card.Id == "202614" && s.Distance <= .20);
        _previouslyOnBoard = visible;
        if (_conqueror is not { } played) return null;
        if (at - played > TimeSpan.FromSeconds(18)) { _conqueror = null; return null; }
        if (!visible) { _survivalVotes = 0; return null; }
        if (at - played < TimeSpan.FromSeconds(3) || at - _lastVote < TimeSpan.FromMilliseconds(500)) return null;
        _lastVote = at;
        if (++_survivalVotes < 2) return null;
        _conqueror = null;
        return "Aen Elle Conqueror recognized in repeated board scans after its play preview and deploy settling time. Survival suggests Devotion; animation delays, summoning/copying and missed effects remain possible.";
    }
    public string? ObserveState(GameStateUpdate update)
    {
        if (!update.Accepted || update.After.At is not { } at || update.After.Phase != GamePhase.Playing) return null;
        string? Complete(string evidence) { _effectSource = null; _marinePlayed = null; return evidence; }
        bool FreshEffect(string id, int seconds = 20) => _effectSource == id && InEffectWindow(at, seconds);
        bool Visible(GameCardInstance card, PlayerSide side) => card.Location.Value.Controller == side &&
            card.Location.Value.Zone == CardZone.Board && card.Presence is CardPresence.Visible or CardPresence.LastKnown;
        if (FreshEffect("203281") && update.After.Cards.Any(card => card.Card.Id == "203281" && Visible(card, PlayerSide.Opponent) &&
            card.Status(CardStatus.Immune) is { Value: true } status && status.At >= _effectPlayedAt && status.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed))
            return Complete("Princess Adda gained visibly measured Immunity after Deploy. That status is her Devotion payoff; copied/transformed status sources remain a small ambiguity.");
        if (FreshEffect("202660") && update.After.Cards.Count(card => Visible(card, PlayerSide.User) &&
            card.Status(CardStatus.Spying) is { Value: true } status && status.At >= _effectPlayedAt && status.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed) >= 3)
            return Complete("Three player-side units gained visibly measured Spying after Fergus var Emreis. His ordinary Deploy marks only one; the Devotion payoff resolved.");
        if (FreshEffect("202891"))
        {
            var damagedUnboosted = update.After.Cards.Where(card => Visible(card, PlayerSide.User) && card.Power is { } power && power.At == at && !card.IdentityContinuityUncertain)
                .Select(card => (After: card, Before: update.Before.Cards.FirstOrDefault(old => old.InstanceId == card.InstanceId)))
                .Any(pair => pair.Before?.Power is { } oldPower && pair.Before.BasePower is { } oldBase && oldPower.Value <= oldBase.Value &&
                    pair.After.Power!.Value < oldPower.Value && oldPower.At <= at && oldBase.At <= at);
            if (damagedUnboosted)
                return Complete("Whoreson Junior's Deploy was followed by measured damage to a continuous enemy unit that was not boosted beforehand. The non-Devotion Deploy cannot select that target.");
        }
        if (FreshEffect("202632") && update.After.Cards.Any(card => Visible(card, PlayerSide.Opponent) && card.Card.Id != "202632" &&
            !card.Card.IsGold && card.Card.HasCategory("Firesworn") && card.LastSeen == at && card.Power is { } power &&
            power.At == at && power.Value == card.Card.Power + 2))
            return Complete("Ulrich's newly spawned bronze Firesworn copy was measured at printed power +2. That extra boost is his Devotion payoff.");
        if (update.After.Round?.Value is >= 2 && update.After.Cards.Any(card => card.Card.Id == "202620" && Visible(card, PlayerSide.Opponent) &&
            card.BasePower is { } power && power.At == at && power.Value > card.Card.Power))
            return Complete("Skjordal Drummond's measured base power exceeds his printed base in a later round, positively supporting the Devotion Veteran effect.");
        var leaderSpent = update.Before.Opponent.LeaderCharges is { Value: > 0 } beforeCharges &&
            update.After.Opponent.LeaderCharges is { Value: 0 } afterCharges && afterCharges.At == at;
        if (leaderSpent && update.After.Cards.Any(card => card.Card.Id == "203282" && Visible(card, PlayerSide.Opponent) &&
            card.LastSeen == at) && !update.Events.Any(e => e.Kind == "PlayPreview" && e.CardId == "203282"))
            return Complete("Radovid: Judgment arrived as the opponent's final leader charge was spent, without a matching play preview. His automatic summon requires Devotion.");
        var belohun = update.After.Cards.Any(card => card.Card.Id == "202647" && Visible(card, PlayerSide.Opponent));
        if (belohun && update.After.Cards.Any(card => card.Card.Id != "202647" && Visible(card, PlayerSide.Opponent) && card.LastSeen == at &&
            card.Card.Kind == CardKind.Unit && card.Card.Power < 5 && card.Power is { } power && power.At == at && power.Value == 6))
            return Complete("A newly arrived sub-5-power unit beside King Belohun was measured at 6. His non-Devotion ceiling is 5; concurrent boosts remain a small ambiguity.");
        var marine = update.After.Cards.Any(card => card.Card.Id == "202652" &&
            card.Location.Value.Controller == PlayerSide.Opponent && card.Location.Value.Zone == CardZone.Board &&
            card.Presence is CardPresence.Visible or CardPresence.LastKnown);
        if (!marine) return null;
        var recentDeploy = _marinePlayed is { } played && at >= played && at - played <= TimeSpan.FromSeconds(12);
        var chargeSpent = update.After.Cards.Where(card => card.Card.Id == "202652" &&
                card.Location.Value.Controller == PlayerSide.Opponent && card.Location.Value.Zone == CardZone.Board)
            .Any(card => update.Before.Cards.FirstOrDefault(old => old.InstanceId == card.InstanceId)?.Charges is { Value: > 0 } before &&
                card.Charges is { Value: 0 } after && before.At <= after.At && after.At == at);
        if (!recentDeploy && !chargeSpent) return null;
        // Attribute only a single, measured +4 continuity with no intervening opponent card preview.
        // This is a strong visual hint, not a proof: another engine can still boost while Marine is present.
        if (update.Events.Any(e => e.Kind == "PlayPreview" && e.Side == PlayerSide.Opponent && e.CardId != "202652")) return null;
        var deltas = update.After.Cards.Where(card => card.Location.Value.Controller == PlayerSide.Opponent &&
                card.Location.Value.Zone == CardZone.Board && card.Power is { } power && power.At == at && !card.IdentityContinuityUncertain)
            .Select(card => (Card: card, Before: update.Before.Cards.FirstOrDefault(old => old.InstanceId == card.InstanceId)))
            .Where(pair => pair.Before?.Power is { } oldPower && oldPower.At <= at && pair.Card.Power!.Value != oldPower.Value)
            .Select(pair => pair.Card.Power!.Value - pair.Before!.Power!.Value).ToArray();
        if (deltas.Length != 1 || deltas[0] != 4) return null;
        return Complete("Kerack Marine remained on the opponent board while one continuous allied unit received a measured +4 boost immediately after its deploy/Order window. Its non-Devotion Order boosts by 2; concurrent engine boosts remain a small ambiguity.");
    }
    private bool InEffectWindow(DateTimeOffset at, int seconds = 20) => _effectSource is not null && at >= _effectPlayedAt && at - _effectPlayedAt <= TimeSpan.FromSeconds(seconds);
    public void Reset() { _conqueror = null; _marinePlayed = null; _effectSource = null; _effectPlayedAt = default;
        _lastVote = default; _survivalVotes = 0; _previouslyOnBoard = false; }
}
