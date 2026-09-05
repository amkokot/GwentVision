using System.Globalization;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private sealed record ProjectionKey(object Decks, object? Memory, object? Pin, object? Catalog, string State);
    private sealed record ProjectionInput(DeckDefinition[] Decks, ObservedCard[] Evidence, string? Faction,
        DeckDefinition? Pin, DeckProjectionEdits Edits, ObservedStartingDeckAssessment Rules, int MinimumSize,
        string? Leader, IReadOnlyList<CardDefinition>? Catalog, string? Stratagem,
        IReadOnlyList<LearnedOpponentDeck>? Memory, int? Mmr, string Encounter, IReadOnlyList<SummonCandidateEvidence> SummonEvidence,
        IReadOnlyList<DeckCompositionClue> CompositionClues, IReadOnlyList<OpponentSequenceEvidence> SequenceEvidence);
    private LatestWorkQueue<ProjectionKey, ProjectionInput, OpponentDeckProjection>? _projectionQueue;
    private string? _projectionEncounter;

    private void QueueDeckProjection()
    {
        if (_windowClosing) return;
        var evidence = _opponentTracker.DeckBuildingObservations.ToArray();
        var rules = _opponentKnowledge.Assess(evidence);
        var edits = _opponentEdits.Snapshot();
        var input = new ProjectionInput(_cachedDecks, evidence,
            _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null, _confirmedOpponentDeck, edits, rules,
            _opponentKnowledge.MinimumSize(evidence), _opponentKnowledge.StartingLeader, _candidateCatalog,
            _opponentKnowledge.StartingStratagemId, _opponentMemory?.Records, MatchMmr(), CurrentEncounterId,_opponentKnowledge.SummonAbsence.Evidence,
            _deckCompositionClues.Where(item => item.Key.Side == PlayerSide.Opponent).Select(item => item.Value).ToArray(),
            _opponentKnowledge.Sequences.Evidence);
        // Recognition timestamps/board points/unchanged hand counts do not change
        // starting-deck inference. Actual copies, origins, confidence, choices and
        // hard-rule changes do. Catalogue/library/memory replacements invalidate it.
        var state = JsonSerializer.Serialize(new {
            input.Faction, input.Leader, input.Stratagem, input.MinimumSize, input.Mmr, input.Encounter,
            Patch = DeckPatchMetadata.Current(DateTimeOffset.Now).Label,
            input.SummonEvidence,
            input.CompositionClues,
            input.SequenceEvidence,
            Cards = evidence.OrderBy(o => o.Card.Id, StringComparer.Ordinal).Select(o => new {
                o.Card.Id, o.ObservedCopies, o.Provenance, o.Confidence }),
            Rules = new[] { rules.Shupe.State, rules.Radeyah.State, rules.GoldenNekker.State,
                rules.Renfri.State, rules.Devotion.State, rules.Musicians?.State },
            Picks = edits.Included.Keys.OrderBy(k => k.CardId, StringComparer.Ordinal).ThenBy(k => k.Copy),
            Exclusions = edits.Excluded.OrderBy(k => k.CardId, StringComparer.Ordinal).ThenBy(k => k.Copy)
        });
        if (_projectionEncounter != input.Encounter)
        {
            _projectionEncounter = input.Encounter;
            _lastProjection = null;
            OpponentDeckCards.Rows = null;
            CandidateCardTray.Rows = null;
            OpponentDeckSummary.Text = "Updating deck from the current match…";
        }
        _projectionQueue ??= new(
            request => {
                using var tracking = _gameplayPriority.EnterRecognition();
                return new OpponentDeckProjector().Build(request.Decks, request.Evidence, request.Faction,
                request.Pin, request.Edits, request.Rules, request.MinimumSize, request.Leader, request.Catalog,
                request.Stratagem, request.Memory is null ? null : new OpponentEncounterPrior(request.Memory, request.Mmr, request.Encounter, request.Decks), request.SummonEvidence,
                request.CompositionClues, request.SequenceEvidence);
            },
            projection => {
                if (_windowClosing) return;
                _lastProjection = projection;
                RenderCompletedProjection();
                RenderDeckRuleBadges();
                if (_lastGameStateUpdate is not null) RefreshThreats(_gameState.Current.At ?? DateTimeOffset.Now);
            },
            exception => ShowAnalysisFailure("Deck suggestions could not update; recording is independent.", exception));
        if (_projectionQueue.Request(new(input.Decks, input.Memory, input.Pin, input.Catalog, state), input))
            OpponentDeckSummary.Text = (_lastProjection?.Summary.Split(". ")[0] ?? "Deck suggestions") + " · updating…";
    }
}
