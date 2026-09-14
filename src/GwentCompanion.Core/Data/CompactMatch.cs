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
    private const int UserReferenceMismatchThreshold = 3;
    private readonly Dictionary<int, MatchRound> _rounds = [];
    private readonly HashSet<(PlayerSide Side, string Card)> _visibleIds = [];
    private readonly HashSet<string> _selectedUserCardIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offReferenceUserCards = new(StringComparer.Ordinal);
    private MatchCard[] _trackerHypotheses = [];
    private MatchCard[] _projectionHypotheses = [];
    private MatchCard[] _userTrackerHypotheses = [];
    private MatchCard[] _userProjectionHypotheses = [];
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
    public bool UserReferenceRejected { get; private set; }

    public MatchAcquisition(string detectorVersion, Guid installationId, DeckDefinition? selectedUserDeck = null)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("Installation ID is required.", nameof(installationId));
        _version = detectorVersion; _installationId = installationId;
        if (selectedUserDeck is not null)
        {
            _selectedUserReference = Reference(selectedUserDeck);
            _selectedUserCardIds.UnionWith(selectedUserDeck.Cards.Select(card => card.Card.Id));
        }
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
        ReviewSelectedUserReference(userObservations.Where(card => Seen(PlayerSide.User, card)));
        _userTrackerHypotheses = userObservations.Where(c => !Seen(PlayerSide.User, c))
            .Select(c => c with { Evidence = MatchCardEvidence.Inferred }).ToArray();
        _user = Player(_state.User, userObservations.Where(c => Seen(PlayerSide.User, c)).ToArray(),
            CombinedUserHypotheses(),
            includeReference: !UserReferenceRejected);
        if (!UserReferenceRejected && _selectedUserReference is { } reference)
            _user = reference with { Observations = _user.Observations, Hypothesis = _user.Hypothesis };
        _opponent = Player(_state.Opponent, opponentObservations.Where(c => Seen(PlayerSide.Opponent, c)).ToArray(), CombinedHypotheses());
        RetainPostMatchRating(screen.PostMatchMmr);
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

    public void SetUserHypothesis(MatchCard[] cards)
    {
        if (cards.Any(c => c.Evidence is not MatchCardEvidence.Inferred and not MatchCardEvidence.ManualHypothesis))
            throw new ArgumentException("Hypotheses cannot contain observed/reference claims.", nameof(cards));
        if (_userProjectionHypotheses.SequenceEqual(cards)) return;
        _userProjectionHypotheses = cards.ToArray();
        _user = _user with { Hypothesis = CombinedUserHypotheses() }; _revision++;
    }

    /// <summary>
    /// Preserve the strongest valid rating read, including a one-frame fallback
    /// supplied when capture ends. Missing fields may be filled by a compatible
    /// read, but a weaker or conflicting read cannot replace stronger evidence.
    /// </summary>
    public void RetainPostMatchRating(GwentCompanion.Core.Vision.PostMatchMmr? reading)
    {
        if (reading is null || reading.RatingAfter is < 0 or > 10000 ||
            reading.Change is < -1000 or > 1000 || reading.SeasonPeak is < 0 or > 10000 ||
            reading.RatingAfter is null && reading.Change is null && reading.SeasonPeak is null) return;
        var previous = _mmr;
        if (previous is null)
        {
            _mmr = reading; _resultObserved = true; _revision++; return;
        }
        // A labelled faction rating is more useful than an unqualified aggregate.
        // Within the same scope, confirmation and then corroborating reads decide.
        var readingWins = reading.IsFactionRating != previous.IsFactionRating
            ? reading.IsFactionRating
            : reading.Confirmed != previous.Confirmed
                ? reading.Confirmed
                : reading.ReadCount != previous.ReadCount
                    ? reading.ReadCount > previous.ReadCount
                    : Completeness(reading) >= Completeness(previous);
        var stronger = readingWins ? reading : previous;
        var weaker = readingWins ? previous : reading;
        if (stronger.IsFactionRating == weaker.IsFactionRating &&
            (stronger.RatingAfter is null || weaker.RatingAfter is null || stronger.RatingAfter == weaker.RatingAfter))
            stronger = stronger with
            {
                RatingAfter = stronger.RatingAfter ?? weaker.RatingAfter,
                Change = stronger.Change ?? weaker.Change,
                SeasonPeak = stronger.SeasonPeak ?? weaker.SeasonPeak,
            };
        if (stronger == previous) return;
        _mmr = stronger; _resultObserved = true; _revision++;
    }

    private static int Completeness(GwentCompanion.Core.Vision.PostMatchMmr reading) =>
        (reading.RatingAfter.HasValue ? 1 : 0) + (reading.Change.HasValue ? 1 : 0) + (reading.SeasonPeak.HasValue ? 1 : 0);

    private MatchCard[] CombinedHypotheses() => _projectionHypotheses.Concat(_trackerHypotheses)
        .GroupBy(c => (c.CardId, c.Evidence)).Select(g => g.MaxBy(c => c.Copies)!).ToArray();

    private MatchCard[] CombinedUserHypotheses() => _userProjectionHypotheses.Concat(_userTrackerHypotheses)
        .GroupBy(c => (c.CardId, c.Evidence)).Select(g => g.MaxBy(c => c.Copies)!).ToArray();

    private void ReviewSelectedUserReference(IEnumerable<MatchCard> observations)
    {
        if (UserReferenceRejected || _selectedUserReference is null) return;
        foreach (var card in observations.Where(card =>
                     StartingDeckRules.CountsAgainstStartingDeck(card.Origin) &&
                     !_selectedUserCardIds.Contains(card.CardId)))
            _offReferenceUserCards.Add(card.CardId);
        // One substituted card or one uncertain generated card should not discard a
        // complete selected list. Three distinct, independently classified starting
        // cards outside that list establish a broad mismatch.
        if (_offReferenceUserCards.Count < UserReferenceMismatchThreshold) return;
        UserReferenceRejected = true;
        _selectedUserReference = null;
    }

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

    private static MatchPlayer Player(PlayerGameState state, MatchCard[] observed, MatchCard[] hypothesis,
        bool includeReference = true) =>
        new(state.Faction?.Value, state.StartingLeader?.Value, state.OpeningStratagemId?.Value,
            observed.ToArray(), (includeReference ? state.StartingDeckReference?.Cards : null)?.Select(c => new MatchCard(c.Card.Id,
                c.Count, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, byte.MaxValue)).ToArray() ?? [],
            hypothesis.ToArray());
}
