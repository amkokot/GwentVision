using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

internal static class StreamValidationCorpusTests
{
    private sealed record Manifest(int SchemaVersion, int FileCount, long Bytes, string CorpusSha256,
        Anonymization Anonymization, Source[] Sources, Case[] Cases);
    private sealed record Anonymization(string Status, string Version, bool MetadataRemoved,
        string[] ProtectedRegions, PrivacyMask[] DefaultMasks);
    private sealed record Source(string SourceTag, string VideoTag);
    private sealed record Case(string Id, string SourceTag, string VideoTag, string Summary, string[] Expected,
        string[] Rejected, Frame[] Frames, PrivacyMask[]? PrivacyMasks = null, string? Procedure = null);
    private sealed record Frame(string File, double Seconds, string Role);
    private sealed record PrivacyMask(string Name, int X, int Y, int Width, int Height);

    public static void Run(string project)
    {
        var root = Path.GetFullPath(Path.Combine(project, "tests", "stream-validation"));
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "manifest.json")))
            ?? throw new InvalidDataException("Stream validation manifest is empty.");
        Check(manifest.SchemaVersion == 1, "Unknown stream validation schema.");
        Check(manifest.Anonymization is { Status: "anonymized", Version: "stream-identity-mask-v2",
            MetadataRemoved: true } && manifest.Anonymization.ProtectedRegions.Length >= 4 &&
            manifest.Anonymization.DefaultMasks.Length >= 2,
            "Stream validation anonymization policy is absent or incomplete.");
        Check(manifest.Sources.Select(item => item.SourceTag).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3,
            "Required anonymous source diversity is missing.");
        Check(manifest.Sources.All(item => item.SourceTag.StartsWith("source-", StringComparison.Ordinal) &&
            item.VideoTag.StartsWith("video-", StringComparison.Ordinal)), "A source is not anonymously tagged.");
        foreach (var required in new[] { "source-a-round-draw-negative", "source-a-corner-overlay-actions",
                     "source-b-target-cancel-retry", "source-b-artifact-board", "source-b-dense-crow-board",
                     "source-c-round-redraw-negative", "source-c-portal-hive-mind", "source-c-layered-board-actions" })
            Check(manifest.Cases.Any(item => item.Id == required), "Required reviewed case is missing: " + required);
        foreach (var required in new[] { "source-d-deck-builder-overlay", "source-e-deck-builder-overlay",
                     "source-h-edited-game-boundary", "source-i-round-result-continuity",
                     "source-i-roman-first-round-boundary" })
            Check(manifest.Cases.Any(item => item.Id == required), "Required expanded stream case is missing: " + required);

        var privateTokens = new[] { "shinmiri", "qcento", "kerpeten", "theabeasty", "dosen", "platinum patrol",
            "youtube.com", "youtu.be", "PLw3URzeT-Y", "kOUypE8b-6s", "Cp909pw7WJQ", "jQpmwhiEmAk",
            "PQsyFanzWsw", "Lx0_Rr9EHZw", "vJruYfmEF9s", "U85nGCWGRI4", "Ym9he8QfAyY", "40wAtT9qXBM",
            "rY_ZZhABjvA", "LZF_2PKYLHE", "FUVwa0SDm9Q", "kPUTqy8u18E", "-nUQlRNaU78",
            "x0xgZ2-ImmE", "gnAwXV4kgFc", "a2hL-YcMamU", "hs5jlNEb7SI", "IIqDlqPs5FM",
            "i44oRttskwY", "RJuv0UDQO78", "_n2bZDUl05k", "zBul3y8z5mQ", "kungfoorabbit" };
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Check(!privateTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)),
                "Identifying source data leaked into a validation path: " + path);
            if (new[] { ".json", ".md", ".txt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(path);
                Check(!privateTokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)),
                    "Identifying source data leaked into validation metadata: " + path);
            }
        }

        var frames = manifest.Cases.SelectMany(@case => @case.Frames.Select(frame => (Case: @case, Frame: frame))).ToArray();
        Check(frames.Length == manifest.FileCount && frames.Select(item => item.Frame.File)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == manifest.FileCount,
            "Stream corpus count or path uniqueness is invalid.");
        var lines = new List<string>(); long bytes = 0;
        foreach (var entry in frames.OrderBy(item => item.Frame.File, StringComparer.Ordinal))
        {
            Check(entry.Frame.Seconds >= 0 && !string.IsNullOrWhiteSpace(entry.Frame.Role), "A frame lacks a manual timestamp or role.");
            var path = Path.GetFullPath(Path.Combine(root, entry.Frame.File.Replace('/', Path.DirectorySeparatorChar)));
            Check(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Stream corpus entry escapes its root: " + entry.Frame.File);
            var info = new FileInfo(path);
            Check(info.Exists && info.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase),
                "Missing stream validation frame: " + entry.Frame.File);
            CheckPrivacyMasks(path, entry.Frame.File, manifest.Anonymization.DefaultMasks,
                entry.Case.PrivacyMasks ?? []);
            bytes += info.Length;
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            lines.Add($"{entry.Frame.File}|{info.Length}|{hash}");
        }
        Check(bytes == manifest.Bytes && bytes <= 16L * 1024 * 1024,
            "Stream corpus size changed or exceeded its compact bound.");
        var corpusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        Check(corpusHash.Equals(manifest.CorpusSha256, StringComparison.OrdinalIgnoreCase),
            "Stream validation corpus hash mismatch.");
        Check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(path =>
            new[] { ".mp4", ".mkv", ".webm", ".mov", ".avi" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)),
            "A source video entered the compact stream validation corpus.");
        Console.WriteLine($"PASS stream validation corpus: {manifest.FileCount} manually labelled frames, " +
            $"{manifest.Cases.Length} cases, {manifest.Sources.Length} videos, {bytes / 1024d / 1024d:F2} MiB, no source video.");
    }

    private static void CheckPrivacyMasks(string path, string relativePath, PrivacyMask[] defaults,
        PrivacyMask[] additional)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Color);
        Check(!image.Empty() && image.Width == 960 && image.Height == 540,
            "Stream validation frame has unexpected dimensions: " + relativePath);
        foreach (var mask in defaults.Concat(additional))
        {
            Check(mask.X >= 0 && mask.Y >= 0 && mask.Width > 20 && mask.Height > 20 &&
                mask.X + mask.Width <= image.Width && mask.Y + mask.Height <= image.Height,
                "Invalid privacy-mask bounds in manifest: " + mask.Name);
            var inset = Math.Min(10, Math.Min(mask.Width / 4, mask.Height / 4));
            CheckDarkRegion(image, new Rect(mask.X + inset, mask.Y + inset,
                mask.Width - inset * 2, mask.Height - inset * 2), relativePath, mask.Name);
        }
    }

    private static void CheckDarkRegion(Mat image, Rect rectangle, string relativePath, string region)
    {
        using var crop = new Mat(image, rectangle);
        Cv2.MeanStdDev(crop, out var mean, out var deviation);
        Check(mean.Val0 < 24 && mean.Val1 < 24 && mean.Val2 < 24 &&
              deviation.Val0 < 4 && deviation.Val1 < 4 && deviation.Val2 < 4,
            $"Unanonymized {region} in stream validation frame: {relativePath}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
