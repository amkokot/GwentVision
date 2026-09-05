using GwentCompanion.Core.Domain;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Vision;

public sealed record VisionEvidenceEvent(DateTimeOffset ObservedAt, CardSighting Sighting, string Description,
    int? ResolvedDeckCopies = null);

/// <summary>
/// Separates play-preview episodes, historical evidence and board presence. Counts are
/// lower bounds: a replay or summon never proves another starting-deck copy.
/// </summary>
public sealed class MatchVisionLedger
{
    private readonly Dictionary<PlayerSide, PreviewEpisode> _previews = [];
    private readonly Dictionary<PlayerSide, (string Id, DateTimeOffset At)> _weakPreviews = [];
    private readonly Dictionary<PlayerSide, (string Id, DateTimeOffset At)> _recentWeakPreviews = [];
    private readonly HashSet<(PlayerSide, string)> _actionEvidence = [];
    private readonly HashSet<(PlayerSide, string)> _boardEvidence = [];
    private readonly Dictionary<(PlayerSide, string), BoardVote> _boardVotes = [];
    private readonly Dictionary<string, DateTimeOffset> _boardHoverTitles = [];
    private readonly Dictionary<string, HoverBoardVote> _spatialHoverTitles = [];
    private (DateTimeOffset At,CardSighting[] Cards)? _recentBoardCandidates;
    private readonly Dictionary<PlayerSide, DateTimeOffset> _shupePlayed = [];
    private readonly Dictionary<PlayerSide, PendingDeckReveal> _deckReveals = [];
    private PendingHandPlay? _playerHandHover;
    private PendingPlayChoice? _playChoiceHover;
    private (CardDefinition Card, DateTimeOffset At)? _recentChoiceSource;
    private (string Id, DateTimeOffset Until)? _suppressedChoiceHover;
    private (int Count, DateTimeOffset At)? _lastReliableUserHandCount;
    private (int Count, DateTimeOffset At)? _pendingChoiceSourceHandDrop;
    private DateTimeOffset _openingDealUntil;

    // A selected card can remain in Gwent's targeting/placement state for several
    // seconds before the hand counter settles. Keep the last exact hand identity
    // long enough to span that animation, but never across another hand hover.
    private static readonly TimeSpan PlayerCommitWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecentHandCountWindow = TimeSpan.FromSeconds(20);

