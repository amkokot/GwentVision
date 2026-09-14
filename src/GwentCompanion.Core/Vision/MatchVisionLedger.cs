using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Vision;

public sealed record VisionEvidenceEvent(DateTimeOffset ObservedAt, CardSighting Sighting, string Description,
    int? ResolvedDeckCopies = null, bool EstablishesDistinctDeckCopy = false);

/// <summary>
/// Separates play-preview episodes, historical evidence and board presence. Counts are
/// lower bounds: a replay or summon never proves another starting-deck copy.
/// </summary>
public sealed class MatchVisionLedger
{
    private readonly Dictionary<PlayerSide, PreviewEpisode> _previews = [];
    private readonly Dictionary<PlayerSide, (string Id, DateTimeOffset At)> _weakPreviews = [];
    private readonly Dictionary<PlayerSide, (string Id, DateTimeOffset At)> _recentWeakPreviews = [];
    private readonly Dictionary<PlayerSide, (CardSighting Sighting, DateTimeOffset At)> _quarantinedProjectedPreviews = [];
    private readonly HashSet<(PlayerSide, string)> _actionEvidence = [];
    private readonly HashSet<(PlayerSide, string)> _boardEvidence = [];
    private readonly Dictionary<(PlayerSide, string), BoardVote> _boardVotes = [];
    private readonly Dictionary<(PlayerSide, string), WeakAutomaticVote> _weakAutomaticVotes = [];
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
    private readonly Dictionary<PlayerSide, PileSnapshot> _pileSnapshots = [];
    private readonly Dictionary<PlayerSide, List<DeckDeparture>> _unmatchedDeckDepartures = [];
    private readonly Dictionary<PlayerSide, (int Count, DateTimeOffset At)> _recentHandCounts = [];
    private readonly Dictionary<PlayerSide, DateTimeOffset> _recentEist = [];
    private readonly Dictionary<(PlayerSide Side, string TargetId), (DateTimeOffset At, string Trigger)> _recentTriggeredPairs = [];
    private readonly Dictionary<PlayerSide, (CardDefinition Source, DateTimeOffset StartedAt)> _recurringDeckSummons = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _recentBroadDeckSummons = [];
    private readonly Dictionary<PlayerSide, PendingSourceScopedSummon> _pendingSourceScopedSummons = [];
    private readonly List<VisionEvidenceEvent> _deferredPileEvents = [];
    private readonly HashSet<PlayerSide> _broadDeployConsumed = [];
    private readonly HashSet<(PlayerSide Side, string SourceId, DateTimeOffset StartedAt, string Placement)> _sourcePlacementConsumed = [];
    private readonly Dictionary<PlayerSide, NamedMultiPlay> _namedMultiPlays = [];
    private readonly Dictionary<PlayerSide, (CardDefinition Source, int Stage)> _activeScenarios = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source, int Chapter)> _pendingScenarioDeckPlays = [];
    private PendingOpponentPreview? _pendingOpponentPreview;
    private DateTimeOffset _openingDealUntil;
    private int? _observedRound;

    // A selected card can remain in Gwent's targeting/placement state for several
    // seconds before the hand counter settles. Keep the last exact hand identity
    // long enough to span that animation, but never across another hand hover.
    private static readonly TimeSpan PlayerCommitWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecentHandCountWindow = TimeSpan.FromSeconds(20);

    public IReadOnlyList<VisionEvidenceEvent> Observe(DateTimeOffset at, GwentVisualObservation screen,
        IReadOnlyList<CardSighting> sightings, bool boardWasScanned = true, CardDefinition? confirmedHover = null,
        bool artworkWasScanned = true, bool hoverInPlayerHand = false, bool? pointerInPlayerHand = null)
    {
        ObserveRoundBoundary(screen);
        ObservePileConservation(at, screen);
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
            _playerHandHover = CanInferPlayerHandCommit(confirmedHover)
                ? new(confirmedHover, at, at, screen.UserHandCount ?? RecentHandCount(at))
                : null;
        if (sightings.Any(s => s.Side == PlayerSide.User && s.Source == CardSightSource.PlayPreview && !s.NeedsTemporalConfirmation))
            _playerHandHover = null;
        if (screen.IsCardSelectionOverlay)
        {
            _weakPreviews.Clear(); _recentWeakPreviews.Clear(); _quarantinedProjectedPreviews.Clear();
            _pendingOpponentPreview = null;
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
        if (_playerHandHover is null && confirmedHover is not null && CanInferPlayerHandCommit(confirmedHover) &&
            !effectiveHandHover && boardWasScanned &&
            screen.MatchHudVisible == true && screen.UserScore == 0 && screen.OpponentScore == 0 &&
            string.IsNullOrWhiteSpace(screen.ScreenHeader) &&
            !sightings.Any(sight => sight.Source == CardSightSource.Board) &&
            (screen.UserHandCount ?? RecentHandCount(at)) is { } placementHand)
            _playerHandHover = new(confirmedHover, at, at, placementHand, at);
        foreach (var side in _recentWeakPreviews.Where(item => at - item.Value.At > TimeSpan.FromSeconds(4)).Select(item => item.Key).ToArray())
            _recentWeakPreviews.Remove(side);
        foreach (var side in _quarantinedProjectedPreviews.Where(item => at - item.Value.At > TimeSpan.FromSeconds(2)).Select(item => item.Key).ToArray())
            _quarantinedProjectedPreviews.Remove(side);
        var events = new List<VisionEvidenceEvent>();
        if (_deferredPileEvents.Count > 0)
        {
            events.AddRange(_deferredPileEvents);
            _deferredPileEvents.Clear();
        }
        if (_pendingOpponentPreview is { } pendingTitle &&
            sightings.FirstOrDefault(item => item.Side == PlayerSide.Opponent &&
                item.Card.Id == pendingTitle.Sighting.Card.Id &&
                item.Source == CardSightSource.PlayPreview) is { } pendingArtwork)
        {
            // A title-only fallback is useful when popup art is wholly obscured.
            // Once the same popup does yield artwork, however, that evidence must
            // pass the ordinary identity margin; exact text must not launder an
            // explicitly ambiguous visual match into a paid play.
            _pendingOpponentPreview = pendingArtwork.Distance <= .30 && pendingArtwork.Margin >= .70
                ? pendingTitle with { Sighting = pendingArtwork }
                : null;
        }
        // A difficult enlarged card can expose enough artwork for only one sampled
        // tick while its exact GWENT-font title remains readable for several ticks.
        // Do not lower the global artwork threshold: hold that joined text/art
        // candidate until the opponent HUD independently pays exactly one card.
        if (_pendingOpponentPreview is { } differentPending && confirmedHover is not null &&
            confirmedHover.Id != differentPending.Sighting.Card.Id)
            _pendingOpponentPreview = null;
        if (_pendingOpponentPreview is { } opponentPending)
        {
            var current = screen.OpponentHandCount;
            var timely = at > opponentPending.SeenAt && at - opponentPending.SeenAt <= TimeSpan.FromSeconds(6);
            if (timely && current == opponentPending.HandBefore - 1 && screen.View == GwentViewKind.Board &&
                screen.MatchHudVisible == true && !screen.IsCardSelectionOverlay && string.IsNullOrWhiteSpace(screen.ScreenHeader))
            {
                var confirmed = opponentPending.Sighting with
                {
                    NeedsTemporalConfirmation = false,
                    Evidence = (opponentPending.Sighting.Evidence ?? "") +
                        "; repeated exact popup title and subsequent one-card opponent hand decrement"
                };
                _previews[PlayerSide.Opponent] = new PreviewEpisode(confirmed.Card.Id);
                _actionEvidence.Add((PlayerSide.Opponent, confirmed.Card.Id));
                events.Add(new(at, confirmed,
                    "A single difficult opponent preview was corroborated by its repeated exact popup title and a subsequent one-card opponent hand decrement."));
                _pendingOpponentPreview = null;
            }
            else if (!timely || current is { } changed && changed != opponentPending.HandBefore)
                _pendingOpponentPreview = null;
        }
        // Player-side enlarged play animations are less reliable than opponent previews.
        // A currently/recently hovered exact hand identity plus a corroborated one-card
        // hand decrement is strong public-pixel evidence that this was the committed card.
        // Keep the window tight so browsing a hand card cannot implicate a later play.
        var targetSelectionStillOpen = _playerHandHover is { } selecting && RequiresBoardTarget(selecting.Card) &&
            confirmedHover is not null && confirmedHover.Id != selecting.Card.Id && !effectiveHandHover && screen.HasCardTooltip;
        if (!targetSelectionStillOpen && _playerHandHover is { InitialHandCount: { } before } pending && screen.UserHandCount == before - 1 &&
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
        var priorOpponentHand = _recentHandCounts.TryGetValue(PlayerSide.Opponent, out var priorOpponent)
            ? priorOpponent : ((int Count, DateTimeOffset At)?)null;
        if (screen.UserHandCount is { } reliableHandCount)
            _lastReliableUserHandCount = (reliableHandCount, at);
        if (screen.View == GwentViewKind.Board && !screen.IsCardSelectionOverlay && screen.MatchHudVisible == true)
        {
            if (screen.UserHandCount is >= 0 and <= 10) _recentHandCounts[PlayerSide.User] = (screen.UserHandCount.Value, at);
            if (screen.OpponentHandCount is >= 0 and <= 10) _recentHandCounts[PlayerSide.Opponent] = (screen.OpponentHandCount.Value, at);
        }
        if (confirmedHover is not null && effectiveHandHover && CanInferPlayerHandCommit(confirmedHover))
        {
            if (_playerHandHover is { } existingHover && existingHover.Card.Id == confirmedHover.Id)
                _playerHandHover = existingHover with { LastSeen = at, InitialHandCount = existingHover.InitialHandCount ?? RecentHandCount(at) };
            else _playerHandHover = new(confirmedHover, at, at, RecentHandCount(at));
        }
        else if (confirmedHover is not null && effectiveHandHover)
            _playerHandHover = null;
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
            // Some opponent plays never expose enough unobscured art for even a
            // quarantined preview candidate, while their exact GWENT-font title is
            // readable beside the newly placed board card. Hold the repeated title
            // as an identity candidate, but require the opponent hand HUD to pay one
            // card on a later settled sample before emitting a play. Board browsing,
            // spawned cards and leader-created cards therefore remain non-counting.
            if (count >= 2 && IsOpponentBoardTooltip(tooltip) &&
                screen.View == GwentViewKind.Board && screen.MatchHudVisible == true &&
                !screen.IsCardSelectionOverlay && string.IsNullOrWhiteSpace(screen.ScreenHeader) &&
                confirmedHover.CanBeInStartingDeck &&
                !_actionEvidence.Contains((PlayerSide.Opponent, confirmedHover.Id)) &&
                _pendingOpponentPreview is null &&
                !sightings.Any(item => item.Side == PlayerSide.Opponent && item.Card.Id == confirmedHover.Id &&
                    item.Source is CardSightSource.PlayPreview or CardSightSource.DeckRevealCandidate) &&
                _recentHandCounts.TryGetValue(PlayerSide.Opponent, out var titleHand) &&
                at >= titleHand.At && at - titleHand.At <= TimeSpan.FromSeconds(12) &&
                (screen.OpponentHandCount is null || screen.OpponentHandCount == titleHand.Count))
            {
                var titleOnly = new CardSighting(confirmedHover, PlayerSide.Opponent, CardSightSource.PlayPreview,
                    OpponentCardBesideTooltip(tooltip), 0, 1,
                    "Repeated exact opponent-board tooltip title; artwork unavailable and a subsequent one-card hand decrement is still required.",
                    NeedsTemporalConfirmation: true);
                _pendingOpponentPreview = new(titleOnly, at, titleHand.Count);
            }
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
            if (_namedMultiPlays.TryGetValue(side, out var activeNamedGap) && at >= activeNamedGap.At &&
                at - activeNamedGap.At <= TimeSpan.FromSeconds(20))
                foreach (var (targetId, presentation) in activeNamedGap.Presentations)
                    if (preview?.Card.Id != targetId) presentation.ObserveMissing(at);
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
                    missing.MissingArtworkScans++;
                    // Artwork work is intentionally sparse while a tooltip is open.
                    // Two independent full scans with no preview, separated by at
                    // least a second, are therefore enough to prove a real gap even
                    // when the first scan arrived late. This preserves separately
                    // animated same-name children, such as the two Slave Drivers
                    // played from hand by Battle Stations.
                    if (at - missing.MissingSince >= TimeSpan.FromSeconds(2) ||
                        missing.MissingArtworkScans >= 2 && at - missing.MissingSince >= TimeSpan.FromSeconds(1))
                        _previews.Remove(side);
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
            var reverseMixedLaneCorroborated = preview.Source==CardSightSource.PlayPreview && preview.NeedsTemporalConfirmation &&
                _quarantinedProjectedPreviews.TryGetValue(side,out var projected) && projected.Sighting.Card.Id==preview.Card.Id &&
                at>projected.At && at-projected.At<=TimeSpan.FromSeconds(2) &&
                projected.Sighting.Distance<=.42 && projected.Sighting.Margin>=.70 &&
                preview.Distance<=.38 && preview.Margin>=.70;
            // Low-feature candidates are quarantined unless printed effect context
            // independently says that a card from this deck should be on this lane.
            if (preview.Source == CardSightSource.DeckRevealCandidate && !revealCandidate && !mixedLaneCorroborated)
            {
                _quarantinedProjectedPreviews[side]=(preview,at);
                continue;
            }
            if(mixedLaneCorroborated || reverseMixedLaneCorroborated)
            {
                preview=preview with { Source=CardSightSource.PlayPreview, NeedsTemporalConfirmation=false,
                    Evidence=(preview.Evidence??"")+"; same identity appeared immediately before this in the other enlarged-card lane" };
                _weakPreviews.Remove(side); _recentWeakPreviews.Remove(side);
                _quarantinedProjectedPreviews.Remove(side);
            }
            if (_previews.TryGetValue(side, out var episode) && episode.CardId == preview.Card.Id)
            {
                episode.MissingSince = null;
                episode.MissingArtworkScans = 0;
                if (_namedMultiPlays.TryGetValue(side, out var named) && at >= named.At &&
                    at - named.At <= TimeSpan.FromSeconds(20) && named.TargetNames.Contains(preview.Card.Name) &&
                    named.Presentations.TryGetValue(preview.Card.Id, out var presentation) && !presentation.ResolvedPair)
                {
                    var firstPhaseWasUnframed = !presentation.SawFrameBoundary;
                    var separatedPresentation = presentation.ObserveSeen(at, preview);
                    var continuousSecondPhase = firstPhaseWasUnframed &&
                        at - presentation.FirstSeen >= TimeSpan.FromSeconds(1.2) &&
                        at - presentation.FirstSeen <= TimeSpan.FromSeconds(6) && IsExactFramedPreview(preview);
                    if (separatedPresentation || continuousSecondPhase)
                    {
                        presentation.ResolvedPair = true;
                        events.Add(new(at, preview,
                            separatedPresentation
                                ? $"{named.Source.Name}'s printed play-all-copies resolution showed {preview.Card.Name}, a sustained blank interval, then the same GWENT-font title again; two original deck copies are established without treating ordinary OCR flicker as another play."
                                : $"{named.Source.Name}'s printed play-all-copies resolution showed a second framed {preview.Card.Name} presentation after the first title-only phase; two original deck copies are established without counting the continuous popup as arbitrary replay spam.",
                            ResolvedDeckCopies: 2));
                    }
                }
                continue;
            }
            // DeckRevealCandidate already requires a small projected-card index,
            // four spatially spread correspondences in the enlarged lane, and this
            // pending printed effect. Those are independent gates; the flip often
            // presents only one usable frame, so a second image is not required.
            if (preview.NeedsTemporalConfirmation && preview.Source != CardSightSource.DeckRevealCandidate)
            {
                if (side == PlayerSide.Opponent && preview.Source == CardSightSource.PlayPreview &&
                    preview.Distance <= .30 && preview.Margin >= .70 &&
                    _spatialHoverTitles.TryGetValue(preview.Card.Id, out var popupTitle) && popupTitle.Count >= 2 &&
                    at >= popupTitle.At && at - popupTitle.At <= TimeSpan.FromSeconds(2) &&
                    PreviewAdjacentToTooltip(preview, popupTitle.Region) &&
                    _recentHandCounts.TryGetValue(side, out var handBefore) && at >= handBefore.At &&
                    at - handBefore.At <= TimeSpan.FromSeconds(12))
                {
                    _pendingOpponentPreview = new(preview, at, handBefore.Count);
                    _weakPreviews.Remove(side);
                    _recentWeakPreviews.Remove(side);
                    continue;
                }
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
            if (preview.Card.Id == "202883") _recentEist[side] = at;
            VisionEvidenceEvent? scenarioDeckPlay=null;
            if(_pendingScenarioDeckPlays.TryGetValue(side,out var pendingScenario) && at>pendingScenario.At &&
                at-pendingScenario.At<=TimeSpan.FromSeconds(20) &&
                ScenarioChapterCanPlayFromDeck(pendingScenario.Source,pendingScenario.Chapter,preview.Card) &&
                ConsumeDeckDeparture(side,at,TimeSpan.FromSeconds(20),pendingScenario.At))
            {
                _pendingScenarioDeckPlays.Remove(side);
                scenarioDeckPlay=new(at,preview,
                    $"{pendingScenario.Source.Name} Chapter {pendingScenario.Chapter}: exact following preview plus a same-hand deck decrement establish that {preview.Card.Name} was played from the deck.",1);
            }
            if(IsScenario(preview.Card)) _activeScenarios[side]=(preview.Card,0);
            else if(_activeScenarios.TryGetValue(side,out var scenario) && scenario.Stage<2 &&
                    ScenarioRules.Matches(scenario.Source,PlayRules.Compile(preview.Card)))
            {
                var stage=Math.Min(2,scenario.Stage+1);
                _activeScenarios[side]=(scenario.Source,stage);
                if(ScenarioChapterPlaysFromDeck(scenario.Source,stage))
                    _pendingScenarioDeckPlays[side]=(at,scenario.Source,stage);
            }
            foreach (var target in CompanionCardRules.TriggeredAutomaticPairTargets(preview.Card))
                _recentTriggeredPairs[(side, target)] = (at, preview.Card.Name);
            var namedTargets = NamedTargetsFromText(preview.Card);
            if (namedTargets.Count > 0)
                _namedMultiPlays[side] = new(preview.Card, at, namedTargets);
            if (CompanionCardRules.IsRecurringDeckSummonSource(preview.Card))
            {
                _recurringDeckSummons[side] = (preview.Card, at);
                _recentBroadDeckSummons[side] = (at, preview.Card);
                _broadDeployConsumed.Remove(side);
            }
            // A recurring printed deck summon can use the same enlarged-card lane
            // as an ordinary play. Do not charge the opponent's hand when a legal
            // target appears after the source and two independent HUD samples show
            // that the hand did not change. The target is still identified by the
            // ordinary repeated title/art path; this changes only its causal route.
            if (side == PlayerSide.Opponent &&
                _recurringDeckSummons.TryGetValue(side, out var recurringPreview) &&
                recurringPreview.Source.Id != preview.Card.Id && at > recurringPreview.StartedAt &&
                at - recurringPreview.StartedAt <= TimeSpan.FromMinutes(10) &&
                CompanionCardRules.RecurringSummonPool(recurringPreview.Source, [preview.Card]).Contains(preview.Card.Id) &&
                priorOpponentHand is { } earlierHand && at > earlierHand.At &&
                at - earlierHand.At <= TimeSpan.FromSeconds(12) && screen.OpponentHandCount == earlierHand.Count)
            {
                _previews[side] = new PreviewEpisode(preview.Card.Id);
                _boardEvidence.Add((side, preview.Card.Id));
                if (at - recurringPreview.StartedAt <= TimeSpan.FromSeconds(12))
                    _broadDeployConsumed.Add(side);
                events.Add(new(at, preview with { Source = CardSightSource.Board },
                    $"{recurringPreview.Source.Name}'s printed deck summon presented {preview.Card.Name} in the enlarged lane while repeated opponent HUD reads held the hand at {earlierHand.Count}; deck origin is established and no hand play is invented.",
                    ResolvedDeckCopies: 1));
                continue;
            }
            if (_namedMultiPlays.TryGetValue(side, out var activeNamed) &&
                activeNamed.TargetNames.Contains(preview.Card.Name))
                activeNamed.Presentations.TryAdd(preview.Card.Id,
                    new(at, IsExactFramedPreview(preview)));
            _actionEvidence.Add((side, preview.Card.Id));
            if (side == PlayerSide.User)
            {
                _playerHandHover = null;
                if (OpensCardChoice(preview.Card)) _recentChoiceSource = (preview.Card, at);
            }
            events.Add(scenarioDeckPlay ?? new VisionEvidenceEvent(at, preview, "Recognized in the side-specific play preview; one visible episode, not a new deck-copy claim."));
            if (DeckRevealRoute(preview.Card, side, at) is { } route) _deckReveals[side] = route;
        }

        foreach (var sighting in sightings.Where(item => item.Source == CardSightSource.History))
        {
            if (_actionEvidence.Add((sighting.Side, sighting.Card.Id)))
                events.Add(new VisionEvidenceEvent(at, sighting, "Recovered from Move History; exact play time and number of starting-deck copies are unknown."));
        }
        // Some automatic pairs listen to a category on a different played card.
        // Highwaymen, for example, is never itself played in this route: a Bonded
        // play can summon both legal bronze copies. Require the committed trigger,
        // an exact two-card same-hand deck departure and two spatially stable exact
        // target-title reads. The rule/counters establish quantity without opening
        // the broad colour-based pair scanner to unrelated same-colour regions.
        if (confirmedHover is { IsGold: false, Kind: CardKind.Unit, CanBeInStartingDeck: true } triggeredTarget &&
            !effectiveHandHover && screen.View == GwentViewKind.Board && screen.MatchHudVisible == true &&
            !screen.IsCardSelectionOverlay && CompanionCardRules.IsInherentDeckArrival(triggeredTarget) &&
            _spatialHoverTitles.TryGetValue(triggeredTarget.Id, out var triggeredTitle) && triggeredTitle.Count >= 2 &&
            IsOpponentBoardExactTooltip(triggeredTitle.Region) &&
            _recentTriggeredPairs.TryGetValue((PlayerSide.Opponent, triggeredTarget.Id), out var pairTrigger) &&
            at >= pairTrigger.At && at - pairTrigger.At <= TimeSpan.FromSeconds(25) &&
            !_actionEvidence.Contains((PlayerSide.Opponent, triggeredTarget.Id)) &&
            !_boardEvidence.Contains((PlayerSide.Opponent, triggeredTarget.Id)) &&
            ConsumeExactDeckDeparture(PlayerSide.Opponent, at, 2, TimeSpan.FromSeconds(20), pairTrigger.At))
        {
            var estimatedCard = OpponentCardBesideTooltip(triggeredTitle.Region);
            var sighting = new CardSighting(triggeredTarget, PlayerSide.Opponent, CardSightSource.Board,
                estimatedCard, 0, 1,
                $"Repeated exact board tooltip after {pairTrigger.Trigger} plus an exact same-hand two-card deck decrement; triggered automatic pair evidence.");
            _boardEvidence.Add((PlayerSide.Opponent, triggeredTarget.Id));
            events.Add(new(at, sighting,
                $"Recent {pairTrigger.Trigger} trigger, an exact two-card deck departure with no hand change, and a repeated exact opponent-board title establish both legal bronze targets.",
                ResolvedDeckCopies: 2));
        }
        // Eist's Devotion branch can turn Blaze of Glory's deck -> graveyard move
        // into an immediate summon without either card using the ordinary play lane.
        // Join the causal source, a conserved deck decrement and the repeated exact
        // opponent-board title. Consuming that departure here also prevents a later
        // unrelated automatic-arrival fallback from reusing it.
        if (confirmedHover is { Kind: CardKind.Unit } eistArrival &&
            !string.Equals(eistArrival.Faction, "Neutral", StringComparison.OrdinalIgnoreCase) &&
            !effectiveHandHover && screen.View == GwentViewKind.Board && screen.MatchHudVisible == true &&
            !screen.IsCardSelectionOverlay &&
            _spatialHoverTitles.TryGetValue(eistArrival.Id, out var eistTitle) && eistTitle.Count >= 2 &&
            IsOpponentBoardExactTooltip(eistTitle.Region) &&
            _recentEist.TryGetValue(PlayerSide.Opponent, out var eistAt) && at >= eistAt &&
            at - eistAt <= TimeSpan.FromSeconds(35) &&
            !_actionEvidence.Contains((PlayerSide.Opponent, eistArrival.Id)) &&
            !_boardEvidence.Contains((PlayerSide.Opponent, eistArrival.Id)) &&
            ConsumeDeckDeparture(PlayerSide.Opponent, at, TimeSpan.FromSeconds(20), eistAt))
        {
            var estimatedCard = OpponentCardBesideTooltip(eistTitle.Region);
            var sighting = new CardSighting(eistArrival, PlayerSide.Opponent, CardSightSource.Board,
                estimatedCard, 0, 1,
                "Repeated exact board tooltip after Eist plus a same-hand deck decrement; deck-to-graveyard-to-board identity evidence.");
            _boardEvidence.Add((PlayerSide.Opponent, eistArrival.Id));
            events.Add(new(at, sighting,
                "Recent Eist, an unclaimed same-hand deck decrement and a repeated exact opponent-board title establish the deck-to-graveyard-to-board summon; intermediate leader/discard source is not invented."));
        }
        // A broad random summon has no target name in its source text, so exact
        // tooltip identity is especially valuable. Join it to the one Deploy
        // credit, or to a later conserved deck departure. This replaces guessed
        // Timer turns: generated/tutored preview chains are not turns.
        if (confirmedHover is { Kind: CardKind.Unit, IsGold: false, CanBeInStartingDeck: true } broadArrival &&
            !string.Equals(broadArrival.Faction, "Neutral", StringComparison.OrdinalIgnoreCase) &&
            !effectiveHandHover && screen.View == GwentViewKind.Board && screen.MatchHudVisible == true &&
            !screen.IsCardSelectionOverlay &&
            _spatialHoverTitles.TryGetValue(broadArrival.Id, out var broadTitle) && broadTitle.Count >= 2 &&
            IsOpponentBoardExactTooltip(broadTitle.Region) &&
            _recurringDeckSummons.TryGetValue(PlayerSide.Opponent, out var activeBroad) &&
            at >= activeBroad.StartedAt && at - activeBroad.StartedAt <= TimeSpan.FromMinutes(10) &&
            CompanionCardRules.RecurringSummonPool(activeBroad.Source, [broadArrival]).Contains(broadArrival.Id) &&
            _recentBroadDeckSummons.TryGetValue(PlayerSide.Opponent, out var broadSignal) &&
            at >= broadSignal.At && at - broadSignal.At <= TimeSpan.FromSeconds(45) &&
            !_actionEvidence.Contains((PlayerSide.Opponent, broadArrival.Id)) &&
            !_boardEvidence.Contains((PlayerSide.Opponent, broadArrival.Id)))
        {
            // A player may not hover the summoned body until after the following
            // animations and turn transition. Join only the latest bounded signal:
            // an older timer activation must never lend its deck decrement to a
            // different card found after a later activation.
            var counterCredit = ConsumeDeckDeparture(PlayerSide.Opponent, at, TimeSpan.FromSeconds(45), broadSignal.At);
            var deployCredit = !_broadDeployConsumed.Contains(PlayerSide.Opponent) &&
                broadSignal.At == activeBroad.StartedAt && at - activeBroad.StartedAt <= TimeSpan.FromSeconds(55);
            if (counterCredit || deployCredit)
            {
                _broadDeployConsumed.Add(PlayerSide.Opponent);
                var estimatedCard = OpponentCardBesideTooltip(broadTitle.Region);
                var sighting = new CardSighting(broadArrival, PlayerSide.Opponent, CardSightSource.Board,
                    estimatedCard, 0, 1,
                    $"Repeated exact far-side board tooltip after {activeBroad.Source.Name}; " +
                    (counterCredit ? "conserved deck departure" : "one bounded Deploy summon credit"));
                _boardEvidence.Add((PlayerSide.Opponent, broadArrival.Id));
                events.Add(new(at, sighting,
                    $"{activeBroad.Source.Name}'s printed random deck summon and the repeated exact target tooltip establish this board arrival without inventing a play preview.",
                    ResolvedDeckCopies: 1));
            }
        }
        // Sparse board scans can see the same difficult automatic arrival several
        // times, then stop matching it shortly before the player opens its tooltip.
        // Preserve that per-card evidence across intervening scans. The longer join
        // is intentionally stricter than the immediate path: two same-body guarded
        // art observations and two exact title readings are both mandatory.
        if (confirmedHover is not null && !effectiveHandHover && screen.MatchHudVisible == true &&
            _spatialHoverTitles.TryGetValue(confirmedHover.Id, out var delayedTitle) && delayedTitle.Count >= 2 &&
            IsOpponentBoardTooltip(delayedTitle.Region) &&
            _weakAutomaticVotes.TryGetValue((PlayerSide.Opponent, confirmedHover.Id), out var delayedVote) &&
            delayedVote.Count >= 2 && at >= delayedVote.LastSeen &&
            at - delayedVote.LastSeen <= TimeSpan.FromSeconds(20) &&
            CompanionCardRules.IsInherentDeckArrival(confirmedHover) &&
            !_actionEvidence.Contains((PlayerSide.Opponent, confirmedHover.Id)) &&
            _boardEvidence.Add((PlayerSide.Opponent, confirmedHover.Id)))
            events.Add(new(at, delayedVote.Sighting,
                "Two same-body guarded automatic-arrival board observations were followed by a repeated exact opponent-board tooltip title; presence and deck origin established across a sparse scan gap."));
        if (boardWasScanned)
            _recentBoardCandidates=(at,sightings.Where(s=>s.Source==CardSightSource.Board).Take(128).ToArray());
        // Corroboration works in either order: a board scan can precede the second
        // exact title frame. Text-only ticks never count as another artwork vote.
        if (_recentBoardCandidates is {} recentBoard && at-recentBoard.At<=TimeSpan.FromSeconds(3) && screen.MatchHudVisible==true)
        foreach(var candidate in recentBoard.Cards)
        {
            var candidateKey=(candidate.Side,candidate.Card.Id);
            if (_actionEvidence.Contains(candidateKey) || _boardEvidence.Contains(candidateKey)) continue;
            if (!_spatialHoverTitles.TryGetValue(candidate.Card.Id,out var title)) continue;
            var adjacentTitleIdentity = AdjacentToTooltip(candidate,title.Region) &&
                recentBoard.Cards.Where(s=>AdjacentToTooltip(s,title.Region))
                    .Select(s=>s.Card.Id).Distinct().Count()==1;
            // The automatic-arrival fallback deliberately reports a synthetic .40
            // identity distance so that artwork alone can never commit it. Permit
            // that guarded candidate to cross the ordinary .36 title/art gate only
            // when an earlier scan saw the same board body, the card's printed rule
            // explicitly allows it to arrive from deck, and the title is in the
            // opponent-board tooltip lane. Board reflow and duplicate bronze copies
            // can separate the selected body from the earlier artwork box, so this
            // route does not require adjacency; the repeated exact title remains the
            // independent identity evidence and the repeated art establishes side.
            var guardedAutomaticTitleJoin = IsGuardedAutomaticArrival(candidate) && candidate.Distance <= .40 &&
                candidate.Side == PlayerSide.Opponent && CompanionCardRules.IsInherentDeckArrival(candidate.Card) &&
                IsOpponentBoardTooltip(title.Region) &&
                _weakAutomaticVotes.TryGetValue(candidateKey, out var automaticVote) &&
                at >= automaticVote.LastSeen && at - automaticVote.LastSeen <= TimeSpan.FromSeconds(10) &&
                SameBoardBody(automaticVote.Region, candidate.Region);
            if (title.Count>=2 &&
                at>=title.At && at-title.At<=TimeSpan.FromSeconds(3) &&
                (candidate.Distance<=.36 && adjacentTitleIdentity || guardedAutomaticTitleJoin) && candidate.Margin>=.50)
            {
                _boardEvidence.Add(candidateKey);
                events.Add(new(at,candidate, guardedAutomaticTitleJoin
                    ? "Repeated exact tooltip title spatially corroborates the same guarded inherent deck-arrival candidate from a prior board scan; presence and deck origin established."
                    : "Repeated exact tooltip title spatially corroborates adjacent recent board artwork; presence/controller only, not a played or original-copy claim."));
            }
        }
        if (!boardWasScanned) return events;
        // Some printed Deploys name two exact deck targets. Gwent inserts both on
        // one side of the played source, each new arrival taking the nearest slot;
        // the settled nearest-to-farthest order is therefore the reverse of the
        // rules-text order. Requiring the complete adjacent sequence turns a
        // difficult three-card animation into source-scoped evidence without
        // relaxing ordinary board recognition.
        foreach (var named in _namedMultiPlays.Values.Where(item => item.IsDeckSummonSequence &&
                     !item.BoardSequenceResolved && at >= item.At && at - item.At <= TimeSpan.FromSeconds(20)))
        {
            if (!TryNamedDeckSummonSequence(named, sightings, out var resolvedTargets)) continue;
            named.BoardSequenceResolved = true;
            ConsumeExactDeckDeparture(resolvedTargets[0].Side, at, resolvedTargets.Count,
                TimeSpan.FromSeconds(20), named.At);
            foreach (var target in resolvedTargets)
            {
                if (!named.ResolvedTargetIds.Add(target.Card.Id)) continue;
                _boardEvidence.Add((target.Side, target.Card.Id));
                events.Add(new(at, target,
                    $"{target.Card.Name} occupied its printed source-relative slot in the complete adjacent {named.Source.Name} summon sequence; the exact source, identity order and physical board geometry establish deck origin without a play preview.",
                    ResolvedDeckCopies: 1, EstablishesDistinctDeckCopy: true));
            }
        }
        // Conditional named Deploys can resolve one target or their paid aggregate
        // branch. Treat every target independently: a complete ordered group above
        // can recover all bodies at once, while this path reports only the Peaches,
        // Seductress (or analogous named target) actually supported by pixels. A
        // source-relative neighbor is sufficient physical evidence; if the played
        // source is too dark to localize again, require a same-hand deck decrement.
        foreach (var named in _namedMultiPlays.Values.Where(item => item.IsDeckSummonSequence &&
                     item.AllowsIndependentTargets &&
                     at >= item.At && at - item.At <= TimeSpan.FromSeconds(20)))
        foreach (var target in sightings.Where(item => item.Source == CardSightSource.Board &&
                     item.Side == PlayerSide.Opponent && named.TargetNames.Contains(item.Card.Name) &&
                     !named.ResolvedTargetIds.Contains(item.Card.Id)).OrderBy(item => item.Distance))
        {
            var tagged = (target.Evidence ?? "").Contains(
                "legal target of a recently committed printed deck summon", StringComparison.Ordinal);
            var strongIdentity = target.Distance <= .34 && target.Margin >= .50 && tagged;
            if (!strongIdentity) continue;
            var adjacent = TryNamedDeckSummonNeighbor(named, target, sightings);
            var conserved = adjacent || ConsumeDeckDeparture(target.Side, at, TimeSpan.FromSeconds(20), named.At);
            if (!conserved || !named.ResolvedTargetIds.Add(target.Card.Id)) continue;
            // Retire a matching pile credit when geometry supplied the evidence, so
            // that decrement cannot later corroborate an unrelated automatic card.
            if (adjacent) ConsumeDeckDeparture(target.Side, at, TimeSpan.FromSeconds(20), named.At);
            _boardEvidence.Add((target.Side, target.Card.Id));
            events.Add(new(at, target,
                $"{target.Card.Name} is a visually confirmed named deck-summon target beside the freshly played {named.Source.Name}; only this observed branch is recorded.",
                ResolvedDeckCopies: 1, EstablishesDistinctDeckCopy: true));
        }
        // Fixed-placement sources provide unusually strong instance evidence. Run
        // this before global identity deduplication so Portal can establish a second
        // copy of a bronze already played earlier in the round. The printed left/
        // right slot is consumed once per physical source play.
        foreach (var sighting in sightings.Where(item => item.Source == CardSightSource.Board)
                     .OrderBy(item => item.Distance))
        {
            if (!_recurringDeckSummons.TryGetValue(sighting.Side, out var placedSource) ||
                at < placedSource.StartedAt || at - placedSource.StartedAt > TimeSpan.FromMinutes(10) ||
                !CompanionCardRules.RecurringSummonPool(placedSource.Source, [sighting.Card]).Contains(sighting.Card.Id) ||
                !TryExplicitSourceNeighbor(sighting, sightings, placedSource.Source, out var placement)) continue;
            var placementKey = (sighting.Side, placedSource.Source.Id, placedSource.StartedAt, placement);
            var deploySlot = placement == "left";
            var timing = deploySlot
                ? at - placedSource.StartedAt <= TimeSpan.FromSeconds(55) && !_broadDeployConsumed.Contains(sighting.Side)
                : at - placedSource.StartedAt >= TimeSpan.FromSeconds(12);
            var taggedCandidate = (sighting.Evidence ?? "").Contains(
                "legal target of a recently committed printed deck summon", StringComparison.Ordinal);
            var identity = deploySlot
                ? sighting.Distance <= .36 && sighting.Margin >= .50 && (taggedCandidate || sighting.Margin >= .75)
                : sighting.Distance <= .30 && sighting.Margin >= .70;
            if (!timing || !identity || !_sourcePlacementConsumed.Add(placementKey)) continue;
            if (deploySlot) _broadDeployConsumed.Add(sighting.Side);
            _boardEvidence.Add((sighting.Side, sighting.Card.Id));
            events.Add(new(at, sighting,
                $"{sighting.Card.Name} is the legal {placement} neighbor of the still-visible {placedSource.Source.Name}; that source's printed fixed-placement deck summon establishes this physical copy and its origin without a guessed hand play.",
                ResolvedDeckCopies: 1, EstablishesDistinctDeckCopy: true));
        }
        foreach (var sighting in sightings.Where(item => item.Source == CardSightSource.Board)
                     .GroupBy(item => (item.Side, item.Card.Id)).Select(group => group.OrderBy(item => item.Distance).First()))
        {
            var key = (sighting.Side, sighting.Card.Id);
            if (_actionEvidence.Contains(key) || _boardEvidence.Contains(key)) continue;
            var sourceScopedSummon = _recentBroadDeckSummons.TryGetValue(sighting.Side, out var broad)
                ? broad : ((DateTimeOffset At, CardDefinition Source)?)null;
            var isSourceScopedSummonCandidate = (sighting.Evidence ?? "").Contains(
                "legal target of a recently committed printed deck summon", StringComparison.Ordinal);
            var clearAlignedSourceIdentity = sighting.Distance <= .32 && sighting.Margin >= .40 &&
                (sighting.Evidence ?? "").Contains("relabeled by clear aligned appearance", StringComparison.Ordinal);
            var strongSourceIdentity = sighting.Distance <= .30 && sighting.Margin >= .70 || clearAlignedSourceIdentity;
            var boundedDeployIdentity = sighting.Distance <= .35 && sighting.Margin >= .90;
            var recurringSourceActive = _recurringDeckSummons.TryGetValue(sighting.Side, out var pendingRecurring) &&
                at >= pendingRecurring.StartedAt && at - pendingRecurring.StartedAt <= TimeSpan.FromMinutes(10);
            var deployCredit = recurringSourceActive && !_broadDeployConsumed.Contains(sighting.Side) &&
                sourceScopedSummon is { } deploySignal && deploySignal.At == pendingRecurring.StartedAt &&
                at - pendingRecurring.StartedAt <= TimeSpan.FromSeconds(55);
            if (isSourceScopedSummonCandidate &&
                sighting.Side == PlayerSide.Opponent &&
                IsRightmostObservedOnRow(sighting, sightings) &&
                (strongSourceIdentity || deployCredit && boundedDeployIdentity) &&
                sourceScopedSummon is { } broadSummon && at >= broadSummon.At &&
                at - broadSummon.At <= TimeSpan.FromSeconds(45))
            {
                var counterCredit = strongSourceIdentity &&
                    ConsumeDeckDeparture(sighting.Side, at, TimeSpan.FromSeconds(45), broadSummon.At);
                if (counterCredit || deployCredit && boundedDeployIdentity)
                {
                    _boardEvidence.Add(key);
                    _pendingSourceScopedSummons.Remove(sighting.Side);
                    events.Add(new(at, sighting,
                        $"Strong rightmost source-scoped board geometry after {broadSummon.Source.Name}, " +
                        (counterCredit ? "corroborated by a same-hand deck decrement" : "joined to its one bounded Deploy summon credit") +
                        "; presence and deck-summon route established.",
                        ResolvedDeckCopies: 1));
                    _broadDeployConsumed.Add(sighting.Side);
                    continue;
                }
            }
            // HUD digits can settle one or two sampled frames after the summoned
            // body becomes visible. Retain only a strong, legal, rightmost target;
            // a following same-hand deck decrement can then corroborate origin in
            // either observation order without turning ordinary board persistence
            // into a starting-deck claim.
            if (isSourceScopedSummonCandidate && sighting.Side == PlayerSide.Opponent &&
                recurringSourceActive && strongSourceIdentity && IsRightmostObservedOnRow(sighting, sightings))
                RememberPendingSourceScopedSummon(sighting.Side, sighting, at, pendingRecurring.Source);
            // The candidate index only says this identity is legal for the active
            // source. Keep it provisional while causal confirmation can still
            // arrive; two ordinary board votes must not consume the identity and
            // prevent a later exact-title/deck-departure origin upgrade.
            if (isSourceScopedSummonCandidate &&
                recurringSourceActive)
                continue;
            // Automatic arrivals can be briefly visible or visually difficult
            // (Winter Queen is often monochrome under Frost). One strong board-art
            // sighting is sufficient only when public HUD counters independently
            // recorded a recent deck decrement with the hand unchanged, and the
            // card's own printed text permits this exact deck-to-board route.
            var weakAutomatic = IsGuardedAutomaticArrival(sighting);
            var repeatedWeakAutomatic = !weakAutomatic;
            var persistentWeakAutomatic = false;
            var referenceSupportedAutomatic = !weakAutomatic;
            if (weakAutomatic)
            {
                var hasRecentPrior = _weakAutomaticVotes.TryGetValue(key, out var automaticVote) &&
                    at > automaticVote.LastSeen && at - automaticVote.LastSeen <= TimeSpan.FromSeconds(90);
                var hasSpatialPrior = hasRecentPrior && SameBoardBody(automaticVote!.Region, sighting.Region);
                referenceSupportedAutomatic = PlausibleAutomaticAppearance(sighting) ||
                    hasRecentPrior && PlausibleAutomaticAppearance(automaticVote!.Sighting);
                // A deck decrement corroborates origin, not identity. Two weak artwork
                // boxes must therefore describe the same board body unless the current
                // match has independently strong two-anchor appearance evidence.
                repeatedWeakAutomatic = hasRecentPrior && at - automaticVote!.LastSeen <= TimeSpan.FromSeconds(10);
                // Sparse scans can miss the exact deck-counter transition and then leave a
                // self-summoned card visible for the rest of the round. Recover that case
                // only from a later clean, low-distance, two-anchor match at the same board
                // body. The minimum span prevents adjacent samples of one bad frame from
                // becoming independent evidence. Side and identity are already in the key.
                var tooltipObscuresBody=screen.HasCardTooltip && screen.TooltipRegion is {} tip &&
                    sighting.Region.Left<tip.Right+.02 && sighting.Region.Right>tip.Left-.02 &&
                    sighting.Region.Top<tip.Bottom+.02 && sighting.Region.Bottom>tip.Top-.12;
                var persistenceSpan = (sighting.Evidence ?? "").Contains(
                        "Localized board appearance persisted in an independent scan", StringComparison.Ordinal)
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromSeconds(3);
                persistentWeakAutomatic = hasSpatialPrior && at - automaticVote!.FirstSeen >= persistenceSpan &&
                    !tooltipObscuresBody && StrongAutomaticAppearance(sighting) &&
                    CompanionCardRules.HasExplicitInherentDeckArrival(sighting.Card);
                _weakAutomaticVotes[key] = hasSpatialPrior
                    ? automaticVote! with { LastSeen = at, Count = automaticVote.Count + 1, Sighting = sighting }
                    : new(at, at, 1, sighting.Region, sighting);
            }
            if (sighting.Distance <= .40 && sighting.Margin >= .50 && screen.MatchHudVisible == true &&
                CompanionCardRules.IsInherentDeckArrival(sighting.Card) &&
                (persistentWeakAutomatic || repeatedWeakAutomatic &&
                    referenceSupportedAutomatic &&
                    ConsumeDeckDeparture(sighting.Side, at)))
            {
                _boardEvidence.Add(key);
                events.Add(new(at, sighting,
                    persistentWeakAutomatic
                        ? "An inherent automatic deck-arrival identity persisted in spatially consistent independent board scans, including a later unobscured low-distance match; presence and deck origin established despite unavailable deck OCR."
                        : "Strong board artwork for an inherent automatic deck arrival was corroborated by a recent deck decrement with no hand change; presence and deck origin established without inventing a play preview."));
                continue;
            }
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
            if (sighting.Evidence?.Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal) == true)
                continue;
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

    // Hand-count conservation is deliberately only a recovery signal. It must not
    // turn a short-name OCR error into a played token: non-ownable identities such
    // as Frog cannot originate in an ordinary hand. Evolved forms are the one safe
    // exception because they represent their collectible starting identity.
    private static bool CanInferPlayerHandCommit(CardDefinition card) =>
        card.CanBeInStartingDeck || EvolvingCardCatalog.IsEvolved(card.Id);

    public void Reset()
    {
        _previews.Clear(); _weakPreviews.Clear(); _recentWeakPreviews.Clear(); _quarantinedProjectedPreviews.Clear(); _actionEvidence.Clear(); _boardEvidence.Clear(); _boardVotes.Clear(); _weakAutomaticVotes.Clear(); _boardHoverTitles.Clear(); _shupePlayed.Clear(); _deckReveals.Clear();
        _playerHandHover = null;
        _playChoiceHover = null;
        _recentChoiceSource = null;
        _suppressedChoiceHover = null;
        _lastReliableUserHandCount = null;
        _pendingChoiceSourceHandDrop = null;
        _pendingOpponentPreview = null;
        _recentHandCounts.Clear();
        _recentEist.Clear(); _recurringDeckSummons.Clear(); _recentBroadDeckSummons.Clear(); _pendingSourceScopedSummons.Clear(); _deferredPileEvents.Clear(); _broadDeployConsumed.Clear(); _sourcePlacementConsumed.Clear(); _namedMultiPlays.Clear();
        _activeScenarios.Clear(); _pendingScenarioDeckPlays.Clear();
        _recentTriggeredPairs.Clear();
        _pileSnapshots.Clear();
        _unmatchedDeckDepartures.Clear();
        _openingDealUntil = default;
        _observedRound = null;
        _spatialHoverTitles.Clear();
        _recentBoardCandidates=null;
    }

    private static IReadOnlyList<string> NamedTargetsFromText(CardDefinition source)
    {
        var deckSummons = CompanionCardRules.NamedDeckSummonTargetNames(source);
        if (deckSummons.Count > 0) return deckSummons;

        // Play-all-copies effects (for example Tempest) use the same presentation
        // episode tracker but are not a simultaneous named summon group. Keep this
        // distinct so their alternative names never arm the board-pair recognizer.
        var match = Regex.Match(source.AbilityText ?? "",
            @"\bPlay all copies of (.+?) from your deck\b", RegexOptions.IgnoreCase);
        if (!match.Success) return [];
        return Regex.Split(match.Groups[1].Value, @",\s*(?:or\s+)?|\s+or\s+", RegexOptions.IgnoreCase)
            .Select(name => name.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ObserveRoundBoundary(GwentVisualObservation screen)
    {
        var header = (screen.ScreenHeader ?? "").Trim();
        var number = header.Equals("FINAL ROUND", StringComparison.OrdinalIgnoreCase) ? 3 :
            Regex.Match(header, @"^ROUND\s+([123])$", RegexOptions.IgnoreCase) is { Success: true } match
                ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        if (number is null || _observedRound == number) return;
        var crossedRound = number > 1 || _observedRound is not null;
        _observedRound = number;
        if (!crossedRound) return;

        // These objects live on the board or describe an animation in the old
        // round. Retaining them can lend a stale Portal/Saskia/Scenario route or
        // stale pile decrement to a generated card after redraw. Starting-deck
        // identity evidence remains intact; only causal windows are retired.
        _recurringDeckSummons.Clear();
        _recentBroadDeckSummons.Clear();
        _pendingSourceScopedSummons.Clear();
        _broadDeployConsumed.Clear();
        _sourcePlacementConsumed.Clear();
        _activeScenarios.Clear();
        _pendingScenarioDeckPlays.Clear();
        _unmatchedDeckDepartures.Clear();
        _pileSnapshots.Clear();
        _previews.Clear();
        _weakPreviews.Clear();
        _recentWeakPreviews.Clear();
        _quarantinedProjectedPreviews.Clear();
        _pendingOpponentPreview = null;
        _playerHandHover = null;
        _playChoiceHover = null;
        _recentChoiceSource = null;
        _pendingChoiceSourceHandDrop = null;
        _namedMultiPlays.Clear();
        _recentBoardCandidates = null;
    }

    private static bool TryNamedDeckSummonSequence(NamedMultiPlay named, IReadOnlyList<CardSighting> sightings,
        out IReadOnlyList<CardSighting> targets)
    {
        targets = [];
        var source = sightings.Where(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                item.Card.Id == named.Source.Id && item.Distance <= .34 && item.Margin >= .35)
            .OrderBy(item => item.Distance).FirstOrDefault();
        var selected = new List<CardSighting>();
        foreach (var name in named.OrderedTargetNames)
        {
            var target = sightings.Where(item => item.Side == PlayerSide.Opponent && item.Source == CardSightSource.Board &&
                    item.Card.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && item.Distance <= .34 && item.Margin >= .35 &&
                    (item.Evidence ?? "").Contains("legal target of a recently committed printed deck summon", StringComparison.Ordinal))
                .OrderBy(item => item.Distance).FirstOrDefault();
            if (target is null) return false;
            selected.Add(target);
        }
        if (selected.Select(item => item.Card.Id).Distinct(StringComparer.Ordinal).Count() != selected.Count) return false;
        var rowY = selected.Average(item => (item.Region.Top + item.Region.Bottom) / 2);
        if (selected.Any(item => Math.Abs((item.Region.Top + item.Region.Bottom) / 2 - rowY) > .045)) return false;
        var targetWidths = selected.Select(item => item.Region.Right - item.Region.Left).ToArray();
        var typicalWidth = targetWidths.Average();
        if (typicalWidth <= .02 || targetWidths.Any(width => width < typicalWidth * .65 || width > typicalWidth * 1.35)) return false;
        if (source is null)
        {
            // The played Silver Witcher can be extremely dark and lose its own
            // board-art match. Both exact named partners still form a complete
            // source-scoped sequence: Gwent inserts them contiguously on one side,
            // yielding either printed or reverse printed left-to-right order.
            // The fresh exact source preview and two restricted-candidate identities
            // are retained; this does not become a generic whole-board pair rule.
            var leftToRight = selected.OrderBy(item => (item.Region.Left + item.Region.Right) / 2).ToArray();
            var names = leftToRight.Select(item => item.Card.Name).ToArray();
            if (!names.SequenceEqual(named.OrderedTargetNames, StringComparer.OrdinalIgnoreCase) &&
                !names.SequenceEqual(named.OrderedTargetNames.Reverse(), StringComparer.OrdinalIgnoreCase)) return false;
            for (var index = 1; index < leftToRight.Length; index++)
            {
                var previous = (leftToRight[index - 1].Region.Left + leftToRight[index - 1].Region.Right) / 2;
                var current = (leftToRight[index].Region.Left + leftToRight[index].Region.Right) / 2;
                var gap = current - previous;
                if (gap < typicalWidth * .60 || gap > typicalWidth * 1.65) return false;
            }
            targets = selected;
            return true;
        }
        var sourceX = (source.Region.Left + source.Region.Right) / 2;
        var sourceY = (source.Region.Top + source.Region.Bottom) / 2;
        var sourceWidth = source.Region.Right - source.Region.Left;
        if (Math.Abs(sourceY - rowY) > .045) return false;
        var signed = selected.Select(item => ((item.Region.Left + item.Region.Right) / 2) - sourceX).ToArray();
        if (!(signed.All(value => value > sourceWidth * .45) || signed.All(value => value < -sourceWidth * .45))) return false;
        var nearestFirst = selected.OrderBy(item => Math.Abs((item.Region.Left + item.Region.Right) / 2 - sourceX)).ToArray();
        if (!nearestFirst.Select(item => item.Card.Name).SequenceEqual(named.OrderedTargetNames.Reverse(),
                StringComparer.OrdinalIgnoreCase)) return false;
        var centers = new[] { sourceX }.Concat(nearestFirst.Select(item => (item.Region.Left + item.Region.Right) / 2)).ToArray();
        for (var index = 1; index < centers.Length; index++)
        {
            var gap = Math.Abs(centers[index] - centers[index - 1]);
            if (gap < sourceWidth * .60 || gap > sourceWidth * 1.65) return false;
        }
        targets = selected;
        return true;
    }

    private static bool TryNamedDeckSummonNeighbor(NamedMultiPlay named, CardSighting target,
        IReadOnlyList<CardSighting> sightings)
    {
        var source = sightings.Where(item => item.Side == target.Side && item.Source == CardSightSource.Board &&
                item.Card.Id == named.Source.Id && item.Distance <= .34 && item.Margin >= .35)
            .OrderBy(item => item.Distance).FirstOrDefault();
        if (source is null) return false;
        var sourceX = (source.Region.Left + source.Region.Right) / 2;
        var sourceY = (source.Region.Top + source.Region.Bottom) / 2;
        var targetX = (target.Region.Left + target.Region.Right) / 2;
        var targetY = (target.Region.Top + target.Region.Bottom) / 2;
        var width = source.Region.Right - source.Region.Left;
        var gap = Math.Abs(targetX - sourceX);
        return width > .02 && Math.Abs(targetY - sourceY) <= .045 &&
            gap >= width * .60 && gap <= width * 1.65;
    }

    private static bool IsExactFramedPreview(CardSighting sighting) => sighting.Distance <= .11 &&
        (sighting.Evidence ?? "").Contains("card-frame boundary", StringComparison.OrdinalIgnoreCase);

    private static bool IsScenario(CardDefinition card) =>
        card.AbilityText?.Contains("Scenario: Progress whenever you play",StringComparison.OrdinalIgnoreCase)==true;
    private static bool ScenarioChapterPlaysFromDeck(CardDefinition source,int chapter) =>
        Regex.IsMatch(source.AbilityText??"",@"(?:^|\n)Chapter\s+"+chapter+@"\s*:\s*Play\b[^.\n]*\bfrom your deck\b",
            RegexOptions.IgnoreCase);
    private static bool ScenarioChapterCanPlayFromDeck(CardDefinition source,int chapter,CardDefinition target)
    {
        var match=Regex.Match(source.AbilityText??"",@"(?:^|\n)Chapter\s+"+chapter+@"\s*:\s*(?<clause>Play\b[^.\n]*\bfrom your deck\b)",
            RegexOptions.IgnoreCase);
        if(!match.Success) return false;
        var clause=match.Groups["clause"].Value;
        if(Regex.IsMatch(clause,@"\bunit\b",RegexOptions.IgnoreCase)&&target.Kind!=CardKind.Unit ||
           Regex.IsMatch(clause,@"\bspecial\b",RegexOptions.IgnoreCase)&&target.Kind!=CardKind.Special ||
           Regex.IsMatch(clause,@"\bartifact\b",RegexOptions.IgnoreCase)&&target.Kind!=CardKind.Artifact ||
           Regex.IsMatch(clause,@"\bbronze\b",RegexOptions.IgnoreCase)&&target.IsGold ||
           Regex.IsMatch(clause,@"\bgold\b",RegexOptions.IgnoreCase)&&!target.IsGold ||
           Regex.IsMatch(clause,@"\bnon-Neutral\b",RegexOptions.IgnoreCase)&&target.Faction=="Neutral") return false;
        return target.CanBeInStartingDeck;
    }

    private int? RecentHandCount(DateTimeOffset at) =>
        _lastReliableUserHandCount is { } hand && at - hand.At <= RecentHandCountWindow ? hand.Count : null;

    private void ObservePileConservation(DateTimeOffset at, GwentVisualObservation screen)
    {
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            if (_unmatchedDeckDepartures.TryGetValue(side, out var pending))
                pending.RemoveAll(item => at < item.At || at - item.At > TimeSpan.FromMinutes(3));
            if (_pendingSourceScopedSummons.TryGetValue(side, out var pendingSource) &&
                (at < pendingSource.At || at - pendingSource.At > TimeSpan.FromSeconds(8)))
                _pendingSourceScopedSummons.Remove(side);
            if (screen.View != GwentViewKind.Board || screen.IsCardSelectionOverlay || screen.MatchHudVisible != true ||
                !string.IsNullOrWhiteSpace(screen.ScreenHeader)) continue;
            var hand = side == PlayerSide.User ? screen.UserHandCount : screen.OpponentHandCount;
            var deck = side == PlayerSide.User ? screen.UserDeckCount : screen.OpponentDeckCount;
            if (hand is not >= 0 or > 10 || deck is not >= 0 or > 100) continue;
            var current = new PileSnapshot(hand.Value, deck.Value, at);
            // Round redraws retire a recurring board source. Requiring both a hand
            // increase and a multi-card deck decrease avoids confusing an ordinary
            // one-card draw with a round transition.
            if (_pileSnapshots.TryGetValue(side, out var redraw) &&
                !(redraw.Hand == 0 && redraw.Deck >= 25 && current.Hand > 0) &&
                current.Hand > redraw.Hand && redraw.Deck - current.Deck >= 2)
            {
                _recurringDeckSummons.Remove(side);
                _recentBroadDeckSummons.Remove(side);
                _pendingSourceScopedSummons.Remove(side);
                _broadDeployConsumed.Remove(side);
            }
            var sparseHudBridge = _pendingSourceScopedSummons.TryGetValue(side, out var bridgedSource)
                ? bridgedSource : null;
            var pendingSourceBridgesSparseHud = sparseHudBridge is { } bridge &&
                at >= bridge.At && at - bridge.At <= TimeSpan.FromSeconds(8);
            if (_pileSnapshots.TryGetValue(side, out var previous) && at > previous.At &&
                (at - previous.At <= TimeSpan.FromSeconds(10) ||
                 pendingSourceBridgesSparseHud && sparseHudBridge!.At >= previous.At) &&
                current.Hand == previous.Hand && previous.Deck - current.Deck is >= 1 and <= 4)
            {
                if (!_unmatchedDeckDepartures.TryGetValue(side, out pending))
                    _unmatchedDeckDepartures[side] = pending = [];
                // A sparse-HUD interval may aggregate an ordinary draw with the
                // one source-scoped summon seen immediately before the counter
                // settles. It confirms that candidate, but its residual delta is
                // not reusable evidence for an unrelated automatic arrival.
                var conservedCopies = pendingSourceBridgesSparseHud &&
                    at - previous.At > TimeSpan.FromSeconds(10)
                    ? 1 : previous.Deck - current.Deck;
                pending.Add(new(at, conservedCopies));
                if (pending.Count > 8) pending.RemoveRange(0, pending.Count - 8);
                // The recurring source remains public on board. A pile departure
                // is the general causal signal for its later activation; do not
                // infer Timer state from how many preview animations happened.
                if (_recurringDeckSummons.TryGetValue(side, out var recurring) &&
                    at >= recurring.StartedAt && at - recurring.StartedAt <= TimeSpan.FromMinutes(10))
                {
                    if (_pendingSourceScopedSummons.TryGetValue(side, out var sourceCandidate) &&
                        at >= sourceCandidate.At && at - sourceCandidate.At <= TimeSpan.FromSeconds(8) &&
                        sourceCandidate.Source.Id == recurring.Source.Id &&
                        !_actionEvidence.Contains((side, sourceCandidate.Sighting.Card.Id)) &&
                        !_boardEvidence.Contains((side, sourceCandidate.Sighting.Card.Id)) &&
                        CompanionCardRules.RecurringSummonPool(recurring.Source, [sourceCandidate.Sighting.Card])
                            .Contains(sourceCandidate.Sighting.Card.Id) &&
                        ConsumeDeckDeparture(side, at, TimeSpan.FromSeconds(8), sourceCandidate.At))
                    {
                        _boardEvidence.Add((side, sourceCandidate.Sighting.Card.Id));
                        _broadDeployConsumed.Add(side);
                        _deferredPileEvents.Add(new(at, sourceCandidate.Sighting,
                            $"Strong rightmost {recurring.Source.Name} target artwork immediately preceded a same-hand deck decrement; presence and deck-summon route established despite delayed HUD digits.",
                            ResolvedDeckCopies: 1));
                        _pendingSourceScopedSummons.Remove(side);
                    }
                    _recentBroadDeckSummons[side] = (at, recurring.Source);
                }
            }
            _pileSnapshots[side] = current;
        }
    }

    private bool ConsumeDeckDeparture(PlayerSide side, DateTimeOffset at, TimeSpan? maximumAge = null,
        DateTimeOffset? notBefore = null)
    {
        if (!_unmatchedDeckDepartures.TryGetValue(side, out var pending)) return false;
        pending.RemoveAll(item => at < item.At || at - item.At > TimeSpan.FromMinutes(3));
        if (pending.Count == 0) return false;
        var age = maximumAge ?? TimeSpan.FromSeconds(45);
        var index = pending.FindLastIndex(item => at >= item.At && at - item.At <= age &&
            (notBefore is null || item.At >= notBefore.Value));
        if (index < 0) return false;
        var departure = pending[index];
        if (departure.Copies == 1) pending.RemoveAt(index);
        else pending[index] = departure with { Copies = departure.Copies - 1 };
        return true;
    }

    private void RememberPendingSourceScopedSummon(PlayerSide side, CardSighting sighting, DateTimeOffset at,
        CardDefinition source)
    {
        var candidate = new PendingSourceScopedSummon(sighting, at, source);
        if (!_pendingSourceScopedSummons.TryGetValue(side, out var existing) || at > existing.At ||
            at == existing.At && SourceIdentityScore(sighting) > SourceIdentityScore(existing.Sighting))
            _pendingSourceScopedSummons[side] = candidate;
    }

    private static double SourceIdentityScore(CardSighting sighting) =>
        (1 - Math.Clamp(sighting.Distance, 0, 1)) * .7 + Math.Clamp(sighting.Margin, 0, 1) * .3;

    private bool ConsumeExactDeckDeparture(PlayerSide side, DateTimeOffset at, int copies, TimeSpan maximumAge,
        DateTimeOffset notBefore)
    {
        if (!_unmatchedDeckDepartures.TryGetValue(side, out var pending)) return false;
        pending.RemoveAll(item => at < item.At || at - item.At > TimeSpan.FromMinutes(3));
        var index = pending.FindLastIndex(item => item.Copies == copies && item.At >= notBefore &&
            at >= item.At && at - item.At <= maximumAge);
        if (index < 0) return false;
        pending.RemoveAt(index);
        return true;
    }

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
        public int MissingArtworkScans { get; set; }
    }
    private sealed record BoardVote(DateTimeOffset LastSeen, int Count);
    private sealed record WeakAutomaticVote(DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Count,
        NormalizedRegion Region, CardSighting Sighting);
    private sealed record HoverBoardVote(DateTimeOffset At, NormalizedRegion Region, int Count);
    private static bool IsGuardedAutomaticArrival(CardSighting sighting) =>
        sighting.Evidence?.Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal) == true;
    private static bool IsOpponentBoardTooltip(NormalizedRegion tooltip) => tooltip.Top <= .40 && tooltip.Bottom <= .65;
    private static bool IsOpponentBoardExactTooltip(NormalizedRegion tooltip) => tooltip.Top <= .45 && tooltip.Bottom <= .80;
    private static NormalizedRegion OpponentCardBesideTooltip(NormalizedRegion tooltip) =>
        new(Math.Max(0, tooltip.Left - .075), Math.Max(.08, tooltip.Top - .17),
            Math.Min(1, tooltip.Left + .02), Math.Min(.48, tooltip.Top + .02));
    private static bool SameBoardBody(NormalizedRegion prior, NormalizedRegion current) =>
        Math.Abs((prior.Left + prior.Right - current.Left - current.Right) / 2) <= .045 &&
        Math.Abs((prior.Top + prior.Bottom - current.Top - current.Bottom) / 2) <= .035 &&
        Math.Abs((prior.Right - prior.Left) - (current.Right - current.Left)) <= .02 &&
        Math.Abs((prior.Bottom - prior.Top) - (current.Bottom - current.Top)) <= .03;
    private static bool IsRightmostObservedOnRow(CardSighting candidate, IReadOnlyList<CardSighting> sightings)
    {
        var width = candidate.Region.Right - candidate.Region.Left;
        var height = candidate.Region.Bottom - candidate.Region.Top;
        var centerX = (candidate.Region.Left + candidate.Region.Right) / 2;
        var centerY = (candidate.Region.Top + candidate.Region.Bottom) / 2;
        return !sightings.Any(other => other.Source == CardSightSource.Board &&
            other.Side == candidate.Side && !SameBoardBody(candidate.Region, other.Region) &&
            Math.Abs((other.Region.Top + other.Region.Bottom) / 2 - centerY) <= height * .40 &&
            (other.Region.Left + other.Region.Right) / 2 > centerX + width * .35);
    }
    private static bool TryExplicitSourceNeighbor(CardSighting candidate, IReadOnlyList<CardSighting> sightings,
        CardDefinition source, out string placement)
    {
        placement = "";
        if (CompanionCardRules.ExplicitDeckSummonPlacement(source, candidate.Card) is null) return false;
        var cardWidth = candidate.Region.Right - candidate.Region.Left;
        var cardHeight = candidate.Region.Bottom - candidate.Region.Top;
        var candidateX = (candidate.Region.Left + candidate.Region.Right) / 2;
        var candidateY = (candidate.Region.Top + candidate.Region.Bottom) / 2;
        var sourceSight = sightings.Where(other => other.Source == CardSightSource.Board &&
                other.Side == candidate.Side && other.Card.Id == source.Id &&
                Math.Abs((other.Region.Top + other.Region.Bottom) / 2 - candidateY) <= cardHeight * .35)
            .OrderBy(other => Math.Abs((other.Region.Left + other.Region.Right) / 2 - candidateX)).FirstOrDefault();
        if (sourceSight is null) return false;
        var sourceX = (sourceSight.Region.Left + sourceSight.Region.Right) / 2;
        var gap = candidateX < sourceX
            ? sourceSight.Region.Left - candidate.Region.Right
            : candidate.Region.Left - sourceSight.Region.Right;
        if (gap < -cardWidth * .10 || gap > cardWidth * .28) return false;
        if (sightings.Any(other => other.Source == CardSightSource.Board && other.Side == candidate.Side &&
            other.Card.Id != candidate.Card.Id && other.Card.Id != source.Id &&
            Math.Abs((other.Region.Top + other.Region.Bottom) / 2 - candidateY) <= cardHeight * .35 &&
            (other.Region.Left + other.Region.Right) / 2 > Math.Min(candidateX, sourceX) + cardWidth * .25 &&
            (other.Region.Left + other.Region.Right) / 2 < Math.Max(candidateX, sourceX) - cardWidth * .25)) return false;
        placement = candidateX < sourceX ? "left" : "right";
        return true;
    }
    private static bool StrongAutomaticAppearance(CardSighting sighting)
        => DirectAutomaticAppearanceDistance(sighting) is <= .55;
    private static bool PlausibleAutomaticAppearance(CardSighting sighting)
        // A same-hand deck decrement plus two spatially consistent samples supplies
        // independent origin/timing evidence, so this boundary may be slightly wider
        // than the no-HUD sparse-persistence path. It still requires a direct match
        // to reference art; crop-to-crop persistence alone never identifies a card.
        => DirectAutomaticAppearanceDistance(sighting) is <= .75;
    private static double? DirectAutomaticAppearanceDistance(CardSighting sighting)
    {
        var evidence = sighting.Evidence ?? "";
        // Only distance to the card's reference art establishes identity. The
        // recognizer's localized-persistence distance compares a crop with an
        // earlier crop of the same board body; it proves continuity, not identity.
        var match = Regex.Match(evidence, @"appearance distance (?<value>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value)) return null;
        return evidence.Contains("Two geometrically agreeing anchors", StringComparison.Ordinal) ||
               evidence.Contains("One scene anchor independently selected", StringComparison.Ordinal)
            ? value : null;
    }
    private static bool AdjacentToTooltip(CardSighting sight, NormalizedRegion tip)
    {
        var card=sight.Region;
        var center=(card.Top+card.Bottom)/2;
        if (sight.Side==PlayerSide.Opponent ? center>=.45 : center<=.45 || center>=.86) return false;
        var gap=Math.Min(Math.Abs(tip.Left-card.Right),Math.Abs(card.Left-tip.Right));
        return gap<=.06 && tip.Top>=card.Top-.04 && tip.Top<=card.Bottom+.12;
    }
    private static bool PreviewAdjacentToTooltip(CardSighting sight, NormalizedRegion tip)
    {
        var card = sight.Region;
        var center = (card.Top + card.Bottom) / 2;
        if (sight.Side != PlayerSide.Opponent || center >= .45 || tip.Right > card.Left + .01) return false;
        var gap = card.Left - tip.Right;
        return gap >= 0 && gap <= .08 && tip.Top >= card.Top - .04 && tip.Top <= card.Bottom + .12;
    }
    private sealed record PendingHandPlay(CardDefinition Card, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int? InitialHandCount,
        DateTimeOffset? TargetedAt = null);
    private sealed record PendingOpponentPreview(CardSighting Sighting, DateTimeOffset SeenAt, int HandBefore);
    private sealed record PileSnapshot(int Hand, int Deck, DateTimeOffset At);
    private sealed record DeckDeparture(DateTimeOffset At, int Copies);
    private sealed record PendingSourceScopedSummon(CardSighting Sighting, DateTimeOffset At, CardDefinition Source);
    private sealed class NamedMultiPlay(CardDefinition source, DateTimeOffset at, IReadOnlyList<string> targetNames)
    {
        public CardDefinition Source { get; } = source;
        public DateTimeOffset At { get; } = at;
        public IReadOnlyList<string> OrderedTargetNames { get; } = targetNames;
        public IReadOnlySet<string> TargetNames { get; } = targetNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public bool IsDeckSummonSequence { get; } = Regex.IsMatch(source.AbilityText ?? "",
            @"\bSummon\s+[^\.\r\n]{2,120}?\s+from your deck\b", RegexOptions.IgnoreCase) && targetNames.Count >= 2;
        public bool AllowsIndependentTargets { get; } = CompanionCardRules.AllowsIndependentNamedDeckSummons(source);
        public bool BoardSequenceResolved { get; set; }
        public HashSet<string> ResolvedTargetIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, NamedTargetPresentation> Presentations { get; } = [];
    }
    private sealed class NamedTargetPresentation(DateTimeOffset firstSeen, bool sawFrameBoundary)
    {
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public bool SawFrameBoundary { get; set; } = sawFrameBoundary;
        public bool ResolvedPair { get; set; }
        private DateTimeOffset LastSeen { get; set; } = firstSeen;
        private DateTimeOffset? MissingSince { get; set; }
        private DateTimeOffset? LastMissingVote { get; set; }
        private DateTimeOffset? LastReturnVote { get; set; }
        private int MissingVotes { get; set; }
        private int ReturnVotes { get; set; }
        private bool QualifiedGap { get; set; }

        public void ObserveMissing(DateTimeOffset at)
        {
            if (ResolvedPair || at <= LastSeen || at - LastSeen > TimeSpan.FromSeconds(3.5) ||
                LastMissingVote == at) return;
            MissingSince ??= at;
            LastMissingVote = at;
            MissingVotes++;
        }

        public bool ObserveSeen(DateTimeOffset at, CardSighting sighting)
        {
            if (!QualifiedGap && MissingSince is { } missing && MissingVotes >= 3 &&
                at - missing >= TimeSpan.FromSeconds(.45) && at - LastSeen <= TimeSpan.FromSeconds(3.5))
                QualifiedGap = true;
            if (QualifiedGap && sighting.Distance <= .16 &&
                (sighting.Evidence ?? "").Contains("visible preview", StringComparison.OrdinalIgnoreCase) &&
                LastReturnVote != at)
            {
                ReturnVotes = LastReturnVote is { } prior && at - prior <= TimeSpan.FromSeconds(1.2)
                    ? Math.Min(2, ReturnVotes + 1) : 1;
                LastReturnVote = at;
            }
            LastSeen = at;
            SawFrameBoundary |= IsExactFramedPreview(sighting);
            return QualifiedGap && (IsExactFramedPreview(sighting) || ReturnVotes >= 2);
        }
    }
    private sealed record PendingPlayChoice(CardDefinition Card, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Votes,
        CardDefinition? Source);
    private sealed record PendingDeckReveal(string SourceId, string SourceName, PlayerSide VisualSide,
        PlayerSide TargetSide, DateTimeOffset At, bool UnitOnly)
    {
        public bool Matches(CardDefinition card) => !UnitOnly || card.Kind == CardKind.Unit;
    }
}
