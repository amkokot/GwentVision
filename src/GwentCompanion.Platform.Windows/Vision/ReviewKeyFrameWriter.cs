using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Unlabelled review packs, independent of card recognition. Never used as automatic training labels.</summary>
public sealed class ReviewKeyFrameWriter(string directory, int maximumClips = 200, long maximumBytes = 100 * 1024 * 1024)
{
    private readonly PreviewReviewSelector<BitmapSource> _selector = new();
    public int SavedClips { get; private set; }
    public int SkippedClips { get; private set; }
    public long SavedBytes { get; private set; }
    public void Observe(BitmapSource frame, DateTimeOffset at, PreviewMotion motion)
    {
        foreach (var clip in _selector.Observe(frame, at, motion)) Save(clip);
    }
    public void Finish()
    {
        foreach (var clip in _selector.Flush()) Save(clip);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "review-summary.json"), JsonSerializer.Serialize(new
        {
            SavedClips, SkippedClips, SavedBytes, MaximumClips = maximumClips, MaximumBytes = maximumBytes,
            Labels = "Unlabelled pixel-change candidates, not confirmed plays. Includes unknowns and false triggers; misses remain possible.",
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Save(PreviewReviewClip<BitmapSource> clip)
    {
        if (SavedClips >= maximumClips || SavedBytes >= maximumBytes) { SkippedClips++; return; }
        var target = Path.Combine(directory, $"{clip.TriggeredAt:HHmmssfff}-{clip.Side}");
        Directory.CreateDirectory(target);
        foreach (var (name, frame) in new[] { ("before", clip.Before), ("during", clip.During), ("after", clip.After) })
        {
            if (frame is null) continue;
            using var output = File.Create(Path.Combine(target, name + ".jpg"));
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(frame.Value));
            encoder.Save(output);
            SavedBytes += output.Length;
        }
        File.WriteAllText(Path.Combine(target, "candidate.json"), JsonSerializer.Serialize(new
        {
            Side = clip.Side.ToString(), clip.TriggeredAt, clip.Change, Before = clip.Before.At, During = clip.During.At,
            After = clip.After?.At, Label = (string?)null, Status = "Needs review; pixel change is not proof of a play",
        }, new JsonSerializerOptions { WriteIndented = true }));
        SavedClips++;
    }
}
