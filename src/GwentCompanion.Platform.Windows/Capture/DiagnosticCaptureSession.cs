using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Windows;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.Platform.Windows.Capture;

public sealed record DiagnosticProgress(
    int CapturedFrames,
    int SavedFrames,
    string SessionDirectory,
    BitmapSource? Preview,
    BitmapSource? AnalysisFrame = null,
    GwentVisualObservation? Observation = null,
    string? Error = null,
    DateTimeOffset? SampledAt = null,
    double PreviewChange = 0, bool PointerInPlayerHand = false, double OpponentPreviewChange = 0, double TrackingPriority = 0);

public sealed class DiagnosticCaptureSession : IAsyncDisposable
{
    private readonly Win32FrameCapture _capture;
    private readonly string _sessionRoot;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _retainTrainingFrames;

    public DiagnosticCaptureSession(Win32FrameCapture capture, string? sessionRoot = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _sessionRoot = sessionRoot ?? ResolveSessionRoot();
    }

    public event EventHandler<DiagnosticProgress>? Progress;

    public bool IsRunning => _worker is { IsCompleted: false };
    public bool RetainTrainingFrames { get => _retainTrainingFrames; set => _retainTrainingFrames = value; }
    public string? CurrentSessionDirectory { get; private set; }

    public void Start(GwentWindowSnapshot window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (IsRunning)
        {
            throw new InvalidOperationException("A diagnostic capture session is already running.");
        }

        CurrentSessionDirectory = Path.Combine(
            _sessionRoot,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(CurrentSessionDirectory);

        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(window.Handle, CurrentSessionDirectory, _cancellation.Token));
    }

    public async Task StopAsync()
    {
        if (_worker is null)
        {
            return;
        }

        _cancellation?.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the user stops a session.
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _worker = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(nint windowHandle, string directory, CancellationToken cancellationToken)
    {
        const int intervalMilliseconds = 100;
        const int maximumSavedFrames = 18000;
        const long maximumSavedBytes = 2L * 1024 * 1024 * 1024;

        var locator = new GwentWindowService();
        var started = DateTimeOffset.Now;
        var captured = 0;
        var saved = 0;
        long savedBytes = 0;
        var retentionLimitReached = false;
        var lastSavedAt = DateTimeOffset.MinValue;
        var visualDetector = new GwentVisualStateDetector();
        var motionSampler = new PreviewMotionSampler();
        ReviewKeyFrameWriter? review = null;
        var lastReviewAt = DateTimeOffset.MinValue;
        string? reviewError = null;
        string? finalError = null;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMilliseconds));
            do
            {
                try
                {
                    var window = locator.Snapshot(windowHandle);
                    var fullFrame = _capture.Capture(window);
                    var frame = ScaleToWidth(fullFrame, 1280);
                    // Keep the small opponent-board artwork at recording resolution.
                    // Preview-only passes downscale inside the pipeline; the queue is bounded.
                    var analysisFrame = frame;
                    var analysisPixels = BitmapFrameAdapter.ToPixelFrame(analysisFrame);
                    var observation = visualDetector.Analyze(analysisPixels);
                    var previewMotion = new PreviewMotion(0, 0);
                    captured++;

                    var now = DateTimeOffset.Now;
                    if (now - lastReviewAt >= TimeSpan.FromMilliseconds(190))
                    {
                        // Measure at the retained cadence, so a change on an intervening
                        // capture cannot disappear before it reaches the rolling buffer.
                        previewMotion = motionSampler.Measure(analysisPixels);
                        if (RetainTrainingFrames && reviewError is null)
                        {
                            try { (review ??= new ReviewKeyFrameWriter(Path.Combine(directory, "review-keyframes"))).Observe(analysisFrame, now, previewMotion); }
                            catch (IOException exception) { reviewError = "Review packs disabled: " + exception.Message; }
                        }
                        lastReviewAt = now;
                    }
                    // Keep five frames/second regardless of global image change. Small, brief
                    // special-card animations were previously lost against a static battlefield.
                    var periodic = now - lastSavedAt >= TimeSpan.FromMilliseconds(190);
                    retentionLimitReached = saved >= maximumSavedFrames || savedBytes >= maximumSavedBytes;
                    if (RetainTrainingFrames && !retentionLimitReached && (saved == 0 || periodic))
                    {
                        var file = Path.Combine(directory, $"frame-{captured:000000}-{now:HHmmssfff}.jpg");
                        SaveJpeg(frame, file);
                        savedBytes += new FileInfo(file).Length;
                        saved++;
                        lastSavedAt = now;
                    }

                    var preview = observation.View == GwentViewKind.MoveHistory || captured % 10 == 0
                        ? frame
                        : null;
                    Progress?.Invoke(this, new DiagnosticProgress(
                        captured,
                        saved,
                        directory,
                        preview,
                        analysisFrame,
                        observation,
                        Error: RetainTrainingFrames && retentionLimitReached ? "Recording limit reached (2 GB or 18,000 frames); live analysis continues, but later frames are not retained." : reviewError,
                        SampledAt: now,
                        PreviewChange: previewMotion.Maximum, OpponentPreviewChange: previewMotion.Opponent));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    finalError = exception.Message;
                    Progress?.Invoke(this, new DiagnosticProgress(
                        captured,
                        saved,
                        directory,
                        null,
                        null,
                        Error: exception.Message));
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected shutdown.
        }
        finally
        {
            try { review?.Finish(); }
            catch (Exception exception) { reviewError ??= "Review pack: " + exception.Message; }
            var manifest = new
            {
                Started = started,
                Ended = DateTimeOffset.Now,
                CapturedFrames = captured,
                SavedFrames = saved,
                LastError = finalError,
                ReviewError = reviewError,
                CaptureMethod = "Win32 BitBlt external window crop",
                RetentionLimitReached = retentionLimitReached,
                SavedBytes = savedBytes,
                TrainingRecordingEnabledAtEnd = RetainTrainingFrames,
                RetainedIntervalMilliseconds = 200,
                MaximumImageWidth = 1280,
                Version = 2,
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(directory, "session.json"), json).ConfigureAwait(false);
        }
    }

    private static BitmapSource ScaleToWidth(BitmapSource source, int maximumWidth)
    {
        if (source.PixelWidth <= maximumWidth)
        {
            return source;
        }

        var scale = maximumWidth / (double)source.PixelWidth;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        // Detach the small frame so the rolling buffer cannot retain full-resolution parents.
        var stride = (transformed.PixelWidth * transformed.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * transformed.PixelHeight];
        transformed.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(transformed.PixelWidth, transformed.PixelHeight, 96, 96,
            transformed.Format, transformed.Palette, pixels, stride);
        detached.Freeze();
        return detached;
    }

    private static void SaveJpeg(BitmapSource source, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ResolveSessionRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "GwentCompanion", "sessions");
        }

        return Path.Combine(AppContext.BaseDirectory, "sessions");
    }
}