    public IReadOnlyList<VisionEvidenceEvent> Observe(DateTimeOffset at, GwentVisualObservation screen,
        IReadOnlyList<CardSighting> sightings, bool boardWasScanned = true, CardDefinition? confirmedHover = null,
        bool artworkWasScanned = true, bool hoverInPlayerHand = false, bool? pointerInPlayerHand = null)
    {
        // The live OS pointer is authoritative when available. Legacy/static inputs
        // can still use tooltip geometry, but reject a geometric hand classification
        // when the same identity is visibly/recently on the player's board. Also do
        // not manufacture a second gold play from a later board hover.
        var recentMatchingBoard = confirmedHover is not null &&
            (sightings.Any(s => s.Side == PlayerSide.User && s.Source == CardSightSource.Board && s.Card.Id == confirmedHover.Id) ||
             _recentBoardCandidates is { } recent && at >= recent.At && at - recent.At <= TimeSpan.FromSeconds(5) &&
             recent.Cards.Any(s => s.Side == PlayerSide.User && s.Source == CardSightSource.Board && s.Card.Id == confirmedHover.Id));
        var pendingChoiceDrop = _pendingChoiceSourceHandDrop is { } sourceDrop && at >= sourceDrop.At &&
            at - sourceDrop.At <= PlayerCommitWindow;
        var effectiveHandHover = pointerInPlayerHand ?? (hoverInPlayerHand && !pendingChoiceDrop && !recentMatchingBoard &&
            !(confirmedHover?.IsGold == true && _actionEvidence.Contains((PlayerSide.User, confirmedHover.Id))));
        if (_pendingChoiceSourceHandDrop is { } settledDrop && screen.UserHandCount is { } settledCount &&
            settledCount <= settledDrop.Count - 1)
            _pendingChoiceSourceHandDrop = null;
        if (screen.UserHandCount == 0 && screen.UserDeckCount is >= 25 && screen.UserScore == 0 && screen.OpponentScore == 0)
            _openingDealUntil = at.AddSeconds(8);
        if (screen.UnresolvedHandSelection) _playerHandHover = null;
        // Retire a changed selection before evaluating a simultaneous hand drop.
        // Choice-card tooltips can overlap the geometric hand band. They are
        // candidates inside an overlay, never evidence that this identity was
        // selected from the player's actual hand.
        if (!screen.IsCardSelectionOverlay && effectiveHandHover && confirmedHover is not null &&
            _playerHandHover?.Card.Id != confirmedHover.Id)
            _playerHandHover = new(confirmedHover, at, at, screen.UserHandCount ?? RecentHandCount(at));
        if (sightings.Any(s => s.Side == PlayerSide.User && s.Source == CardSightSource.PlayPreview && !s.NeedsTemporalConfirmation))
            _playerHandHover = null;
        if (screen.IsCardSelectionOverlay)
        {
            _weakPreviews.Clear(); _recentWeakPreviews.Clear();
            // Hidden artwork is not verified absence. Nested Create/target menus
            // can cover a still-lingering source preview for many seconds.
            foreach (var episode in _previews.Values) episode.MissingSince = null;
            // Exact tooltip OCR is knowledge, not normally a play. A hand tooltip
            // followed promptly by Gwent's generic PICK A CARD overlay is different:
            // the transition corroborates that a choice-producing card was committed,
            // even when the enlarged play animation fell between sampled frames.
            // Deck/graveyard browsing and stale/unrelated hovers are deliberately
            // excluded so this cannot manufacture routine play events.
            var pick = (screen.ScreenHeader ?? "").Contains("PICK", StringComparison.OrdinalIgnoreCase) &&
                (screen.ScreenHeader ?? "").Contains("CARD", StringComparison.OrdinalIgnoreCase);
            // Create/tutor/graveyard choices often skip the ordinary enlarged play
            // animation. Remember only an exact title which repeats while the
            // explicit PLAY choice is open. Resolution still requires the same
            // selected identity to remain visible as the menu closes; browsing,
            // changing the highlighted option, and cancelling therefore abstain.
            if (pick && (screen.ScreenHeader ?? "").Contains("PLAY", StringComparison.OrdinalIgnoreCase) &&
                confirmedHover is not null)
            {
                _playChoiceHover = _playChoiceHover is { } choice && choice.Card.Id == confirmedHover.Id &&
                        at > choice.LastSeen && at - choice.LastSeen <= TimeSpan.FromSeconds(2)
                    ? choice with { LastSeen = at, Votes = Math.Min(3, choice.Votes + 1) }
                    : new(confirmedHover, at, at, 1,
                        _recentChoiceSource is { } source && at - source.At <= TimeSpan.FromSeconds(5) &&
                        (!DeckPlayResolutionTracker.IsGenericDeckPlaySource(source.Card) ||
                         DeckPlayResolutionTracker.CanSelectFromDeck(source.Card, confirmedHover)) ? source.Card : null);
            }
            if (pick && _playerHandHover is { } hand && at > hand.LastSeen && at - hand.LastSeen <= TimeSpan.FromSeconds(15) &&
                OpensCardChoice(hand.Card) && ChoiceMatches(hand.Card, screen.ScreenHeader) &&
                (!_previews.TryGetValue(PlayerSide.User, out var existing) || existing.CardId != hand.Card.Id))
            {
                _recentChoiceSource = (hand.Card, at);
                var sighting = new CardSighting(hand.Card, PlayerSide.User, CardSightSource.PlayPreview,
                    new(.815, .413, .918, .665), 0, 1,
                    "Exact player-hand tooltip followed by the PICK A CARD selection overlay.");
                _previews[PlayerSide.User] = new PreviewEpisode(hand.Card.Id);
                _actionEvidence.Add((PlayerSide.User, hand.Card.Id));
                if (hand.InitialHandCount is { } sourceHandCount)
                    _pendingChoiceSourceHandDrop = (sourceHandCount, at);
                _playerHandHover = null;
                return [new(at, sighting,
                    "Exact player-hand tooltip was followed by its card-choice overlay; inferred committed play after the animation was missed.")];
            }
            return [];
        }
        if (_playChoiceHover is { Votes: >= 2 } selected && confirmedHover?.Id == selected.Card.Id &&
            at > selected.LastSeen && at - selected.LastSeen <= TimeSpan.FromSeconds(1.5) && screen.HasCardTooltip)
        {
            // A deck tutor can close while the tooltip for a merely highlighted
            // alternative still lingers over an existing board card. Its dedicated
            // resolver requires an independent deck decrement; do not manufacture
            // that target from the stale title alone.
            if (selected.Source is not null && DeckPlayResolutionTracker.IsGenericDeckPlaySource(selected.Source))
                _suppressedChoiceHover = (selected.Card.Id, at.AddSeconds(3));
            else
            {
                var sighting = new CardSighting(selected.Card, PlayerSide.User, CardSightSource.PlayPreview,
                    new(.78, .20, .98, .50), 0, 1,
                    "Repeated exact title in a resolved PICK CARD TO PLAY selection.");
                _previews[PlayerSide.User] = new PreviewEpisode(selected.Card.Id);
                _actionEvidence.Add((PlayerSide.User, selected.Card.Id));
                _playChoiceHover = null;
                _playerHandHover = null;
                return [new(at, sighting,
                    "The exact highlighted title repeated in PICK CARD TO PLAY and remained selected as the menu closed; recovered the nested played card without assigning starting-deck origin.")];
            }
        }
        _playChoiceHover = null;
        if (_recentChoiceSource is { } oldSource && at - oldSource.At > TimeSpan.FromSeconds(8)) _recentChoiceSource = null;
        if (_suppressedChoiceHover is { } expiredChoice && at > expiredChoice.Until) _suppressedChoiceHover = null;
        if (_playerHandHover is { } stale && at - stale.LastSeen > PlayerCommitWindow) _playerHandHover = null;
        // The first card can be dragged while GWENT's round-start banner is still
        // fading. Its selected tooltip then sits over the empty board rather than
        // in the bottom hand band. On a verified 0-0 empty board, an exact title
        // absent from all board sightings is placement evidence; a later one-card
        // hand decrement remains mandatory before this can become a play.
        if (_playerHandHover is null && confirmedHover is not null && !effectiveHandHover && boardWasScanned &&
            screen.MatchHudVisible == true && screen.UserScore == 0 && screen.OpponentScore == 0 &&
            string.IsNullOrWhiteSpace(screen.ScreenHeader) &&
            !sightings.Any(sight => sight.Source == CardSightSource.Board) &&
            (screen.UserHandCount ?? RecentHandCount(at)) is { } placementHand)
            _playerHandHover = new(confirmedHover, at, at, placementHand, at);
        foreach (var side in _recentWeakPreviews.Where(item => at - item.Value.At > TimeSpan.FromSeconds(4)).Select(item => item.Key).ToArray())
            _recentWeakPreviews.Remove(side);
        var events = new List<VisionEvidenceEvent>();
        // Player-side enlarged play animations are less reliable than opponent previews.
        // A currently/recently hovered exact hand identity plus a corroborated one-card
        // hand decrement is strong public-pixel evidence that this was the committed card.
        // Keep the window tight so browsing a hand card cannot implicate a later play.
        if (_playerHandHover is { InitialHandCount: { } before } pending && screen.UserHandCount == before - 1 &&
            at > pending.LastSeen && at - pending.LastSeen <= PlayerCommitWindow && screen.MatchHudVisible == true &&
            string.IsNullOrWhiteSpace(screen.ScreenHeader))
        {
            var sighting = new CardSighting(pending.Card, PlayerSide.User, CardSightSource.PlayPreview,
                new(.815, .413, .918, .665), 0, 1,
                "Exact player-hand tooltip followed by a corroborated one-card hand decrement.");
            _previews[PlayerSide.User] = new PreviewEpisode(pending.Card.Id);
            _actionEvidence.Add((PlayerSide.User, pending.Card.Id));
            events.Add(new(at, sighting,
                pending.TargetedAt is null
                    ? "Exact player-hand tooltip was followed by a one-card hand decrement inside the bounded selection window; inferred the missed player play animation."
                    : "Exact player-hand tooltip was followed by board targeting and a one-card hand decrement; inferred the missed player play animation."));
            _playerHandHover = null;
        }
        if (screen.UserHandCount is { } reliableHandCount)
            _lastReliableUserHandCount = (reliableHandCount, at);
        if (confirmedHover is not null && effectiveHandHover)
        {
            if (_playerHandHover is { } existingHover && existingHover.Card.Id == confirmedHover.Id)
                _playerHandHover = existingHover with { LastSeen = at, InitialHandCount = existingHover.InitialHandCount ?? RecentHandCount(at) };
            else _playerHandHover = new(confirmedHover, at, at, RecentHandCount(at));
        }
        else if (confirmedHover is not null && _playerHandHover is { } targeting && RequiresBoardTarget(targeting.Card) &&
            at > targeting.LastSeen && at - targeting.LastSeen <= TimeSpan.FromSeconds(3))
            _playerHandHover = targeting with { TargetedAt = at };
        if (confirmedHover is not null && !effectiveHandHover) _boardHoverTitles[confirmedHover.Id] = at;
        if (confirmedHover is not null && !effectiveHandHover && screen.TooltipRegion is {} tooltip)
        {
            var count=_spatialHoverTitles.TryGetValue(confirmedHover.Id,out var priorTitle) && at>priorTitle.At &&
                at-priorTitle.At<=TimeSpan.FromSeconds(3) && Math.Abs(tooltip.Left-priorTitle.Region.Left)<.04 &&
                Math.Abs(tooltip.Top-priorTitle.Region.Top)<.08 ? Math.Min(2,priorTitle.Count+1) : 1;
            _spatialHoverTitles[confirmedHover.Id]=new(at,tooltip,count);
        }
        foreach(var id in _spatialHoverTitles.Where(item=>at-item.Value.At>TimeSpan.FromSeconds(10)).Select(item=>item.Key).ToArray())
            _spatialHoverTitles.Remove(id);
        foreach (var expired in _boardHoverTitles.Where(item => at - item.Value > TimeSpan.FromSeconds(10)).Select(item => item.Key).ToArray())
            _boardHoverTitles.Remove(expired);
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var preview = sightings.Where(item => at >= _openingDealUntil && screen.MatchHudVisible != false && item.Side == side &&
                    item.Source is CardSightSource.PlayPreview or CardSightSource.DeckRevealCandidate)
                .OrderBy(item => item.Distance).FirstOrDefault();
            if (side == PlayerSide.User && preview is not null &&
                _suppressedChoiceHover is { } suppressed && suppressed.Id == preview.Card.Id && at <= suppressed.Until)
                preview = null;
            if (preview is null)
            {
                // Intermittent empty artwork/text ticks are not contrary identity
                // evidence. Keep only a short vote; overlays/different cards reset it.
                if (_weakPreviews.TryGetValue(side, out var vote) && at - vote.At > TimeSpan.FromSeconds(2))
                    _weakPreviews.Remove(side);
                // A text-only tick cannot prove that a previously recognized artwork preview disappeared.
                // Expiring on these ticks split a single tutor animation into duplicate play events.
                if (!artworkWasScanned) continue;
                if (_previews.TryGetValue(side, out var missing))
                {
                    missing.MissingSince ??= at;
                    if (at - missing.MissingSince >= TimeSpan.FromSeconds(2)) _previews.Remove(side);
                }
                continue;
            }
            var revealCandidate = _deckReveals.TryGetValue(side, out var pendingReveal) && at > pendingReveal.At &&
                at - pendingReveal.At <= TimeSpan.FromSeconds(12) && pendingReveal.SourceId != preview.Card.Id &&
                pendingReveal.Matches(preview.Card);
            // During the enlarged-card transition, the same physical card can move
            // from the ordinary preview crop into the projected/reveal crop between
            // samples. Treat two matching, side-consistent lanes as temporal
            // corroboration only when the second match is still strong and no
            // printed deck-reveal route owns it. This recovers brief plays such as
            // Quarixis without turning a standalone reveal candidate into a play.
            var mixedLaneCorroborated = preview.Source==CardSightSource.DeckRevealCandidate && !revealCandidate &&
                _weakPreviews.TryGetValue(side,out var mixedWeak) && mixedWeak.Id==preview.Card.Id &&
                at>mixedWeak.At && at-mixedWeak.At<=TimeSpan.FromSeconds(2) &&
                preview.Distance<=.42 && preview.Margin>=.70;
            // Low-feature candidates are quarantined unless printed effect context
            // independently says that a card from this deck should be on this lane.
            if (preview.Source == CardSightSource.DeckRevealCandidate && !revealCandidate && !mixedLaneCorroborated) continue;
            if(mixedLaneCorroborated)
            {
                preview=preview with { Source=CardSightSource.PlayPreview, NeedsTemporalConfirmation=false,
                    Evidence=(preview.Evidence??"")+"; same identity immediately preceded this in the ordinary play-preview lane" };
                _weakPreviews.Remove(side); _recentWeakPreviews.Remove(side);
            }
            if (_previews.TryGetValue(side, out var episode) && episode.CardId == preview.Card.Id)
            {
                episode.MissingSince = null;
                continue;
            }
            // DeckRevealCandidate already requires a small projected-card index,
            // four spatially spread correspondences in the enlarged lane, and this
            // pending printed effect. Those are independent gates; the flip often
            // presents only one usable frame, so a second image is not required.
            if (preview.NeedsTemporalConfirmation && preview.Source != CardSightSource.DeckRevealCandidate)
            {
                var corroborated = _weakPreviews.TryGetValue(side, out var weak) && weak.Id == preview.Card.Id &&
                    at > weak.At && at - weak.At <= TimeSpan.FromSeconds(2);
                _weakPreviews[side] = (preview.Card.Id, at);
                _recentWeakPreviews[side] = (preview.Card.Id, at);
                if (!corroborated) continue;
            }
            else { _weakPreviews.Remove(side); _recentWeakPreviews.Remove(side); }
            _previews[side] = new PreviewEpisode(preview.Card.Id);
            if (revealCandidate && pendingReveal is { } reveal)
            {
                _deckReveals.Remove(side);
                var revealed = preview with { Side = reveal.TargetSide, Source = CardSightSource.DeckReveal };
                events.Add(new(at, revealed,
                    $"Artwork shown by {reveal.SourceName}'s explicit deck-reveal animation; deck identity evidence only, not another play or hand commitment."));
                continue;
            }
            if (preview.Card.Name == "Shupe's Day Off") _shupePlayed[side] = at;
            _actionEvidence.Add((side, preview.Card.Id));
            if (side == PlayerSide.User)
            {
                _playerHandHover = null;
                if (OpensCardChoice(preview.Card)) _recentChoiceSource = (preview.Card, at);
            }
            events.Add(new VisionEvidenceEvent(at, preview, "Recognized in the side-specific play preview; one visible episode, not a new deck-copy claim."));
            if (DeckRevealRoute(preview.Card, side, at) is { } route) _deckReveals[side] = route;
        }

