namespace GwentCompanion.Core.Inference;

/// <summary>Match-local presentation preference. Never edits observations or watch evidence.</summary>
public sealed class TacticalDismissals
{
    private readonly HashSet<string> _keys = [];
    public bool Contains(TacticalWatchRow row) => _keys.Contains(row.Rule.Key);
    public void Hide(TacticalWatchRow row) => _keys.Add(row.Rule.Key);
    public void Restore(IEnumerable<TacticalWatchRow> rows)
    { foreach (var row in rows) _keys.Remove(row.Rule.Key); }
    public void Reset() => _keys.Clear();
}
