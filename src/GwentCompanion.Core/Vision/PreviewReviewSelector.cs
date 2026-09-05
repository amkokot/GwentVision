using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public sealed record PreviewMotion(double User, double Opponent)
{
    public double Maximum => Math.Max(User, Opponent);
}

/// <summary>Retain a few settled frames after motion, not only the blurred transition itself.</summary>
public sealed class PreviewPriorityWindow
{
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private double _priority;
    public double Score(DateTimeOffset at, double motion)
    {
        if (motion >= .075)
        {
            _until = at.AddMilliseconds(600);
            _priority = motion;
        }
        return at <= _until ? Math.Max(motion, _priority * .85) : motion;
    }
}

/// <summary>Cheap change signal, not a card detector. Keeps unknown events eligible for review.</summary>
public sealed class PreviewMotionSampler
{
    private double[][]? _previous;
    public PreviewMotion Measure(PixelFrame frame)
    {
        var samples = new[] { new NormalizedRegion(.81, .39, .95, .73), new NormalizedRegion(.80, .11, .95, .46) }
            .Select(region => Sample(frame, region)).ToArray();
        var change = _previous is null ? new double[2] : Enumerable.Range(0, 2)
            .Select(side => samples[side].Zip(_previous[side], (a, b) => Math.Abs(a - b)).Average() / 255).ToArray();
        _previous = samples;
        return new PreviewMotion(change[0], change[1]);
    }

    private static double[] Sample(PixelFrame frame, NormalizedRegion region)
    {
        var samples = new List<double>();
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 12; x++)
            {
                var px = (int)((region.Left + (x + .5) / 12 * (region.Right - region.Left)) * frame.Width);
                var py = (int)((region.Top + (y + .5) / 20 * (region.Bottom - region.Top)) * frame.Height);
                var color = frame.GetPixel(px, py);
                samples.Add((color.Red + color.Green + color.Blue) / 3.0);
            }
        return samples.ToArray();
    }
}

public sealed record ReviewFrame<T>(T Value, DateTimeOffset At);
public sealed record PreviewReviewClip<T>(PlayerSide Side, DateTimeOffset TriggeredAt, double Change,
    ReviewFrame<T> Before, ReviewFrame<T> During, ReviewFrame<T>? After);

/// <summary>Two independent lanes, bounded pre-roll, delayed stills and a per-lane cooldown.</summary>
public sealed class PreviewReviewSelector<T>
{
    private readonly Queue<ReviewFrame<T>> _history = new();
    private readonly Dictionary<PlayerSide, Pending> _pending = [];
    private readonly Dictionary<PlayerSide, DateTimeOffset> _lastTrigger = [];

    public IReadOnlyList<PreviewReviewClip<T>> Observe(T frame, DateTimeOffset at, PreviewMotion motion)
    {
        var current = new ReviewFrame<T>(frame, at);
        var clips = new List<PreviewReviewClip<T>>();
        foreach (var (side, pending) in _pending.ToArray())
        {
            if (at - pending.At >= TimeSpan.FromMilliseconds(180) && pending.During is null) pending.During = current;
            if (at - pending.At < TimeSpan.FromMilliseconds(750)) continue;
            clips.Add(new PreviewReviewClip<T>(side, pending.At, pending.Change, pending.Before, pending.During ?? current, current));
            _pending.Remove(side);
        }
        foreach (var (side, change) in new[] { (PlayerSide.User, motion.User), (PlayerSide.Opponent, motion.Opponent) })
        {
            if (change < .075 || _history.Count == 0 || _pending.ContainsKey(side) ||
                (_lastTrigger.TryGetValue(side, out var last) && at - last < TimeSpan.FromSeconds(3))) continue;
            var before = _history.LastOrDefault(item => at - item.At >= TimeSpan.FromMilliseconds(350)) ?? _history.Peek();
            _pending[side] = new Pending(at, change, before, current);
            _lastTrigger[side] = at;
        }
        _history.Enqueue(current);
        while (_history.Count > 12 || at - _history.Peek().At > TimeSpan.FromSeconds(2)) _history.Dequeue();
        return clips;
    }

    public IReadOnlyList<PreviewReviewClip<T>> Flush()
    {
        var clips = _pending.Select(item => new PreviewReviewClip<T>(item.Key, item.Value.At, item.Value.Change,
            item.Value.Before, item.Value.During ?? item.Value.TriggerFrame, null)).ToArray();
        _pending.Clear();
        _history.Clear();
        return clips;
    }
    private sealed class Pending(DateTimeOffset at, double change, ReviewFrame<T> before, ReviewFrame<T> triggerFrame)
    {
        public DateTimeOffset At { get; } = at;
        public double Change { get; } = change;
        public ReviewFrame<T> Before { get; } = before;
        public ReviewFrame<T> TriggerFrame { get; } = triggerFrame;
        public ReviewFrame<T>? During { get; set; }
    }
}
