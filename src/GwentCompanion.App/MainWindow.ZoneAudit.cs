using System.Windows;
using System.Windows.Controls;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly ZoneEvidenceLedger _zones = new();
    private readonly HiddenTrapLedger _hiddenTraps = new();
    private bool _renderingZoneAudit;
    private sealed record ZoneAuditItem(ZoneCardEvidence Entry)
    { public string Label => $"{Entry.Side?.ToString() ?? "Side ?"} · {Entry.Card.Name} · {Entry.Route}"; }
    private void InitializeZoneAudit()
    {
        if (ZoneAuditCard.ItemsSource is not null || _candidateCatalog is null) return;
        ZoneAuditCard.ItemsSource = _candidateCatalog.Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact).OrderBy(card => card.Name).ToArray();
        TrapIdentityChoice.ItemsSource = _candidateCatalog.Where(card => card.HasCategory("Trap") && card.CanBeInStartingDeck).OrderBy(card => card.Name).ToArray();
        TrapIdentityChoice.SelectedIndex = 0;
        TrapTriggerChoice.ItemsSource = new[] { "Player played a face-up card", "Player played a unit on own side", "Player played a special", "Player passed",
            "Two trap-owner turn ends since set", "Three trap-owner turn ends since set", "Player played a unit with current power ≤4" }
            .Select(text => new ComboBoxItem { Content = new TextBlock { Text = text, Foreground = Controls.FactionPalette.Brush("#E8E0CC"), FontSize = 10 } }).ToArray();
        TrapTriggerChoice.SelectedIndex = 0;
    }
    private void RenderZoneAudit()
    {
        InitializeZoneAudit();
        var selected = (ZoneAuditList.SelectedItem as ZoneAuditItem)?.Entry.Key;
        _renderingZoneAudit = true;
        ZoneAuditList.ItemsSource = _zones.Entries.OrderByDescending(item => item.LastSeen).Select(item => new ZoneAuditItem(item)).ToArray();
        ZoneAuditList.SelectedItem = ZoneAuditList.Items.OfType<ZoneAuditItem>().FirstOrDefault(item => item.Entry.Key == selected);
        _renderingZoneAudit = false;
        ZoneAuditStatus.Text = $"{_zones.Entries.Count} partial identity records. Not a complete/current graveyard or a copy count. Side-unknown OCR never changes deck composition.";
    }
    private void ObserveZoneInspection(VisibleZoneInspection inspection, DateTimeOffset at)
    {
        var ownFaction = _selectedUserDeck?.Faction ?? (_userTracker.HasStableFaction ? _userTracker.Faction : null);
        var opponentFaction = _opponentTracker.HasStableFaction ? _opponentTracker.Faction : _confirmedOpponentDeck?.Faction;
        foreach (var card in inspection.Cards)
        {
            PlayerSide? side = null;
            if (ownFaction is { Length: > 0 } knownOwn && opponentFaction is { Length: > 0 } knownOpponent)
            {
                var own = FactionCompatibility.IsPlayableBy(card, knownOwn);
                var opponent = FactionCompatibility.IsPlayableBy(card, knownOpponent);
                if (own != opponent) side = opponent ? PlayerSide.Opponent : PlayerSide.User;
            }
            _zones.Record(card, side, VisibleCardZone.Graveyard, at, evidence: side is null
                ? "Exact standalone name read during graveyard inspection. Side, arrival time, route and copies are unverified."
                : $"Exact standalone name read during graveyard inspection; side inferred as {side} because the card is legal for only that known faction. Cross-faction creation/copying can invalidate this inference; copies and route remain unverified.");
        }
        RenderZoneAudit();
    }
    private void ZoneAuditList_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingZoneAudit || ZoneAuditList.SelectedItem is not ZoneAuditItem item) return;
        ZoneAuditCard.SelectedItem = _candidateCatalog?.FirstOrDefault(card => card.Id == item.Entry.Card.Id);
        ZoneAuditSide.SelectedIndex = item.Entry.Side is null ? 0 : item.Entry.Side == PlayerSide.Opponent ? 1 : 2;
        ZoneAuditRoute.SelectedIndex = (int)item.Entry.Route;
    }
    private void RecordZoneEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (ZoneAuditCard.SelectedItem is not CardDefinition card) return;
        PlayerSide? side = ZoneAuditSide.SelectedIndex == 0 ? null : ZoneAuditSide.SelectedIndex == 1 ? PlayerSide.Opponent : PlayerSide.User;
        var route = (ZoneEntryRoute)ZoneAuditRoute.SelectedIndex;
        if (route == ZoneEntryRoute.RioghanSetup && card.Id != "203042" || route == ZoneEntryRoute.HeulynSetup &&
            !(card.Faction == "Skellige" && card.Kind == CardKind.Unit && !card.IsGold && card.HasCategory("Human")))
        { ZoneAuditStatus.Text = "The chosen identity does not match that setup rule."; return; }
        if (ZoneAuditList.SelectedItem is ZoneAuditItem old && old.Entry.Card.Id == card.Id)
        { UndoZoneOwnedOrigin(old.Entry); _zones.Remove(old.Entry.Key); }
        _zones.Record(card, side, route == ZoneEntryRoute.ReturnedToDeck ? VisibleCardZone.Deck : VisibleCardZone.Graveyard,
            WatchTime, route, "User reviewed visible zone/return target; " + route);
        if (side is { } reviewedSide)
            _zoneInventory.Review(reviewedSide, card.Id, route == ZoneEntryRoute.ReturnedToDeck ? GwentCompanion.Core.GameState.CardZone.Deck :
                GwentCompanion.Core.GameState.CardZone.Graveyard, 1, WatchTime);
        Watch?.ObserveInventory(_zoneInventory.Entries, WatchTime);
        _liveValues.ObserveInventory(_zoneInventory.Entries, _gameState.Current);
        if (side == PlayerSide.Opponent && route == ZoneEntryRoute.ReturnedToDeck) Watch?.ReturnedToDeck(card.Id, WatchTime);
        if (side is { } known && route is ZoneEntryRoute.StartingOriginal or ZoneEntryRoute.RioghanSetup)
            (known == PlayerSide.Opponent ? _opponentTracker : _userTracker).ConsiderDirectPlay(card, 1, WatchTime,
                "User verified original in graveyard; not an extra play/copy.", CardProvenance.ConfirmedStartingDeck);
        RenderZoneAudit(); RenderLiveInference(); PersistCurrentMatch();
    }
    private void RemoveZoneEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (ZoneAuditList.SelectedItem is ZoneAuditItem item) { UndoZoneOwnedOrigin(item.Entry); _zones.Remove(item.Entry.Key); }
        RenderZoneAudit(); RenderLiveInference(); PersistCurrentMatch();
    }
    private void UndoZoneOwnedOrigin(ZoneCardEvidence entry)
    {
        if (entry.Side is not { } side) return;
        if (side == PlayerSide.Opponent && entry.Route == ZoneEntryRoute.ReturnedToDeck) Watch?.ClearReturn(entry.Card.Id);
        var tracker = side == PlayerSide.Opponent ? _opponentTracker : _userTracker;
        if (tracker.Observations.FirstOrDefault(item => item.Card.Id == entry.Card.Id)?.Evidence?.StartsWith("User verified original in graveyard;", StringComparison.Ordinal) == true)
            tracker.SetProvenance(entry.Card.Id, CardProvenance.Unknown, "User removed/corrected graveyard original confirmation.");
    }
    private void RenderHiddenTraps()
    {
        var index = HiddenTrapChoice.SelectedIndex;
        HiddenTrapChoice.ItemsSource = _hiddenTraps.Entries.Select(trap => new ComboBoxItem { Content = new TextBlock {
            Text = $"Trap {trap.Number} · {(trap.Removed ? "removed" : trap.RevealedId is not null ? "revealed" : "unknown face down")}", Foreground = Controls.FactionPalette.Brush("#E8E0CC") } }).ToArray();
        HiddenTrapChoice.SelectedIndex = index >= 0 && index < _hiddenTraps.Entries.Count ? index : _hiddenTraps.Entries.Count - 1;
    }
    private HiddenTrapEvidence? SelectedTrap => _hiddenTraps.Entries.ElementAtOrDefault(HiddenTrapChoice.SelectedIndex);
    private void AddHiddenTrap_OnClick(object sender, RoutedEventArgs e) { _hiddenTraps.Add(WatchTime); RenderHiddenTraps(); }
    private void RemoveHiddenTrap_OnClick(object sender, RoutedEventArgs e) { if (SelectedTrap is { } trap) _hiddenTraps.Remove(trap.Number); RenderHiddenTraps(); }
    private void HiddenTrapChoice_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrapCandidatesText is null || SelectedTrap is not { } trap || _candidateCatalog is null) return;
        TrapCandidatesText.Text = trap.Removed ? "Removed: no further flip expected." : string.Join("\n", _hiddenTraps.Candidates(trap.Number, _candidateCatalog)
            .Select(item => (item.Weakened ? "Less likely: " : "Possible: ") + item.Card.Name));
    }
    private void TrapSurvived_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTrap is not { } trap || TrapTriggerChoice.SelectedIndex < 0) return;
        _hiddenTraps.Survived(trap.Number, (TrapOpportunity)TrapTriggerChoice.SelectedIndex); HiddenTrapChoice_OnChanged(sender, null!);
    }
    private void RevealTrap_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTrap is not { } trap || TrapIdentityChoice.SelectedItem is not CardDefinition card) return;
        _hiddenTraps.Reveal(trap.Number, card); RenderHiddenTraps();
        var sight = new CardSighting(card, PlayerSide.Opponent, CardSightSource.Board, new(0, 0, 1, 1), 0, 1);
        var risk = _deckMutations.OriginRisk(sight, WatchTime, _opponentTracker.Observations);
        _opponentTracker.ConsiderDirectPlay(card, 1, WatchTime, "User reviewed trap flip; same placed card, not a second play. " + risk?.Reason,
            risk?.Provenance ?? CardProvenance.ProbableStartingDeck);
        RenderLiveInference(); PersistCurrentMatch();
    }
}
