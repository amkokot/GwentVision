using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace GwentCompanion.Core.Vision;

/// <summary>Bounded, chronological work buffer. Under load, preview changes outlive redundant samples.</summary>
public sealed class RollingVisionBuffer<T>(int capacity = 24, TimeSpan? maximumAge = null)
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly TimeSpan _maximumAge = maximumAge ?? TimeSpan.FromSeconds(6);
    private DateTimeOffset? _latest;
    private bool _complete;
    public int Dropped { get; private set; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public void Offer(T item, DateTimeOffset at, double priority)
    {
        lock (_gate)
        {
            if (_complete) return;
            if (_latest is not null && at <= _latest) { Dropped++; return; }
            _latest = at;
            while (_entries.Count > 0 && at - _entries[0].At > _maximumAge)
            { _entries.RemoveAt(0); Dropped++; }
            _entries.Add(new Entry(item, at, double.IsFinite(priority) ? priority : 0));
            if (_entries.Count > Math.Max(1, capacity))
            {
                // Keep the newest sample for freshness; remove the lowest-change older one.
                var remove = Enumerable.Range(0, _entries.Count - 1).MinBy(index => _entries[index].Priority);
                _entries.RemoveAt(remove);
                Dropped++;
            }
            _signal.Writer.TryWrite(true);
        }
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_signal.Reader.TryRead(out _)) { }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                T item;
                lock (_gate)
                {
                    if (_entries.Count == 0) break;
                    item = _entries[0].Item;
                    _entries.RemoveAt(0);
                }
                yield return item;
            }
        }
    }

    public void Complete() { lock (_gate) { _complete = true; _signal.Writer.TryComplete(); } }
    private sealed record Entry(T Item, DateTimeOffset At, double Priority);
}
