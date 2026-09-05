using System.Diagnostics;

namespace GwentCompanion.Core.Vision;

/// <summary>Recognition and evidence commits never wait for Reach. Only the optional calculator yields.</summary>
public sealed class GameplayWorkPriority(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private int _recognition;
    private DateTimeOffset _actionUntil, _pressureUntil;
    private long _pauses;
    public long PauseCount => Interlocked.Read(ref _pauses);
    public string? PauseReason
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (_actionUntil > now) return "Reading a possible play";
                if (_pressureUntil > now) return "Catching up card detection";
                return _recognition > 0 ? "Card detection and deck tracking first" : null;
            }
        }
    }

    public void SignalAction()
    { lock (_gate) _actionUntil = _clock.GetUtcNow().AddMilliseconds(900); }

    public void ObservePressure(int pendingFrames, TimeSpan lag)
    {
        if (pendingFrames < 3 && lag < TimeSpan.FromMilliseconds(900)) return;
        lock (_gate) _pressureUntil = _clock.GetUtcNow().AddMilliseconds(350);
    }

    public IDisposable EnterRecognition()
    {
        lock (_gate) _recognition++;
        return new Lease(() => { lock (_gate) _recognition--; });
    }

    public void WaitForReach(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PauseReason is null) return;
        Interlocked.Increment(ref _pauses);
        while (PauseReason is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.WaitHandle.WaitOne(15);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>Thread-local cooperative checkpoints also cover nested engines and automatic horizons.</summary>
public sealed class ReachWorkScope : IDisposable
{
    [ThreadStatic] private static ReachWorkScope? _current;
    private readonly ReachWorkScope? _previous;
    private readonly GameplayWorkPriority _priority;
    private readonly CancellationToken _cancellation;
    private long _slice = Stopwatch.GetTimestamp();

    public ReachWorkScope(GameplayWorkPriority priority, CancellationToken cancellationToken)
    { _priority = priority; _cancellation = cancellationToken; _previous = _current; _current = this; }

    public static void Checkpoint()
    {
        if (_current is not { } work) return;
        work._cancellation.ThrowIfCancellationRequested();
        work._priority.WaitForReach(work._cancellation);
        if (Stopwatch.GetElapsedTime(work._slice) < TimeSpan.FromMilliseconds(12)) return;
        // A short duty-cycle limit leaves headroom even before a sudden play is detected.
        if (work._cancellation.WaitHandle.WaitOne(6)) work._cancellation.ThrowIfCancellationRequested();
        work._slice = Stopwatch.GetTimestamp();
    }

    public void Dispose() => _current = _previous;
}
