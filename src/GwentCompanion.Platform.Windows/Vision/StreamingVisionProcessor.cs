using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GwentCompanion.Core.Vision;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record VisionInput<T>(PixelFrame Frame, DateTimeOffset SampledAt, double Priority, T Context, bool PointerInPlayerHand = false);
public sealed record VisionOutput<T>(CardVisionResult Result, T Context);

/// <summary>
/// Read text before selecting expensive artwork frames. Skipping an artwork frame must not
/// discard its already-read title. One consumer merges both paths in capture order.
/// </summary>
public sealed class StreamingVisionProcessor<T>(ICardVisionPipeline pipeline, int artworkCapacity = 4, int pendingCapacity = 48)
{
    public int PreparedFrames { get; private set; }
    public int ArtworkPasses { get; private set; }
    public int SkippedArtworkFrames { get; private set; }
    public int MaximumPendingFrames { get; private set; }
    private int _errors;
    public int Errors => Volatile.Read(ref _errors);
    public Action<Exception>? OnError { get; init; }
    public GameplayWorkPriority? WorkPriority { get; init; }
    // Presentation only. Never commit evidence here: the chronological path remains authoritative.
    public Action<CardVisionResult, T>? OnPreparedText { get; init; }

    public async IAsyncEnumerable<VisionOutput<T>> RunAsync(IAsyncEnumerable<VisionInput<T>> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (artworkCapacity < 1 || pendingCapacity <= artworkCapacity)
            throw new ArgumentOutOfRangeException(nameof(pendingCapacity), "Pending capacity must exceed artwork capacity.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var capacity = new SemaphoreSlim(pendingCapacity);
        var pending = new ConcurrentQueue<PreparedInput>();
        var artwork = new RollingVisionBuffer<PreparedInput>(artworkCapacity);
        var schedule = new VisionScanSchedule();
        var producer = Task.Run(async () =>
        {
            DateTimeOffset? last = null;
            DateTimeOffset? thinningPriorityUntil = null;
            int? userScore = null, opponentScore = null, userHand = null, opponentHand = null;
            try
            {
                await foreach (var sample in input.WithCancellation(stop.Token).ConfigureAwait(false))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (last is not null && sample.SampledAt <= last) continue;
                    last = sample.SampledAt;
                    await capacity.WaitAsync(stop.Token).ConfigureAwait(false);
                    PreparedVisionFrame prepared;
                    try
                    {
                        using var recognition = WorkPriority?.EnterRecognition();
                        var analyzed = await pipeline.PrepareAsync(sample.Frame, sample.SampledAt).ConfigureAwait(false);
                        // Live capture has the actual OS pointer position. Preserve it
                        // separately and make it authoritative for zone-sensitive
                        // bookkeeping: lower-board tooltips occupy the same visual band
                        // as hand tooltips and must not manufacture another hand play.
                        prepared = analyzed with { HoverInPlayerHand = sample.PointerInPlayerHand,
                            PointerInPlayerHand = sample.PointerInPlayerHand };
                        prepared = prepared with { Screen = prepared.Screen with { UnresolvedHandSelection =
                            sample.PointerInPlayerHand && prepared.HoveredCard is null && !prepared.Screen.IsCardSelectionOverlay &&
                            prepared.Screen.HasCardTooltip && prepared.Screen.TooltipRegion is { Top: >= .65, Bottom: >= .86 } } };
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        capacity.Release(); Interlocked.Increment(ref _errors); OnError?.Invoke(exception); continue;
                    }
                    var screen = prepared.Screen;
                    if (prepared.Titles.Any(item => item.Source == CardSightSource.PlayPreview &&
                        CompanionCardRules.ThinningPairs.Contains(item.Card.Id)))
                        thinningPriorityUntil = sample.SampledAt.AddSeconds(7);
                    var opponentPlayed = screen.OpponentHandCount is {} enemyHand && opponentHand is {} oldEnemyHand && enemyHand < oldEnemyHand ||
                        prepared.Titles.Any(sight => sight.Side == GwentCompanion.Core.Domain.PlayerSide.Opponent && sight.Source == CardSightSource.PlayPreview);
                    var userPlayed = screen.UserHandCount is {} hand && userHand is {} oldHand && hand < oldHand;
                    var hudChanged = userPlayed ||
                        screen.UserScore is {} own && userScore is {} oldOwn && own != oldOwn ||
                        screen.OpponentScore is {} other && opponentScore is {} oldOther && other != oldOther;
                    userScore = screen.UserScore ?? userScore; opponentScore = screen.OpponentScore ?? opponentScore;
                    userHand = screen.UserHandCount ?? userHand;
                    opponentHand = screen.OpponentHandCount ?? opponentHand;
                    if (opponentPlayed || hudChanged || prepared.NeedsArtwork || prepared.Titles.Any(s => s.Source == CardSightSource.PlayPreview))
                        WorkPriority?.SignalAction();
                    // Snapshot the latest confirmed HUD for scheduling only. Most HUD
                    // readings occur on text-only frames, which the art worker can skip.
                    // Do not copy these cached values into Screen as fresh measurements.
                    var item = new PreparedInput(prepared, sample.Context, userScore, opponentScore, userHand, opponentHand);
                    try { OnPreparedText?.Invoke(prepared.TextResult, sample.Context); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { Interlocked.Increment(ref _errors); OnError?.Invoke(exception); }
                    pending.Enqueue(item);
                    PreparedFrames++;
                    MaximumPendingFrames = Math.Max(MaximumPendingFrames, pending.Count);
                    // A framed, white-letter header that OCR could not resolve needs the
                    // artwork path more than a preview whose exact title already survived.
                    var thinningPriority = thinningPriorityUntil is { } until && sample.SampledAt <= until;
                    // A player hand decrement is the best bounded opportunity to
                    // recover a missed/tutored play and its settled board arrival.
                    // Give it the same queue protection as an opponent hand drop.
                    artwork.Offer(item, sample.SampledAt, opponentPlayed || userPlayed || thinningPriority ? Math.Max(3, sample.Priority) :
                        prepared.NeedsArtwork || hudChanged ? Math.Max(2, sample.Priority) : sample.Priority);
                    WorkPriority?.ObservePressure(artwork.Count, TimeSpan.Zero);
                    SkippedArtworkFrames = artwork.Dropped;
                }
            }
            finally { artwork.Complete(); }
        });
        try
        {
            await foreach (var selected in artwork.ReadAllAsync().WithCancellation(stop.Token).ConfigureAwait(false))
            {
                stop.Token.ThrowIfCancellationRequested();
                // These frames no longer have artwork pending. Commit their text
                // before starting the next expensive scan instead of delaying both.
                while (pending.TryPeek(out var earlier) && earlier.Prepared.SampledAt < selected.Prepared.SampledAt)
                {
                    if (!pending.TryDequeue(out earlier)) break;
                    capacity.Release();
                    yield return new VisionOutput<T>(pipeline.Commit(earlier.Prepared.TextResult), earlier.Context);
                }
                CardVisionResult recognized;
                try
                {
                    using var recognition = WorkPriority?.EnterRecognition();
                    if (!selected.Prepared.HoverInPlayerHand && !selected.Prepared.Screen.IsCardSelectionOverlay)
                        schedule.ObserveBoardHover(selected.Prepared.HoveredCard?.Id);
                    schedule.ObservePreview(selected.Prepared.SampledAt, selected.Prepared.Titles);
                    recognized = pipeline.RecognizePrepared(selected.Prepared, schedule.NextIncludesBoard(selected.Prepared.SampledAt,
                        selected.UserScore, selected.OpponentScore, selected.UserHand,
                        selected.Prepared.HoverInPlayerHand && selected.Prepared.HoveredCard is not null, selected.OpponentHand,
                        artworkBacklogged: artwork.Count >= 3));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Increment(ref _errors); OnError?.Invoke(exception); recognized = selected.Prepared.TextResult;
                }
                if (recognized.ArtworkWasScanned) ArtworkPasses++;
                schedule.ObserveArtwork(recognized.Sightings, recognized.BoardWasScanned);
                while (pending.TryPeek(out var next) && next.Prepared.SampledAt <= selected.Prepared.SampledAt)
                {
                    if (!pending.TryDequeue(out next)) break;
                    capacity.Release();
                    var result = ReferenceEquals(next, selected) ? recognized : next.Prepared.TextResult;
                    yield return new VisionOutput<T>(pipeline.Commit(result), next.Context);
                }
            }
            await producer.ConfigureAwait(false);
            // The newest artwork sample is normally retained. Flush any remaining text
            // even when stopping during a skipped frame or after a recoverable failure.
            while (pending.TryDequeue(out var next))
            {
                capacity.Release();
                yield return new VisionOutput<T>(pipeline.Commit(next.Prepared.TextResult), next.Context);
            }
        }
        finally
        {
            stop.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed record PreparedInput(PreparedVisionFrame Prepared, T Context, int? UserScore, int? OpponentScore, int? UserHand, int? OpponentHand);
}
