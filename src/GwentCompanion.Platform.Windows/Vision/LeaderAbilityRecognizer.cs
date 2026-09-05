using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;
using System.IO;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record LeaderAbilityReading(CardDefinition Card, DateTimeOffset At, double Confidence,
    NormalizedRegion Region, int AgreeingFrames, string Evidence);

/// <summary>
/// Reads the persistent opponent ability medallion from its fixed HUD position.
/// This deliberately has its own tiny feature index: running the full card-art
/// detector over the animated leader model would be slower and less reliable.
/// </summary>
public sealed class LeaderAbilityRecognizer : IDisposable
{
    private sealed record Reference(CardDefinition Card, Mat Descriptors);
    private readonly SIFT _sift = SIFT.Create(420, 3, .022, 10, 1.4);
    private readonly List<Reference> _references = [];
    private readonly FlannBasedMatcher? _matcher;
    private readonly Queue<(string Id, double Confidence, DateTimeOffset At)> _recent = new();
    private LeaderAbilityReading? _confirmed;
    private DateTimeOffset _lastScan;
    public Action<string>? Trace { get; set; }

    public LeaderAbilityRecognizer(IEnumerable<CardDefinition> catalog, string? assetDirectory = null)
    {
        assetDirectory ??= Path.Combine(AppContext.BaseDirectory, "vision-assets", "leaders");
        foreach (var card in GwentCompanion.Core.Data.GwentOneCardCatalog.StartingLeaders(catalog))
        {
            var path = Path.Combine(assetDirectory, card.Id + ".jpg");
            using var source = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (source.Empty()) continue;
            // Public ability art is a card-shaped plate. The distinctive emblem is
            // confined to the middle; excluding the faction texture prevents every
            // leader from the same faction looking artificially similar.
            var rect = new Rect((int)(source.Width * .25), (int)(source.Height * .20),
                (int)(source.Width * .50), (int)(source.Height * .48));
            using var emblem = new Mat(source, rect);
            // Match the reference's information content to the tiny live badge
            // before enlarging it. Descriptors made directly from the pristine
            // 250px plate do not survive the HUD's ~24px rasterization reliably.
            using var reduced = new Mat(); using var enlarged = new Mat();
            Cv2.Resize(emblem, reduced, new Size(30, 41), 0, 0, InterpolationFlags.Area);
            Cv2.Resize(reduced, enlarged, new Size(120, 164), 0, 0, InterpolationFlags.Cubic);
            var descriptors = new Mat();
            _sift.DetectAndCompute(enlarged, null, out _, descriptors);
            if (descriptors.Rows < 6) { descriptors.Dispose(); continue; }
            _references.Add(new(card, descriptors));
        }
        if (_references.Count == 0) return;
        _matcher = new FlannBasedMatcher(new KDTreeIndexParams(4), new SearchParams(64));
        _matcher.Add(_references.Select(item => item.Descriptors));
        _matcher.Train();
    }

