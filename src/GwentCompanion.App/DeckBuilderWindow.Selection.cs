using System.Windows;
using System.Windows.Input;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class DeckBuilderWindow
{
    private readonly HashSet<DeckCopyKey> _draftSelection = [];
    private DeckCopyKey? _selectionAnchor;
    private DeckCard[] _recommendationFocus = [];
    private static DeckCopyKey CopyKey(CardRow row) => new(row.Card.Id, row.Copy);
    private CardRow[] VisibleDraftRows => DraftCards.Rows?.OfType<CardRow>().ToArray() ?? [];
    private void ActivateDraftCard(object item, ModifierKeys modifiers)
    {
        if (_rebuilding || item is not CardRow row) return;
        CardInspected(this, row);
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            if (IsOpponentReview && row.Badge == "AUTO") FixDraftRow(row);
            else RemoveDraftRows([row]);
            return;
        }
        var rows = VisibleDraftRows; var index = Array.FindIndex(rows, r => CopyKey(r) == CopyKey(row));
        if (index < 0) return;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            var anchor = _selectionAnchor is { } key ? Array.FindIndex(rows, r => CopyKey(r) == key) : index;
            if (anchor < 0) anchor = index;
            if ((modifiers & ModifierKeys.Control) == 0) _draftSelection.Clear();
            foreach (var entry in rows.Skip(Math.Min(anchor, index)).Take(Math.Abs(index - anchor) + 1)) _draftSelection.Add(CopyKey(entry));
            _selectionAnchor ??= CopyKey(row);
        }
        else
        {
            if (!_draftSelection.Add(CopyKey(row))) _draftSelection.Remove(CopyKey(row));
            _selectionAnchor = CopyKey(row);
        }
        _selected = _draftSelection.Contains(CopyKey(row)) ? row : null;
        UpdateDraftHighlights();
    }
    private void UpdateDraftHighlights()
    {
        var rows = VisibleDraftRows;
        _draftSelection.IntersectWith(rows.Select(CopyKey));
        var selected = rows.Where(r => _draftSelection.Contains(CopyKey(r))).ToArray();
        _selected = selected.Length == 1 ? selected[0] : null;
        DraftCards.HighlightRows(selected);
        SelectedButtons();
        RemoveButton.Content = selected.Length > 1 ? $"Remove {selected.Length}" : "Remove";
        RemoveButton.ToolTip = "Remove Ctrl/Shift-selected copies as one undoable edit";
    }
    private void ClearDraftSelection()
    { _draftSelection.Clear(); _selectionAnchor = null; _selected = null; }
    private void RemoveDraftRows(IEnumerable<CardRow> selection)
    {
        if (_rebuilding) return;
        var rows = selection.DistinctBy(CopyKey).ToArray(); if (rows.Length == 0) return;
        Checkpoint();
        foreach (var group in rows.GroupBy(r => r.Card.Id))
        {
            var fixedCount = _fixed.GetValueOrDefault(group.Key)?.Count ?? 0;
            var remainingFixed = Math.Max(0, fixedCount - group.Count(r => r.Copy <= fixedCount));
            if (remainingFixed == 0) _fixed.Remove(group.Key); else _fixed[group.Key] = new(group.First().Card, remainingFixed);
            // Exclude the removed physical count, so auto-fill cannot put it straight back.
            var total = VisibleDraftRows.Count(r => r.Card.Id == group.Key);
            for (var copy = Math.Max(1, total - group.Count() + 1); copy <= total; copy++) _excluded.Add(new(group.Key, copy));
        }
        ClearDraftSelection(); _dirty = true; Rebuild();
    }
    private void RecommendationFocusClicked(object sender, RoutedEventArgs e)
    {
        if (_rebuilding) return;
        var selected = VisibleDraftRows.Where(r => _draftSelection.Contains(CopyKey(r))).ToArray(); if (selected.Length == 0) return;
        _recommendationFocus = selected.GroupBy(r => r.Card.Id).Select(g => new DeckCard(g.First().Card, g.Count())).ToArray();
        if (CardSortChoice.SelectedIndex != 3) CardSortChoice.SelectedIndex = 3;
        else { RefreshRelatedCards(); RenderCollection(); }
    }
    private void ClearRecommendationFocusClicked(object sender, RoutedEventArgs e)
    { _recommendationFocus = []; RefreshRelatedCards(); RenderCollection(); }
}
