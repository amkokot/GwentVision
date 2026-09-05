using System.IO;
using System.Windows.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private void RenderCandidateTray()
    {
        if (CandidateCardTray is null || _lastProjection is null) return;
        if (_confirmedOpponentDeck is not null)
        {
            CandidateCardTray.Rows = null;
            CandidateTrayStatus.Text = "A full reference is pinned. Clear the pin to edit individual assumptions.";
            return;
        }
        try
        {
            _candidateCatalog ??= GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json"));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            CandidateTrayStatus.Text = "Full catalog unavailable: " + exception.Message;
            _candidateCatalog = _cachedDecks.SelectMany(deck => deck.Cards).Select(item => item.Card).DistinctBy(card => card.Id).ToArray();
        }
        var terms = DeckSearchCatalog.Terms(CandidateCardSearch.Text);
        var meta = _lastProjection.Meta.Cards.ToDictionary(item => item.Card.Id);
        var packages = (_lastProjection.PackageHints ?? []).ToDictionary(item => item.Card.Id);
        var rows = _lastProjection.Slots.Where(row => row.Card is not null).ToArray();
        var renfriRuledOut = _opponentKnowledge.Assess(_opponentTracker.DeckBuildingObservations).Renfri.State == ConstraintState.RuledOut;
        var candidates = _candidateCatalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(card => !renfriRuledOut || card.Name != "Renfri")
            .Where(card => !_opponentTracker.HasStableFaction || FactionCompatibility.IsPlayableBy(card, _opponentTracker.Faction!))
            .Where(card => DeckSearchCatalog.Matches(DeckSearchCatalog.Normalize(card.Name + " " + card.Kind + " " + string.Join(' ', card.Categories) + " " + card.AbilityText), terms))
            .Select(card => new { Card = card, Copy = Enumerable.Range(1, card.IsGold ? 1 : 2).FirstOrDefault(copy => !rows.Any(row => row.Card!.Id == card.Id && row.Copy == copy)), Meta = meta.GetValueOrDefault(card.Id), Package = packages.GetValueOrDefault(card.Id) })
            .Where(item => item.Copy > 0)
            .OrderByDescending(item => item.Copy == 1 && item.Package is not null)
            .ThenByDescending(item => item.Meta?.CopyRecommendations?.ElementAtOrDefault(item.Copy - 1)?.Significance?.Value ?? 0)
            .ThenByDescending(item => item.Meta?.CopyRecommendations?.ElementAtOrDefault(item.Copy - 1)?.Score ?? 0)
            .ThenByDescending(item => item.Meta?.RecentPrevalence ?? 0).ThenBy(item => item.Card.Name)
            .ToArray();
        CandidateCardTray.Rows = candidates.Select(item => Strip(new ProjectedDeckSlot(0, item.Card, item.Copy,
            DeckSlotState.Predicted, item.Meta?.CopyPresence.ElementAtOrDefault(item.Copy - 1), item.Meta, false,
            $"Candidate copy {item.Copy}, not a sighting. Click to assume it in the deck and reweight related suggestions. " +
            (item.Copy == 1 && item.Package is not null ? item.Package.Explanation + " " : "") +
            item.Meta?.CopyRecommendations?.ElementAtOrDefault(item.Copy - 1)?.Evidence +
            (_opponentEdits.Excluded.Contains(new(item.Card.Id, item.Copy)) ? " You dismissed this suggestion; it will not auto-fill again this match." : ""),
            item.Copy == 1 ? item.Package : null))).ToArray();
        CandidateTrayStatus.Text = candidates.Length == 0 ? "No matching candidates. Try another card name." :
            $"{candidates.Length} cards · patch {_lastProjection.Meta.TargetPatch} · significance 0–1, not probability";
    }

    private void CandidateCardSearch_OnChanged(object sender, TextChangedEventArgs e) => RenderCandidateTray();

    private void CandidateCard_OnActivated(object? sender, object row)
    {
        if (_confirmedOpponentDeck is not null || row is not DeckStripRow { Slot.Card: { } card } item || _lastProjection is null) return;
        if (_lastProjection.Slots.All(slot => slot.State is DeckSlotState.Observed or DeckSlotState.Selected))
        {
            CandidateTrayStatus.Text = "All slots are occupied by evidence or your choices. Return an unseen choice first.";
            return;
        }
        _opponentEdits.Include(card, item.Slot.Copy);
        RenderLiveInference(); PersistCurrentMatch();
        FooterStatusText.Text = $"Assumed {card.Name}; updating related suggestions. No observed evidence was changed.";
    }

    private void OpponentSlot_OnActivated(object? sender, object row)
    {
        if (row is not DeckStripRow item) return;
        if (item.Slot.State == DeckSlotState.Observed)
        {
            FooterStatusText.Text = "Observed plays are evidence and cannot be dismissed as suggestions.";
            return;
        }
        if (_confirmedOpponentDeck is not null)
        {
            FooterStatusText.Text = "Clear the full-deck pin before editing individual assumptions.";
            return;
        }
        if (item.Slot.Card is null) { CandidateCardSearch.Focus(); return; }
        // Duplicate copies are interchangeable. Remove the highest unseen copy index
        // so returning copy one does not strand a second copy without its first.
        var copy = _lastProjection!.Slots.Where(slot => slot.Card?.Id == item.Slot.Card.Id && slot.State != DeckSlotState.Observed)
            .Select(slot => slot.Copy).DefaultIfEmpty(item.Slot.Copy).Max();
        _opponentEdits.Exclude(item.Slot.Card, copy);
        RenderLiveInference(); PersistCurrentMatch();
        FooterStatusText.Text = $"Returned {item.Slot.Card.Name} to candidates; auto-fill dismissed for this match.";
    }
}
