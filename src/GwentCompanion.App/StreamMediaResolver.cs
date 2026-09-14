using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public sealed record ResolvedStreamMedia(string MediaUrl, string? Title, string? Channel, double? DurationSeconds,
    Uri[] DeckUris, DateTimeOffset? PublishedAtUtc);

public static class StreamMediaResolver
{
    public static string? FindExecutable(string dataRoot)
    {
        for (var current = new DirectoryInfo(dataRoot); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, ".tools", "yt-dlp", "yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<ResolvedStreamMedia> ResolveAsync(string executable, StreamSourceIdentity source,
        CancellationToken cancellationToken)
    {
        var metadata = await RunAsync(executable,
            ["--no-playlist", "--skip-download", "--no-warnings", "--print", "%(title)j", "--print", "%(channel)j",
             "--print", "%(duration)s", "--print", "%(release_timestamp)s", "--print", "%(timestamp)s",
             "--print", "%(description)j", source.CanonicalUri.AbsoluteUri], cancellationToken).ConfigureAwait(false);
        var lines = metadata.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var title = JsonString(lines.ElementAtOrDefault(0)); var channel = JsonString(lines.ElementAtOrDefault(1));
        double? duration = double.TryParse(lines.ElementAtOrDefault(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds : null;
        var publishedAtUtc = UnixTime(lines.ElementAtOrDefault(3)) ?? UnixTime(lines.ElementAtOrDefault(4));
        var description = JsonString(lines.ElementAtOrDefault(5));
        var deckUris = DeckLinkFileReader.FromText(description ?? "", "Stream description")
            .Select(entry => entry.DeckUri).Distinct().Take(3).ToArray();
        // Audio is irrelevant to vision, and older uploads increasingly expose no
        // muxed format. Prefer a video-only HTTP stream at <=720p, then fall back
        // to any video stream rather than failing an otherwise readable archive.
        var resolved = await RunAsync(executable,
            ["--no-playlist", "--no-warnings", "--format",
             "bestvideo[height<=720][protocol^=http]/bestvideo[height<=720]/bestvideo/best",
             "--get-url", source.CanonicalUri.AbsoluteUri], cancellationToken).ConfigureAwait(false);
        var mediaUrl = resolved.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(mediaUrl)) throw new InvalidOperationException("The media resolver returned no playable URL.");
        return new(mediaUrl.Trim(), title, channel, duration, deckUris, publishedAtUtc);
    }

    private static DateTimeOffset? UnixTime(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? JsonString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "null") return null;
        try { return JsonSerializer.Deserialize<string>(value); }
        catch (JsonException) { return value; }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the stream resolver.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"The stream resolver exited with code {process.ExitCode}." : stderr.Trim().Split('\n').Last().Trim());
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
