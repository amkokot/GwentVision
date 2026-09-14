using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace GwentCompanion.Core.Vision;

/// <summary>Bounded, chronological work buffer. Under load, preview changes outlive redundant samples.</summary>
public sealed class RollingVisionBuffer<T>(int capacity = 24, TimeSpan? maximumAge = null,
    int? protectedCapacity = null, double protectedMinimumPriority = double.PositiveInfinity,
    TimeSpan? protectedMaximumAge = null)
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly TimeSpan _maximumAge = maximumAge ?? TimeSpan.FromSeconds(6);
    private readonly TimeSpan _protectedMaximumAge = protectedMaximumAge ?? maximumAge ?? TimeSpan.FromSeconds(6);
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly int _protectedCapacity = Math.Max(Math.Max(1, capacity), protectedCapacity ?? capacity);
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
            Dropped += _entries.RemoveAll(entry => at - entry.At >
                (IsProtected(entry) ? _protectedMaximumAge : _maximumAge));
            _entries.Add(new Entry(item, at, double.IsFinite(priority) ? priority : 0));
            while (_entries.Count(entry => !IsProtected(entry)) > _capacity)
            {
                // Ordinary traffic cannot consume the reserved critical slots.
                // Keep the newest sample for freshness and remove the least useful
                // older ordinary frame.
                var remove = Enumerable.Range(0, _entries.Count - 1)
                    .Where(index => !IsProtected(_entries[index]))
                    .MinBy(index => _entries[index].Priority);
                _entries.RemoveAt(remove);
                Dropped++;
            }
            while (_entries.Count > _protectedCapacity)
            {
                var remove = Enumerable.Range(0, _entries.Count - 1).MinBy(index => _entries[index].Priority);
                _entries.RemoveAt(remove); Dropped++;
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
    private bool IsProtected(Entry entry) => entry.Priority >= protectedMinimumPriority;
    private sealed record Entry(T Item, DateTimeOffset At, double Priority);
}
