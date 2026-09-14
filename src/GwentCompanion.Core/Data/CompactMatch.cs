using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Data;

// Observed identity does NOT prove membership in the original deck. Preserve Origin separately.
public enum MatchCardEvidence : byte { Observed, SelectedReference, Inferred, ManualHypothesis }
public sealed record MatchCard(string CardId, int Copies, MatchCardEvidence Evidence,
    CardProvenance Origin, byte Confidence)
{
    // Expose the distinction in decoded data too: a recognized identity is not an
    // exact census of physical starting-deck copies. Reference counts are only
    // exact within that user-selected list, whose accuracy is separately qualified.
    public bool CopyCountIsEstimate => Evidence != MatchCardEvidence.SelectedReference;
}
public sealed record MatchPlayer(string? Faction, string? Leader, string? Stratagem,
    MatchCard[] Observations, MatchCard[] Reference, MatchCard[] Hypothesis);
public sealed record MatchAction(string? CardId, PlayerSide? Side, int Round, string Kind);
public sealed record MatchRound(int Number, int? UserScore, int? OpponentScore, bool FinalConfirmed);

/// <summary>Local analytics, not a full replay. No per-action dates, names, images or diagnostic text.</summary>
public sealed record CompactMatch(Guid InstallationId, Guid MatchId, DateOnly GameDateUtc, string DetectorVersion,
    string RulesVersion, string Patch, bool PatchInferred, long Revision, bool CaptureStopped,
    bool ResultObserved, string? Result, int? MmrAfter, int? MmrChange, int? MmrPeak,
    bool FactionMmr, int? Rank, MatchPlayer User, MatchPlayer Opponent,
    MatchRound[] Rounds, MatchAction[] Actions, bool SequenceTruncated = false, DateTimeOffset? StartedAtUtc = null,
    bool MmrUnconfirmed = false);

/// <summary>Consumes accepted detector deltas, not the bounded RecentEvents snapshot.</summary>
public sealed class MatchAcquisition
{
    private readonly Dictionary<int, MatchRound> _rounds = [];
    private readonly HashSet<(PlayerSide Side, string Card)> _visibleIds = [];
    private MatchCard[] _trackerHypotheses = [];
    private MatchCard[] _projectionHypotheses = [];
    private MatchPlayer? _selectedUserReference;
    private readonly string _version;
    private readonly Guid _installationId;
    private DateTimeOffset? _lastAt;
    private DateTimeOffset? _startedAt;
    private GameStateSnapshot? _state;
    private MatchPlayer _user = new(null, null, null, [], [], []);
    private MatchPlayer _opponent = new(null, null, null, [], [], []);
    private GwentCompanion.Core.Vision.PostMatchMmr? _mmr;
    private int? _rank;
    private string? _result;
    private bool _resultObserved;
    private long _revision;
    public Guid MatchId { get; } = Guid.NewGuid();
    public DateOnly? GameDateUtc { get; private set; }

