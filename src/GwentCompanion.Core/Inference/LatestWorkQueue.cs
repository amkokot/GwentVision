namespace GwentCompanion.Core.Inference;

/// <summary>
/// UI-owned scheduler: one background calculation and one replaceable pending input.
/// Request/Dispose are called on the owning synchronization context. Results return
/// there; obsolete results never overwrite newer evidence. Only derived work is
/// coalesced, never the underlying recognition/event stream.
/// </summary>
public sealed class LatestWorkQueue<TKey, TInput, TResult>(
    Func<TInput, TResult> calculate, Action<TResult> apply, Action<Exception> error) : IDisposable where TKey : notnull
{
    private (TKey Key, TInput Input, long Version)? _latest;
    private bool _running, _closed;
    private long _version;
    public Task Idle { get; private set; } = Task.CompletedTask;
    public int Calculations { get; private set; }
    public int DiscardedResults { get; private set; }
    public int AppliedResults { get; private set; }

    public bool Request(TKey key, TInput input)
    {
        if (_closed || _latest is { } previous && EqualityComparer<TKey>.Default.Equals(previous.Key, key)) return false;
        _latest = (key, input, ++_version);
        if (_running) return true;
        _running = true;
        Idle = RunAsync();
        return true;
    }

    private async Task RunAsync()
    {
        // Always yield before returning from Request, including a trivial calculation.
        await Task.Yield();
        try
        {
            while (!_closed && _latest is { } request)
            {
                try
                {
                    Calculations++;
                    var result = await Task.Run(() => calculate(request.Input));
                    if (_closed) return;
                    if (_latest?.Version != request.Version) { DiscardedResults++; continue; }
                    apply(result);
                    AppliedResults++;
                }
                catch (Exception exception)
                {
                    if (_closed) return;
                    if (_latest?.Version != request.Version) continue;
                    // A subsequent request of the same input can retry a failed calculation.
                    _latest = null;
                    error(exception);
                    // A retry requested by the error handler must not be stranded.
                    if (_latest is null) return;
                }
                if (_latest?.Version == request.Version) return;
            }
        }
        finally { _running = false; }
    }

    public void Dispose() { _closed = true; _latest = null; }
}
