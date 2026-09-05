using System.Diagnostics;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Bounded stage counters shared by the two recognition workers; no frame data retained.</summary>
public sealed class VisionStageTimings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StageTiming> _stages = [];
    public long Record(string stage, long started)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
        lock (_gate)
        {
            var old = _stages.GetValueOrDefault(stage) ?? new(0, 0, 0);
            _stages[stage] = new(old.Calls + 1, old.TotalMilliseconds + elapsed, Math.Max(old.MaximumMilliseconds, elapsed));
        }
        return now;
    }
    public IReadOnlyDictionary<string, StageTiming> Snapshot() { lock (_gate) return new Dictionary<string, StageTiming>(_stages); }
    public void Reset() { lock (_gate) _stages.Clear(); }
    public sealed record StageTiming(int Calls, double TotalMilliseconds, double MaximumMilliseconds)
    { public double MeanMilliseconds => TotalMilliseconds / Math.Max(1, Calls); }
}
