namespace GwentCompanion.Core.Vision;

/// <summary>A slow board pass must not immediately schedule another slow board pass.</summary>
public sealed class VisionScanSchedule
{
    private DateTimeOffset? _lastBoard;
    private int _previewPasses;
    private int? _userScore, _opponentScore;
    private bool _refreshPending;
    private bool _copyConfirmationPending;
    private int? _userHand;
    private int? _opponentHand;
    private int? _userDeck;
    private int? _opponentDeck;
    private bool _hovering;
    private readonly Dictionary<(Domain.PlayerSide Side,string Id),int> _boardCopies = [];
    private readonly HashSet<string> _hoverRefreshes = [];
    private (Domain.PlayerSide Side, string Id, DateTimeOffset At)? _thinningEpisode;
    private int _thinningFollowups;
    private (Domain.PlayerSide Side, string Id, DateTimeOffset At)? _previewEpisode;
    private DateTimeOffset? _summonFollowupStartedAt;
    private int _summonFollowups;

    /// <summary>
    /// A matching-copy summon is conditional, so its printed text alone cannot
    /// establish two starting copies. Request four bounded settled-board looks:
    /// early passes may still contain the enlarged preview or summon animation,
    /// while the copy floor still requires two independently settled frames.
    /// </summary>
    public void ObservePreview(DateTimeOffset at, IReadOnlyList<CardSighting> sightings)
    {
        var preview = sightings.FirstOrDefault(item => item.Source == CardSightSource.PlayPreview);
        if (preview is null) return;
        if (_previewEpisode is { } seen && seen.Side == preview.Side && seen.Id == preview.Card.Id &&
            at >= seen.At && at - seen.At <= TimeSpan.FromSeconds(4)) return;
        _previewEpisode = (preview.Side, preview.Card.Id, at);
        var ability = preview.Card.AbilityText ?? "";
        var deckSummon = System.Text.RegularExpressions.Regex.IsMatch(ability,
            @"\bSummon\b[\s\S]{0,180}\bfrom your deck\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (deckSummon && !Inference.CompanionCardRules.ThinningPairs.Contains(preview.Card.Id)) ArmSummonFollowups(at);
        if (!Inference.CompanionCardRules.ThinningPairs.Contains(preview.Card.Id)) return;
        _thinningEpisode = (preview.Side, preview.Card.Id, at);
        _thinningFollowups = 4;
        _refreshPending = true;
        _copyConfirmationPending = true;
    }

    private void ArmSummonFollowups(DateTimeOffset at)
    {
        _summonFollowupStartedAt = at; _summonFollowups = 4;
    }

    public void ObserveBoardHover(string? id)
    {
        // One bounded discovery refresh per identity, not a heavy scan per hover.
        if (id is not null && _hoverRefreshes.Count<256 && _hoverRefreshes.Add(id))
        { _refreshPending=true; _copyConfirmationPending=true; }
    }

    /// <summary>New identities/copy counts need a second look before a brief summon disappears.</summary>
    public void ObserveArtwork(IReadOnlyList<CardSighting> sightings, bool boardWasScanned)
    {
        if (!boardWasScanned) return;
        foreach(var group in sightings.Where(s=>s.Source==CardSightSource.Board && s.Distance<=.4).GroupBy(s=>(s.Side,s.Card.Id)))
        {
            var count=Math.Min(9,group.Count());
            if (count<=_boardCopies.GetValueOrDefault(group.Key) || _boardCopies.Count>=256) continue;
            _boardCopies[group.Key]=count;
            _refreshPending=true;
            _copyConfirmationPending=true;
        }
        // Uses the existing one-preview/one-second guard. Reacquisition, ongoing
        // animation and repeated frames cannot request endless heavy board passes.
    }

    public bool NextIncludesBoard(DateTimeOffset at, int? userScore = null, int? opponentScore = null, int? userHand = null,
        bool hovering = false, int? opponentHand = null, int? userDeck = null, int? opponentDeck = null,
        bool artworkBacklogged = false, bool deckDepartureSignal = false)
    {
        // Deck movement is the common observable shared by timers, tutors,
        // scenario chapters and ordinary summon clauses. It is more reliable than
        // pretending every preview is a "turn" (one action can expose several
        // generated/tutored previews), and it naturally covers future card rules.
        var deckDeparture = deckDepartureSignal || IsSmallDeckDeparture(_userDeck, userDeck) ||
                            IsSmallDeckDeparture(_opponentDeck, opponentDeck);
        if (deckDeparture)
        {
            _refreshPending = true;
            ArmSummonFollowups(at);
        }
        _userDeck = userDeck ?? _userDeck;
        _opponentDeck = opponentDeck ?? _opponentDeck;
        if (opponentHand is not null && _opponentHand is not null && opponentHand < _opponentHand) _refreshPending = true;
        _opponentHand = opponentHand ?? _opponentHand;
        if(userHand is not null && _userHand is not null && userHand<_userHand || hovering && !_hovering) _refreshPending=true;
        _userHand=userHand ?? _userHand; _hovering=hovering;
        if (userScore is not null && userScore != _userScore || opponentScore is not null && opponentScore != _opponentScore) _refreshPending = true;
        _userScore = userScore ?? _userScore; _opponentScore = opponentScore ?? _opponentScore;
        // A full board pass can take several seconds under live-game pressure. Keep
        // the fourth look available long enough to corroborate a late-settling pair,
        // while the fixed four-pass budget prevents ongoing scanning overhead.
        var thinningFollowup = _thinningFollowups > 0 && _thinningEpisode is { } thinning && at > thinning.At &&
            at - thinning.At <= TimeSpan.FromSeconds(15) && (_lastBoard is null || at - _lastBoard >= TimeSpan.FromSeconds(1));
        var summonFollowup = _summonFollowups > 0 && _summonFollowupStartedAt is { } summonAt && at > summonAt &&
            at - summonAt >= TimeSpan.FromSeconds(3) && at - summonAt <= TimeSpan.FromSeconds(15) &&
            (_lastBoard is null || at - _lastBoard >= TimeSpan.FromSeconds(3));
        // Preview identity is urgent; a full-board scan is several times slower.
        // Under load preserve preview opportunities while still guaranteeing a
        // periodic board refresh for summons and physical-copy corroboration.
        if (artworkBacklogged && _lastBoard is not null && at - _lastBoard < TimeSpan.FromSeconds(6) &&
            !thinningFollowup && !summonFollowup && !(_copyConfirmationPending && _previewPasses >= 1 && at - _lastBoard >= TimeSpan.FromSeconds(1)))
        { _previewPasses++; return false; }
        if (_lastBoard is null || thinningFollowup || summonFollowup || (_previewPasses >= 3 && at - _lastBoard >= TimeSpan.FromSeconds(3)) ||
            (_refreshPending && _previewPasses >= 1 && at - _lastBoard >= TimeSpan.FromSeconds(1)))
        {
            if (thinningFollowup) _thinningFollowups--;
            if (summonFollowup) _summonFollowups--;
            _lastBoard = at;
            _previewPasses = 0;
            _refreshPending = false;
            _copyConfirmationPending = false;
            return true;
        }
        _previewPasses++;
        return false;
    }

    private static bool IsSmallDeckDeparture(int? prior, int? current) =>
        prior is >= 0 and <= 25 && current is >= 0 and <= 25 && current < prior && prior - current <= 3;
}
