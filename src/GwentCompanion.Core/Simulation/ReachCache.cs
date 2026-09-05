using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GwentCompanion.Core.Simulation;

/// <summary>Bounded per-analyzer caches; completed entries survive cancellation of a later candidate.</summary>
internal sealed class ReachCache<T>(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _values = [];
    private readonly Queue<string> _order = new();
    public long Hits { get; private set; }
    public int Count { get { lock (_gate) return _values.Count; } }
    public bool TryGet(string key, out T value)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out value!)) return false;
            Hits++; return true;
        }
    }
    public T Add(string key, T value)
    {
        lock (_gate)
        {
            if (capacity <= 0 || _values.ContainsKey(key)) return value;
            _values.Add(key, value); _order.Enqueue(key);
            while (_values.Count > capacity) _values.Remove(_order.Dequeue());
            return value;
        }
    }
}

internal static class ReachCacheKey
{
    // Length-delimited JSON avoids collisions between adjacent metadata fields. Full notation includes
    // ordered instances, statuses, resources, inventory, weather, provenance and persistent card memory.
    public static string For(GamePosition position, params object?[] context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PositionNotation.Write(position) + "\n" + JsonSerializer.Serialize(context))));
}
