using System.IO;
using GwentCompanion.Core.Domain;
using OpenCvSharp;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

public static class VisionReferenceLibrary
{
    public static IReadOnlyList<(CardDefinition Card, string Path)> Load(IEnumerable<CardDefinition> cards, string cacheDirectory)
    {
        var result = new List<(CardDefinition Card, string Path)>();
        var definitions = cards.GroupBy(card => card.Id).Select(group => group.First()).ToArray();
        var families = new CardAppearanceFamilies(definitions);
        foreach (var card in definitions)
        {
            var portrait = Path.Combine(cacheDirectory, "portraits", card.Id + ".jpg");
            if (File.Exists(portrait)) result.Add((card, portrait));
            var variants = Path.Combine(cacheDirectory, "premium-frames", card.Id);
            // Repair a partially populated local reference cache for any identity.
            // This never downloads during startup and never trains on inferred plays.
            var premiumVideo = Path.Combine(cacheDirectory, "premium", card.Id + ".webm");
            if (File.Exists(premiumVideo) && (!Directory.Exists(variants) || Directory.GetFiles(variants, "*.jpg").Length == 0))
                ExtractPremiumFrames(premiumVideo, variants);
            if (Directory.Exists(variants))
                result.AddRange(Directory.GetFiles(variants, "*.jpg").OrderBy(path => path).Select(path => (card, path)));
            var observed = Path.Combine(cacheDirectory, "observed-art", card.Id);
            if (Directory.Exists(observed))
                result.AddRange(Directory.GetFiles(observed, "*.jpg").OrderBy(path => path).Select(path => (card, path)));
        }
        return result.Select(item => (families.Normalize(item.Card), item.Path)).ToArray();
    }

    public static int ExtractPremiumFrames(string videoPath, string outputDirectory, int count = 12)
    {
        Directory.CreateDirectory(outputDirectory);
        using var video = new VideoCapture(videoPath);
        if (!video.IsOpened()) throw new InvalidDataException($"Cannot read premium video: {videoPath}");
        using var frame = new Mat();
        var written = 0;
        for (var index = 0; index < count; index++)
        {
            video.Set(VideoCaptureProperties.PosFrames, (int)(video.FrameCount * (index + 0.5) / count));
            if (!video.Read(frame) || frame.Empty()) continue;
            using var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(249, (int)(frame.Height * 249.0 / frame.Width)), 0, 0, InterpolationFlags.Area);
            Cv2.ImWrite(Path.Combine(outputDirectory, $"{index:00}.jpg"), resized);
            written++;
        }
        return written;
    }
}
