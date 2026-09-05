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
    private bool _hovering;
    private readonly Dictionary<(Domain.PlayerSide Side,string Id),int> _boardCopies = [];
    private readonly HashSet<string> _hoverRefreshes = [];
    private (Domain.PlayerSide Side, string Id, DateTimeOffset At)? _thinningEpisode;
    private int _thinningFollowups;

    /// <summary>
    /// A matching-copy summon is conditional, so its printed text alone cannot
    /// establish two starting copies. Request four bounded settled-board looks:
    /// early passes may still contain the enlarged preview or summon animation,
    /// while the copy floor still requires two independently settled frames.
    /// </summary>
    public void ObservePreview(DateTimeOffset at, IReadOnlyList<CardSighting> sightings)
    {
        var preview = sightings.FirstOrDefault(item => item.Source == CardSightSource.PlayPreview &&
            Inference.CompanionCardRules.ThinningPairs.Contains(item.Card.Id));
        if (preview is null) return;
        if (_thinningEpisode is { } prior && prior.Side == preview.Side && prior.Id == preview.Card.Id &&
            at >= prior.At && at - prior.At <= TimeSpan.FromSeconds(4)) return;
        _thinningEpisode = (preview.Side, preview.Card.Id, at);
        _thinningFollowups = 4;
        _refreshPending = true;
        _copyConfirmationPending = true;
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
        bool hovering = false, int? opponentHand = null, bool artworkBacklogged = false)
    {
        if (opponentHand is not null && _opponentHand is not null && opponentHand < _opponentHand) _refreshPending = true;
        _opponentHand = opponentHand ?? _opponentHand;
        if(userHand is not null && _userHand is not null && userHand<_userHand || hovering && !_hovering) _refreshPending=true;
        _userHand=userHand ?? _userHand; _hovering=hovering;
        if (userScore is not null && userScore != _userScore || opponentScore is not null && opponentScore != _opponentScore) _refreshPending = true;
        _userScore = userScore ?? _userScore; _opponentScore = opponentScore ?? _opponentScore;
        var thinningFollowup = _thinningFollowups > 0 && _thinningEpisode is { } thinning && at > thinning.At &&
            at - thinning.At <= TimeSpan.FromSeconds(7) && (_lastBoard is null || at - _lastBoard >= TimeSpan.FromSeconds(1));
        // Preview identity is urgent; a full-board scan is several times slower.
        // Under load preserve preview opportunities while still guaranteeing a
        // periodic board refresh for summons and physical-copy corroboration.
        if (artworkBacklogged && _lastBoard is not null && at - _lastBoard < TimeSpan.FromSeconds(6) &&
            !thinningFollowup && !(_copyConfirmationPending && _previewPasses >= 1 && at - _lastBoard >= TimeSpan.FromSeconds(1)))
        { _previewPasses++; return false; }
        if (_lastBoard is null || thinningFollowup || (_previewPasses >= 3 && at - _lastBoard >= TimeSpan.FromSeconds(3)) ||
            (_refreshPending && _previewPasses >= 1 && at - _lastBoard >= TimeSpan.FromSeconds(1)))
        {
            if (thinningFollowup) _thinningFollowups--;
            _lastBoard = at;
            _previewPasses = 0;
            _refreshPending = false;
            _copyConfirmationPending = false;
            return true;
        }
        _previewPasses++;
        return false;
    }
}
