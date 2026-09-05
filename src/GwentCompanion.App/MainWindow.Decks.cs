using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private OpponentDeckProjection? _lastProjection;
    private readonly Dictionary<string, BitmapImage?> _deckArt = [];
    private readonly DeckProjectionEdits _opponentEdits = new();
    private IReadOnlyList<CardDefinition>? _candidateCatalog;
    private bool _compactPanel;

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Pages is null) return;
        UpdateWorkspaceLayout();
        var compact = ActualHeight < 760;
        if (compact == _compactPanel) return;
        _compactPanel = compact;
        BrandHeader.Margin = new Thickness(0, 0, 0, compact ? 3 : 6);
        GameStatusPanel.Margin = new Thickness(0, 0, 0, compact ? 4 : 8);
        DeckInteractionHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LibraryManagementScroll.MaxHeight = compact ? 80 : 130;
        if (_lastProjection is not null) RenderCompletedProjection();
    }

    private void RenderDeckProjection()
    {
        if (OpponentDeckCards is null) return;
        // Package hints can reference a card absent from every cached deck, so load the
        // local full catalogue before the first projection, not only the candidate tray.
        try { _candidateCatalog ??= GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json")); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException) { }
        InitializeKnowledgeChoices();
        QueueDeckProjection();
        // Cheap bookkeeping remains current while statistical work runs separately.
        RenderPlayerReferenceAndAudit();
        RenderOpponentKnowledge();
    }

    private void RenderCompletedProjection()
    {
        if (_lastProjection is not { } projection) return;
        OpponentDeckCards.Rows = projection.Slots.Select(Strip).ToArray();
        OpponentDeckSummary.Text = projection.Summary.Split(". ")[0] + "." +
            (projection.Meta.ObservedIdentities > 0 && projection.Meta.BestObservedCoverage < .5 ?
                $" Weak cache fit ({projection.Meta.BestMatchedIdentities}/{projection.Meta.ObservedIdentities})." : "");
        OpponentDeckSummary.ToolTip = "Observed copies are lower bounds, not cards remaining.";
        AutomationProperties.SetHelpText(OpponentDeckSummary, projection.Summary);
        OpponentFactionText.Text = _opponentTracker.HasStableFaction ? _opponentTracker.Faction : "Faction unresolved";
        OpponentDevotionText.Text = "DEVOTION · " + ShortState(projection.Devotion.State).ToUpperInvariant();
        OpponentDevotionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            projection.Devotion.State == ConstraintState.RuledOut ? "#F2AF60" : "#85D7B1"));
        OpponentDevotionText.ToolTip = "Starting deck has no Neutral cards.";
        AutomationProperties.SetHelpText(OpponentDevotionText, projection.Devotion.Reason);
        var rules = _opponentKnowledge.Assess(_opponentTracker.DeckBuildingObservations);
        OpponentProvisionText.Text = $"{rules.ProvisionLowerBound}p likely starting-deck floor" + (_compactPanel ? "" :
            $" · GN {ShortState(rules.GoldenNekker.State)} · Renfri {ShortState(rules.Renfri.State)} · Singleton {ShortState(rules.Shupe.State)}");
        OpponentProvisionText.ToolTip = "GN · Renfri · Singleton checks";
        AutomationProperties.SetHelpText(OpponentProvisionText, rules.GoldenNekker.Reason + "\n" + rules.Renfri.Reason + "\n" + rules.Shupe.Reason);
        if (_confirmedOpponentDeck is not null)
        {
            var remaining = 150 + _confirmedOpponentDeck.LeaderProvisionBonus - rules.ProvisionLowerBound;
            OpponentProvisionText.Text += $" · {Math.Max(0, remaining)}p unaccounted under pinned leader";
        }
        OpponentPinSummary.Text = _confirmedOpponentDeck is null ? "No pinned list · edit individual assumptions below" :
            $"Pin: {_confirmedOpponentDeck.Name} · {projection.PinDeviations} deviation(s) · influence {projection.PinInfluence:P0}";
        OpponentPinSummary.Visibility = _confirmedOpponentDeck is null ? Visibility.Collapsed : Visibility.Visible;
        DevotionAssumptionText.Text = projection.DevotionAssumption;
        var meta = projection.Meta;
        // Feed the vision worker a bounded soft prior after each meaningful deck-model
        // update. This is especially valuable for small summoned cards; it does not turn
        // a prediction into evidence because the recognizer still checks pixels and side.
        var likelyOpponentIds = projection.Slots.Where(slot => slot.Card is not null).Select(slot => slot.Card!.Id)
            .Concat(meta.Cards.OrderByDescending(card => card.CopyRecommendations?.FirstOrDefault()?.Significance?.Value ?? card.RecentPrevalence)
                .Take(50).Select(card => card.Card.Id))
            .Distinct(StringComparer.Ordinal).Take(70).ToArray();
        _visionPipeline?.SetLikelyOpponentCards(likelyOpponentIds);
        MetaSampleText.Text = $"Patch {meta.TargetPatch}: {meta.CorpusDecks} unique complete faction lists; {meta.RecentDecks} this patch vs {meta.OlderDecks} in the previous three. " +
            $"{meta.PatchDatedDecks} patch-tagged; {meta.DateFallbackDecks} date fallbacks. Weight halves every {meta.HalfLifePatches:0.#} patches. " +
            (meta.ObservedIdentities > 0 ? $"Best list explains {meta.BestMatchedIdentities}/{meta.ObservedIdentities} observed identities. " : "") +
            "Estimates use observed overlap, your explicit card picks, known original leader/stratagem, reference variants, patch proximity and reviewed encounter history (optional faction MMR). Auto-filled guesses never reinforce themselves. Significance is a 0–1 recommendation index, not probability or a p-value. PKG hints are separate curated fallbacks. These are curated examples, not ladder usage. Rising does not prove a balance-change effect.";
        UnseenForecastText.Text = UnseenForecast(projection);
        StrategySignalList.ItemsSource = meta.Cards.Where(item => item.StrategyLinked).Take(10).Select(Signal).ToArray();
        StrategyEmptyText.Visibility = meta.Cards.Any(item => item.StrategyLinked) ? Visibility.Collapsed : Visibility.Visible;
        PopularSignalList.ItemsSource = meta.Cards.OrderByDescending(item => item.RecentPrevalence).ThenByDescending(item => item.SupportingDecks).Take(12).Select(Signal).ToArray();
        UpdateReferenceCandidates();
        RenderCandidateTray();
        OpponentCandidatesText.Text = $"{meta.CorpusDecks} cached examples considered. Generated, uncertain and off-faction evidence stays outside starting-deck slots.";
        RenderPlayerReferenceAndAudit();
        RenderOpponentKnowledge();
        RefreshHypothesisWindow();
    }

    private DeckDefinition? _renderedPlayerReference;
    private void RenderPlayerReferenceAndAudit()
    {
        RefreshPinnedOpponentReference();
        UserReferenceSummary.Text = _selectedUserDeck is null ? "No player deck pinned. Choose a Library deck for a reliable reference, or scan/import your deck there first." :
            $"{_selectedUserDeck.Name}\n{_selectedUserDeck.Faction} · {_selectedUserDeck.Leader}\n{_selectedUserDeck.CardCount} cards · {_selectedUserDeck.ProvisionTotal}p · Stratagem: {_selectedUserDeck.Stratagem?.Name ?? "unknown"}\n" +
            "Pinned starting list · not a live draw-pile count.";
        ClearMyReferenceButton.IsEnabled = _selectedUserDeck is not null;
        if (!ReferenceEquals(_renderedPlayerReference, _selectedUserDeck))
        {
            _renderedPlayerReference = _selectedUserDeck;
            UserReferenceCards.Rows = _selectedUserDeck is null ? [] : OpponentDeckProjector.Reference(_selectedUserDeck).Select(Strip).ToArray();
        }
        UserCardsText.Text = LeaderAudit(_userTracker) + "\n" + Audit(_userTracker.Observations);
        UserCandidatesText.Text = _selectedUserDeck is null ? "No own-deck reference selected." : "Known deck: " + _selectedUserDeck.Name;
        OpponentCardsText.Text = LeaderAudit(_opponentTracker) + "\n" + Audit(_opponentTracker.Observations);
    }

    private void UpdateReferenceCandidates()
    {
        if (OpponentCandidateList is null || OpponentDeckSearchBox is null) return;
        var previous = (OpponentCandidateList.SelectedItem as LiveDeckCandidateItem)?.Deck.Id;
        var scores = (_lastProjection?.Meta.RankedDecks ?? []).ToDictionary(item => item.Deck.Id);
        var source = _cachedDecks.Concat(_confirmedOpponentDeck is null ? [] : [_confirmedOpponentDeck]).ToArray();
        RefreshDeckSearchOptions();
        RefreshReferenceFilters(source);
        var candidates = ReferenceDeckSearch.Search(source, OpponentDeckSearchBox.Text,
                ReferenceFactionFilter?.SelectedIndex > 0 ? ReferenceFactionFilter.SelectedItem as string : null,
                ReferenceLeaderFilter?.SelectedIndex > 0 ? ReferenceLeaderFilter.SelectedItem as string : null, _opponentTracker.DeckBuildingObservations)
            .Where(deck => ReferenceSearch.Filters.Matches(deck) && DeckSearchCatalog.ContainsCards(deck, ReferenceSearch.CardNames.Text) && DeckSearchCatalog.ExcludesCards(deck, ReferenceSearch.ExcludedCards))
            .Select(deck => new LiveDeckCandidateItem(deck.Name, $"{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · {deck.ProvisionTotal}p" +
                (scores.TryGetValue(deck.Id, out var ranked) ? $" · {ranked.Contradictions.Count} missing observations" : " · other faction") +
                "\n" + ReferenceDeckSearch.DateLabel(deck) +
                (deck.Id == _confirmedOpponentDeck?.Id ? " · PINNED" : ""), deck)).ToArray();
        OpponentCandidateList.ItemsSource = candidates;
        OpponentCandidateList.SelectedItem = candidates.FirstOrDefault(item => item.Deck.Id == previous);
        OpponentSearchStatus.Text = candidates.Length == 0 ? "No matching cached decks. Change the query/filters, or import in Library." :
            $"{candidates.Length} lists · {(string.IsNullOrWhiteSpace(OpponentDeckSearchBox.Text) ? "observed overlap" : "query relevance")}, then newest first";
        OpponentConfirmedText.Text = _confirmedOpponentDeck is null ? "No pinned opponent deck." : "Pinned: " + _confirmedOpponentDeck.Name;
    }

    private string UnseenForecast(OpponentDeckProjection projection)
    {
        if ((!_opponentTracker.HasStableFaction || projection.Meta.BestObservedCoverage < .5) && projection.PinInfluence < .55)
            return "Unseen provision forecast withheld: faction or cache fit is insufficient. Observed provision floor remains available in Deck.";
        var weights = projection.Meta.RankedDecks.Select(item => (item.Deck, Weight: item.Score * (1 - projection.PinInfluence))).ToList();
        if (_confirmedOpponentDeck is not null && projection.PinInfluence > 0) weights.Add((_confirmedOpponentDeck, projection.PinInfluence));
        var total = weights.Sum(item => item.Weight);
        if (total <= 0) return "No supported unseen provision forecast.";
        var observed = _opponentTracker.DeckBuildingObservations.ToDictionary(item => item.Card.Id, item => item.ObservedCopies);
        var provisions = weights.Sum(item => item.Weight * item.Deck.Cards.Sum(card => card.Card.Provision * Math.Max(0, card.Count - observed.GetValueOrDefault(card.Card.Id)))) / total;
        var big = weights.Sum(item => item.Weight * item.Deck.Cards.Where(card => card.Card.Provision >= 10).Sum(card => Math.Max(0, card.Count - observed.GetValueOrDefault(card.Card.Id)))) / total;
        return $"Cached/reference forecast: ~{provisions:F0} listed provisions unseen; ~{big:F1} unseen 10+p cards. Not a hand/draw-pile count; manual picks are excluded.";
    }

    private void OpponentDeckSearch_OnTextChanged(object sender, TextChangedEventArgs e) => UpdateReferenceCandidates();
    private void ChooseOpponentReference_OnClick(object sender, RoutedEventArgs e)
    { ShowPage(UiPage.Reference); ReferencePicker.IsExpanded = true; }

    private string? _pinnedReferenceKey;
    private void RefreshPinnedOpponentReference()
    {
        if (PinnedOpponentCards is null) return;
        var deck = _confirmedOpponentDeck;
        var key = deck is null ? "none" : deck.Id + deck.Name + DeckLibrary.Fingerprint(deck);
        if (_pinnedReferenceKey == key) return;
        _pinnedReferenceKey = key;
        ClearPinnedOpponentButton.IsEnabled = deck is not null;
        PinnedOpponentSummary.Text = deck is null ? "No opponent deck pinned." :
            $"{deck.Name}\n{deck.Leader} · {deck.CardCount} cards · {deck.ProvisionTotal}p";
        PinnedOpponentCards.Rows = deck is null ? [] : OpponentDeckProjector.Reference(deck).Select(Strip).ToArray();
        PinnedOpponentCards.ToolTip = "Pinned starting-list reference, not a claim about remaining cards or the opponent's hand.";
    }
    private void ChooseOwnReference_OnClick(object sender, RoutedEventArgs e)
    {
        ShowPage(UiPage.Library);
        LibraryManagement.IsExpanded = true;
        DeckSearchBox.Focus();
        FooterStatusText.Text = "Select a Library deck, then Use as my deck. Your opponent reference is unchanged.";
    }

    private DeckStripRow Strip(ProjectedDeckSlot slot)
    {
        var card = slot.Card;
        var meta = slot.Meta;
        var copySignal = meta?.CopyRecommendations?.ElementAtOrDefault(slot.Copy - 1);
        var significance = copySignal?.Significance;
        var color = slot.DeviatesFromPin ? "#F2AF60" : slot.State switch
        { DeckSlotState.Observed => "#7DD9AE", DeckSlotState.Selected => "#E5C77E", DeckSlotState.Pinned => "#C5A5F7", DeckSlotState.Predicted => "#89BFFF", DeckSlotState.Reference => "#C5CED6", _ => "#7E8B97" };
        var badge = slot.DeviatesFromPin ? "SEEN +" : slot.State switch
        { DeckSlotState.Observed => "SEEN", DeckSlotState.Selected => "PICK ?", DeckSlotState.Pinned => "PIN ?",
            DeckSlotState.Predicted => slot.PackageHint is not null ? "PKG ?" : significance is not null ? significance.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "—", DeckSlotState.Reference => "LISTED", _ => "?" };
        var detail = slot.State == DeckSlotState.Reference && card is not null ? string.Join(" · ", card.Categories.Take(2)) :
            slot.State == DeckSlotState.Observed ? (slot.Copy > 1 ? $"copy {slot.Copy} · " : "") + "observed identity" :
            slot.State == DeckSlotState.Selected ? "your assumption · click to remove" :
            slot.PackageHint is { } hint ? hint.Package + " · " + (hint.FromUserPick ? "from your pick" : "unseen partner") :
            significance is not null ? significance.Label : card is null ? "identity / provisions unknown" : "no cached support";
        if (significance?.Specificity >= .2 && meta?.Associations.FirstOrDefault() is { } association) detail += " · " + association.ObservedCard;
        if (meta?.ReturningPattern is not null && slot.State == DeckSlotState.Predicted) detail = "Returning pattern · " + detail;
        if (meta?.Rising == true) detail += " · rising";
        var tooltip = card is null ? slot.Reason : $"{card.Name} · {card.Kind} · {card.Provision} provisions\n{slot.Reason}" +
            (meta?.ReturningPattern is { } returning ? "\n" + returning.Explanation : "") +
            (significance is null ? "" : $"\nSignificance {significance.Value:0.00}/1: recommendation strength, not a p-value or chance of inclusion. " +
                $"Context specificity {significance.Specificity:0.00}; reliability {significance.Reliability:0.00}; " +
                $"effective supporting families {significance.EffectiveSupportingFamilies:0.0}; patch proximity {significance.Freshness:0.00}; evidence fit {significance.EvidenceFit:0.00}.") +
            (meta is null ? "" : $"\nCurrent patch: {meta.RecentPrevalence:P0}; previous three patches: {meta.OlderPrevalence:P0}; all-faction current {meta.GlobalRecentPrevalence:P0}." +
                $"\nMatch-conditioned share {meta.ConditionalPresence:P0}; association ×{meta.AssociationLift:F2}; {meta.SupportingDecks} supporting lists." +
                (copySignal is null ? "" : $"\nCopy {slot.Copy}: {copySignal.Evidence}. Smoothed curated-sample estimate, not ladder probability.") +
                (meta.Associations.Count == 0 ? "" : "\nPatch/family-weighted associations: " + string.Join("; ", meta.Associations.Select(pair => $"{pair.ObservedCard} ×{pair.Lift:F1} ({pair.JointDecks}/{pair.ConditionDecks} all-history; {pair.RecentJointDecks}/{pair.RecentConditionDecks} current + previous three patches)")))) +
            (card.AbilityText is { Length: > 0 } ability ? "\n\n" + ability : "");
        return new(slot.Position == 0 ? "+" : slot.Position.ToString("00"), card?.Name ?? "Unknown card", card is null ? "?" : card.Provision.ToString(), badge,
            detail, color, card?.IsGold == true ? "#9C834D" : "#485767", CardArt(card), tooltip,
            slot.Position == 0 ? $"Add {card?.Name}, copy {slot.Copy}, to opponent deck; significance {(significance is null ? "unavailable" : significance.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))}" :
            $"Slot {slot.Position}: {card?.Name ?? "Unknown"}, {(slot.State == DeckSlotState.Predicted ? "GUESS " : "")}{badge}, {card?.Provision.ToString() ?? "unknown"} provisions", slot,
            slot.State != DeckSlotState.Reference && card is not null && _liveValues.Growth(PlayerSide.Opponent, card.Id) is { } growth ?
                ValueRange(growth.Minimum, growth.Maximum) + (growth.Unit switch { "damage" => " dmg", "power" => " pw", _ => " " + growth.Unit }) : null);
    }

    private BitmapImage? CardArt(CardDefinition? card)
    {
        if (card is null) return null;
        if (_deckArt.TryGetValue(card.Id, out var cached)) return cached;
        var path = Path.Combine(ResolveArtCacheDirectory(), card.Id + ".jpg");
        BitmapImage? image = null;
        if (File.Exists(path))
        {
            try
            {
                image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 200; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or FormatException) { image = null; }
        }
        _deckArt[card.Id] = image;
        return image;
    }

    private static DeckSignalRow Signal(CardMetaSignal item) => new(item.Card.Name,
        $"Significance {item.CopyRecommendations?[0].Significance?.Value:0.00} · this patch {item.RecentPrevalence:P0}" + (item.Rising ? " · RISING" : ""),
        (item.StrategyLinked ? "Specific association. " : "General prevalence; not a distinctive strategy signal. ") +
        $"Previous three patches {item.OlderPrevalence:P0}; {item.SupportingDecks} lists. " +
        (item.ReturningPattern is { } returning ? returning.Explanation + " " : "") +
        string.Join("; ", item.Associations.Select(pair => $"with {pair.ObservedCard}: ×{pair.Lift:F1}, {pair.JointDecks} lists")));
    private static string Audit(IEnumerable<ObservedCard> observations) => string.Join(Environment.NewLine,
        observations.OrderByDescending(item => item.ObservedAt).Select(item => $"{item.Card.Name} · {item.Provenance}\n{item.Evidence}"));

    private string LeaderAudit(LiveDeckTracker tracker)
    {
        var leader = InferLeaderState(tracker);
        return (leader.StartingName is null ? "Starting leader unknown; not visually identified." : $"Starting leader: {leader.StartingName} " +
            (tracker.Side == PlayerSide.Opponent && _opponentKnowledge.StartingLeader is not null ? "(user confirmed original)." : "(reference assumption, not visual confirmation).")) +
            (leader.WasReplaced ? $" Possible replacement: {leader.CurrentName} ({leader.CurrentFaction}); resolution unverified." : "");
    }


    private async void PinIndexedOpponentDeck_OnClick(object sender, RoutedEventArgs e)
    {
        var deck = await LoadSelectedIndexDeckAsync();
        if (deck is null) return;
        _confirmedOpponentDeck = deck; RenderLiveInference(); PersistCurrentMatch(); ShowPage(UiPage.Deck);
    }
    private async Task<DeckDefinition?> LoadSelectedIndexDeckAsync(DeckListItem? item = null)
    {
        item ??= DeckList.SelectedItem as DeckListItem;
        if (item is null) { DeckDataStatusText.Text = "Select a deck first."; return null; }
        if (item.Deck is not null) return CurrentDeck(item.Deck);
        if (item.IndexEntry is null) { DeckDataStatusText.Text = "A partial observation is not a complete reference deck."; return null; }
        _deckLoads++;
        try
        {
            DeckDataStatusText.Text = "Loading selected deck…";
            var result = await _deckCacheService.SyncAsync([item.IndexEntry], ResolveDeckCacheDirectory(), 1);
            var deck = result.Decks.FirstOrDefault();
            if (deck is null) { DeckDataStatusText.Text = "Deck expired or unavailable; references unchanged."; return null; }
            MergeLibrary([deck]);
            deck = CurrentDeck(_library.Find(deck.Id)?.Deck ?? deck);
            RefreshDeckList(); DeckDataStatusText.Text = "Selected deck cached."; return deck;
        }
        catch (Exception exception) { DeckDataStatusText.Text = "Deck unavailable: " + exception.Message; return null; }
        finally { _deckLoads--; }
    }
    private sealed record DeckStripRow(string Position, string Name, string Provision, string Badge, string Detail,
        string StateColor, string FrameColor, ImageSource? Artwork, string Tooltip, string AccessibleName, ProjectedDeckSlot Slot, string? ValueBadge = null);
    private sealed record DeckSignalRow(string Name, string Numbers, string Explanation);
}
