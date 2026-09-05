using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class DeckBuilderWindow
{
    private readonly Dictionary<string, string> _cardSearchText = [];
    private readonly Dictionary<string, int> _currentProvisions = [];
    private readonly List<DraftSnapshot> _undo = [], _redo = [];
    private DraftSnapshot? _inputSnapshot;
    private DeckDefinition? _baseline;
    private bool _rebuilding;
    private bool? _shortEditor;
    private readonly Dictionary<string, double> _relatedCards = [];
    private int _relatedBasisCount;
    private RelatedCardReport? _relatedReport;
    private CardBalanceChanges _balanceChanges = CardBalanceChanges.Empty;
    private Dictionary<string, CardBalanceChange> _changedCards = [];
    private sealed record DraftSnapshot(string Name, string? Faction, CardDefinition? Leader, CardDefinition? Stratagem,
        DeckCard[] Fixed, DeckCopyKey[] Excluded, bool AutoFill, DeckDetails? Details, DeckDefinition? Baseline, string? ObservedSourceKey);

    private void ConfigureCollectionFilters()
    {
        CardKindFilter.ItemsSource = new[] { "All types", "Units", "Specials", "Artifacts" };
        CardRarityFilter.ItemsSource = new[] { "Gold + bronze", "Gold", "Bronze" };
        CardSortChoice.ItemsSource = new[] { "Provisions ↓", "Name A–Z", "Power ↓", "Recommended" };
        try { _balanceChanges = CardBalanceChanges.Load(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "gwent-one-cards.json")); }
        catch (Exception) { _balanceChanges = CardBalanceChanges.Empty; }
        _changedCards = _balanceChanges.Cards.ToDictionary(c => c.CardId);
        CardUpdateFilter.ItemsSource = new[] { "All updates", "Changed cards", "Stat buffs", "Stat nerfs", "New / mixed / reworked" };
        CardUpdateFilter.SelectedIndex = 0;
        CardKindFilter.SelectedIndex = CardRarityFilter.SelectedIndex = CardSortChoice.SelectedIndex = 0;
        AvailableCopiesOnly.IsChecked = true;
        foreach (var card in _catalog)
        {
            _cardSearchText[card.Id] = DeckSearchCatalog.Normalize(card.Name + " " + card.Kind + " " + string.Join(' ', card.Categories) + " " + card.AbilityText);
            _currentProvisions[card.Id] = card.Provision;
        }
    }
    private void TemplateSearchChanged(object sender, TextChangedEventArgs e) { if (!_rendering) RefreshTemplates(); }
    private void CollectionFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        if (sender == CardSortChoice) RefreshRelatedCards();
        RenderCollection();
    }
    private void ResetCollectionClicked(object sender, RoutedEventArgs e)
    {
        _rendering = true; CardSearch.Clear(); MinimumProvision.Clear(); MaximumProvision.Clear();
        CardUpdateFilter.SelectedIndex = 0;
        CardKindFilter.SelectedIndex = CardRarityFilter.SelectedIndex = CardSortChoice.SelectedIndex = 0;
        AvailableCopiesOnly.IsChecked = FixedBudgetOnly.IsChecked = false; _rendering = false; RenderCollection();
    }
    private void NekkerFilterClicked(object sender, RoutedEventArgs e)
    { _rendering = true; MinimumProvision.Clear(); MaximumProvision.Text = "9"; _rendering = false; RenderCollection(); }
    private void RenderFilteredCollection()
    {
        if (CollectionStatus is null) return;
        CollectionStatus.ToolTip = null;
        var minimum = 0; var maximum = int.MaxValue;
        if (!string.IsNullOrWhiteSpace(MinimumProvision.Text) && (!int.TryParse(MinimumProvision.Text, out minimum) || minimum < 0) ||
            !string.IsNullOrWhiteSpace(MaximumProvision.Text) && (!int.TryParse(MaximumProvision.Text, out maximum) || maximum < 0) || minimum > maximum)
        { Collection.Rows = Array.Empty<CardRow>(); CollectionStatus.Text = "Enter nonnegative provision bounds, minimum ≤ maximum."; return; }
        var terms = DeckSearchCatalog.Terms(CardSearch.Text);
        var fixedBudget = _result?.Leader is { } leader ? 150 + leader.Provision - _fixed.Values.Sum(c => _currentProvisions.GetValueOrDefault(c.Card.Id, c.Card.Provision) * c.Count) : (int?)null;
        var kind = CardKindFilter.SelectedIndex switch { 1 => CardKind.Unit, 2 => CardKind.Special, 3 => CardKind.Artifact, _ => (CardKind?)null };
        var eligible = _catalog.Where(c => c.CanBeInStartingDeck && c.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(c => Faction is null || FactionCompatibility.IsPlayableBy(c, Faction)).ToArray();
        var matches = eligible.Where(MatchesUpdateFilter).Where(c => (kind is null || c.Kind == kind) &&
            (CardRarityFilter.SelectedIndex != 1 || c.IsGold) && (CardRarityFilter.SelectedIndex != 2 || !c.IsGold) &&
            c.Provision >= minimum && c.Provision <= maximum && DeckSearchCatalog.Matches(_cardSearchText.GetValueOrDefault(c.Id, ""), terms))
            .Where(c => AvailableCopiesOnly.IsChecked != true || (_fixed.GetValueOrDefault(c.Id)?.Count ?? 0) < (c.IsGold ? 1 : 2))
            .Where(c => FixedBudgetOnly.IsChecked != true || fixedBudget is { } budget && c.Provision <= budget).ToArray();
        var sorted = CardSortChoice.SelectedIndex switch
        {
            1 => matches.OrderBy(c => c.Name),
            2 => matches.OrderByDescending(c => c.Power).ThenBy(c => c.Name),
            3 => matches.OrderByDescending(c => _relatedCards.GetValueOrDefault(c.Id)).ThenBy(c => c, DeckBuilderOrder.Comparer),
            _ => matches.OrderBy(c => c, DeckBuilderOrder.Comparer)
        };
        Collection.Rows = sorted.Select(c => Row(c, 0, false, true)).ToArray();
        CollectionStatus.Text = $"{matches.Length} / {eligible.Length} cards" +
            (CardUpdateFilter.SelectedIndex > 0 ? _balanceChanges.Available ? $" · {_balanceChanges.FromVersion} → {_balanceChanges.ToVersion}" : " · Run Settings → Update card data to load comparison history." : "") +
            (matches.Length == 0 ? " · No matches; try resetting filters." : "") +
            (FixedBudgetOnly.IsChecked == true && fixedBudget is null ? "\nChoose a leader to calculate a budget." : "") +
            (CardSortChoice.SelectedIndex == 3 ? _relatedBasisCount > 0 ? $" · {_relatedBasisCount} related list" + (_relatedBasisCount == 1 ? "" : "s") : " · No related library cards" : "");
        var collectionDetail = "Faction-compatible cards. Search names, categories and abilities.\n" +
            (CardSortChoice.SelectedIndex == 3 ? _relatedBasisCount > 0 ? $"Recommended: available cards from {_relatedBasisCount} complete related lists, weighted by shared provision spend. Best overlap {_relatedReport!.BestOverlap:P0}. " +
                (_relatedReport.ExactCore ? "Exact selected/core matches. " : "Similar lists provide alternatives, even for a full deck. ") +
                "Leader, stratagem and exclusions are respected. Duplicate compositions count once. Not win-rate evidence." : "No related alternatives in compatible library lists; provision order breaks unscored ties." : "Click a result to add it to your fixed core.");
        CollectionStatus.ToolTip = CardSortChoice.SelectedIndex == 3 ? "Related library lists; not win-rate evidence." : null;
        System.Windows.Automation.AutomationProperties.SetHelpText(CollectionStatus, collectionDetail);
    }
    private void RefreshRelatedCards()
    {
        _relatedCards.Clear(); _relatedBasisCount = 0;
        RecommendationFocusPanel.Visibility = _recommendationFocus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecommendationFocusText.Text = "Related to: " + string.Join(", ", _recommendationFocus.Select(c => c.Card.Name + (c.Count > 1 ? $" ×{c.Count}" : "")));
        RecommendationFocusPanel.ToolTip = null;
        System.Windows.Automation.AutomationProperties.SetHelpText(RecommendationFocusPanel, RecommendationFocusText.Text);
        // Related-list ranking walks the complete library. It is useful only when
        // that sort is visible, so ordinary post-match edits should not pay for it.
        if (CardSortChoice.SelectedIndex != 3) { _relatedReport = null; return; }
        var leader = (LeaderChoice.SelectedItem as CardChoice)?.Card;
        var stratagem = (StratagemChoice.SelectedItem as CardChoice)?.Card;
        var current = _fixed.Values.ToArray();
        _relatedReport = DeckRelatedCards.Rank(_libraryDecks, _catalog, current, _recommendationFocus.Length > 0 ? _recommendationFocus : current,
            Faction, leader?.Name, stratagem?.Id, _excluded);
        _relatedBasisCount = _relatedReport.Lists;
        foreach (var pair in _relatedReport.Scores) _relatedCards[pair.Key] = pair.Value;
    }
    private void RefreshEditorHeader()
    {
        var faction = Faction;
        var palette = FactionPalette.For(faction);
        EditorFactionName.Text = faction ?? "Choose faction";
        EditorLeaderName.Text = (LeaderChoice.SelectedItem as CardChoice)?.Card?.Name ?? "Choose leader";
        EditorStratagemName.Text = (StratagemChoice.SelectedItem as CardChoice)?.Card?.Name ?? "Not selected";
        foreach (var text in new[] { EditorFactionName, EditorLeaderName, EditorStratagemName }) text.Foreground = FactionPalette.Brush("#E5C77E");
        EditorDeckHeader.Background = FactionPalette.Brush("#101419");
        EditorDeckHeader.BorderBrush = FactionPalette.Brush("#485767");
        EditorInspector.Background = FactionPalette.Brush(palette.Surface);
        EditorInspector.BorderBrush = FactionPalette.Brush(palette.Edge);
    }
    private void CardInspected(object? sender, object item)
    {
        if (item is not CardRow row) return;
        InspectedCardName.Text = row.Card.Name;
        InspectedCardMeta.Text = $"{row.Card.Faction} · {row.Card.Kind} · {(row.Card.IsGold ? "Gold" : "Bronze")}\n" +
            $"{row.Card.Provision} provisions" + (row.Card.Kind == CardKind.Unit ? $" · {row.Card.Power} base power" : "") +
            (row.Card.Kind == CardKind.Unit && row.Card.PrintedArmor is > 0 ? $" · {row.Card.PrintedArmor} armor" : "") + "\n" + string.Join(" · ", row.Card.Categories);
        InspectedCardAbility.Text = string.IsNullOrWhiteSpace(row.Card.AbilityText) ? "No ability text in the local card catalogue." : row.Card.AbilityText;
        if (_changedCards.TryGetValue(row.Card.Id, out var change)) InspectedCardMeta.Text += "\n" + change.Summary + $" ({_balanceChanges.FromVersion} → {_balanceChanges.ToVersion})";
        InspectedCardArt.Source = _art(row.Card);
    }
    private void CardDetailsClicked(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var (content, title) in new[] { (InspectedCardName.Text, true), (InspectedCardMeta.Text, false),
                     (InspectedCardAbility.Text, false), ("DECK STATS", true), (DraftStatistics.Text, false), (TemplateDifference.Text, false) })
            panel.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, FontSize = title ? 16 : 12,
                FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 10) });
        new Window { Owner = this, Title = "Deck editor · Card and draft details", Width = 460, Height = 650, MinWidth = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }
    private bool MatchesUpdateFilter(CardDefinition card)
    {
        var change = _changedCards.GetValueOrDefault(card.Id);
        return CardUpdateFilter.SelectedIndex switch
        {
            1 => change is not null, 2 => change?.StatBuff == true, 3 => change?.StatNerf == true,
            4 => change is not null && !change.StatBuff && !change.StatNerf, _ => true,
        };
    }
    private void RenderDraftStatistics()
    {
        if (_result is not { } result) return;
        var cards = result.Cards; var spent = cards.Sum(c => c.Count * c.Card.Provision);
        var fixedCopies = _fixed.Values.Sum(c => c.Count); var total = cards.Sum(c => c.Count);
        DraftStatistics.Text = $"{fixedCopies} FIXED · {Math.Max(0, total - fixedCopies)} AUTO · {_excluded.Count} excluded copies\n" +
            $"{cards.Where(c => c.Card.IsGold).Sum(c => c.Count)} gold · {cards.Where(c => !c.Card.IsGold).Sum(c => c.Count)} bronze\n" +
            $"{cards.Where(c => c.Card.Kind == CardKind.Unit).Sum(c => c.Count)} units · {cards.Where(c => c.Card.Kind == CardKind.Special).Sum(c => c.Count)} specials · {cards.Where(c => c.Card.Kind == CardKind.Artifact).Sum(c => c.Count)} artifacts\n" +
            (result.Leader is null ? "Choose a leader to see remaining budget." : $"{150 + result.Leader.Provision - spent}p remaining · {spent}p spent") + "\n\nProvision curve (copies)\n" +
            string.Join(" · ", cards.GroupBy(c => c.Card.Provision).OrderBy(g => g.Key).Select(g => $"{g.Key}p: {g.Sum(c => c.Count)}"));
        if (_baseline is null) { TemplateDifference.Text = ""; return; }
        var before = _baseline.Cards.ToDictionary(c => c.Card.Id, c => c);
        var after = cards.ToDictionary(c => c.Card.Id, c => c);
        var additions = cards.Select(c => (c.Card.Name, Count: c.Count - (before.GetValueOrDefault(c.Card.Id)?.Count ?? 0))).Where(c => c.Count > 0).ToArray();
        var removals = _baseline.Cards.Select(c => (c.Card.Name, Count: c.Count - (after.GetValueOrDefault(c.Card.Id)?.Count ?? 0))).Where(c => c.Count > 0).ToArray();
        // Current catalogue values avoid making balance-patch differences look like card substitutions.
        var prices = _catalog.ToDictionary(c => c.Id, c => c.Provision);
        var beforeSpend = _baseline.Cards.Sum(c => prices.GetValueOrDefault(c.Card.Id, c.Card.Provision) * c.Count);
        var shared = cards.Sum(c => Math.Min(c.Count, before.GetValueOrDefault(c.Card.Id)?.Count ?? 0) * c.Card.Provision);
        var overlap = Math.Max(beforeSpend, spent) == 0 ? 0 : shared / (double)Math.Max(beforeSpend, spent);
        TemplateDifference.Text = $"Template: {_baseline.Name}\n{overlap:P1} shared provision spend\n" +
            (additions.Length == 0 && removals.Length == 0 ? "Same card composition." :
                "Added: " + (additions.Length == 0 ? "none" : string.Join(", ", additions.Select(c => $"{c.Name} ×{c.Count}"))) +
                "\nRemoved: " + (removals.Length == 0 ? "none" : string.Join(", ", removals.Select(c => $"{c.Name} ×{c.Count}")))) +
            (result.Leader?.Name != _baseline.Leader ? "\nLeader changed." : "") +
            (result.Stratagem?.Id != _baseline.Stratagem?.Id ? "\nStratagem changed." : "");
        TemplateDifference.ToolTip = null;
    }
    private DraftSnapshot CaptureInputs() => new(DeckName.Text, Faction, _explicitLeader, _explicitStratagem,
        _fixed.Values.ToArray(), _excluded.ToArray(), AutoFill.IsChecked == true, _details, _baseline, _observedSourceKey);
    private void Checkpoint() => AddUndo(CaptureInputs());
    private void CheckpointPrevious() { if (_inputSnapshot is { } previous) AddUndo(previous with { Name = DeckName.Text }); }
    private void AddUndo(DraftSnapshot snapshot)
    {
        _undo.Add(snapshot); if (_undo.Count > 60) _undo.RemoveAt(0); _redo.Clear(); RefreshDraftState();
    }
    private void UndoEditClicked(object sender, RoutedEventArgs e) => RestoreHistory(_undo, _redo);
    private void RedoEditClicked(object sender, RoutedEventArgs e) => RestoreHistory(_redo, _undo);
    private void RestoreHistory(List<DraftSnapshot> source, List<DraftSnapshot> destination)
    {
        if (source.Count == 0) return;
        ClearDraftSelection();
        destination.Add(CaptureInputs()); var snapshot = source[^1]; source.RemoveAt(source.Count - 1);
        _fill?.Cancel(); _rendering = true; _fixed.Clear(); _excluded.Clear();
        foreach (var card in snapshot.Fixed) _fixed[card.Card.Id] = card;
        _excluded.UnionWith(snapshot.Excluded); _explicitLeader = snapshot.Leader; _explicitStratagem = snapshot.Stratagem;
        _details = snapshot.Details; _baseline = snapshot.Baseline; _observedSourceKey = snapshot.ObservedSourceKey; DeckName.Text = snapshot.Name;
        FactionChoice.SelectedItem = FactionChoice.Items.Cast<Choice>().FirstOrDefault(c => c.Value == snapshot.Faction) ?? FactionChoice.Items[0];
        RefreshHeaders(); AutoFill.IsChecked = snapshot.AutoFill; _rendering = false; _dirty = true; Rebuild();
    }
    private void KeepAllClicked(object sender, RoutedEventArgs e)
    {
        if (_rebuilding || _result is not { } result) return;
        Checkpoint(); foreach (var card in result.Cards) _fixed[card.Card.Id] = card;
        _excluded.RemoveWhere(key => (_fixed.GetValueOrDefault(key.CardId)?.Count ?? 0) >= key.Copy);
        _dirty = true; Rebuild();
    }
    private void RefreshDraftState()
    {
        if (DraftStateText is null) return;
        DraftStateText.Text = (_dirty ? "Unsaved" : _baseline is null ? "New deck" : "Saved / loaded") +
            $" · {_fixed.Values.Sum(c => c.Count)} fixed" + (_excluded.Count > 0 ? $" · {_excluded.Count} excluded" : "");
        var validDeck = _result?.Leader is { } leader && DeckBuildValidation.Errors(new DeckDefinition("draft-check", DeckName.Text,
            leader.Faction, leader.Name, leader.Provision, _result.Cards, Stratagem: _result.Stratagem)).Count == 0;
        var showDraftSave = _observedSourceKey is not null || _saveObservedDraft is not null && !_rebuilding && _result is not null && !validDeck;
        ObservedDraftNotice.Visibility = SaveObservedDraftButton.Visibility = showDraftSave ? Visibility.Visible : Visibility.Collapsed;
        SaveObservedDraftButton.IsEnabled = showDraftSave && !_rebuilding && _result is not null && (IsOpponentReview || _saveObservedDraft is not null);
        ReviewLaterChoice.Visibility = IsOpponentReview ? Visibility.Visible : Visibility.Collapsed;
        EditorTitle.Text = IsOpponentReview ? "OPPONENT DECK REVIEW" : "DECK EDITOR";
        SaveObservedDraftButton.Content = IsOpponentReview ? "Save opponent list" : IsLocalDraft || _observedSourceKey is null ? "Save work in progress" : "Save draft";
        var saveHelp = IsOpponentReview
            ? "Save reviewed opponent name, cards and headers separately from raw match evidence. Partial lists may stay marked for review."
            : "Save an unfinished observed-list draft separately from raw evidence; reopen it as a manual list.";
        SaveObservedDraftButton.ToolTip = IsOpponentReview ? "Save opponent list · Ctrl+S" : "Save incomplete or provision-invalid draft · Ctrl+S";
        System.Windows.Automation.AutomationProperties.SetHelpText(SaveObservedDraftButton, saveHelp);
        ObservedDraftNotice.Text = IsOpponentReview
            ? $"Faced {_opponentReview!.Record.EncounterCount}× · Observed FIXED; proposals AUTO · click AUTO to fix, then click FIXED to remove · raw matches unchanged."
            : IsLocalDraft || _observedSourceKey is null ? "Work-in-progress draft · incomplete or provision-invalid is allowed · excluded from prediction/export until legal."
            : "Observed-list draft · raw matches unchanged.";
        SaveDeckButton.Content = CanOverwriteBaseline ? "Save as version" : "Save to library";
        SaveDeckButton.ToolTip = CanOverwriteBaseline ? "Keep the current library deck and save this card list as another version" : "Save a complete legal deck to the library";
        OverwriteDeckButton.Visibility = CanOverwriteBaseline ? Visibility.Visible : Visibility.Collapsed;
        OverwriteDeckButton.IsEnabled = CanOverwriteBaseline && SaveDeckButton.IsEnabled;
        ClearExclusionsButton.Visibility = _excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoEditButton.IsEnabled = _undo.Count > 0; RedoEditButton.IsEnabled = _redo.Count > 0;
        KeepAllButton.IsEnabled = !_rebuilding && _result is { } result && result.Cards.Sum(c => c.Count) > _fixed.Values.Sum(c => c.Count);
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase && _draftSelection.Count > 0)
        { RemoveClicked(this, e); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.F) { CardSearch.Focus(); CardSearch.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.S) { if (SaveObservedDraftButton.Visibility == Visibility.Visible && SaveObservedDraftButton.IsEnabled) SaveObservedDraftClicked(this, e); else SaveClicked(this, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not TextBoxBase && e.Key is Key.Z or Key.Y)
        { if (e.Key == Key.Z) RestoreHistory(_undo, _redo); else RestoreHistory(_redo, _undo); e.Handled = true; }
    }
    private void EditorSizeChanged(object sender, SizeChangedEventArgs e) => ApplyEditorLayout(ActualWidth, ActualHeight);
    internal void ApplyEditorLayout(double width, double height = 900)
    {
        if (EditorColumns is null) return;
        var expanded = width >= 1180;
        EditorColumns.ColumnDefinitions[3].Width = new GridLength(expanded ? 14 : 0);
        EditorColumns.ColumnDefinitions[4].Width = new GridLength(expanded ? 280 : 0);
        EditorInspector.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CardDetailsButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        var shortEditor = height < 760;
        if (shortEditor && _shortEditor != true) EditorSetup.IsExpanded = false;
        _shortEditor = shortEditor;
    }
}
