using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class DeckBuilderWindow : Window
{
    private readonly DeckLibrary _library;
    private DeckDefinition[] _libraryDecks;
    private readonly string _path;
    private readonly CardDefinition[] _catalog;
    private readonly Action _changed;
    private readonly Func<CardDefinition, ImageSource?> _art;
    private readonly Dictionary<string, DeckCard> _fixed = [];
    private readonly HashSet<DeckCopyKey> _excluded = [];
    private readonly SemaphoreSlim _fillGate = new(1, 1);
    private CancellationTokenSource? _fill;
    private DeckAutoFillResult? _result;
    private CardRow? _selected;
    private DeckDetails? _details;
    private bool _rendering = true, _dirty, _closed;
    private CardDefinition? _explicitLeader, _explicitStratagem;
    private string? _observedSourceKey;
    private readonly Action<DeckEditorDraft>? _saveObservedDraft;
    private readonly OpponentReviewOptions? _opponentReview;
    private bool IsOpponentReview => _opponentReview is not null && _observedSourceKey == OpponentReviewDraft.SourceKey(_opponentReview.Record);
    private bool IsLocalDraft => _observedSourceKey?.StartsWith("builder-draft:", StringComparison.Ordinal) == true;
    internal bool IsReviewingOpponent(string id) => IsOpponentReview && _opponentReview!.Record.Id == id;

    public DeckBuilderWindow(DeckLibrary library, string path, IEnumerable<CardDefinition> catalog,
        DeckDefinition? template, Action changed, Func<CardDefinition, ImageSource?>? art = null,
        string? observedSourceKey = null, Action<DeckEditorDraft>? saveObservedDraft = null, OpponentReviewOptions? opponentReview = null)
    {
        _library = library; _libraryDecks = library.Decks; _path = path; _catalog = catalog.ToArray(); _changed = changed; _art = art ?? (_ => null);
        _saveObservedDraft = saveObservedDraft;
        _opponentReview = opponentReview;
        InitializeComponent();
        if (opponentReview is not null) ReviewLaterChoice.IsChecked = opponentReview.Record.NeedsReview;
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 30); Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        ConfigureCollectionFilters();
        FactionChoice.ItemsSource = new[] { new Choice("Choose / infer faction", null) }.Concat(GwentOneCardCatalog.StartingLeaders(_catalog)
            .Select(c => c.Faction).Distinct().Order().Select(f => new Choice(f, f))).ToArray();
        FactionChoice.SelectedIndex = 0; RefreshTemplates(); RefreshHeaders();
        _rendering = false;
        if (template is not null) LoadTemplateCore(template, observedSourceKey); else { AutoFill.IsChecked = true; Rebuild(); }
        Closing += ClosingBuilder;
        Closed += (_, _) => { _closed = true; _fill?.Cancel(); };
    }

    private void RefreshTemplates()
    {
        var previous = (TemplateChoice.SelectedItem as DeckTemplate)?.Deck.Id;
        var terms = DeckSearchCatalog.Terms(TemplateSearchBox.Text);
        // The unopened template picker starts blank. Avoid normalizing every card name
        // in thousands of library lists until the user actually enters a search.
        var source = terms.Length == 0 ? _libraryDecks : _libraryDecks.Where(d => DeckSearchCatalog.Matches(DeckSearchCatalog.Normalize(
            d.Name + " " + d.Faction + " " + d.Leader + " " + string.Join(' ', d.Cards.Select(c => c.Card.Name))), terms));
        var templates = source
            .OrderBy(d => d.Name).ThenBy(d => d.Leader).Select(d => new DeckTemplate(d, $"{d.Faction} · {d.Leader} · {d.CardCount} cards")).ToArray();
        TemplateChoice.ItemsSource = templates;
        TemplateChoice.SelectedItem = TemplateChoice.Items.Cast<DeckTemplate>().FirstOrDefault(t => t.Deck.Id == previous);
        TemplateResultsText.Text = templates.Length == 0 ? "No matching templates" : $"{templates.Length:N0} matching template{(templates.Length == 1 ? "" : "s")} · ↑↓ select · Enter use";
        UseTemplateButton.IsEnabled = TemplateChoice.SelectedItem is DeckTemplate;
    }
    private void OpenTemplatePopup()
    { RefreshTemplates(); TemplatePopup.IsOpen = true; }
    private void TemplateSearchFocused(object sender, KeyboardFocusChangedEventArgs e) => OpenTemplatePopup();
    private void TemplateSearchMouseDown(object sender, MouseButtonEventArgs e)
    { Dispatcher.BeginInvoke(OpenTemplatePopup, System.Windows.Threading.DispatcherPriority.Input); }
    private void TemplateSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { TemplatePopup.IsOpen = false; e.Handled = true; return; }
        if (e.Key is Key.Down or Key.Up)
        {
            if (!TemplatePopup.IsOpen) OpenTemplatePopup();
            if (TemplateChoice.Items.Count > 0)
            {
                var next = TemplateChoice.SelectedIndex < 0 ? 0 : Math.Clamp(TemplateChoice.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, TemplateChoice.Items.Count - 1);
                TemplateChoice.SelectedIndex = next; TemplateChoice.ScrollIntoView(TemplateChoice.SelectedItem);
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.Enter && TemplateChoice.SelectedItem is DeckTemplate)
        { TemplateClicked(UseTemplateButton, new RoutedEventArgs()); e.Handled = true; }
    }
    private void TemplateChoiceChanged(object sender, SelectionChangedEventArgs e) => UseTemplateButton.IsEnabled = TemplateChoice.SelectedItem is DeckTemplate;
    private void TemplateChoiceDoubleClicked(object sender, MouseButtonEventArgs e)
    { if (TemplateChoice.SelectedItem is DeckTemplate) TemplateClicked(UseTemplateButton, new RoutedEventArgs()); }
    private string? Faction => (FactionChoice.SelectedItem as Choice)?.Value;
    private void RefreshHeaders()
    {
        var faction = Faction;
        LeaderChoice.ItemsSource = new[] { new CardChoice(null, "Suggest leader") }.Concat(GwentOneCardCatalog.StartingLeaders(_catalog)
            .Where(c => faction is null || c.Faction == faction).Select(c => new CardChoice(c, c.Name))).ToArray();
        LeaderChoice.SelectedItem = LeaderChoice.Items.Cast<CardChoice>().FirstOrDefault(c => c.Card?.Id == _explicitLeader?.Id) ?? LeaderChoice.Items[0];
        StratagemChoice.ItemsSource = new[] { new CardChoice(null, "Choose / suggest stratagem") }.Concat(_catalog
            .Where(c => c.Kind == CardKind.Stratagem && (faction is null || FactionCompatibility.IsPlayableBy(c, faction)))
            .OrderBy(c => c.Name).Select(c => new CardChoice(c, c.Name))).ToArray();
        StratagemChoice.SelectedItem = StratagemChoice.Items.Cast<CardChoice>().FirstOrDefault(c => c.Card?.Id == _explicitStratagem?.Id) ?? StratagemChoice.Items[0];
    }
    private void HeaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        CheckpointPrevious();
        if (sender == FactionChoice)
        {
            if (_explicitLeader?.Faction != Faction) _explicitLeader = null;
            if (_explicitStratagem is { } stratagem && Faction is { } faction && !FactionCompatibility.IsPlayableBy(stratagem, faction)) _explicitStratagem = null;
            _rendering = true; RefreshHeaders(); _rendering = false;
        }
        else if (sender == LeaderChoice) _explicitLeader = (LeaderChoice.SelectedItem as CardChoice)?.Card;
        else _explicitStratagem = (StratagemChoice.SelectedItem as CardChoice)?.Card;
        _dirty = true; Rebuild();
    }
    private void NameChanged(object sender, TextChangedEventArgs e) { if (!_rendering) { _dirty = true; RefreshDraftState(); } }
    private void AutoFillChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        CheckpointPrevious();
        // Switching to manual mode drops suggestions, but keeps the visible deck header.
        if (AutoFill.IsChecked != true && _result is { } current)
        { _explicitLeader ??= current.Leader; _explicitStratagem ??= current.Stratagem; }
        _dirty = true; Rebuild(debounce: false);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!_rendering) RenderCollection(); }
    private void RenderCollection()
    {
        RenderFilteredCollection();
    }
    private CardRow Row(CardDefinition card, int copy, bool pinned, bool collection = false) => new(card, copy,
        collection ? "+" : copy.ToString(), card.Name, card.Provision.ToString(), collection ? ((_fixed.GetValueOrDefault(card.Id)?.Count ?? 0) >= (card.IsGold ? 1 : 2) ? "FULL" : "ADD") : pinned ? "FIXED" : "AUTO",
        string.Join(" · ", (collection ? new[] { $"{_fixed.GetValueOrDefault(card.Id)?.Count ?? 0}/{(card.IsGold ? 1 : 2)} fixed" } :
            IsOpponentReview ? new[] { copy <= OpponentReviewDraft.ObservedCopies(_opponentReview!.Record, card.Id) ? "OBSERVED COPY" :
                pinned ? "PROPOSED · FIXED BY YOU · NOT OBSERVED" : "PROPOSED · AUTO · NOT OBSERVED" } : Array.Empty<string>()).Concat(card.Categories.Take(2))),
        pinned ? "#85D7B1" : "#E5C77E", card.IsGold ? "#9C834D" : "#485767", () => _art(card), card.AbilityText ?? "",
        $"{(collection ? "Add" : IsOpponentReview && !pinned ? "Fix proposed" : "Remove")} {card.Name}, copy {copy}, {card.Provision} provisions" +
            (collection ? "" : "; Ctrl-click selects, Shift-click selects a range"));

    private DeckAutoFillResult BuildOpponentReviewProposal(IReadOnlyList<DeckCard> fixedCards, IReadOnlySet<DeckCopyKey> excluded,
        string? faction, CardDefinition? leader, CardDefinition? stratagem)
    {
        var cards = fixedCards.ToDictionary(c => c.Card.Id, c => c);
        foreach (var proposal in _baseline?.Cards ?? [])
        {
            var count = cards.GetValueOrDefault(proposal.Card.Id)?.Count ?? 0;
            while (count < proposal.Count && !excluded.Contains(new(proposal.Card.Id, count + 1))) count++;
            if (count > 0) cards[proposal.Card.Id] = new(proposal.Card, count);
        }
        var result = DeckBuilderOrder.Sort(cards.Values).ToArray();
        var draft = new DeckDefinition("opponent-review", DeckName.Text, faction ?? "", leader?.Name ?? "", leader?.Provision ?? 0,
            result, Stratagem: stratagem);
        var proposed = result.Sum(c => Math.Max(0, c.Count - (_fixed.GetValueOrDefault(c.Card.Id)?.Count ?? 0)));
        return new(result, leader, stratagem,
            $"{proposed} proposed AUTO cop{(proposed == 1 ? "y" : "ies")}. Turn off Auto-fill to hide all proposals; observed and manually fixed copies remain.",
            DeckBuildValidation.Errors(draft, archetypes: true));
    }

    private async void Rebuild(bool debounce = true)
    {
        if (_rendering || _closed) return;
        _inputSnapshot = CaptureInputs(); _result = null; _rebuilding = true; SelectedButtons(); RefreshDraftState();
        _fill?.Cancel(); var source = _fill = new CancellationTokenSource();
        var seeds = _fixed.Values.ToArray(); var excluded = _excluded.ToHashSet();
        var faction = Faction; var leader = _explicitLeader; var stratagem = _explicitStratagem;
        var enabled = AutoFill.IsChecked == true; var corpus = _libraryDecks;
        RefreshEditorHeader();
        SaveDeckButton.IsEnabled = OverwriteDeckButton.IsEnabled = ExportDeckButton.IsEnabled = RepairDeckButton.IsEnabled = false;
        BuilderStatus.Text = enabled ? "Updating suggestions…" : "Updating draft…";
        BuilderExplanation.Visibility = Visibility.Collapsed;
        BuilderReason.Text = "";
        try
        {
            DeckAutoFillResult result;
            if (enabled && IsOpponentReview)
                result = BuildOpponentReviewProposal(seeds, excluded, faction, leader, stratagem);
            else if (enabled && seeds.Length == 0 && faction is null && leader is null)
                result = new([], null, null,
                    "Choose a faction, leader, template, or faction card to start AUTO suggestions.", []);
            else if (enabled)
            {
                if (debounce) await Task.Delay(120, source.Token); // Coalesce card edits, never an explicit auto-fill click.
                await _fillGate.WaitAsync(source.Token);
                try { result = await Task.Run(() => DeckAutoFill.Build(corpus, _catalog, seeds, excluded, faction, leader, stratagem, true, source.Token), source.Token); }
                finally { _fillGate.Release(); }
            }
            else result = DeckAutoFill.Build(corpus, _catalog, seeds, excluded, faction, leader, stratagem, false, source.Token);
            if (_closed || source.IsCancellationRequested) return;
            _result = result;
            _rendering = true;
            if (Faction is null && result.Leader is { } inferred)
            { FactionChoice.SelectedItem = FactionChoice.Items.Cast<Choice>().First(c => c.Value == inferred.Faction); RefreshHeaders(); }
            LeaderChoice.SelectedItem = LeaderChoice.Items.Cast<CardChoice>().FirstOrDefault(c => c.Card?.Id == result.Leader?.Id) ?? LeaderChoice.Items[0];
            StratagemChoice.SelectedItem = StratagemChoice.Items.Cast<CardChoice>().FirstOrDefault(c => c.Card?.Id == result.Stratagem?.Id) ?? StratagemChoice.Items[0];
            _rendering = false;
            var assignedRows = DeckBuilderOrder.Sort(result.Cards).SelectMany(c => Enumerable.Range(1, c.Count)
                .Select(copy => Row(c.Card, copy, (_fixed.GetValueOrDefault(c.Card.Id)?.Count ?? 0) >= copy))).Cast<object>().ToList();
            var assignedCopies = result.Cards.Sum(c => c.Count);
            assignedRows.AddRange(Enumerable.Range(assignedCopies + 1, Math.Max(0, 25 - assignedCopies)).Select(slot => new EmptyDeckSlot(slot)));
            DraftCards.Rows = assignedRows;
            RefreshEditorHeader(); RefreshRelatedCards();
            CompositionStatus.Text = $"{result.Cards.Sum(c => c.Count)} cards · {result.Cards.Sum(c => c.Card.Provision * c.Count)} / {(result.Leader is null ? "?" : (150 + result.Leader.Provision).ToString())}p · {result.Cards.Where(c => c.Card.Kind == CardKind.Unit).Sum(c => c.Count)} units";
            BuilderStatus.Text = enabled && result.Leader is null ? result.Reason : result.Errors.Count > 0 ? string.Join("\n", result.Errors) :
                enabled ? "Review AUTO suggestions before saving." : "Ready to save.";
            BuilderReason.Text = result.Reason;
            System.Windows.Automation.AutomationProperties.SetHelpText(BuilderStatus, result.Reason);
            BuilderExplanation.Visibility = Visibility.Collapsed;
            SaveDeckButton.IsEnabled = ExportDeckButton.IsEnabled = result.Leader is not null && DeckBuildValidation.Errors(new DeckDefinition(
                "draft", DeckName.Text, result.Leader.Faction, result.Leader.Name, result.Leader.Provision, result.Cards, Stratagem: result.Stratagem)).Count == 0;
            OverwriteDeckButton.IsEnabled = SaveDeckButton.IsEnabled && CanOverwriteBaseline;
            _rebuilding = false; _inputSnapshot = CaptureInputs();
            RepairDeckButton.IsEnabled = result.Leader is not null;
            UpdateDraftHighlights(); RenderCollection(); RenderDraftStatistics(); RefreshDraftState();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closed && !source.IsCancellationRequested) { _rebuilding = false; BuilderStatus.Text = "Could not complete the draft: " + exception.Message; RefreshDraftState(); } }
    }
    private void CollectionActivated(object? sender, object item)
    {
        if (item is not CardRow row) return;
        var count = _fixed.GetValueOrDefault(row.Card.Id)?.Count ?? 0;
        if (count >= (row.Card.IsGold ? 1 : 2)) { BuilderStatus.Text = "That card is already fixed at its copy limit."; return; }
        Checkpoint(); CardInspected(sender, row);
        _fixed[row.Card.Id] = new(row.Card, count + 1); _excluded.RemoveWhere(key => key.CardId == row.Card.Id && key.Copy <= count + 1);
        if (Faction is null && row.Card.Faction != "Neutral" && row.Card.SecondaryFactions.Count == 0)
        { _rendering = true; FactionChoice.SelectedItem = FactionChoice.Items.Cast<Choice>().First(c => c.Value == row.Card.Faction); RefreshHeaders(); _rendering = false; }
        _dirty = true; Rebuild();
    }
    private void DraftActivated(object? sender, object item)
    {
        ActivateDraftCard(item, System.Windows.Input.Keyboard.Modifiers);
    }
    private void SelectedButtons()
    {
        KeepButton.IsEnabled = !_rebuilding && _selected is { Badge: "AUTO" };
        ReleaseButton.IsEnabled = !_rebuilding && _selected is { Badge: "FIXED" } && AutoFill.IsChecked == true;
        RemoveButton.IsEnabled = !_rebuilding && _draftSelection.Count > 0;
        RecommendationFocusButton.IsEnabled = !_rebuilding && _draftSelection.Count > 0;
    }
    private void KeepClicked(object sender, RoutedEventArgs e)
    { if (!_rebuilding && _selected is { Badge: "AUTO" } row) FixDraftRow(row); }
    private void FixDraftRow(CardRow row)
    {
        Checkpoint(); ClearDraftSelection();
        _fixed[row.Card.Id] = new(row.Card, row.Copy);
        _excluded.RemoveWhere(key => key.CardId == row.Card.Id && key.Copy <= row.Copy);
        _dirty = true; Rebuild(debounce: false);
    }
    private void ReleaseClicked(object sender, RoutedEventArgs e) => RemoveSelection(false);
    private void RemoveClicked(object sender, RoutedEventArgs e) => RemoveDraftRows(VisibleDraftRows.Where(r => _draftSelection.Contains(CopyKey(r))));
    private void RemoveSelection(bool exclude)
    {
        if (_rebuilding || _selected is not { } row) return;
        Checkpoint();
        var count = _fixed.GetValueOrDefault(row.Card.Id)?.Count ?? 0;
        if (row.Badge == "FIXED")
        { if (count > 1) _fixed[row.Card.Id] = new(row.Card, count - 1); else _fixed.Remove(row.Card.Id); }
        if (exclude) _excluded.Add(new(row.Card.Id, row.Badge == "FIXED" ? Math.Max(1, count) : row.Copy));
        _dirty = true; Rebuild();
    }
    private void ClearExcludedClicked(object sender, RoutedEventArgs e) { if (_excluded.Count == 0) return; Checkpoint(); _excluded.Clear(); _dirty = true; Rebuild(); }
    private bool DiscardChanges() => !_dirty || MessageBox.Show(this, "Discard the unsaved draft? Saved library decks will not be removed.", "Deck builder", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    private void TemplateClicked(object sender, RoutedEventArgs e)
    { if (TemplateChoice.SelectedItem is DeckTemplate template && DiscardChanges()) { TemplatePopup.IsOpen = false; LoadTemplate(template.Deck); } }
    private void LoadTemplate(DeckDefinition deck) => LoadTemplateCore(deck, null);
    private void LoadTemplateCore(DeckDefinition deck, string? observedSourceKey)
    {
        deck = new CurrentCardValues(_catalog).Deck(deck);
        if (_inputSnapshot is not null) Checkpoint();
        ClearDraftSelection(); _recommendationFocus = [];
        _observedSourceKey = observedSourceKey;
        _baseline = deck;
        _rendering = true; _fixed.Clear(); _excluded.Clear();
        foreach (var item in deck.Cards.Where(c => StartingDeckRules.IsStartingCard(c.Card)))
        {
            var count = IsOpponentReview ? Math.Min(item.Count, OpponentReviewDraft.ObservedCopies(_opponentReview!.Record, item.Card.Id)) : item.Count;
            if (count > 0) _fixed[item.Card.Id] = item with { Count = count };
        }
        _details = _library.Find(deck.Id)?.Details; DeckName.Text = deck.Name;
        _explicitLeader = GwentOneCardCatalog.StartingLeaders(_catalog).FirstOrDefault(c => c.Faction == deck.Faction && c.Name == deck.Leader);
        _explicitStratagem = deck.Stratagem;
        FactionChoice.SelectedItem = FactionChoice.Items.Cast<Choice>().FirstOrDefault(c => c.Value == deck.Faction) ?? FactionChoice.Items[0];
        RefreshHeaders(); AutoFill.IsChecked = IsOpponentReview; _dirty = false; _rendering = false; Rebuild();
    }
    private void NewClicked(object sender, RoutedEventArgs e)
    {
        OpenDraft(null);
    }
    internal bool OpenDraft(DeckDefinition? template, Func<bool>? confirmDiscard = null, string? observedSourceKey = null)
    {
        if (template is not null && _baseline?.Id == template.Id && _observedSourceKey == observedSourceKey && !_dirty) return true;
        if (_dirty && !(confirmDiscard?.Invoke() ?? DiscardChanges())) return false;
        if (template is not null) { LoadTemplateCore(template, observedSourceKey); return true; }
        Checkpoint(); _baseline = null;
        ClearDraftSelection(); _recommendationFocus = [];
        _observedSourceKey = null;
        _rendering = true; _fixed.Clear(); _excluded.Clear(); _details = null;
        _explicitLeader = _explicitStratagem = null; FactionChoice.SelectedIndex = 0; RefreshHeaders();
        DeckName.Text = "My new deck"; AutoFill.IsChecked = true; _dirty = false; _rendering = false; Rebuild();
        return true;
    }
    private bool CanOverwriteBaseline => _observedSourceKey is null && !IsOpponentReview && _baseline is not null && _library.Find(_baseline.Id) is not null;
    private DeckDefinition SaveLocal() => SaveLocalCore(false);
    private DeckDefinition SaveLocalCore(bool overwrite)
    {
        if (_rebuilding || _result?.Leader is not { } leader || _fill?.IsCancellationRequested == true) throw new InvalidDataException("Wait for the draft to finish and choose a leader.");
        if (string.IsNullOrWhiteSpace(DeckName.Text) || DeckName.Text.Trim().Length > 160) throw new InvalidDataException("Enter a name of 1–160 characters.");
        var deck = new DeckDefinition("builder-" + Guid.NewGuid().ToString("N"), DeckName.Text.Trim(), leader.Faction, leader.Name, leader.Provision,
            _result.Cards, Stratagem: _result.Stratagem, LastEdited: DateTimeOffset.Now);
        var errors = DeckBuildValidation.Errors(deck);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        var prior = _library.Records.FirstOrDefault(r => r.Fingerprint == DeckLibrary.Fingerprint(deck));
        if (overwrite)
        {
            if (!CanOverwriteBaseline) throw new InvalidOperationException("The deck being edited is no longer in the library.");
            deck = _library.Replace(_baseline!.Id, deck);
        }
        else if (prior is null) { _library.Merge([deck]); deck = _library.Rename(deck.Id, deck.Name); }
        else deck = _library.Rename(prior.Deck.Id, deck.Name);
        if (_details is not null && _library.Find(deck.Id)?.Details is null) _library.SetDetails(deck.Id, _details);
        _library.Save(_path); _libraryDecks = _library.Decks; _dirty = false; _baseline = deck; _changed(); RefreshTemplates(); RenderDraftStatistics(); RefreshDraftState();
        BuilderStatus.Text = overwrite ? "Overwrote the selected library deck. The previous library file is retained as .bak." :
            prior is null ? "Saved as a new library version. The original deck is unchanged." : "Saved to the library/cache. This exact composition already existed; its name was updated.";
        return deck;
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    { try { SaveLocal(); } catch (Exception exception) { BuilderStatus.Text = "Not saved: " + exception.Message; } }
    private void OverwriteClicked(object sender, RoutedEventArgs e)
    {
        if (!CanOverwriteBaseline) { BuilderStatus.Text = "Cannot overwrite: the deck being edited is no longer in the library."; return; }
        var name = _library.Find(_baseline!.Id)!.Deck.Name;
        if (MessageBox.Show(this, $"Replace ‘{name}’ with the current card list?\n\nThe previous composition, import history and export receipt will leave the active library. A .bak copy is retained when the library is saved.",
            "Overwrite library deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { SaveLocalCore(true); }
        catch (Exception exception) { BuilderStatus.Text = "Not overwritten: " + exception.Message; }
    }
    private void SaveObservedDraftClicked(object sender, RoutedEventArgs e)
    { try { SaveObservedDraftLocal(); } catch (Exception exception) { BuilderStatus.Text = "Draft not saved: " + exception.Message; } }
    private void SaveObservedDraftLocal()
    {
        if (_rebuilding || _result is null || !IsOpponentReview && _saveObservedDraft is null)
            throw new InvalidOperationException("Wait for the observed draft to finish updating.");
        if (string.IsNullOrWhiteSpace(DeckName.Text) || DeckName.Text.Trim().Length > 160)
            throw new InvalidDataException("Enter a name of 1–160 characters.");
        var sourceKey = _observedSourceKey ?? "builder-draft:" + Guid.NewGuid().ToString("N");
        var draft = new DeckEditorDraft(sourceKey, DeckName.Text.Trim(), Faction, _result.Leader?.Id,
            _result.Stratagem?.Id, DeckBuilderOrder.Sort(_result.Cards).ToArray(), DateTimeOffset.UtcNow);
        if (IsOpponentReview) _opponentReview!.Save(draft, ReviewLaterChoice.IsChecked == true);
        else _saveObservedDraft!(draft);
        _observedSourceKey = sourceKey;
        _dirty = false; RefreshDraftState();
        BuilderStatus.Text = IsOpponentReview ? "Opponent list saved. Raw match observations and encounter counts are unchanged." :
            IsLocalDraft ? "Work-in-progress saved as a Draft. It is excluded from prediction and export until legal." :
            "Draft saved. Raw match observations are unchanged; reopen from Draft in the Library.";
    }
    private void ReviewLaterChanged(object sender, RoutedEventArgs e) { if (!_rendering && IsOpponentReview) { _dirty = true; RefreshDraftState(); } }
    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var deck = SaveLocal();
            new DeckExportWindow(_library, _path, deck, _catalog, _changed) { Owner = this }.ShowDialog();
            _details = _library.Find(deck.Id)?.Details;
        }
        catch (Exception exception) { BuilderStatus.Text = "Cannot export: " + exception.Message; }
    }
    private void ClosingBuilder(object? sender, CancelEventArgs e) { if (!DiscardChanges()) e.Cancel = true; }
    private sealed record Choice(string Label, string? Value);
    private sealed record CardChoice(CardDefinition? Card, string Label);
    private sealed record DeckTemplate(DeckDefinition Deck, string Label)
    { public string Summary => $"{Deck.Faction} · {Deck.Leader} · {Deck.CardCount} cards"; }
    private sealed record CardRow(CardDefinition Card, int Copy, string Position, string Name, string Provision, string Badge, string Detail,
        string StateColor, string FrameColor, Func<ImageSource?> ArtworkLoader, string Tooltip, string AccessibleName)
    {
        private readonly Lazy<ImageSource?> _artwork = new(ArtworkLoader);
        public ImageSource? Artwork => _artwork.Value;
        public string ValueBadge => "";
        public double RowOpacity => Badge == "AUTO" ? 0.76 : 1d;
    }
    private sealed record EmptyDeckSlot(int Slot)
    {
        public string Position => "";
        public string Name => "Empty slot";
        public string Provision => "";
        public string Badge => "";
        public string ValueBadge => "";
        public string Detail => "";
        public string StateColor => "#3B4853";
        public string FrameColor => "#34414C";
        public ImageSource? Artwork => null;
        public string Tooltip => "";
        public string AccessibleName => $"Empty deck slot {Slot} of 25; no card assigned or imputed";
        public double RowOpacity => 0.48;
    }
}
