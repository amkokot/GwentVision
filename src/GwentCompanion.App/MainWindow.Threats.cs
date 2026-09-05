using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    // The engine and its regression suite are intentionally retained for later
    // work, but Reach is not a shipped main-app feature in this release.
    private static readonly bool ReachEnabledInMainApp = false;
    private CancellationTokenSource? _threatCancellation;
    private readonly SemaphoreSlim _reachWorker = new(1, 1);
    private bool _waitingForThreatBoard, _suspendReach;
    private GamePosition? _threatBoard;
    private GameStateSnapshot? _threatSnapshot;
    private DateTimeOffset _lastThreatSearch, _lastHoverAt;
    private string? _threatKey, _threatReferenceId;
    private CardDefinition? _liveHover;
    private DateTimeOffset _latestHoverAt;
    private GwentCompanion.Core.Vision.GwentVisualObservation? _latestHoverScreen;
    private string? _lastThreatCardId;
    private bool _settingThreatChoice;
    private ThreatAnalyzer? _threatAnalyzer;
    private object? _threatAnalyzerCatalog;
    private string _reachTitle = "Hover a card in your hand to explore its play.";
    private string _reachGap = "", _reachSummary = "", _reachStatus = "";
    private sealed record ThreatRow(string Heading, string Detail, string Line, string Immediate = "", string OneTurn = "",
        string TwoTurns = "", double TwoTurnOpacity = 1, Visibility HorizonVisibility = Visibility.Collapsed);
    private void ResetThreats()
    {
        _threatCancellation?.Cancel(); _threatCancellation = null;
        _reachProgress = null; _waitingForThreatBoard = false;
        _zoneInventory.Reset(); _grantedMechanics.Reset(); _pirateArmor.Reset(); _threatBoard = null; _threatSnapshot = null; _threatKey = null;
        _cultists.Reset();
        _liveHover = null; _lastThreatSearch = default;
        _latestHoverAt = default; _latestHoverScreen = null; _lastThreatCardId = null;
        _reachTitle = "Hover a card in your hand to explore its play."; _reachGap = _reachSummary = _reachStatus = "";
        Interlocked.Exchange(ref _pendingFastHover, null);
        RefreshHoverBanner();
        _liveValues.Reset(); _liveValuesKey = null;
        _tributeRefunds.Reset();
        if (HoverThreatDetailReplies is not null) HoverThreatDetailReplies.ItemsSource = null;
        if (HoverThreatRange is not null) HoverThreatRange.Text = "";
        if (PlayerStatisticsPanel is not null) ClearThreatStatistics();
        if (ThreatCardChoice is not null)
        {
            _settingThreatChoice = true; ThreatCardChoice.SelectedIndex = -1; _settingThreatChoice = false;
            ThreatChoicePanel.Visibility = ThreatDetailPanel.Visibility = Visibility.Collapsed;
            ThreatDetailText.Text = "No current calculation.";
            UpdateThreatChoiceControls();
        }
    }
    private void UpdateThreats(CardVisionResult result)
    {
        if (!ReachEnabledInMainApp) return;
        if (ThreatCardChoice is null || _candidateCatalog is null || _lastGameStateUpdate is null) return;
        SyncThreatReference();
        var state = _lastGameStateUpdate.After;
        if (state.Phase is GamePhase.Ended or GamePhase.RoundTransition || _lastGameStateUpdate.Events.Any(item => item.Kind == "PlayPreview") ||
            _threatSnapshot is { } previous && ThreatBoardFreshness.Invalidated(previous, state))
        { _threatBoard = null; _threatSnapshot = null; _waitingForThreatBoard = true; }
        if (state.Phase == GamePhase.Playing && !state.BoardObscured && result.BoardWasScanned && !ThreatBoardFreshness.MissingScoringSide(state))
        { _threatBoard = _calculationPosition; _threatSnapshot = state; _waitingForThreatBoard = false; }
        AcceptHover(result);
        RefreshThreats(_latestHoverAt > result.SampledAt ? _latestHoverAt : result.SampledAt);
    }
    private void ThreatCardChoice_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingThreatChoice) return;
        UpdateThreatChoiceControls();
        _threatKey = null; RefreshThreats(_gameState.Current.At ?? DateTimeOffset.Now);
    }
    private void ClearThreatChoice_OnClick(object sender, RoutedEventArgs e)
    {
        ThreatCardChoice.SelectedIndex = -1; ThreatChoicePanel.Visibility = Visibility.Collapsed;
        UpdateThreatChoiceControls();
        _threatKey = null; RefreshThreats(_gameState.Current.At ?? DateTimeOffset.Now);
    }

    private void SyncThreatReference()
    {
        if (_threatReferenceId == _selectedUserDeck?.Id && ThreatCardChoice.ItemsSource is not null) return;
        _settingThreatChoice = true;
        ThreatCardChoice.ItemsSource = _selectedUserDeck?.Cards.Select(item => item.Card).OrderBy(card => card.Name).ToArray() ?? [];
        ThreatCardChoice.SelectedIndex = -1; _threatReferenceId = _selectedUserDeck?.Id;
        _settingThreatChoice = false;
        UpdateThreatChoiceControls();
    }

    private void ToggleThreatChoice_OnClick(object sender, RoutedEventArgs e)
    {
        SyncThreatReference();
        ThreatChoicePanel.Visibility = ThreatChoicePanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        ThreatDetailPanel.Visibility = Visibility.Collapsed;
        UpdateThreatChoiceControls();
    }

    private void UpdateThreatChoiceControls()
    {
        if (FollowHoverButton is null) return;
        var manual = ThreatCardChoice.SelectedItem is CardDefinition;
        FollowHoverButton.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ThreatChooseButton.ToolTip = manual ? "Manual card selected" : "Choose a reference card";
        ThreatChoiceHelp.Text = ThreatCardChoice.Items.Count == 0 ? "Pin your deck in Reference → My deck to choose a card here. Game hover still works."
            : "Choose from My deck; selection does not confirm a card is in hand.";
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        if (manual || ThreatChoicePanel.Visibility == Visibility.Visible) ThreatChooseButton.BorderBrush = accent;
        else ThreatChooseButton.ClearValue(Control.BorderBrushProperty);
        // Detailed Reach is a persistent top-level toggle; the hover banner may be
        // absent when the user chooses it, so no transient info-button state lives here.
    }

    private async void RefreshThreats(DateTimeOffset at)
    {
        if (!ReachEnabledInMainApp) return;
        if (HoverThreatTitle is null || _candidateCatalog is null || _windowClosing || _suspendReach) return;
        var card = ThreatCardChoice.SelectedItem as CardDefinition ?? _liveHover;
        if (card is null)
        {
            _threatCancellation?.Cancel(); _threatKey = null;
            _reachProgress = null;
            ClearThreatStatistics();
            _reachTitle = "Hover a card in your hand to explore its play.";
            _reachGap = CurrentScoreGap(at); _reachSummary = "";
            ThreatDetailText.Text = "No current calculation. Select or hover a card with a recent unobscured board.";
            HoverThreatRange.Text = ""; HoverThreatDetailReplies.ItemsSource = null; ThreatExtendedSummaryText.Text = ""; HoverThreatLeaderDetail.Text = "";
            HoverThreatHorizons.Visibility = Visibility.Collapsed;
            _reachStatus = "Hover a card to calculate supported interactions.";
            RefreshHoverBanner();
            return;
        }
        if (_waitingForThreatBoard || !HoverBoardCompatible())
        {
            _threatCancellation?.Cancel(); _threatKey = null; _reachProgress = null;
            _reachTitle = card.Name + " · waiting for card detection…";
            _reachSummary = _reachGap = "";
            _reachStatus = "Opponent tracking first · Reach resumes after the current play is read.";
            HoverThreatHorizons.Visibility = Visibility.Collapsed;
            HoverThreatRange.Text = ""; HoverThreatDetailReplies.ItemsSource = null;
            ThreatExtendedSummaryText.Text = HoverThreatLeaderDetail.Text = "";
            ThreatDetailText.Text = "Previous calculation invalidated by new gameplay evidence.";
            ClearThreatStatistics(); RefreshHoverBanner(); return;
        }
        var snapshot = _threatSnapshot ?? _lastGameStateUpdate?.After;
        var exactBoard = _threatBoard is not null && _threatSnapshot?.At is { } observedAt && at - observedAt <= TimeSpan.FromSeconds(20) && HoverBoardCompatible();
        var sourceBoard = exactBoard ? _threatBoard! : _calculationPosition ?? GamePosition.EmptyKnown();
        var catalogSource = _candidateCatalog;
        var definitions = catalogSource.ToArray();
        string? LeaderId(PlayerGameState player) => definitions.FirstOrDefault(item => item.Kind == CardKind.Leader &&
            item.Name.Equals(player.CurrentLeader?.Value ?? player.StartingLeader?.Value, StringComparison.OrdinalIgnoreCase))?.Id;
        var board = sourceBoard with
        {
            Zones = sourceBoard.Zones.Select(zone => zone.Zone != CardZone.Board ? zone : zone with
            {
                Cards = zone.Cards.Select(boardCard => _liveValues.ScenarioStage(zone.Side, boardCard.CardId) is { } stage
                    ? boardCard with { Charges = stage, Cooldown = 0 } : boardCard).ToImmutableArray()
            }).ToImmutableArray(),
            User = sourceBoard.User with { CurrentLeaderId = snapshot is null ? sourceBoard.User.CurrentLeaderId : LeaderId(snapshot.User) },
            Opponent = sourceBoard.Opponent with { CurrentLeaderId = snapshot is null ? sourceBoard.Opponent.CurrentLeaderId : LeaderId(snapshot.Opponent),
                StartingDeckIds = _confirmedOpponentDeck is { CardCount: >= 25 } pin ? pin.Cards.Select(item => item.Card.Id).ToImmutableHashSet() : null,
                Devotion = _lastProjection?.Devotion.State switch { ConstraintState.Confirmed => true, ConstraintState.RuledOut => false, _ => null } },
            CardValues = (sourceBoard.CardValues ?? []).Concat(_liveValues.Growing.Select(value => new PositionCardValue(value.Side, value.CardId, value.Unit.Replace(' ', '-'),
                    value.Minimum, value.Maximum)))
                .Concat(_liveValues.Stored.Select(value => new PositionCardValue(value.Side, value.SourceId, "stored-soul",
                    StoredCardId: value.Target.Id)))
                .Concat(_liveValues.SpyingGranted.Select(value => new PositionCardValue(value.Side, value.Card.Id, "spying-granted",
                    StoredCardId: value.Card.Id)))
                .Concat(_liveValues.Bounties.SelectMany(value => new[]
                {
                    new PositionCardValue(value.Side, "bounty-history", "bounty-total-base-power", value.TotalBasePower, value.TotalBasePower),
                    new PositionCardValue(value.Side, "bounty-history", "bounty-placements", value.TotalPlacements, value.TotalPlacements),
                    new PositionCardValue(value.Side, "bounty-history", "bounty-max-base-power", value.MaximumBasePower, value.MaximumBasePower)
                }))
                .Concat(Enum.GetValues<PlayerSide>().SelectMany(side => new[]
                {
                    new PositionCardValue(side, "played-history", "specials-played", _liveValues.PlayedCount(side, "specials-played"), _liveValues.PlayedCount(side, "specials-played")),
                    new PositionCardValue(side, "203113", "raid-damage", _liveValues.PlayedCount(side, "raid-damage"), _liveValues.PlayedCount(side, "raid-damage"))
                }))
                .Concat(Enum.GetValues<PlayerSide>().Where(side => _liveValues.WasPlayed(side, "203192"))
                    .Select(side => new PositionCardValue(side, "203192", "torres-played", 1, 1)))
                .Concat(Enum.GetValues<PlayerSide>().Where(side => _liveValues.WasPlayed(side, "203149"))
                    .SelectMany(side => definitions.Where(card => card.HasCategory("Naiad"))
                        .Select(card => new PositionCardValue(side, card.Id, "aucwenn-nature", 1, 1))))
                .DistinctBy(value => (value.Side, value.CardId, value.Kind)).ToImmutableArray()
        };
        var slots = _lastProjection?.Slots.Where(slot => slot.Card is not null).ToArray() ?? [];
        var deck = slots.GroupBy(slot => slot.Card!.Id).Select(group => new DeckCard(group.First().Card!, group.Count())).ToArray();
        var position = ThreatPositionBuilder.Build(board, definitions, card, _selectedUserDeck, deck, _zoneInventory.Entries,
            _userTracker.DeckBuildingObservations, _opponentTracker.DeckBuildingObservations);
        if (!exactBoard) position = position with { Assumptions = position.Assumptions.Append(
            "No fresh complete board was available; bounded card ranges and readable board reactions remain available.").ToArray() };
        position = position with { Statistics = new(_selectedUserDeck?.Faction, _opponentTracker.Faction,
            _liveValues.WasPlayed(PlayerSide.User, "203109"), _liveValues.WasPlayed(PlayerSide.Opponent, "203109")),
            UserHorizon = ReachHorizonContext.FromSnapshot(snapshot, PlayerSide.User),
            OpponentHorizon = ReachHorizonContext.FromSnapshot(snapshot, PlayerSide.Opponent) };
        var opponentHand = position.Position.Zone(PlayerSide.Opponent, CardZone.Hand).TotalCount;
        var opponentDeck = position.Position.Zone(PlayerSide.Opponent, CardZone.Deck).TotalCount;
        var candidates = slots.Where(slot => slot.State is DeckSlotState.Predicted or DeckSlotState.Selected or DeckSlotState.Pinned)
            .GroupBy(slot => slot.Card!.Id).Select(group =>
            {
                var first = group.First(); var copies = group.Count();
                var handChance = OpponentReachModel.ConditionalHandChance(opponentHand, opponentDeck, copies);
                var availability = $"{copies} remaining hypothetical cop{(copies == 1 ? "y" : "ies")}; known board, graveyard, banished and played copies excluded" +
                    (handChance is { } chance ? $"; {chance:P0} hand availability conditional on this working deck" : "; hand availability unverified");
                return new ThreatCandidate(first.Card!, group.Max(slot => slot.ModelShare), availability, copies, handChance);
            }).ToArray();
        var lead = snapshot is null ? null : ThreatAnalyzer.UserLead(snapshot);
        var key = ThreatAnalyzer.RequestKey(position, lead, candidates);
        if (key == _threatKey) { RefreshHoverBanner(); return; }
        // Reject stale results immediately, including within the rescheduling debounce window.
        _threatCancellation?.Cancel(); _threatKey = null;
        // A different hovered card must not wait behind the previous card's cooldown.
        if (card.Id == _lastThreatCardId && at - _lastThreatSearch < TimeSpan.FromMilliseconds(350)) return;
        _lastThreatCardId = card.Id;
        _lastThreatSearch = at; _threatKey = key;
        _threatCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); _threatCancellation = cancellation;
        _reachProgress = new(0, Math.Min(30, candidates.Length) + 1);
        _reachTitle = card.Name + (exactBoard ? " · calculating…" : " · estimating from partial board…"); _reachSummary = "";
        ThreatDetailText.Text = exactBoard ? "Calculating current position…" : "Calculating a board-aware bounded range from the visible partial position…";
        HoverThreatRange.Text = ""; HoverThreatDetailReplies.ItemsSource = null; ThreatExtendedSummaryText.Text = ""; HoverThreatLeaderDetail.Text = "";
        HoverThreatHorizons.Visibility = Visibility.Collapsed;
        _reachStatus = exactBoard ? "Calculating supported interactions; hidden hands remain hypotheses." : "Fresh board unavailable; calculating approximate reach instead of leaving this panel blank.";
        ClearThreatStatistics();
        _reachGap = "";
        RefreshHoverBanner();
        var ownsWorker = false;
        try
        {
            var profilePath = Path.Combine(FindDataRoot(), "cache", "create-point-profiles.json");
            // Cancel superseded requests while they wait: at most one Reach thread can calculate at a time.
            await _reachWorker.WaitAsync(cancellation.Token); ownsWorker = true;
            var report = await Task.Factory.StartNew(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                using var work = new GwentCompanion.Core.Vision.ReachWorkScope(_gameplayPriority, cancellation.Token);
                GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
                if (!ReferenceEquals(_threatAnalyzerCatalog, catalogSource))
                { _threatAnalyzer = null; _threatAnalyzerCatalog = catalogSource; }
                var analyzer = _threatAnalyzer ??= new ThreatAnalyzer(definitions, () => CreatePointProfiles.LoadOrBuild(profilePath, definitions),
                    ReachValidationProfile.ReviewedFootage);
                return analyzer.Analyze(position, lead, candidates, cancellation.Token,
                    progress => QueueReachProgress(key, card, progress, cancellation.Token));
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!CurrentReachRequest(key, card.Id, cancellation.Token)) return;
            var total = Math.Min(30, report.Summary?.CandidateCards ?? 0) + 1;
            _reachProgress = new(total, total, report);
            RenderThreatReport(card, report);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_threatKey == key) { _reachProgress = null; _reachTitle = card.Name + " · calculation unavailable"; _reachStatus = exception.Message; ThreatDetailText.Text = "Current calculation unavailable."; _threatKey = null; RefreshHoverBanner(); }
        }
        finally
        {
            if (ownsWorker) _reachWorker.Release();
            if (_threatCancellation == cancellation) _threatCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RenderThreatReport(CardDefinition card, ThreatReport report)
    {
        RenderThreatStatistics(report);
        var play = report.PlayerPlay;
        var displayedPoints = play.MaximumPoints ?? play.Approximation?.Maximum ?? play.BestModeledPoints;
        _reachTitle = card.Name + (report.PlayerHorizons is null && displayedPoints is { } points ? $" · max {points:+0;-0;0}" : "");
        RenderHorizons(report.PlayerHorizons);
        var displayedGap = report.GapAfterPlay ?? report.EstimatedGapAfterPlay;
        _reachGap = displayedGap is { } value ? value > 0 ? $"After: {value} ahead" :
            value < 0 ? $"After: {-value} behind" : "After: tied" : CurrentScoreGap(_latestHoverAt);
        var extendedSummary = report.Summary is { } summary
            ? (displayedGap is > 0 ? summary.MinimumAheadProvisions is { } minimum
                ? $"Cheapest: {minimum}p" : "No listed card gets ahead" : "Opponent replies") +
              (summary.MaximumCardSwing is { } swing ? $" · Max swing {swing:+0;-0;0}" : "") : "";
        if (report.Reach is { } reach)
            extendedSummary += $" · Reach {reach.Ease} · {reach.Reliability} confidence";
        if (string.Equals(_opponentTracker.Faction, "Syndicate", StringComparison.OrdinalIgnoreCase))
            extendedSummary += _threatBoard?.Opponent.Coins is { } coins
                ? $" · Coins {coins} · +{coins}"
                : "";
        ThreatExtendedSummaryText.Text = extendedSummary;
        var leaderText = report.Leader is { } leader
            ? (leader.Estimate.MaximumPoints is { } leaderPoints
                ? $"Leader {leaderPoints:+0;-0;0} · {leader.ChargesUsed} charge(s)" : "")
            : "";
        HoverThreatLeaderDetail.Text = leaderText;
        var replyGap = report.ReplyGap;
        var plausible = ThreatAnalyzer.CompactLikelyAnswer(report);
        _reachSummary = plausible is null ? (!report.Complete ? "Opponent replies still being evaluated…" :
            report.Replies.Count == 0 ? "No supported likely reply found" : "No evaluated reply reaches the gap") :
            $"{(report.Complete ? "Lowest likely answer" : "Answer found so far")}: {plausible.Candidate.Card.Name} · {plausible.Candidate.Card.Provision}p · {plausible.Points:+0;-0;0} swing" +
            (replyGap is { } gap ? plausible.Points > gap ? $" · {plausible.Points - gap} ahead" : " · ties" : "");
        var strongest = report.Replies.OrderByDescending(reply => reply.Points).Take(5).ToArray();
        var expensive = report.Replies.OrderByDescending(reply => reply.Candidate.Card.Provision).ThenByDescending(reply => reply.Points).Take(4).ToArray();
        var punish = report.Replies.Where(reply => reply.Candidate.Card.AbilityText?.Contains("Destroy", StringComparison.OrdinalIgnoreCase) == true ||
            reply.Candidate.Card.AbilityText?.Contains("Banish", StringComparison.OrdinalIgnoreCase) == true ||
            reply.Candidate.Card.AbilityText?.Contains("reset", StringComparison.OrdinalIgnoreCase) == true).Take(4).ToArray();
        var detailed = ThreatAnalyzer.LikelyOptions(report, 6).Concat(strongest).Concat(expensive).Concat(punish).Distinct()
            .OrderBy(reply => reply.Candidate.Card.Provision).ThenByDescending(reply => reply.Points).Take(14)
            .Select(reply => Row(reply, report, string.Join(" · ", new[] {
                punish.Contains(reply) ? "Tall punish" : null,
                strongest.Contains(reply) ? "Big swing" : null,
                reply.Candidate.Card.Provision >= 10 ? "Expensive" : null,
                (reply.Candidate.ModelShare ?? 0) >= .35 ? "Likely" : null }.Where(tag => tag is not null)!))).ToArray();
        HoverThreatDetailReplies.ItemsSource = detailed;
        ThreatRow Row(CatchUpThreat reply, ThreatReport source, string tag) => new(
            $"{reply.Candidate.Card.Provision}p · {reply.Candidate.Card.Name} · {reply.Points:+0;-0;0}" +
                (reply.Points > source.ReplyGap ? $" · {reply.Points - source.ReplyGap} ahead" : " · ties"),
            string.Join(" · ", new[] { tag, reply.UsesLeader ? $"Leader {reply.LeaderCharges} charge{(reply.LeaderCharges == 1 ? "" : "s")}" : null }
                .Where(value => !string.IsNullOrWhiteSpace(value))), "",
            reply.Horizons?.CardOnly.Compact ?? "", reply.Horizons?.OneTurn.Compact ?? "", reply.Horizons?.TwoTurns.Compact ?? "",
            reply.Horizons?.TwoTurnRelevant switch { false => .22, null => .58, _ => 1 },
            reply.Horizons is null ? Visibility.Collapsed : Visibility.Visible);
        _reachStatus = report.Reach is { } reachStatus
            ? $"Opponent reach: {reachStatus.Ease} · {reachStatus.Reliability} confidence" :
                report.Complete ? "" : "Partial evaluation · more replies may be cheaper or stronger; this does not establish a safe pass.";
        if (!report.Complete) ThreatExtendedSummaryText.Text = "Partial opponent evaluation · " +
            (report.Summary is { } partial ? $"{partial.EvaluatedCards}/{partial.CandidateCards} valued" : "calculating replies");
        ThreatDetailText.Text = string.Join("\n", play.Line.Take(12).Concat(report.Unresolved.Take(12)).Concat(report.Assumptions)
            .Concat(play.Approximation?.Notes ?? [])
            .Concat(report.Leader?.Estimate.Assumptions ?? []).Concat(report.Leader is { } leaderInfo ? [leaderInfo.Commitment] : [])
            .Concat(report.Reach is { } reachInfo ? [reachInfo.Detail] : [])
            .Concat(report.PlayerHorizons?.Notes ?? [])
            .Concat(report.Summary is { } totals ? [$"Evaluated {totals.EvaluatedCards}/{totals.CandidateCards} candidate cards ({totals.EstimatedCards} direct estimates); unsupported cards may be cheaper or stronger."] : [])
            .Concat(report.Replies.Select(reply => $"{reply.Candidate.Card.Provision}p {reply.Candidate.Card.Name}: {reply.Points:+0;-0;0} " +
                (reply.UsesLeader ? "card + leader swing" : "card swing") + $" · {reply.Note}\n{string.Join(" → ", reply.Line)}" +
                (reply.Horizons is null ? "" : "\n" + string.Join("; ", reply.Horizons.Notes)))).Distinct());
        HoverThreatRange.Text = report.PlayerStatistics is { } stats && stats.Distribution.Resolved >= .15
            ? $"Range {stats.Distribution.Quantile(.1)}–{stats.Distribution.Quantile(.9)}"
            : play.Approximation is { } approximation ? "Range " + approximation.RangeText : "";
        RefreshHoverBanner();
    }

    private void RenderHorizons(CardPointHorizons? horizons)
    {
        if (horizons is null) { HoverThreatHorizons.Visibility = Visibility.Collapsed; return; }
        HoverThreatImmediate.Text = horizons.CardOnly.Compact;
        HoverThreatOneTurn.Text = horizons.OneTurn.Compact;
        HoverThreatTwoTurn.Text = horizons.TwoTurns.Compact;
        HoverThreatTwoTurn.Opacity = horizons.TwoTurnRelevant switch { false => .22, null => .58, _ => 1 };
        var details = string.Join("\n", horizons.Notes);
        System.Windows.Automation.AutomationProperties.SetHelpText(HoverThreatHorizons, details);
        HoverThreatTwoTurn.ToolTip = horizons.TwoTurnRelevant == false
            ? "Last known card: a second turn may not occur."
            : "Cumulative second window; automatic pass-safe growth only.";
        HoverThreatHorizons.Visibility = Visibility.Visible;
    }

    private string CurrentScoreGap(DateTimeOffset at)
    {
        var lead = ThreatAnalyzer.UserLead(_gameState.Current with { At=at });
        return lead is >0 ? $"Now {lead} ahead · opponent needs {lead} to tie before your play" :
            lead is <0 ? $"Now {-lead} behind" : lead==0 ? "Currently tied" : "Current score gap unread";
    }
}
