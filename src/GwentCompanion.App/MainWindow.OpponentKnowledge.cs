using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly OpponentKnowledge _opponentKnowledge = new();
    private readonly DeckMutationLedger _deckMutations = new();
    private OpponentDeckMemoryStore? _opponentMemory;
    private string? _memoryLoadError;
    private bool _updatingKnowledge;
    private bool _knowledgeChoicesLoaded;
    private DateTimeOffset _opponentTime = DateTimeOffset.Now;
    private CreatedCardDescription? _descriptionHint;
    private DeckDefinition? _variantAnchor;
    private IReadOnlyList<DeckDefinition> _compatibleVariants = [];
    private bool _restoringMemorySelection;
    private string? _memoryRenderKey;
    private string _manualEncounterId = Guid.NewGuid().ToString("N");
    private string CurrentEncounterId => Path.GetFileName(_diagnosticSession?.CurrentSessionDirectory ?? _reviewEvidencePath ?? _manualEncounterId);
    private PostMatchMmr? _observedPostMatchMmr;
    private PostMatchRank? _observedPostMatchRank;
    private int? MatchMmr() => string.IsNullOrWhiteSpace(MatchMmrInput?.Text) ? _observedPostMatchMmr?.MatchContext :
        int.TryParse(MatchMmrInput.Text, out var rating) && rating is >= 0 and <= 10000 ? rating : null;
    private bool ObservePostMatchMmr(PostMatchMmr? reading)
    {
        if (reading is null || reading == _observedPostMatchMmr) return false;
        _observedPostMatchMmr = reading;
        MatchMmrStatus.Text = reading.Summary;
        MatchMmrStatus.Visibility = Visibility.Visible;
        FooterStatusText.Text = reading.Summary;
        return true;
    }
    private bool ObservePostMatchRank(PostMatchRank? reading)
    {
        if (reading is null || reading == _observedPostMatchRank) return false;
        _observedPostMatchRank = reading;
        MatchMmrStatus.Text = reading.Summary;
        MatchMmrStatus.Visibility = Visibility.Visible;
        FooterStatusText.Text = reading.Summary;
        return true;
    }
    private static string OpponentMemoryPath => Path.Combine(FindDataRoot(), "cache", "opponent-memory.json");

    private void InitializeKnowledgeChoices()
    {
        if (_knowledgeChoicesLoaded || OriginalLeaderChoice is null) return;
        _knowledgeChoicesLoaded = true; _updatingKnowledge = true;
        try
        {
            _candidateCatalog ??= GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json"));
            if(_candidateCatalog.FirstOrDefault(card=>card.Id=="201627") is {} shupe) _opponentKnowledge.ConfigureShupe(shupe);
            var renfri = _candidateCatalog.FirstOrDefault(card => card.Name == "Renfri" && card.CanBeInStartingDeck);
            var units = _candidateCatalog.Where(card => card.CanBeInStartingDeck && card.Kind == CardKind.Unit && card.Provision > 0).ToArray();
            var leaders = GwentOneCardCatalog.StartingLeaders(_candidateCatalog).ToArray();
            if (renfri is not null && units.Length > 0 && leaders.Length > 0)
                _opponentKnowledge.ConfigureRenfriBudget(renfri, units.Min(card => card.Provision), 150 + leaders.Max(card => card.Provision));
            InitializeZoneAudit();
            OriginalLeaderChoice.ItemsSource = new[] { new CardDefinition("", "Unknown original leader", "", CardKind.Leader, 0) }
                .Concat(GwentOneCardCatalog.StartingLeaders(_candidateCatalog).OrderBy(card => card.Name)).ToArray();
            OriginalLeaderChoice.SelectedIndex = 0;
            OpeningStratagemChoice.ItemsSource = new[] { new CardDefinition("", "Unknown opening stratagem", "", CardKind.Stratagem, 0) }
                .Concat(_candidateCatalog.Where(card => card.Kind == CardKind.Stratagem).OrderBy(card => card.Name)).ToArray();
            OpeningStratagemChoice.SelectedIndex = 0; RoundChoice.SelectedIndex = 0;
            AddedCardChoice.ItemsSource = _candidateCatalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact).OrderBy(card => card.Name).ToArray();
            ResolvedEffectChoice.ItemsSource = new[]
            {
                new EffectChoice("Shupe / Radeyah: conditional effect resolved", DeckCondition.Singleton),
                new EffectChoice("Golden Nekker / Nova: conditional effect resolved", DeckCondition.GoldenNekker),
                new EffectChoice("Renfri: leader replacement resolved", DeckCondition.Renfri),
                new EffectChoice("Devotion-only effect resolved", DeckCondition.Devotion),
                new EffectChoice("Musicians started on ranged at game start", DeckCondition.Musicians, "202200"),
                new EffectChoice("Rioghan was in the opening graveyard", CardId: "203042"),
                new EffectChoice("Eudora's infused Zoltan spawned her", CardId: "203280"),
                new EffectChoice("Heulyn's five generated opening Humans verified", CardId: "203278"),
                new EffectChoice("Sunset Wanderers: distinctive hand movement verified (not an ordinary draw)", CardId: "202953"),
                new EffectChoice("Roach absent after gold play; row space verified", CardId: "112210", MissingTrigger: "a gold play"),
                new EffectChoice("Aelirenn absent at turn end with 5 Elves + melee space", CardId: "142211", MissingTrigger: "5 Elves at turn end"),
                new EffectChoice("Affan absent after leader exhausted; row space verified", CardId: "202445", MissingTrigger: "leader exhaustion"),
                new EffectChoice("Radovid absent: Devotion + empty leader + melee space", CardId: "203282", MissingTrigger: "Devotion leader exhaustion"),
                new EffectChoice("Winter Queen absent: turn end, Frost on both rows + space", CardId: "202608", MissingTrigger: "Frost on both rows at turn end"),
                new EffectChoice("Anglerfish absent: turn end, Rain/Storm on both rows + space", CardId: "203217", MissingTrigger: "Rain/Storm on both rows at turn end"),
                new EffectChoice("Flying Redanian absent: turn end, Hoard 9 + row space", CardId: "202367", MissingTrigger: "Hoard 9 at turn end"),
                new EffectChoice("Sewer Raiders: no extra copy after Hoard 4 deploy + space", CardId: "202334", MissingTrigger: "Hoard 4 deploy"),
                new EffectChoice("Wild Hunt Rider: no extra copy after Dominance deploy + space", CardId: "132310", MissingTrigger: "Dominance deploy"),
            };
            ResolvedEffectChoice.SelectedIndex = 0;
        }
        catch (Exception exception) { OpponentKnowledgeStatus.Text = exception.Message; }
        finally { _updatingKnowledge = false; }
    }

    private OpponentProvisionBudget CurrentOpponentBudget()
    {
        var evidence = _opponentTracker.DeckBuildingObservations;
        var rules = _opponentKnowledge.Assess(evidence);
        int? capacity = _opponentKnowledge.LeaderBonus is { } bonus ? 150 + bonus : null;
        var source = "confirmed original leader";
        if (capacity is null && _confirmedOpponentDeck is { } pin)
        { capacity = 150 + pin.LeaderProvisionBonus; source = "pinned leader ASSUMPTION"; }
        if (capacity is null && _candidateCatalog is not null)
        {
            var leaders = GwentOneCardCatalog.StartingLeaders(_candidateCatalog)
                .Where(card => !_opponentTracker.HasStableFaction || card.Faction == _opponentTracker.Faction).ToArray();
            if (leaders.Length > 0) { capacity = 150 + leaders.Max(card => card.Provision); source = "maximum possible starting-leader allowance (leader unknown)"; }
        }
        return OpponentProvisionCalculator.Calculate(evidence, capacity, _opponentKnowledge.MinimumSize(evidence),
            rules.Musicians?.State is ConstraintState.Confirmed or ConstraintState.Likely,
            _opponentKnowledge.Round, _opponentKnowledge.OpponentHand, source, _candidateCatalog);
    }

    private void RenderOpponentKnowledge()
    {
        if (OpponentClueText is null) return;
        InitializeKnowledgeChoices();
        var budget = CurrentOpponentBudget();
        OpponentProvisionText.Text = budget.Summary;
        OpponentProvisionText.ToolTip = budget.Conflict
            ? "Observed cards conflict with the starting budget. Review card origins and leader."
            : "Starting-deck budget estimate, not the current hand or draw pile.";
        System.Windows.Automation.AutomationProperties.SetHelpText(OpponentProvisionText, budget.Detail);
        OpponentProvisionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            budget.Conflict ? "#F2AF60" : _opponentKnowledge.Round == 3 ? "#E5C77E" : "#AEB7C0"));
        OpponentProvisionText.FontWeight = _opponentKnowledge.Round == 3 ? FontWeights.SemiBold : FontWeights.Normal;
        var clues = _opponentKnowledge.Clues(_opponentTracker.Observations, _confirmedOpponentDeck, _lastProjection?.Meta, _opponentTime);
        OpponentClues.ItemsSource = clues;
        OpponentClueText.Text = string.Join(" · ", clues.Take(2).Select(item => item.Text));
        OpponentClueText.ToolTip = clues.Count == 0 ? null : "Open Advanced information for clue details.";
        System.Windows.Automation.AutomationProperties.SetHelpText(OpponentClueText, string.Join("\n\n", clues.Select(item => item.Text + "\n" + item.Detail)));
        OpponentClueText.Visibility = clues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DeckChangesPanel.Visibility = Visibility.Visible; // Manual visible reveals must remain accessible before a source is recognized.
        DeckChangesText.Text = string.Join("\n\n", _deckMutations.Changes.Select(item =>
            $"{item.AffectedSide} · {item.SourceName}: " + (item.AddedCardName is { } card ? card + (item.IsDeckReveal ? " (seen in deck; not a play)" : item.Confirmed ? " (addition confirmed)" : " (possible addition)") : item.Description)));
        SaveLearnedButton.Content = _opponentKnowledge.Ended ? "Match ended · Save…" : "Save learned…";
        RenderVariants(); RenderOpponentMemories(); RenderTacticalWatch(); RenderDeckRuleBadges();
    }

    private void RenderVariants()
    {
        if (_confirmedOpponentDeck is null) { _variantAnchor = null; VariantPanel.Visibility = Visibility.Collapsed; return; }
        if (_variantAnchor is null || DeckVariants.Replacements(_variantAnchor, _confirmedOpponentDeck) > 2 ||
            _variantAnchor.Leader != _confirmedOpponentDeck.Leader || _variantAnchor.Faction != _confirmedOpponentDeck.Faction) _variantAnchor = _confirmedOpponentDeck;
        _compatibleVariants = DeckVariants.CompatibleFamily(_variantAnchor, _cachedDecks, _opponentTracker.DeckBuildingObservations,
            _opponentKnowledge.Assess(_opponentTracker.DeckBuildingObservations));
        var index = _compatibleVariants.ToList().FindIndex(deck => deck.Id == _confirmedOpponentDeck.Id);
        VariantPanel.Visibility = Visibility.Visible;
        VariantSummary.Text = _compatibleVariants.Count == 0 ? "No exact variant remains; pin is a loose reference" :
            index < 0 ? $"Pinned variant conflicts · {_compatibleVariants.Count} alternatives" :
                $"Variant {index + 1}/{_compatibleVariants.Count} · up to 2 card substitutions";
        VariantSummary.ToolTip = "Compatible same-leader lists. Arrows change the reference, not observed evidence.";
        PreviousVariant.IsEnabled = NextVariant.IsEnabled = _compatibleVariants.Count > 1 || index < 0 && _compatibleVariants.Count > 0;
    }
    private void StepVariant(int direction)
    {
        if (_compatibleVariants.Count == 0) return;
        var index = _compatibleVariants.ToList().FindIndex(deck => deck.Id == _confirmedOpponentDeck?.Id);
        _confirmedOpponentDeck = _compatibleVariants[index < 0 ? 0 : (index + direction + _compatibleVariants.Count) % _compatibleVariants.Count];
        RenderLiveInference(); PersistCurrentMatch();
    }
    private void PreviousVariant_OnClick(object sender, RoutedEventArgs e) => StepVariant(-1);
    private void NextVariant_OnClick(object sender, RoutedEventArgs e) => StepVariant(1);

    private LearnedOpponentEncounter CurrentEncounter() => new(
        CurrentEncounterId, _opponentTime,
        _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null, _opponentKnowledge.StartingLeader,
        _opponentKnowledge.LeaderBonus, _opponentKnowledge.StartingStratagemId, _opponentKnowledge.StartingSize,
        _opponentKnowledge.MinimumSize(_opponentTracker.DeckBuildingObservations), _opponentTracker.DeckBuildingObservations.ToArray(),
        _opponentKnowledge.Resolved.Where(item => !item.Evidence.StartsWith("Musicians recognized", StringComparison.Ordinal)).ToArray(),
        CurrentOpponentBudget(), _deckMutations.Changes.ToArray(), _zones.Entries.ToArray(), _hiddenTraps.Entries.ToArray(), MatchMmr(), _observedPostMatchMmr,
        CompositionClues: _deckCompositionClues.Where(item => item.Key.Side == PlayerSide.Opponent).Select(item => item.Value).ToArray(),
        SequenceEvidence: _opponentKnowledge.Sequences.Evidence, PostMatchRank: _observedPostMatchRank);

    private void RenderOpponentMemories()
    {
        if (_opponentMemory is null && _memoryLoadError is null)
        {
            try { _opponentMemory = OpponentDeckMemoryStore.Load(OpponentMemoryPath); }
            catch (Exception exception) { _memoryLoadError = exception.Message; }
        }
        if (_memoryLoadError is not null) { MemoryStatusText.Text = "Memory preserved but unavailable: " + _memoryLoadError; return; }
        var selected = (OpponentMemoryList.SelectedItem as LearnedDeckMatch)?.Deck.Id;
        var encounter = CurrentEncounter();
        var key = string.Join('|', encounter.Cards.Select(item => item.Card.Id + ":" + item.ObservedCopies)) +
            $"/{encounter.StartingLeader}/{encounter.StratagemId}/{encounter.StartingSize}/{encounter.MinimumSize}/" +
            string.Join(',', encounter.Conditions.Select(item => item.Condition)) + "/" +
            string.Join(',', _opponentMemory!.Records.Select(item => item.Id + item.UpdatedAt.ToString("O")));
        if (key != _memoryRenderKey)
        {
            _memoryRenderKey = key;
            var matches = _opponentMemory.FindMatches(encounter);
            _restoringMemorySelection = true;
            OpponentMemoryList.ItemsSource = matches;
            OpponentMemoryList.SelectedItem = matches.FirstOrDefault(item => item.Deck.Id == selected);
            _restoringMemorySelection = false;
        }
        MemoryStatusText.Text = (_opponentKnowledge.Ended ? "Match ended · save/review this encounter. " : "") +
            $"{encounter.Cards.Sum(item => item.ObservedCopies)} original copy/copies learned · {encounter.Budget.ObservedFloor}p · {_opponentMemory.Records.Count} saved memories.";
    }
    private void OpenOpponentMemory_OnClick(object sender, RoutedEventArgs e) => ShowPage(UiPage.Memory);
    private void SaveOpponentMemory_OnClick(object sender, RoutedEventArgs e) => SaveOpponentMemory(false);
    private void MergeOpponentMemory_OnClick(object sender, RoutedEventArgs e) => SaveOpponentMemory(true);
    private void SaveOpponentMemory(bool merge)
    {
        if (_autoEncounterBusy) { MemoryStatusText.Text = "Auto-save is finishing; try again in a moment."; return; }
        if (_reviewEvidencePath is not null) { MemoryStatusText.Text = "Offline review is read-only; no library records changed."; return; }
        if (_opponentMemory is null) return;
        if (!string.IsNullOrWhiteSpace(MatchMmrInput.Text) && MatchMmr() is null)
        { MemoryStatusText.Text = "Enter a whole-number faction MMR (0–10000), or leave it blank."; return; }
        var selected = OpponentMemoryList.SelectedItem as LearnedDeckMatch;
        if (merge && selected is null) { MemoryStatusText.Text = "Select the previous encounter to extend first."; return; }
        try
        {
            // Reload before writing to avoid stale UI copies clobbering newer records.
            var fresh = OpponentDeckMemoryStore.Load(OpponentMemoryPath);
            var record = fresh.Record(CurrentEncounter(), MemoryNameInput.Text, merge ? selected!.Deck.Id : null,
                MemoryCompleteChoice.IsChecked == true, !merge ? selected?.Deck.Id : null);
            fresh.Save(OpponentMemoryPath); _opponentMemory = fresh;
            if (record.Complete && _libraryReady)
            {
                var definition = new OpponentEncounterPrior([record]).CompleteDecks.Single();
                definition = definition with { Patches = DeckOccurrences.Tags(definition.Occurrences) };
                MergeLibrary([definition]);
            }
            RenderOpponentMemories(); RefreshDeckList(); RenderDeckProjection();
            MemoryStatusText.Text = $"Saved {record.Name} · faced {record.EncounterCount}× · {(record.Complete ? "reviewed complete" : "INCOMPLETE")} · {record.Budget.Summary}.";
        }
        catch (Exception exception) { MemoryStatusText.Text = "Not saved: " + exception.Message; }
    }
    private void OpponentMemoryList_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryDetailText is null || OpponentMemoryList.SelectedItem is not LearnedDeckMatch match) return;
        if (!_restoringMemorySelection) MemoryNameInput.Text = match.Deck.Name;
        var ratings = match.Deck.Encounters.Where(e => e.MatchMmr is not null).Select(e => e.MatchMmr!.Value).ToArray();
        MemoryDetailText.Text = $"{(match.Deck.Complete ? "Reviewed complete" : "INCOMPLETE")} · faced {match.Deck.EncounterCount}× · last {match.Deck.LastSeen:yyyy-MM-dd} · {match.Deck.Leader ?? "leader unknown"}\n" +
            "Patches: " + string.Join(", ", match.Deck.Encounters.GroupBy(e => e.Patch ?? DeckPatchMetadata.Current(e.At).Label).Select(g => g.Key + " ×" + g.Select(e => e.SessionId).Distinct().Count())) + "\n" +
            (ratings.Length == 0 ? "MMR unknown · " : $"Recorded faction MMR {ratings.Min()}–{ratings.Max()} · ") + match.Deck.Budget.Summary;
        var memoryDetail = $"First encountered {match.Deck.FirstSeen:g}; last encountered {match.Deck.LastSeen:g}.\n" + match.Deck.Budget.Detail +
            "\n" + string.Join("\n", match.Deck.Encounters.Where(e => e.PostMatchMmr is not null).Select(e => $"{e.At:g}: {e.PostMatchMmr!.Summary}"));
        MemoryDetailText.ToolTip = "Encounter dates and starting-budget history.";
        System.Windows.Automation.AutomationProperties.SetHelpText(MemoryDetailText, memoryDetail);
    }
    private void InspectMemory_OnClick(object sender, RoutedEventArgs e)
    {
        if (OpponentMemoryList.SelectedItem is not LearnedDeckMatch match) return;
        var deck = match.Deck;
        var projection = new OpponentDeckProjector().Build([], deck.Cards, deck.Faction, minimumSize: deck.StartingSize ?? deck.MinimumSize);
        var window = new Window { Owner = this, Title = deck.Name + (deck.Complete ? " · learned complete" : " · INCOMPLETE"), Width = 470,
            Height = Math.Min(820, SystemParameters.WorkArea.Height - 40), MinWidth = 330, MinHeight = 330, Topmost = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var label = new TextBlock { Text = deck.Budget.Summary + "\n" + deck.Budget.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        panel.Children.Add(new Controls.DeckCardList { Rows = projection.Slots.Select(Strip).ToArray() }); window.Content = panel; window.Show();
    }
    private void UseMemoryGuesses_OnClick(object sender, RoutedEventArgs e)
    {
        if (OpponentMemoryList.SelectedItem is not LearnedDeckMatch { Possible: true } match) return;
        if (_confirmedOpponentDeck is not null) { MemoryStatusText.Text = "Clear the full-deck pin before using partial-memory guesses."; return; }
        foreach (var item in match.Deck.Cards) _opponentEdits.Include(CurrentCard(item.Card), item.ObservedCopies);
        RenderLiveInference(); PersistCurrentMatch(); ShowPage(UiPage.Deck);
    }
    private IEnumerable<DeckListItem> LearnedDeckItems() => (_opponentMemory?.Records ?? []).Select(record => new DeckListItem(
        record.Name, $"Learned opponent · {(record.Complete ? "reviewed complete" : "INCOMPLETE")}{(record.NeedsReview ? " · NEEDS REVIEW" : "")}{(record.LibraryFingerprint is not null ? " · library association" : "")} · faced {record.EncounterCount}× · " + record.Budget.Summary,
        null, null, DeckSearchCatalog.Normalize(record.Name + " " + record.Faction + " " + record.Leader + " " + string.Join(' ', record.DraftCards.Select(item => item.Card.Name))),
        FilterFaction: record.Faction, FilterLeader: record.Leader, RecordedAt: record.UpdatedAt,
        ObservedNames: record.DraftCards.Select(item => item.Card.Name).ToArray(),
        PartialSlots: record.DraftCards.Select(item => item with { Card = CurrentCard(item.Card) }).Where(item => StartingDeckRules.IsStartingCard(item.Card) &&
                (record.ReviewedCards is not null || item.Provenance == CardProvenance.Unknown || StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))).OrderBy(item => item.Card, DeckBuilderOrder.Comparer)
            .SelectMany(item => Enumerable.Range(1, Math.Max(1, item.ObservedCopies)).Select(copy =>
                new ProjectedDeckSlot(0, item.Card, copy, record.ReviewedCards is not null || item.Provenance == CardProvenance.Unknown ? DeckSlotState.Selected : DeckSlotState.Observed,
                    null, null, false, record.ReviewedCards is not null ? "User-edited partial list; raw observations retained." : item.Provenance == CardProvenance.Unknown
                        ? "Unseen saved suggestion, not observed evidence." : "Learned opponent observation.")))
            .Select((slot, index) => slot with { Position = index + 1 }).ToArray(), MemoryId: record.Id,
        DraftSourceKey: "learned:" + record.Id, FilterStratagemId: record.StratagemId));

    private void OriginalLeaderChoice_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingKnowledge || OriginalLeaderChoice.SelectedItem is not CardDefinition card) return;
        _opponentKnowledge.StartingLeader = card.Id.Length == 0 ? null : card.Name;
        _opponentKnowledge.LeaderBonus = card.Id.Length == 0 ? null : card.Provision;
        if (card.Id.Length > 0) _opponentTracker.SetFactionPrior(card.Faction);
        else _opponentTracker.ClearFactionPrior();
        RenderLiveInference(); PersistCurrentMatch();
    }
    private void OpeningStratagemChoice_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingKnowledge || OpeningStratagemChoice.SelectedItem is not CardDefinition card) return;
        _opponentKnowledge.StartingStratagemId = card.Id.Length == 0 ? null : card.Id;
        RenderLiveInference(); PersistCurrentMatch();
    }
    private void RoundChoice_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingKnowledge || OpponentProvisionText is null) return;
        _opponentKnowledge.SetRound(RoundChoice.SelectedIndex > 0 ? RoundChoice.SelectedIndex : null);
        RenderLiveInference();
    }
    private void SetStartingSize_OnClick(object sender, RoutedEventArgs e)
    {
        try { _opponentKnowledge.SetStartingSize(string.IsNullOrWhiteSpace(StartingSizeInput.Text) ? null : int.Parse(StartingSizeInput.Text)); RenderLiveInference(); }
        catch (Exception) { OpponentKnowledgeStatus.Text = "Starting size must be blank (unknown) or 25–100."; }
    }
    private void ConfirmEffect_OnClick(object sender, RoutedEventArgs e)
    {
        if (ResolvedEffectChoice.SelectedItem is not EffectChoice effect) return;
        if (effect.Condition is { } condition) _opponentKnowledge.Resolve(condition, _opponentTime, "User verified: " + effect.Label);
        if (effect.MissingTrigger is { } trigger) _opponentKnowledge.Opportunity(new(effect.CardId!, _opponentTime.AddSeconds(-5), trigger, true, true,
            effect.CardId is "202334" or "132310" ? 2 : 1));
        else if (effect.CardId is { } id && _candidateCatalog?.FirstOrDefault(card => card.Id == id) is { } card)
        {
            _opponentTracker.ConsiderDirectPlay(card, 1, _opponentTime, "User verified setup/source effect: " + effect.Label, CardProvenance.ConfirmedStartingDeck);
            if (id == "202953") _liveValues.ConfirmWanderersInHand(PlayerSide.Opponent, card);
            if (id == "203278") _deckMutations.Record(new(_opponentTime, id, card.Name, PlayerSide.Opponent,
                "User verified Heulyn setup: the opening bronze Skellige Humans were generated, not original cards.", CandidateKind: "heulyn", CreatesOutsideDeck: true));
        }
        OpponentKnowledgeStatus.Text = "Recorded: " + effect.Label; RenderLiveInference(); PersistCurrentMatch();
    }
    private void UndoEffect_OnClick(object sender, RoutedEventArgs e)
    {
        if (ResolvedEffectChoice.SelectedItem is not EffectChoice effect) return;
        if (effect.Condition is { } condition) _opponentKnowledge.ClearResolution(condition);
        if (effect.CardId is { } id)
        {
            if (id == "202953") _liveValues.ForgetGrowth(PlayerSide.Opponent, id);
            if (id == "203278") _deckMutations.RemoveReviewedHeulynSetup();
            _opponentKnowledge.ClearOpportunity(id);
            if (_opponentTracker.Observations.FirstOrDefault(item => item.Card.Id == id)?.Evidence?.StartsWith("User verified setup/source effect:", StringComparison.Ordinal) == true)
                _opponentTracker.SetProvenance(id, CardProvenance.Unknown, "User undid setup-origin confirmation.");
        }
        OpponentKnowledgeStatus.Text = "Undid: " + effect.Label;
        RenderLiveInference(); PersistCurrentMatch();
    }
    private void RecordAddition_OnClick(object sender, RoutedEventArgs e)
    {
        var card = AddedCardChoice.SelectedItem as CardDefinition ?? _candidateCatalog?.FirstOrDefault(item => item.Name.Equals(AddedCardChoice.Text, StringComparison.OrdinalIgnoreCase));
        if (card is null) { DescriptionHintText.Text = "Choose an exact card name first."; return; }
        var side = AdditionSideChoice.SelectedIndex == 1 ? PlayerSide.User : PlayerSide.Opponent;
        var reveal = DescriptionIsReveal.IsChecked == true;
        _deckMutations.Record(new(_opponentTime, _descriptionHint?.SourceId ?? "manual", _descriptionHint?.SourceName ?? "Visible effect (user verified)",
            side, "User read the visible description and verified the affected side.", card.Id, card.Name, 1, true, IsDeckReveal: reveal));
        if (reveal)
        {
            var tracker = side == PlayerSide.User ? _userTracker : _opponentTracker;
            var sight = new CardSighting(card, side, CardSightSource.History, new(0, 0, 1, 1), 0, 1, "User reviewed deck reveal, not a play.");
            var risk = _deckMutations.OriginRisk(sight, _opponentTime, tracker.Observations);
            tracker.ConsiderDirectPlay(card, 1, _opponentTime, "User verified current-deck reveal. " + (risk?.Reason ?? "Original membership probable, not certain after deck modifications."),
                risk?.Provenance ?? CardProvenance.ProbableStartingDeck);
        }
        RenderLiveInference(); PersistCurrentMatch();
    }
    private void ResetOpponentKnowledge()
    {
        _manualEncounterId = Guid.NewGuid().ToString("N");
        _autoEncounterError = null;
        _observedPostMatchMmr = null; _observedPostMatchRank = null; MatchMmrInput.Clear(); MatchMmrStatus.Text = ""; MatchMmrStatus.Visibility = Visibility.Collapsed;
        _selectedSummon = null; _summonButtonKey = null;
        SummonWatchList.ResetDismissed();
        _synergyButtonKey = null;
        _referenceFiltersManual = false;
        _opponentKnowledge.Reset(); _deckMutations.Reset(); _tacticalWatch?.Reset(); _thinningCopies.Reset(); _handCommits.Reset(); _variantAnchor = null; _descriptionHint = null;
        _zones.Reset(); _hiddenTraps.Reset(); RenderZoneAudit(); RenderHiddenTraps();
        _updatingKnowledge = true;
        OriginalLeaderChoice.SelectedIndex = 0; OpeningStratagemChoice.SelectedIndex = 0; RoundChoice.SelectedIndex = 0;
        StartingSizeInput.Clear(); MemoryCompleteChoice.IsChecked = false; MemoryNameInput.Text = "Learned opponent deck";
        AddedCardChoice.ItemsSource = _candidateCatalog?.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact).OrderBy(card => card.Name).ToArray();
        AddedCardChoice.SelectedIndex = -1; AddedCardChoice.Text = "";
        AdditionSideChoice.SelectedIndex = 0; DescriptionIsReveal.IsChecked = false;
        OpponentKnowledgeStatus.Text = ""; DescriptionHintText.Text = ""; DescriptionHintText.ToolTip = null; _updatingKnowledge = false;
    }
    private sealed record EffectChoice(string Label, DeckCondition? Condition = null, string? CardId = null, string? MissingTrigger = null);
}
