using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

internal static partial class CaptionArchiveScan
{
    private static readonly Regex Timestamp = TimestampRegex();
    private static readonly Regex Tags = TagsRegex();
    private static readonly Regex Space = SpaceRegex();

    public static int Run(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--video-caption-scan");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --video-caption-scan <vtt-file>.");
        var input = Path.GetFullPath(args[inputIndex + 1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Caption input not found.", input);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.ChangeExtension(input, ".mentions.json");
        var visualIndex = Array.IndexOf(args, "--visual-recognition");
        var visual = visualIndex >= 0 ? LoadVisualEvidence(Path.GetFullPath(args[visualIndex + 1])) : [];
        var catalog = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json")))
            .Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact or CardKind.Stratagem)
            .Where(card => card.Name.Length >= 4)
            .DistinctBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(card => card.Name.Length).ToArray();
        var cues = Parse(input);
        var mentions = new List<Mention>();
        var last = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in cues)
        foreach (var card in catalog)
        {
            if (!Regex.IsMatch(cue.Text, "(?<![\\p{L}\\p{N}])" + Regex.Escape(card.Name) + "(?![\\p{L}\\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;
            if (last.TryGetValue(card.Name, out var previous) && cue.Seconds - previous < 8) continue;
            last[card.Name] = cue.Seconds;
            var visualAt = visual.Where(item => item.Name.Equals(card.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => Math.Abs(item.Seconds - cue.Seconds)).FirstOrDefault();
            var corroborated = visualAt is not null && Math.Abs(visualAt.Seconds - cue.Seconds) <= 20;
            var multiword = card.Name.Any(char.IsWhiteSpace) || card.Name.Contains(':');
            mentions.Add(new Mention(cue.Seconds, card.Id, card.Name, card.Faction, card.Kind.ToString(), card.Provision,
                corroborated ? .98 : multiword ? .72 : .38, corroborated,
                corroborated ? visualAt!.Seconds : null, cue.Text.Length <= 220 ? cue.Text : cue.Text[..220]));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Input = Path.GetFileName(input), CueCount = cues.Count, Mentions = mentions,
            Note = "Exact auto-caption matches with an eight-second duplicate guard. Single-word names are low confidence unless a visual candidate occurs within twenty seconds; captions can still mishear names.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE caption-scan cues={cues.Count} mentions={mentions.Count} visual-corroborated={mentions.Count(item => item.VisualCorroborated)} output={output}");
        return 0;
    }

    private static List<Cue> Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        var result = new List<Cue>();
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Timestamp.Match(lines[index]);
            if (!match.Success) continue;
            var seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                          int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                          int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) +
                          int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) / 1000.0;
            var text = new List<string>();
            while (++index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) text.Add(lines[index]);
            var cleaned = Space.Replace(WebUtility.HtmlDecode(Tags.Replace(string.Join(' ', text), " ")), " ").Trim();
            if (cleaned.Length > 0) result.Add(new Cue(seconds, cleaned));
        }
        return result;
    }

    private static VisualMention[] LoadVisualEvidence(string path)
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Findings").EnumerateArray().SelectMany(finding =>
        {
            var seconds = finding.GetProperty("Seconds").GetDouble();
            return finding.GetProperty("Sightings").EnumerateArray().Select(sighting =>
                new VisualMention(seconds, sighting.GetProperty("Name").GetString()!));
        }).ToArray();
    }

    private sealed record Cue(double Seconds, string Text);
    private sealed record VisualMention(double Seconds, string Name);
    private sealed record Mention(double Seconds, string Id, string Name, string Faction, string Kind, int Provisions,
        double Confidence, bool VisualCorroborated, double? VisualSeconds, string Context);

    [GeneratedRegex(@"^(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s+-->", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagsRegex();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRegex();
}
