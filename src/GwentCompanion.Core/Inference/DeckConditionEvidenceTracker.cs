using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

/// <summary>Bounded, same-controller effect sequences. A card sighting alone is not proof that its condition resolved.</summary>
public sealed class DeckConditionEvidenceTracker
{
    private string? _source;
    private DateTimeOffset _playedAt;
    private int _nekkerStep;
    private readonly HashSet<string> _seenPayoffs = [];
    private static readonly CardKind[] NekkerSequence = [CardKind.Unit, CardKind.Special, CardKind.Artifact];
    private static readonly HashSet<string> RenfriCurses = ["Curse of Lust", "Curse of Gluttony", "Curse of Greed", "Curse of Sloth", "Curse of Envy", "Curse of Wrath", "Curse of Pride"];
    public void Reset() { _source = null; _playedAt = default; _nekkerStep = 0; _seenPayoffs.Clear(); }
    public ResolvedDeckCondition? Observe(VisionEvidenceEvent evidence)
    {
        var sight = evidence.Sighting; var card = sight.Card; var at = evidence.ObservedAt;
        if (sight.Source == CardSightSource.History) return null; // Scroll order is not resolution order.
        if (sight.Side != PlayerSide.Opponent)
        { if (sight.Source == CardSightSource.PlayPreview) _source = null; return null; }
        var payoff = (!card.CanBeInStartingDeck && card.Name.StartsWith("Shupe:", StringComparison.Ordinal)) || card.Kind == CardKind.Stratagem || RenfriCurses.Contains(card.Name);
        var newPayoff = payoff && _seenPayoffs.Add(card.Id);
        if (sight.Source == CardSightSource.PlayPreview && card.Id is "201627" or "202478" or "203088" or "203123" or "201626")
        { _source = card.Id; _playedAt = at; _nekkerStep = 0; return null; }
        if (!Pending(at) || sight.Distance > .20) return null;
        if (_source == "201627" && newPayoff && !card.CanBeInStartingDeck && card.Name.StartsWith("Shupe:", StringComparison.Ordinal))
            return Complete(DeckCondition.Singleton, at, "Shupe's Day Off followed by a newly observed same-side Shupe form; the singleton effect resolved. Exact adventure ability remains unknown.");
        if (_source == "202478" && newPayoff && card.Kind == CardKind.Stratagem)
            return Complete(DeckCondition.Singleton, at, "Radeyah play followed by a newly observed same-side stratagem; singleton activation observed. This is not the opening stratagem.");
        if (_source == "203088" && newPayoff && RenfriCurses.Contains(card.Name) && sight.Source == CardSightSource.Board)
            return Complete(DeckCondition.Renfri, at, "Renfri followed by a newly recognized curse ability on the same side; at least 25 starting units supported by the resolved effect.");
        if (_source == "203123" && sight.Source == CardSightSource.PlayPreview && card.Kind == NekkerSequence[_nekkerStep])
        {
            if (++_nekkerStep == NekkerSequence.Length)
                return Complete(DeckCondition.GoldenNekker, at, "Golden Nekker followed by the same-side unit → special → artifact play sequence, without an intervening opponent turn; conditional effect resolution observed.");
        }
        return null;
    }
    public ResolvedDeckCondition? Observe(GameStateUpdate update)
    {
        if (!update.Accepted || update.After.At is not { } at || !Pending(at)) return null;
        if (update.After.Phase is GamePhase.Ended or GamePhase.RoundTransition) { _source = null; return null; }
        var leader = update.After.Opponent.CurrentLeader;
        if (_source == "203088" && leader is not null && leader.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed &&
            leader.At >= _playedAt && RenfriCurses.Contains(leader.Value) && leader.Value != update.Before.Opponent.CurrentLeader?.Value)
            return Complete(DeckCondition.Renfri, at, "Renfri's same-side leader replacement was observed; at least 25 starting units. Starting leader/faction and provision allowance are unchanged.");
        if (_source == "201626" && update.After.Cards.Any(card => card.Card.Id == "201626" && card.Location.Value.Controller == PlayerSide.Opponent &&
            card.Presence == CardPresence.Visible && new[] { CardStatus.Resilience, CardStatus.Shield, CardStatus.Veil }.All(status =>
                card.Status(status) is { Value: true } fact && fact.At >= _playedAt && fact.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed)))
            return Complete(DeckCondition.GoldenNekker, at, "Ciri: Nova play followed by measured Resilience, Shield and Veil; conditional deploy resolution observed.");
        return null;
    }
    private bool Pending(DateTimeOffset at)
    {
        if (_source is null || at < _playedAt) return false;
        if (at - _playedAt > TimeSpan.FromSeconds(90)) { _source = null; return false; }
        return true;
    }
    private ResolvedDeckCondition Complete(DeckCondition condition, DateTimeOffset at, string reason)
    { _source = null; return new(condition, at, reason); }
}
