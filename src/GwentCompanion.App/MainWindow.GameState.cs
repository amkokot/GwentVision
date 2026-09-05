using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    // UI-thread owner; only immutable snapshots cross back to the recording worker.
    private readonly GameStateTracker _gameState = new();
    private GameStateUpdate? _lastGameStateUpdate;
    private GamePosition? _calculationPosition;
    private readonly ZoneInventoryTracker _zoneInventory = new();
    private readonly GrantedMechanicTracker _grantedMechanics = new();
    private readonly PirateArmorTracker _pirateArmor = new();
    private readonly CultistSynergyTracker _cultists = new();
    private readonly TributeRefundTracker _tributeRefunds = new();
    private readonly Dictionary<(PlayerSide Side, string Source), DeckCompositionClue> _deckCompositionClues = [];

    private void UpdateStoredGameState(CardVisionResult result)
    {
        if (_gameState.Current.Revision == 0) _gameState.Reset(CurrentEncounterId, _selectedUserDeck);
        var at = result.SampledAt;
        var previousUser = _gameState.Current.User; var previousOpponent = _gameState.Current.Opponent;
        StateFact<string>? Reference(string? value, StateFact<string>? previous) => string.IsNullOrWhiteSpace(value) ? null :
            previous is { Kind: EvidenceKind.Reference } && previous.Value == value ? previous :
                new(value, at, 1, EvidenceKind.Reference, "Selected player starting-deck reference; not a current-zone inventory");
        StateFact<string>? Reviewed(string? value, StateFact<string>? previous) => string.IsNullOrWhiteSpace(value) ? null :
            previous is { Kind: EvidenceKind.Reviewed } && previous.Value == value ? previous :
                new(value, at, 1, EvidenceKind.Reviewed, "Starting metadata adopted from opponent knowledge; current ability is separate");
        StateFact<string>? Visible(string? value, double confidence, StateFact<string>? previous) => string.IsNullOrWhiteSpace(value) ? null :
            previous is { Kind: EvidenceKind.Visual } && previous.Value == value ? previous :
                new(value, at, confidence, EvidenceKind.Visual, "Persistent leader-ability HUD emblem");
        var own = new PlayerGameState(PlayerSide.User, Faction: Reference(_selectedUserDeck?.Faction, previousUser.Faction),
            StartingLeader: Reference(_selectedUserDeck?.Leader, previousUser.StartingLeader),
            OpeningStratagemId: Reference(_selectedUserDeck?.Stratagem?.Id, previousUser.OpeningStratagemId),
            StartingDeckReference: _selectedUserDeck);
        var opponent = new PlayerGameState(PlayerSide.Opponent,
            Faction: _opponentTracker.HasStableFaction && _opponentTracker.Faction is { } faction ?
                previousOpponent.Faction?.Value == faction ? previousOpponent.Faction : new(faction, at, .85, EvidenceKind.Inferred, "Existing faction tracker") : null,
            StartingLeader: Reviewed(_opponentKnowledge.StartingLeader, previousOpponent.StartingLeader),
            CurrentLeader: Visible(_opponentKnowledge.CurrentLeader, _opponentKnowledge.VisibleLeaderConfidence, previousOpponent.CurrentLeader),
            OpeningStratagemId: Reviewed(_opponentKnowledge.StartingStratagemId, previousOpponent.OpeningStratagemId));
        var zones = _zones.Entries.Select(entry => new ZoneIdentityEvidence(entry.Card.Id, entry.Card.Name, entry.Side,
            entry.Zone == VisibleCardZone.Deck ? CardZone.Deck : CardZone.Graveyard, entry.FirstSeen, entry.LastSeen, 1, false,
            entry.Route switch { ZoneEntryRoute.Generated or ZoneEntryRoute.HeulynSetup => CardProvenance.Created,
                ZoneEntryRoute.StartingOriginal or ZoneEntryRoute.RioghanSetup => CardProvenance.ProbableStartingDeck, _ => CardProvenance.Unknown }, entry.Evidence)).ToArray();
        _lastGameStateUpdate = GameStateVisionAdapter.Apply(_gameState, result,
            new(User: own, Opponent: opponent, Zones: zones, DeckChanges: _deckMutations.Changes, ReplaceStartingMetadata: true));
        _zoneInventory.Observe(_lastGameStateUpdate);
        if (_candidateCatalog is not null) _grantedMechanics.Observe(_lastGameStateUpdate, _candidateCatalog);
        _pirateArmor.Observe(_lastGameStateUpdate);
        _cultists.Observe(_lastGameStateUpdate, _candidateCatalog ?? []);
        if (_lastGameStateUpdate.Accepted && result.CultistInfusion is { } infusion &&
            result.Screen.View == GwentCompanion.Core.Vision.GwentViewKind.Board && !result.Screen.IsCardSelectionOverlay)
            _cultists.ObserveHover(_lastGameStateUpdate.After, infusion, at, result.HoverInPlayerHand);
        Watch?.ObserveInventory(_zoneInventory.Entries, at);
        if (_opponentKnowledge.ObserveConditions(_lastGameStateUpdate)) RenderLiveInference();
        foreach (var action in result.Events.Where(item => item.Sighting.Source == GwentCompanion.Core.Vision.CardSightSource.PlayPreview))
            _zoneInventory.ObserveSpecial(action.Sighting.Card, action.Sighting.Side, action.ObservedAt);
        _calculationPosition = CalculationPositionAdapter.FromObserved(_lastGameStateUpdate.After);
        if (_candidateCatalog is not null)
        {
            _liveValues.Observe(_lastGameStateUpdate, _candidateCatalog, _selectedUserDeck?.Cards.Select(item => item.Card) ?? [],
                _lastProjection?.Slots.Where(slot => slot.Card is not null).Select(slot => slot.Card!) ?? []);
            _liveValues.ObserveInventory(_zoneInventory.Entries, _lastGameStateUpdate.After);
            if (_candidateCatalog.FirstOrDefault(card => card.Id == "203100") is { } king)
            {
                var inferenceChanged = false;
                foreach (var refund in _tributeRefunds.Observe(_lastGameStateUpdate, _candidateCatalog))
                {
                    if (refund.NoRefund)
                    {
                        if (refund.Side == PlayerSide.Opponent)
                            inferenceChanged |= _opponentKnowledge.SummonAbsence.ObserveTributeResolution(false);
                    }
                    else if (refund.Verified)
                    {
                        _liveValues.TributeRefund(refund.Id, refund.Side, refund.PaidCoins, refund.RefundedCoins, true, refund.At, king);
                        if (refund.Side == PlayerSide.Opponent)
                        {
                            inferenceChanged |= _opponentKnowledge.SummonAbsence.ObserveTributeResolution(true);
                            inferenceChanged |= _opponentTracker.ConsiderDirectPlay(king, .99, refund.At,
                                "Verified Tribute refund before King of Beggars appeared. " + refund.Evidence,
                                CardProvenance.ProbableStartingDeck);
                            if (_liveValues.Growth(refund.Side, king.Id)?.Maximum > 0)
                                _zoneInventory.Review(refund.Side, king.Id, CardZone.Deck, 1, refund.At);
                        }
                    }
                    else
                    {
                        _liveValues.TributeRefundHint(refund.Id, refund.Side, refund.PaidCoins, refund.RefundedCoins, refund.At, king);
                        // One unchanged counter may mean the Tribute was declined. Two independent
                        // compatible events are enough for a visible, explicitly uncertain hypothesis.
                        if (refund.Side == PlayerSide.Opponent && refund.CompatibleEventCount >= 2)
                            inferenceChanged |= _opponentTracker.ConsiderDirectPlay(king, .72, refund.At,
                                $"{refund.CompatibleEventCount} payable Tributes had full-refund-compatible coin totals; activation remains visually ambiguous.",
                                CardProvenance.ProbableStartingDeck);
                    }
                }
                if (inferenceChanged) RenderLiveInference();
            }
            if (result.Description is { SourceId: "202192", Candidates.Count: 1 } soul)
            {
                var swordSide = result.HoverInPlayerHand ? PlayerSide.User : result.Sightings
                    .FirstOrDefault(sighting => sighting.Card.Id == "202192" && sighting.Source == GwentCompanion.Core.Vision.CardSightSource.PlayPreview)?.Side;
                if (swordSide is { } owner)
                    _liveValues.Store(owner, soul.Candidates[0], result.HoverInPlayerHand
                        ? "Read from the player's hovered Sword description; verify OCR target"
                        : "Read from the enlarged Sword play description; opponent stored soul remains OCR-reviewed");
            }
            if (result.RuntimeValue is { } runtime && _candidateCatalog.FirstOrDefault(card => card.Id == runtime.CardId) is { } dynamicCard)
            {
                var side = result.HoverInPlayerHand ? PlayerSide.User : result.Sightings
                    .FirstOrDefault(sighting => sighting.Card.Id == runtime.CardId && sighting.Source == GwentCompanion.Core.Vision.CardSightSource.PlayPreview)?.Side;
                if (side is { } owner)
                {
                    var reason = result.HoverInPlayerHand ? "Repeatedly read from the player's hovered card text; last observed value" :
                        "Repeatedly read from the enlarged play description; last observed value";
                    _liveValues.RecordGrowth(new(owner, dynamicCard.Id, dynamicCard.Name, runtime.Amount, runtime.Amount, runtime.Unit, reason));
                    if (runtime.CardId == "202219")
                    {
                        _deckCompositionClues[(owner, runtime.CardId)] = new(runtime.CardId, dynamicCard.Name, "Nature", runtime.Amount,
                            runtime.CardId, at, .95, $"Spring Equinox displayed {runtime.Amount}: exact count of Nature cards other than Spring Equinox in the starting deck.");
                        if (owner == PlayerSide.Opponent) QueueDeckProjection();
                    }
                }
            }
        }
        RenderLiveValues();
    }
}