    public LeaderAbilityReading? Observe(PixelFrame frame, GwentVisualObservation screen, DateTimeOffset at)
    {
        if (_matcher is null || screen.View != GwentViewKind.Board || screen.IsCardSelectionOverlay || screen.MatchHudVisible == false)
            return _confirmed;
        // The starting ability is persistent. Recheck it periodically once confirmed;
        // return the original timestamp, never turn cached identity into fresh evidence.
        if (_confirmed is not null && at >= _lastScan && at - _lastScan < TimeSpan.FromSeconds(2)) return _confirmed;
        _lastScan = at;
        using var pixels = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        var region = GwentLayout.OpponentLeaderAbility;
        var ranked = Rank(pixels, region, null);
        var pass = "tight";
        // Some ability plaques extend below or to either side of the legacy crop.
        // Preserve that proven path first, then retry against a complete plaque
        // normalized to the same descriptor dimensions as the references. This is
        // a fallback only, so wider animated HUD content cannot displace a sound
        // result from the established detector.
        if (!IsUsable(ranked, 1))
        {
            region = GwentLayout.OpponentLeaderAbilityWide;
            ranked = Rank(pixels, region, new Size(120, 164));
            pass = "wide";
        }
        // The wider crop contains more animated HUD material, so require a
        // two-vote lead in addition to temporal agreement. The tight detector
        // keeps its established one-vote rule for low-detail emblems.
        if (!IsUsable(ranked, pass == "wide" ? 2 : 1)) return _confirmed;
        var best = ranked[0]; var runnerUp = ranked.Length > 1 ? ranked[1].Value : 0;
        Trace?.Invoke($"Leader icon ({pass}) {at:HH:mm:ss.fff}: {_references[best.Key].Card.Name} {best.Value} good feature(s); runner-up {runnerUp}.");
        // One-vote separation is sufficient only because the same identity must
        // win three recent frames. This admits sparse 24px emblems without letting
        // a single ambiguous frame establish deck metadata.
        var confidence = Math.Clamp(.52 + best.Value * .035 + (best.Value - runnerUp) * .025, .54, .96);
        var candidate = _references[best.Key].Card;
        _recent.Enqueue((candidate.Id, confidence, at));
        while (_recent.Count > 7 || _recent.Count > 0 && at - _recent.Peek().At > TimeSpan.FromSeconds(3)) _recent.Dequeue();
        var agreement = _recent.Count(item => item.Id == candidate.Id);
        if (agreement < 3) return _confirmed;
        _confirmed = new(candidate, at, _recent.Where(item => item.Id == candidate.Id).Average(item => item.Confidence),
            region, agreement, $"Persistent opponent HUD ability emblem matched {agreement} recent frames using the {pass} plaque crop; visual identity is independent of deck consensus.");
        return _confirmed;
    }

    private KeyValuePair<int, int>[] Rank(Mat pixels, NormalizedRegion region, Size? normalizedSize)
    {
        var rect = new Rect(region.PixelLeft(pixels.Width), region.PixelTop(pixels.Height),
            region.PixelRight(pixels.Width) - region.PixelLeft(pixels.Width),
            region.PixelBottom(pixels.Height) - region.PixelTop(pixels.Height));
        using var crop = new Mat(pixels, rect);
        using var gray = new Mat(); using var enlarged = new Mat(); using var descriptors = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Resize(gray, enlarged, normalizedSize ?? new Size(crop.Width * 4, crop.Height * 4), 0, 0, InterpolationFlags.Cubic);
        _sift.DetectAndCompute(enlarged, null, out _, descriptors);
        var votes = new Dictionary<int, int>();
        if (descriptors.Rows >= 4)
        {
            var matches = _matcher!.KnnMatch(descriptors, 2);
            foreach (var pair in matches)
            {
                // Small live medallions are rescaled and can be partially composited over
                // the animated leader model. A .70 ratio discarded every Fruits of
                // Ysgith match in recorded footage even though it remained the stable
                // best identity. The vote floor, runner-up margin and three-frame
                // agreement below still guard this slightly wider feature ratio.
                if (pair.Length < 2 || pair[0].Distance >= pair[1].Distance * .78f) continue;
                votes[pair[0].ImgIdx] = votes.GetValueOrDefault(pair[0].ImgIdx) + 1;
            }
        }
        return votes.OrderByDescending(item => item.Value).Take(2).ToArray();
    }

    private static bool IsUsable(KeyValuePair<int, int>[] ranked, int requiredMargin) =>
        ranked.Length > 0 && ranked[0].Value >= 4 && ranked[0].Value >= (ranked.Length > 1 ? ranked[1].Value : 0) + requiredMargin;

    public void Reset() { _recent.Clear(); _confirmed = null; }

    public void Dispose()
    {
        _matcher?.Dispose(); _sift.Dispose();
        foreach (var reference in _references) reference.Descriptors.Dispose();
    }
}
