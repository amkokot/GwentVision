using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private TacticalWatch? _tacticalWatch;
    private TacticalReport? _tacticalReport;
    private PlaysSections? _playsSections;
    private string? _selectedSummon;
    private string? _summonButtonKey;
    private string? _synergyButtonKey;
    private DateTimeOffset WatchTime => _reviewEvidencePath is null ? DateTimeOffset.Now : _opponentTime;
    private TacticalWatch? Watch => _tacticalWatch ??= _candidateCatalog is null ? null : new(_candidateCatalog);

    private void RenderTacticalWatch()
    {
        // This panel is optional. Do not repeatedly build and bind its tactical
        // presentation while the normal low-overhead three-lane layout is active.
        if (!ExperimentalOverviewEnabled && !_analysisControlTest && _reviewEvidencePath is null) return;
        if (SummonWatchList is null || Watch is not { } watch) return;
        var faction = _opponentTracker.HasStableFaction ? _opponentTracker.Faction : _confirmedOpponentDeck?.Faction;
        _tacticalReport = watch.Build(faction, _opponentKnowledge.StartingLeader, _confirmedOpponentDeck,
            _opponentEdits.Included.Values, _opponentTracker.Observations, _lastProjection?.Meta,
            _cachedDecks, _opponentKnowledge.Opportunities, WatchTime, _zoneInventory.Entries,
            _opponentKnowledge.SummonAbsence.Evidence);
        var compatiblePin = _confirmedOpponentDeck?.Faction == faction ? _confirmedOpponentDeck : null;
        _playsSections = PlaysSectionBuilder.Build(_tacticalReport, faction, _opponentTracker.Observations,
            compatiblePin, _opponentEdits.Included.Values);
        PlaysContextText.Text = faction is null ? "Faction not yet observed · neutral and seen cards only"
            : "Opponent · " + faction + (compatiblePin is null ? "" : " · pinned reference active");
        RenderSummons();
        RenderSynergies();
        RenderLiveValues();
    }

    private void RenderSummons()
    {
        if (_playsSections is null) return;
        SummonWatchList.UseDetailOnly();
        SummonWatchList.SetRows(_playsSections.Summons, true);
        // Stable alphabetical order: state changes do not re-sort the buttons.
        var rows = _playsSections.Summons.Where(row => !SummonWatchList.IsDismissed(row))
            .OrderBy(row => row.Rule.Card.Name).ToArray();
        if (!rows.Any(row => row.Rule.Card.Id == _selectedSummon)) _selectedSummon = null;
        var key = _selectedSummon + "|" + string.Join('|', rows.Select(row => $"{row.Rule.Card.Id}:{row.State}:{row.Label}:{row.Promotion}:{row.Detail}"));
        if (key != _summonButtonKey)
        {
            _summonButtonKey = key;
            SummonButtons.Children.Clear();
            foreach (var row in rows)
            {
                var selected = row.Rule.Card.Id == _selectedSummon;
                var color = new TacticalItem(row).Color;
                var button = new Button
                {
                    Style = (Style)FindResource("PlaySynergyButton"),
                    Content = new TextBlock { Text = row.Rule.Card.Name + (row.State == TacticalState.Seen ? " ✓" : row.State == TacticalState.Missed ? " !" : ""),
                        Foreground = color, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 },
                    BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(201, 167, 91)) : color
                };
                AutomationProperties.SetAutomationId(button, "Summon_" + row.Rule.Card.Id);
                AutomationProperties.SetName(button, row.Rule.Card.Name);
                AutomationProperties.SetHelpText(button, row.Label + ". " + row.Promotion + " Click to expand or collapse evidence and controls.");
                button.Click += (_, _) => { _selectedSummon = _selectedSummon == row.Rule.Card.Id ? null : row.Rule.Card.Id; RenderSummons(); };
                SummonButtons.Children.Add(button);
            }
        }
        var hidden = _playsSections.Summons.Count - rows.Length;
        RestoreSummonsButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreSummonsButton.Content = $"Restore ({hidden})";
        SummonEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummonEmptyText.Text = hidden > 0 ? "All summon watches hidden for this match." : "No relevant summon watches yet.";
        SummonWatchList.Visibility = _selectedSummon is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedSummon is not null) SummonWatchList.SelectCard(_selectedSummon);
    }

    private void RestoreSummons_OnClick(object sender, RoutedEventArgs e)
    { SummonWatchList.RestoreDismissed(); RenderSummons(); }

    private void RenderSynergies()
    {
        if (_playsSections is null) return;
        var (opponentLeader, opponentLeaderConfirmed) = SynergyLeader();
        var faction = _opponentTracker.HasStableFaction ? _opponentTracker.Faction : _confirmedOpponentDeck?.Faction;
        UserSynergySideText.Text = "YOU" + (_selectedUserDeck?.Faction is { Length: > 0 } ownFaction ? " · " + ownFaction.ToUpperInvariant() : "");
        OpponentSynergySideText.Text = "OPPONENT" + (faction is { Length: > 0 } opponentFaction ? " · " + opponentFaction.ToUpperInvariant() : "");
        var own = FactionSynergyCatalog.ForFaction(_selectedUserDeck?.Faction)
            .Select(name => LiveSynergyMeter.Read(_gameState.Current,
            PlayerSide.User, name, _selectedUserDeck?.Leader, true, _grantedMechanics.Read(PlayerSide.User, name), _candidateCatalog,
            FunctionalSynergyMeter.Read(_gameState.Current, PlayerSide.User, name, _selectedUserDeck?.Leader),
            _pirateArmor.Read(PlayerSide.User), _cultists.Read(_gameState.Current, PlayerSide.User))).ToArray();
        var opponent = FactionSynergyCatalog.ForFaction(faction).Select(name => LiveSynergyMeter.Read(
            _gameState.Current, PlayerSide.Opponent, name, opponentLeader, opponentLeaderConfirmed,
            _grantedMechanics.Read(PlayerSide.Opponent, name), _candidateCatalog,
            FunctionalSynergyMeter.Read(_gameState.Current, PlayerSide.Opponent, name, opponentLeader, opponentLeaderConfirmed),
            _pirateArmor.Read(PlayerSide.Opponent), _cultists.Read(_gameState.Current, PlayerSide.Opponent))).ToArray();
        var key = string.Join('|', own.Concat(opponent).Select(item => $"{item.Side}:{item.Name}:{item.Value}:{item.Active}:{item.Reason}")) +
            "|" + string.Join('|', _liveValues.SpyingGranted.Select(s => $"{s.Side}:{s.Card.Id}")) + "|" + string.Join('|', _liveValues.Bounties);
        if (_synergyButtonKey != key)
        {
            _synergyButtonKey = key;
            RenderMeters(UserSynergyButtons, own);
            RenderMeters(SynergyButtons, opponent);
            AddHistoryMeter(UserSynergyButtons, _selectedUserDeck?.Faction, PlayerSide.User);
            AddHistoryMeter(SynergyButtons, faction, PlayerSide.Opponent);
        }
        UserSynergyEmptyText.Visibility = own.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UserSynergyEmptyText.Text = _selectedUserDeck is null ? "Pin your deck to show its live mechanics." : "No tracked live synergy in this deck.";
        SynergyEmptyText.Visibility = opponent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SynergyEmptyText.Text = "No tracked opponent synergy yet · identify cards, leader, or pin a reference.";
        if (faction != "Nilfgaard" && _selectedUserDeck?.Faction != "Nilfgaard") SpyingMemoryPanel.Visibility = Visibility.Collapsed;
        if (faction != "Syndicate" && _selectedUserDeck?.Faction != "Syndicate") BountyMemoryPanel.Visibility = Visibility.Collapsed;
        void RenderMeters(Panel panel, IEnumerable<LiveSynergyReading> readings)
        {
            panel.Children.Clear();
            foreach (var reading in readings)
            {
                var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(reading.Active switch
                { true => "#85D7B1", false => "#E8867B", _ => "#D3DCD7" }));
                var button = new Button
                {
                    Style = (Style)FindResource("PlaySynergyButton"),
                    Content = new TextBlock { Text = $"{reading.Name} {reading.Value}", Foreground = color },
                    BorderBrush = color, Focusable = false, IsTabStop = false,
                    ToolTip = new TextBlock { Text = reading.Reason, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }
                };
                AutomationProperties.SetName(button, $"{reading.Side} {reading.Name} {reading.Value}");
                AutomationProperties.SetHelpText(button, reading.Reason);
                panel.Children.Add(button);
            }
        }
        (string? Leader, bool Confirmed) SynergyLeader()
        {
            if (!string.IsNullOrWhiteSpace(_opponentKnowledge.StartingLeader))
                return (_opponentKnowledge.StartingLeader, true);
            if (!string.IsNullOrWhiteSpace(_confirmedOpponentDeck?.Leader))
                return (_confirmedOpponentDeck.Leader, false);
            var meta = _lastProjection?.Meta;
            if (meta is null || meta.ObservedIdentities < 3 || meta.BestObservedCoverage < .5) return (null, false);
            var ranked = meta.RankedDecks.Where(item => item.Score > 0 && !string.IsNullOrWhiteSpace(item.Deck.Leader)).ToArray();
            var total = ranked.Sum(item => item.Score);
            var likely = ranked.GroupBy(item => item.Deck.Leader, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Leader: group.Key, Share: total == 0 ? 0 : group.Sum(item => item.Score) / total))
                .OrderByDescending(item => item.Share).FirstOrDefault();
            return likely.Share >= .8 ? (likely.Leader, false) : (null, false);
        }
    }
    private void AddHistoryMeter(Panel panel, string? faction, PlayerSide side)
    {
        if (faction is not ("Nilfgaard" or "Syndicate")) return;
        var spying = faction == "Nilfgaard";
        var bounty = _liveValues.Bounties.Single(b => b.Side == side);
        var label = spying ? $"Spying {_liveValues.SpyingGranted.Count(s => s.Side == side)}" :
            $"Bounty {bounty.TotalBasePower} / {bounty.TotalPlacements} / {bounty.MaximumBasePower}";
        var button = new Button { Content = label, Style = (Style)FindResource("PlaySynergyButton") };
        AutomationProperties.SetName(button, $"{side} {label}");
        AutomationProperties.SetHelpText(button, spying ? "Granted-Spying identities remembered for Artaud; click to review." :
            "Destroyed base power, placements and maximum base power; click to review.");
        button.Click += (_, _) =>
        {
            if (spying)
            {
                if (SpyingSideChoice.Items.Count == 0) SpyingSideChoice.ItemsSource = new[] { "You gave Spying", "Opponent gave Spying" };
                if (SpyingSideChoice.SelectedIndex == (side == PlayerSide.User ? 0 : 1) && SpyingMemoryPanel.Visibility == Visibility.Visible)
                { SpyingMemoryPanel.Visibility = Visibility.Collapsed; return; }
                SpyingSideChoice.SelectedIndex = side == PlayerSide.User ? 0 : 1;
                SpyingMemoryPanel.Visibility = Visibility.Visible; BountyMemoryPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (BountySideChoice.SelectedIndex == (side == PlayerSide.User ? 0 : 1) && BountyMemoryPanel.Visibility == Visibility.Visible)
                { BountyMemoryPanel.Visibility = Visibility.Collapsed; return; }
                var current = _liveValues.Bounties.Single(b => b.Side == side);
                BountySideChoice.SelectedIndex = side == PlayerSide.User ? 0 : 1;
                BountyTotalBaseText.Text = current.TotalBasePower.ToString(); BountyPlacementsText.Text = current.TotalPlacements.ToString();
                BountyMaximumBaseText.Text = current.MaximumBasePower.ToString();
                BountyMemoryPanel.Visibility = Visibility.Visible; SpyingMemoryPanel.Visibility = Visibility.Collapsed;
            }
        };
        panel.Children.Add(button);
    }
    private void TacticalAction_OnRequested(object? sender, TacticalAction action)
    {
        if (Watch is not { } watch) return;
        var id = action.Row.Rule.Card.Id;
        switch (action.Action)
        {
            case "hide": SummonWatchList.Dismiss(action.Row); _selectedSummon = null; break;
            case "close": _selectedSummon = null; break;
            case "keep": watch.Keep(id, true); break;
            case "unkeep": watch.Keep(id, false); break;
            case "met": watch.SetRuleCondition(action.Row.Rule, true, WatchTime); break;
            case "notmet": watch.SetRuleCondition(action.Row.Rule, false, WatchTime); break;
            case "clearcondition": watch.SetRuleCondition(action.Row.Rule, null, WatchTime); break;
            case "missed":
                if (MessageBox.Show(this, action.Row.Rule.Requirement + "\n\nDid you verify the complete trigger, resolution timing, available row space and that no arrival occurred? A missed capture is not enough. This will NOT exclude the card from the deck.",
                    "Review " + action.Row.Rule.Card.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                watch.Review(id, WatchReview.Missed, WatchTime); break;
            case "arrived": watch.Review(id, WatchReview.Arrived, WatchTime); break;
            case "unavailable": watch.Review(id, WatchReview.Unavailable, WatchTime); break;
            case "clearreview": watch.Review(id, null, WatchTime); _opponentKnowledge.ClearOpportunity(id); break;
        }
        RenderTacticalWatch();
    }
}
