using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly LiveValueLedger _liveValues = new();
    private object? _ledgerCatalog;
    private string? _liveValuesKey;
    private LatestWorkQueue<string, (string Path, string Json), bool>? _valueWriter;
    private sealed record PendingRow(string Id, string Heading, string Detail);
    private sealed record ValueRow(string Heading, string Value, string Detail);
    private sealed record SpyingRow(string Heading);
    private static string SideLabel(PlayerSide side) => side == PlayerSide.User ? "You" : "Opp";
    private static string ValueRange(int minimum, int? maximum) => maximum is null ? $"{minimum}+" :
        minimum == maximum ? minimum.ToString() : $"{minimum}–{maximum} (conditional)";
    private static string CarryoverMaximum(int minimum, int? maximum) => maximum is null ? $"{minimum}+ (unresolved max)" :
        minimum == maximum ? maximum.Value.ToString() : $"{maximum.Value} (max)";

    private static void RenderProvisionHints(TextBlock spent, TextBlock remaining, ProvisionUsage usage, bool knownDeck, bool unknownLeader = false)
    {
        spent.ToolTip = "Minimum spent on original copies; replays/generated cards excluded.\n" +
            (knownDeck ? "Total: selected deck cost." : usage.Total is null ? "Total allowance unknown." :
                unknownLeader ? "Total: maximum allowance; leader unknown." : "Total: leader allowance, not actual deck cost.");
        remaining.ToolTip = "Upper-bound budget / unaccounted starting slots, not draw pile or hand. Approximate average." +
            (usage.AssumedSize ? $"\nAssumes {usage.CommittedCards + usage.UnaccountedCards} starting cards." : "") +
            (usage.RemainingCeiling is null ? "\nRemaining budget unknown." : "");
        // Keep the full accounting explanation available to accessibility and saved diagnostics,
        // without repeating it in every hover popup.
        AutomationProperties.SetHelpText(spent, usage.Detail);
        AutomationProperties.SetHelpText(remaining, usage.Detail);
    }

    private void RenderLiveValues()
    {
        if (UserSpentText is null) return;
        if (_candidateCatalog is not null && _ledgerCatalog != _candidateCatalog)
        {
            _ledgerCatalog = _candidateCatalog;
            LedgerSideChoice.ItemsSource = new[] { "You", "Opponent" }; LedgerSideChoice.SelectedIndex = 1;
            LedgerKindChoice.ItemsSource = new[] { "Hand boost", "Deck boost", "Banked carryover", "Current growing-card power", "Current growing damage (Aerondight / Sihil)", "Sword captured unit", "Verified Tribute refund", "Sunset: verified hand shifts (+1 each)" }; LedgerKindChoice.SelectedIndex = 0;
            LedgerCardChoice.ItemsSource = _candidateCatalog.Where(card => card.CanBeInStartingDeck).OrderBy(card => card.Name).ToArray();
            LedgerTargetChoice.ItemsSource = _candidateCatalog.Where(card => card.Kind == CardKind.Unit).DistinctBy(card => card.Name).OrderBy(card => card.Name).ToArray();
            SpyingSideChoice.ItemsSource = new[] { "You gave Spying", "Opponent gave Spying" }; SpyingSideChoice.SelectedIndex = 1;
            SpyingCardChoice.ItemsSource = _candidateCatalog.Where(card => card.Kind == CardKind.Unit && !PlayRules.Compile(card).Disloyal)
                .DistinctBy(card => card.Name).OrderBy(card => card.Name).ToArray();
        }
        var pending = _liveValues.Pending;
        string Carry(PlayerSide side) => _liveValues.CarryoverReadout(side);
        var own = LiveValueLedger.Provisions(_userTracker.Observations, _selectedUserDeck, null, _liveValues.Spent(PlayerSide.User), _liveValues.SpentCopies(PlayerSide.User));
        var opponent = LiveValueLedger.Provisions(_opponentTracker.DeckBuildingObservations, null, CurrentOpponentBudget().Capacity, _liveValues.Spent(PlayerSide.Opponent), _liveValues.SpentCopies(PlayerSide.Opponent), _opponentKnowledge.StartingSize, _opponentKnowledge.MinimumSize(_opponentTracker.DeckBuildingObservations));
        string Spend(ProvisionUsage usage) => usage.Total is { } total ? $"Spent {usage.SpentFloor} / {total}p" : $"Spent {usage.SpentFloor}p";
        UserSpentText.Text = Spend(own);
        UserRemainingText.Text = own.RemainingReadout;
        OpponentRemainingText.Text = opponent.RemainingReadout;
        OpponentSpentText.Text = Spend(opponent);
        RenderProvisionHints(UserSpentText, UserRemainingText, own, _selectedUserDeck is not null);
        RenderProvisionHints(OpponentSpentText, OpponentRemainingText, opponent, false, _opponentKnowledge.StartingLeader is null);
        UserCarryText.Text = Carry(PlayerSide.User); OpponentCarryText.Text = Carry(PlayerSide.Opponent);
        UserCarryText.ToolTip = OpponentCarryText.ToolTip = "Cautious counter: uses the highest tracked carryover value. Details retain why an effect may be conditional.";
        var rows = pending.Select(entry => new PendingRow(entry.Id, $"{SideLabel(entry.Side)} · {entry.Name} · {CarryoverMaximum(entry.Minimum, entry.Maximum)}", entry.Reason)).ToArray();
        var values = new List<ValueRow>();
        foreach (var growth in _liveValues.Growing)
            values.Add(new($"{SideLabel(growth.Side)} · {growth.Name}", ValueRange(growth.Minimum, growth.Maximum) + " " + growth.Unit,
                growth.Reason.StartsWith("Hypothetical") ? "Hypothetical off-board growth · incomplete turn coverage" : growth.Reason));
        values.AddRange(_liveValues.Stored.Select(stored => new ValueRow($"{SideLabel(stored.Side)} · Hen Gaidth Sword", "→ " + stored.Target.Name,
            "Stored soul, not carryover · " + stored.Reason)));
        var spying = _liveValues.SpyingGranted.OrderBy(value => value.Side).ThenBy(value => value.Card.Name)
            .Select(value => new SpyingRow($"{SideLabel(value.Side)} gave Spying · {value.Card.Name}")).ToArray();
        SpyingMemoryButton.Content = $"NG Spying · {spying.Length}";
        SpyingMemoryButton.ToolTip = spying.Length == 0 ? "No granted-Spying identities remembered yet" :
            $"{_liveValues.SpyingGranted.Count(value => value.Side == PlayerSide.Opponent)} opponent / {_liveValues.SpyingGranted.Count(value => value.Side == PlayerSide.User)} player identities; click to review";
        SpyingMemoryList.ItemsSource = spying;
        var bounty = _liveValues.Bounties;
        var ownBounty = bounty.Single(value => value.Side == PlayerSide.User);
        var opponentBounty = bounty.Single(value => value.Side == PlayerSide.Opponent);
        BountyMemoryButton.Content = $"Bounty · {opponentBounty.TotalBasePower} / {opponentBounty.TotalPlacements} / {opponentBounty.MaximumBasePower}";
        BountyMemoryButton.ToolTip = "Destroyed base power / Bounty placements / highest base power. Click to edit.";
        var key = JsonSerializer.Serialize(new { rows, values, spying, bounty, own, opponent });
        if (key != _liveValuesKey)
        {
            _liveValuesKey = key; CarryoverList.ItemsSource = rows; GrowingCardList.ItemsSource = values;
            RenderSynergies();
            GrowingValuesExpander.Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_lastProjection is { } projection) OpponentDeckCards.Rows = projection.Slots.Select(Strip).ToArray();
            var session = _diagnosticSession?.CurrentSessionDirectory;
            // CurrentSessionDirectory deliberately remains available after Stop so
            // post-match review can find the recording. Do not let the tracker reset
            // performed while starting the next analysis overwrite that prior
            // session's final ledger with an empty opponent state.
            if (RecordingPersistenceGate.CanWriteLiveLedger(_reviewEvidencePath,_diagnosticSession?.IsRunning==true,session))
            {
                var data = JsonSerializer.Serialize(new { Entries = _liveValues.Entries, Stored = _liveValues.Stored, Growing = _liveValues.Growing,
                    SpyingGranted = _liveValues.SpyingGranted, BountyHistory = bounty, UserProvisions = own, OpponentProvisions = opponent });
                _valueWriter ??= new(request => { File.WriteAllText(request.Path, request.Json); return true; }, _ => { },
                    error => FooterStatusText.Text = "Live ledger save failed: " + error.Message);
                _valueWriter.Request(session + key, (Path.Combine(session!, "live-value-ledger.json"), data));
            }
        }
    }
    private void RealizeCarryover_OnClick(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) { _liveValues.Settle(id, "User reviewed as realized or removed"); RenderLiveValues(); } }
    private void UndoCarryover_OnClick(object sender, RoutedEventArgs e) { _liveValues.Undo(); RenderLiveValues(); }
    private void SpyingMemory_OnClick(object sender, RoutedEventArgs e) =>
        SpyingMemoryPanel.Visibility = SpyingMemoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void AddSpyingMemory_OnClick(object sender, RoutedEventArgs e)
    {
        if (SpyingCardChoice.SelectedItem is not CardDefinition card)
        { SpyingMemoryStatus.Text = "Choose the missed non-Disloyal unit identity."; return; }
        var giver = SpyingSideChoice.SelectedIndex == 0 ? PlayerSide.User : PlayerSide.Opponent;
        _liveValues.AddSpying(giver, card);
        SpyingMemoryStatus.Text = $"Added {card.Name} to {SideLabel(giver)}'s Artaud memory.";
        _threatKey = null;
        RenderLiveValues();
        RefreshThreats(DateTimeOffset.Now);
    }
    private void BountyMemory_OnClick(object sender, RoutedEventArgs e) =>
        BountyMemoryPanel.Visibility = BountyMemoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void BountySide_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BountyMaximumBaseText is null) return;
        var side = BountySideChoice.SelectedIndex == 0 ? PlayerSide.User : PlayerSide.Opponent;
        var value = _liveValues.Bounties.Single(b => b.Side == side);
        BountyTotalBaseText.Text = value.TotalBasePower.ToString(); BountyPlacementsText.Text = value.TotalPlacements.ToString();
        BountyMaximumBaseText.Text = value.MaximumBasePower.ToString();
    }
    private void RecordBountyMemory_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BountyTotalBaseText.Text, out var total) || !int.TryParse(BountyPlacementsText.Text, out var placements) ||
            !int.TryParse(BountyMaximumBaseText.Text, out var maximum))
        { BountyMemoryStatus.Text = "Enter three whole numbers."; return; }
        var side = BountySideChoice.SelectedIndex == 0 ? PlayerSide.User : PlayerSide.Opponent;
        if (!_liveValues.SetBountyHistory(side, total, placements, maximum))
        { BountyMemoryStatus.Text = "Use 0–999; maximum destroyed base power cannot exceed the total."; return; }
        BountyMemoryStatus.Text = $"Recorded {SideLabel(side)}: total base {total}, placements {placements}, max base {maximum}.";
        _threatKey = null; RenderLiveValues(); RefreshThreats(DateTimeOffset.Now);
    }
    private void RecordLedger_OnClick(object sender, RoutedEventArgs e)
    {
        if (LedgerCardChoice.SelectedItem is not CardDefinition card || !int.TryParse(LedgerAmountText.Text, out var amount) || amount < 0 || amount > 999)
        { LedgerReviewStatus.Text = "Choose a source card and a value from 0 to 999."; return; }
        var side = LedgerSideChoice.SelectedIndex == 0 ? PlayerSide.User : PlayerSide.Opponent;
        var target = LedgerTargetChoice.SelectedItem as CardDefinition; var at = _gameState.Current.At ?? DateTimeOffset.Now;
        var id = "review:" + Guid.NewGuid().ToString("N"); var round = _gameState.Current.Round?.Value;
        switch (LedgerKindChoice.SelectedIndex)
        {
            case 7:
                if (!_liveValues.ObserveWanderersMovement(id, side, card, amount))
                { LedgerReviewStatus.Text = "Choose Sunset Wanderers; enter 1–30 separately verified start-turn movements (not draws)."; return; }
                if (side == PlayerSide.Opponent) _opponentTracker.ConsiderDirectPlay(card, 1, at,
                    "User verified distinctive Sunset Wanderers hand movement", CardProvenance.ConfirmedStartingDeck);
                RenderLiveInference(); break;
            case 0: case 1: case 2:
                var kind = LedgerKindChoice.SelectedIndex switch { 0 => PendingValueKind.HandBoost, 1 => PendingValueKind.DeckBoost,
                    _ when card.Id == "201579" => PendingValueKind.Phoenix,
                    _ when card.Kind == CardKind.Unit => PendingValueKind.Resilience, _ => PendingValueKind.LocationOrder };
                if (card.Id == "202192" && LedgerKindChoice.SelectedIndex == 2)
                { LedgerReviewStatus.Text = "Record Sword with 'Sword captured unit'; its stored soul is not carryover."; return; }
                var existing = _liveValues.Pending.Where(entry => entry.Side == side && entry.CardId == card.Id && entry.Kind == kind).ToArray();
                if (existing.Length == 1) { _liveValues.Confirm(existing[0].Id, amount, target?.Id); break; }
                if (existing.Length > 1) { LedgerReviewStatus.Text = "Multiple pending effects from this source: settle the relevant entries before recording their revised total."; return; }
                _liveValues.Add(new(id, side, card.Id, card.Name, kind,
                    amount, amount, "User reviewed pending value; optimal target conditions may still apply", at, round, target?.Id)); break;
            case 3: case 4:
                _liveValues.RecordGrowth(new(side, card.Id, card.Name, amount, amount, LedgerKindChoice.SelectedIndex == 4 ? "damage" : "power", "User reviewed current value")); break;
            case 5:
                if (card.Id != "202192" || target is null) { LedgerReviewStatus.Text = "Choose Hen Gaidth Sword as source and its captured unit as target."; return; }
                _liveValues.Store(side, target, "User reviewed captured unit"); break;
            case 6:
                var king = _candidateCatalog!.First(c => c.Id == "203100");
                if (!_liveValues.TributeRefund(id, side, amount, amount, true, at, king)) { LedgerReviewStatus.Text = "Enter 1–12 refunded Tribute coins, not Profit or unrelated coin gains."; return; }
                if (_liveValues.Growth(side, king.Id)?.Maximum > 0) _zoneInventory.Review(side, king.Id, CardZone.Deck, 1, at);
                // At zero it may already have summoned (or be waiting for row space); do not invent a current zone.
                break;
        }
        LedgerReviewStatus.Text = "Recorded. Use ✓ when value is realized/removed; Undo restores the last settlement.";
        RenderLiveValues();
    }
}