        foreach (var sighting in sightings.Where(item => item.Source == CardSightSource.History))
        {
            if (_actionEvidence.Add((sighting.Side, sighting.Card.Id)))
                events.Add(new VisionEvidenceEvent(at, sighting, "Recovered from Move History; exact play time and number of starting-deck copies are unknown."));
        }
        if (boardWasScanned)
            _recentBoardCandidates=(at,sightings.Where(s=>s.Source==CardSightSource.Board).Take(128).ToArray());
        // Corroboration works in either order: a board scan can precede the second
        // exact title frame. Text-only ticks never count as another artwork vote.
        if (_recentBoardCandidates is {} recentBoard && at-recentBoard.At<=TimeSpan.FromSeconds(3) && screen.MatchHudVisible==true)
        foreach(var candidate in recentBoard.Cards)
        {
            var candidateKey=(candidate.Side,candidate.Card.Id);
            if (_actionEvidence.Contains(candidateKey) || _boardEvidence.Contains(candidateKey)) continue;
            if (_spatialHoverTitles.TryGetValue(candidate.Card.Id,out var title) && title.Count>=2 &&
                at>=title.At && at-title.At<=TimeSpan.FromSeconds(3) && candidate.Distance<=.36 && candidate.Margin>=.50 &&
                AdjacentToTooltip(candidate,title.Region) && recentBoard.Cards.Where(s=>AdjacentToTooltip(s,title.Region))
                    .Select(s=>s.Card.Id).Distinct().Count()==1)
            {
                _boardEvidence.Add(candidateKey);
                events.Add(new(at,candidate,"Repeated exact tooltip title spatially corroborates adjacent recent board artwork; presence/controller only, not a played or original-copy claim."));
            }
        }
        if (!boardWasScanned) return events;
        foreach (var sighting in sightings.Where(item => item.Source == CardSightSource.Board)
                     .GroupBy(item => (item.Side, item.Card.Id)).Select(group => group.OrderBy(item => item.Distance).First()))
        {
            var key = (sighting.Side, sighting.Card.Id);
            if (_actionEvidence.Contains(key) || _boardEvidence.Contains(key)) continue;
            // A short play preview and the same artwork arriving on the same side of
            // the board are independent spatial observations. This recovers animations
            // that occur for only one sampled frame without accepting either weak image
            // by itself (for example Brewess in the held-out 14:46:38 sequence).
            if (_recentWeakPreviews.TryGetValue(sighting.Side, out var recentPreview) &&
                recentPreview.Id == sighting.Card.Id && at > recentPreview.At &&
                at - recentPreview.At <= TimeSpan.FromSeconds(4) && sighting.Distance <= .22 &&
                screen.MatchHudVisible != false)
            {
                _recentWeakPreviews.Remove(sighting.Side); _weakPreviews.Remove(sighting.Side);
                _actionEvidence.Add(key); _boardEvidence.Add(key);
                events.Add(new(at, sighting,
                    "A short side-specific play preview was corroborated by the same artwork immediately arriving on that side of the board."));
                continue;
            }
            if (!sighting.Card.CanBeInStartingDeck && sighting.Card.Id.StartsWith("visual-family:", StringComparison.Ordinal) &&
                sighting.Card.Name.StartsWith("Shupe:", StringComparison.Ordinal) && sighting.Distance <= .12 &&
                screen.MatchHudVisible == true && _shupePlayed.TryGetValue(sighting.Side, out var createdAt) &&
                at > createdAt && at - createdAt <= TimeSpan.FromSeconds(20))
            {
                _boardEvidence.Add(key);
                events.Add(new(at, sighting, "Shared-art Shupe form corroborated by this side's recent Shupe's Day Off play. Created card; selected ability remains unknown."));
                continue;
            }
            if (_boardHoverTitles.TryGetValue(sighting.Card.Id, out var titleAt) && at >= titleAt &&
                at - titleAt <= TimeSpan.FromSeconds(10) && sighting.Distance <= .16 && screen.MatchHudVisible != false)
            {
                _boardEvidence.Add(key);
                events.Add(new(at, sighting, "Board artwork corroborated by an exact tooltip title in the recent rolling buffer; controller only, arrival route unresolved."));
                continue;
            }
            if (!_boardVotes.TryGetValue(key, out var vote) || at - vote.LastSeen > TimeSpan.FromSeconds(15))
            {
                _boardVotes[key] = new BoardVote(at, 1);
                continue;
            }
            if (at <= vote.LastSeen) continue;
            _boardVotes[key] = new BoardVote(at, vote.Count + 1);
            _boardEvidence.Add(key);
            events.Add(new VisionEvidenceEvent(at, sighting,
                "Artwork seen on this side of the board in multiple frames. Controller only: play, summon, transform, theft or Disloyal origin is unresolved."));
        }
        return events;
    }

    public void Reset()
    {
        _previews.Clear(); _weakPreviews.Clear(); _recentWeakPreviews.Clear(); _actionEvidence.Clear(); _boardEvidence.Clear(); _boardVotes.Clear(); _boardHoverTitles.Clear(); _shupePlayed.Clear(); _deckReveals.Clear();
        _playerHandHover = null;
        _playChoiceHover = null;
        _recentChoiceSource = null;
        _suppressedChoiceHover = null;
        _lastReliableUserHandCount = null;
        _pendingChoiceSourceHandDrop = null;
        _openingDealUntil = default;
        _spatialHoverTitles.Clear();
        _recentBoardCandidates=null;
    }

    private int? RecentHandCount(DateTimeOffset at) =>
        _lastReliableUserHandCount is { } hand && at - hand.At <= RecentHandCountWindow ? hand.Count : null;

    private static bool OpensCardChoice(CardDefinition card)
    {
        var text = card.AbilityText ?? "";
        return text.Contains("Choose", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("from your deck", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("Play", StringComparison.OrdinalIgnoreCase) || text.Contains("Choose", StringComparison.OrdinalIgnoreCase)) ||
            text.Contains("graveyard", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("Consume", StringComparison.OrdinalIgnoreCase) || text.Contains("Play", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Summon", StringComparison.OrdinalIgnoreCase) || text.Contains("Banish", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ChoiceMatches(CardDefinition card, string? header)
    {
        if (header?.Contains("PLAY", StringComparison.OrdinalIgnoreCase) != true) return true;
        var text=card.AbilityText ?? "";
        return text.Contains("Play",StringComparison.OrdinalIgnoreCase) || text.Contains("Create",StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresBoardTarget(CardDefinition card)
    {
        var text = card.AbilityText ?? "";
        return new[] { "Damage", "Banish", "Destroy", "Boost", "Lock", "Move", "Poison", "Purify", "Seize" }
            .Any(verb => text.Contains(verb, StringComparison.OrdinalIgnoreCase)) &&
            (text.Contains("unit", StringComparison.OrdinalIgnoreCase) || text.Contains("artifact", StringComparison.OrdinalIgnoreCase));
    }

    private static PendingDeckReveal? DeckRevealRoute(CardDefinition source, PlayerSide sourceSide, DateTimeOffset at)
    {
        var text = source.AbilityText ?? "";
        var match = Regex.Match(text,
            @"\bReveal\s+(?:a|the)\s+(?:random\s+|top\s+)?(?<kind>unit|card)\s+(?:from|in)\s+(?<owner>your|your opponent's)\s+deck\b",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var target = match.Groups["owner"].Value.StartsWith("your opponent", StringComparison.OrdinalIgnoreCase)
            ? sourceSide == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User
            : sourceSide;
        var unitOnly = match.Groups["kind"].Value.Equals("unit", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"If it(?:'s| is) a unit[^.]*shuffle it back", RegexOptions.IgnoreCase);
        return new(source.Id, source.Name, sourceSide, target, at, unitOnly);
    }

    private sealed class PreviewEpisode(string cardId)
    {
        public string CardId { get; } = cardId;
        public DateTimeOffset? MissingSince { get; set; }
    }
    private sealed record BoardVote(DateTimeOffset LastSeen, int Count);
    private sealed record HoverBoardVote(DateTimeOffset At, NormalizedRegion Region, int Count);
    private static bool AdjacentToTooltip(CardSighting sight, NormalizedRegion tip)
    {
        var card=sight.Region;
        var center=(card.Top+card.Bottom)/2;
        if (sight.Side==PlayerSide.Opponent ? center>=.45 : center<=.45 || center>=.86) return false;
        var gap=Math.Min(Math.Abs(tip.Left-card.Right),Math.Abs(card.Left-tip.Right));
        return gap<=.06 && tip.Top>=card.Top-.04 && tip.Top<=card.Bottom+.12;
    }
    private sealed record PendingHandPlay(CardDefinition Card, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int? InitialHandCount,
        DateTimeOffset? TargetedAt = null);
    private sealed record PendingPlayChoice(CardDefinition Card, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Votes,
        CardDefinition? Source);
    private sealed record PendingDeckReveal(string SourceId, string SourceName, PlayerSide VisualSide,
        PlayerSide TargetSide, DateTimeOffset At, bool UnitOnly)
    {
        public bool Matches(CardDefinition card) => !UnitOnly || card.Kind == CardKind.Unit;
    }
}