    public MatchAcquisition(string detectorVersion, Guid installationId, DeckDefinition? selectedUserDeck = null)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("Installation ID is required.", nameof(installationId));
        _version = detectorVersion; _installationId = installationId;
        if (selectedUserDeck is not null) _selectedUserReference = Reference(selectedUserDeck);
    }

    public void Observe(GameStateUpdate update, GwentCompanion.Core.Vision.GwentVisualObservation screen,
        MatchCard[] userObservations, MatchCard[] opponentObservations)
    {
        if (!update.Accepted || update.After.At is not { } at || _lastAt is { } last && at <= last ||
            !screen.FrameGeometrySupported) return;
        _lastAt = at;
        // Menus alone must never manufacture a played match.
        if (GameDateUtc is null && screen.MatchHudVisible != true &&
            !update.Events.Any(e => e.Kind == "PlayPreview")) return;
        GameDateUtc ??= DateOnly.FromDateTime(at.UtcDateTime);
        _startedAt ??= at.ToUniversalTime();
        _state = update.After;
        // Copy the selected list once. Later library edits or selection changes must
        // never rewrite the deck that was used for this match.
        if (_selectedUserReference is null && _state.User.StartingDeckReference is { } selected)
            _selectedUserReference = Reference(selected);
        _revision++;
        // Numeric facts are retained as last observations, never silently called final scores.
        var round = _state.Round?.Value ?? 0;
        if (round is >= 1 and <= 3 && _state.Phase == GamePhase.Playing &&
            (!_rounds.TryGetValue(round, out var recorded) || !recorded.FinalConfirmed))
            _rounds[round] = new(round, _state.User.Score?.Value, _state.Opponent.Score?.Value, false);
        if (screen.PostMatchRoundScores is { Length: >= 2 and <= 3 } finalScores)
        {
            foreach (var score in finalScores)
                _rounds[score.Round] = new(score.Round, score.UserScore, score.OpponentScore, true);
            // The final table proves how many rounds were played, even if a
            // transition was mistaken for a third round during live capture.
            foreach (var extra in _rounds.Keys.Where(n => n > finalScores.Length).ToArray()) _rounds.Remove(extra);
        }
        foreach (var e in update.Events)
        {
            if (e.Side is { } side && e.CardId is { } card &&
                e.Kind is "PlayPreview" or "HistoricalAction" or "DeckRevealEvidence" or "BoardEvidence" or "BoardContact")
                _visibleIds.Add((side, card));
        }
        foreach (var card in _state.Cards) _visibleIds.Add((card.Location.Value.Controller, card.Card.Id));
        bool Seen(PlayerSide side, MatchCard card) => _visibleIds.Contains((side, card.CardId));
        _trackerHypotheses = opponentObservations.Where(c => !Seen(PlayerSide.Opponent, c))
            .Select(c => c with { Evidence = MatchCardEvidence.Inferred }).ToArray();
        _user = Player(_state.User, userObservations.Where(c => Seen(PlayerSide.User, c)).ToArray(),
            userObservations.Where(c => !Seen(PlayerSide.User, c)).Select(c => c with { Evidence = MatchCardEvidence.Inferred }).ToArray());
        if (_selectedUserReference is { } reference)
            _user = reference with { Observations = _user.Observations, Hypothesis = _user.Hypothesis };
        _opponent = Player(_state.Opponent, opponentObservations.Where(c => Seen(PlayerSide.Opponent, c)).ToArray(), CombinedHypotheses());
        if (screen.PostMatchMmr is { } mmr && !(_mmr is { Confirmed: true } && !mmr.Confirmed))
        {
            // Partial later reads cannot erase an already confirmed rating or its scope.
            if (_mmr is null || mmr.IsFactionRating == _mmr.IsFactionRating || mmr.IsFactionRating)
                _mmr = _mmr is { } previous && previous.IsFactionRating == mmr.IsFactionRating &&
                    (mmr.RatingAfter is null || previous.RatingAfter == mmr.RatingAfter)
                    ? mmr with { RatingAfter = mmr.RatingAfter ?? previous.RatingAfter,
                        Change = mmr.Change ?? previous.Change, SeasonPeak = mmr.SeasonPeak ?? previous.SeasonPeak }
                    : mmr;
            _resultObserved = true;
        }
        if (screen.PostMatchRank?.Rank is { } rank) { _rank = rank; _resultObserved = true; }
        var header = screen.ScreenHeader?.Trim().ToUpperInvariant();
        if (header is "VICTORY" or "DEFEAT" or "DRAW") { _result = header; _resultObserved = true; }
    }

    public void SetHypothesis(MatchCard[] cards)
    {
        if (cards.Any(c => c.Evidence is not MatchCardEvidence.Inferred and not MatchCardEvidence.ManualHypothesis))
            throw new ArgumentException("Hypotheses cannot contain observed/reference claims.", nameof(cards));
        if (_projectionHypotheses.SequenceEqual(cards)) return;
        _projectionHypotheses = cards.ToArray();
        _opponent = _opponent with { Hypothesis = CombinedHypotheses() }; _revision++;
    }

    private MatchCard[] CombinedHypotheses() => _projectionHypotheses.Concat(_trackerHypotheses)
        .GroupBy(c => (c.CardId, c.Evidence)).Select(g => g.MaxBy(c => c.Copies)!).ToArray();

    public CompactMatch? Snapshot(bool stopped = false)
    {
        if (GameDateUtc is not { } date || _state is null) return null;
        return new(_installationId, MatchId, date, _version, _state.RulesVersion,
            DeckPatchMetadata.Current(_startedAt ?? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)).Label,
            true, _revision, stopped, _resultObserved, _result, _mmr?.RatingAfter, _mmr?.Change,
            _mmr?.SeasonPeak, _mmr?.IsFactionRating ?? false, _rank, _user, _opponent,
            _rounds.Values.OrderBy(r => r.Number).ToArray(), [], StartedAtUtc: _startedAt,
            MmrUnconfirmed: _mmr is { Confirmed: false });
    }

    public static MatchCard[] Observations(IEnumerable<ObservedCard> cards) => cards
        .OrderBy(c => c.Card.Id, StringComparer.Ordinal).Select(c => new MatchCard(c.Card.Id,
            c.ObservedCopies, MatchCardEvidence.Observed, c.Provenance, Confidence(c.Confidence))).ToArray();
    public static byte Confidence(double? value) => value is { } v && double.IsFinite(v)
        ? (byte)Math.Round(Math.Clamp(v, 0, 1) * 254) : byte.MaxValue;

    private static MatchPlayer Reference(DeckDefinition deck) => new(deck.Faction, deck.Leader, deck.Stratagem?.Id,
        [], deck.Cards.Select(c => new MatchCard(c.Card.Id, c.Count, MatchCardEvidence.SelectedReference,
            CardProvenance.Unknown, byte.MaxValue)).ToArray(), []);

    private static MatchPlayer Player(PlayerGameState state, MatchCard[] observed, MatchCard[] hypothesis) =>
        new(state.Faction?.Value, state.StartingLeader?.Value, state.OpeningStratagemId?.Value,
            observed.ToArray(), state.StartingDeckReference?.Cards.Select(c => new MatchCard(c.Card.Id,
                c.Count, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, byte.MaxValue)).ToArray() ?? [],
            hypothesis.ToArray());
}
