using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public enum StreamSourceKind { YouTube, Twitch, Web }

/// <summary>A stable source identity. Query-string tracking parameters never enter the key.</summary>
public sealed record StreamSourceIdentity(StreamSourceKind Kind, string ProviderId, Uri CanonicalUri)
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s<>\""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public string Key => $"{Kind.ToString().ToLowerInvariant()}:{ProviderId}";
    public string SafeKey => $"{Kind.ToString().ToLowerInvariant()}-{ProviderId}";

    public static bool TryCreate(string value, out StreamSourceIdentity? source)
    {
        source = null;
        if (!Uri.TryCreate(value.Trim().TrimEnd('.', ',', ';', ')', ']'), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];
        if (host is "youtu.be" or "youtube.com" or "music.youtube.com")
        {
            var id = host == "youtu.be" ? uri.AbsolutePath.Trim('/').Split('/')[0] :
                PathSegmentId(uri, "shorts", "live", "embed") ?? QueryValue(uri, "v");
            if (!ValidProviderId(id)) return false;
            source = new(StreamSourceKind.YouTube, id!, new Uri($"https://www.youtube.com/watch?v={id}"));
            return true;
        }
        if (host.EndsWith("twitch.tv", StringComparison.Ordinal))
        {
            var id = PathSegmentId(uri, "videos");
            if (!ValidProviderId(id)) return false;
            source = new(StreamSourceKind.Twitch, id!, new Uri($"https://www.twitch.tv/videos/{id}"));
            return true;
        }
        // Let the bundled media resolver decide whether another HTTPS host is
        // supported. Its normalized URL hash still deduplicates repeat imports.
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        var retainedQuery = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(parts => !parts[0].StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                !parts[0].Equals("fbclid", StringComparison.OrdinalIgnoreCase))
            .OrderBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(parts => string.Join("=", parts));
        builder.Query = string.Join("&", retainedQuery);
        var canonical = builder.Uri.AbsoluteUri.TrimEnd('/');
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        source = new(StreamSourceKind.Web, Convert.ToHexString(digest)[..20].ToLowerInvariant(), new Uri(canonical));
        return true;
    }

    public static IReadOnlyList<StreamSourceIdentity> FromText(string? text) => string.IsNullOrWhiteSpace(text) ? [] :
        UrlPattern.Matches(text).Select(match => TryCreate(match.Value, out var source) ? source : null)
            .Where(source => source is not null).Cast<StreamSourceIdentity>()
            .DistinctBy(source => source.Key, StringComparer.Ordinal).ToArray();

    private static string? PathSegmentId(Uri uri, params string[] prefixes)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
            if (prefixes.Contains(segments[i], StringComparer.OrdinalIgnoreCase)) return segments[i + 1];
        return null;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && Uri.UnescapeDataString(pieces[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pieces[1]);
        }
        return null;
    }

    private static bool ValidProviderId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 80 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

public sealed record StreamLinkEntry(StreamSourceIdentity Source, string ImportFile, string Sheet,
    int Row, string Column, string? Label = null);

/// <summary>Reads URL cells from XLSX, CSV, TSV or plain text without Office automation.</summary>
public static class StreamLinkImportReader
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<StreamLinkEntry> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var rows = extension == ".xlsx" ? ReadWorkbook(fullPath) : ReadDelimited(fullPath, extension == ".tsv" ? '\t' : ',');
        // One source is scanned once, but preserve the first human-readable provenance.
        return rows.DistinctBy(entry => entry.Source.Key, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<StreamLinkEntry> ReadDelimited(string path, char separator)
    {
        var file = Path.GetFileName(path);
        var rowNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            rowNumber++;
            var cells = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? new[] { line } : ParseDelimitedLine(line, separator).ToArray();
            for (var column = 0; column < cells.Length; column++)
                foreach (var source in StreamSourceIdentity.FromText(cells[column]))
                    yield return new(source, file, "", rowNumber, ColumnName(column + 1), cells[column].Trim());
        }
    }

    private static IEnumerable<StreamLinkEntry> ReadWorkbook(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var shared = archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry
            ? ReadXml(sharedEntry).Descendants(Spreadsheet + "si")
                .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value))).ToArray() : [];
        var workbook = ReadXml(Required(archive, "xl/workbook.xml"));
        var relationships = ReadRelationships(Required(archive, "xl/_rels/workbook.xml.rels"));
        foreach (var sheet in workbook.Descendants(Spreadsheet + "sheet"))
        {
            var name = sheet.Attribute("name")?.Value ?? "Sheet";
            var relation = sheet.Attribute(OfficeRelationships + "id")?.Value;
            if (relation is null || !relationships.TryGetValue(relation, out var target)) continue;
            var worksheetPath = target.Replace('\\', '/').TrimStart('/');
            if (!worksheetPath.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) worksheetPath = "xl/" + worksheetPath;
            var entry = Required(archive, worksheetPath);
            var document = ReadXml(entry);
            var links = ReadHyperlinks(archive, document, worksheetPath);
            foreach (var row in document.Descendants(Spreadsheet + "row"))
            {
                var rowNumber = ParseInt(row.Attribute("r")?.Value);
                foreach (var cell in row.Elements(Spreadsheet + "c"))
                {
                    var reference = cell.Attribute("r")?.Value ?? "A" + rowNumber.ToString(CultureInfo.InvariantCulture);
                    var raw = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
                    var text = cell.Attribute("t")?.Value switch
                    {
                        "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                                 index >= 0 && index < shared.Length => shared[index],
                        "inlineStr" => string.Concat(cell.Descendants(Spreadsheet + "t").Select(item => item.Value)),
                        _ => raw,
                    };
                    links.TryGetValue(reference, out var hyperlink);
                    var formula = cell.Element(Spreadsheet + "f")?.Value;
                    foreach (var source in StreamSourceIdentity.FromText(string.Join("\n", hyperlink, text, formula)))
                        yield return new(source, Path.GetFileName(path), name, rowNumber,
                            new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant(), text.Trim());
                }
            }
        }
    }

    private static Dictionary<string, string> ReadHyperlinks(ZipArchive archive, XDocument document, string worksheetPath)
    {
        var directory = Path.GetDirectoryName(worksheetPath)?.Replace('\\', '/') ?? "xl/worksheets";
        var relationPath = $"{directory}/_rels/{Path.GetFileName(worksheetPath)}.rels";
        var relationships = archive.GetEntry(relationPath) is { } entry ? ReadRelationships(entry) : [];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in document.Descendants(Spreadsheet + "hyperlink"))
        {
            var cell = link.Attribute("ref")?.Value;
            var id = link.Attribute(OfficeRelationships + "id")?.Value;
            if (cell is not null && id is not null && relationships.TryGetValue(id, out var target)) result[cell] = target;
        }
        return result;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchiveEntry entry) => ReadXml(entry)
        .Descendants(PackageRelationships + "Relationship")
        .Where(item => item.Attribute("Id") is not null && item.Attribute("Target") is not null)
        .ToDictionary(item => item.Attribute("Id")!.Value, item => item.Attribute("Target")!.Value, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ParseDelimitedLine(string line, char separator)
    {
        var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (line[i] == separator && !quoted) { yield return value.ToString(); value.Clear(); }
            else value.Append(line[i]);
        }
        yield return value.ToString();
    }

    private static ZipArchiveEntry Required(ZipArchive archive, string path) => archive.GetEntry(path) ??
        throw new InvalidDataException($"The workbook does not contain '{path}'.");
    private static XDocument ReadXml(ZipArchiveEntry entry) { using var stream = entry.Open(); return XDocument.Load(stream); }
    private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static string ColumnName(int number) { var result = ""; while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } return result; }
}

public enum StreamFrameDisposition { OutsideGame, Reliable, Reduced, SkippedObscured }

public sealed record StreamGameBoundary(int Index, double StartSeconds, double EndSeconds, int ReliableSamples,
    int ReducedSamples, int SkippedSamples)
{
    public string Tag(StreamSourceIdentity source) =>
        $"stream:{source.Key}:game:{Index:000}:t{(long)Math.Round(StartSeconds):000000}";
}

/// <summary>Conservative, hysteretic game splitting for edited videos and long VODs.</summary>
public sealed class StreamGameSegmenter(double absenceSeconds = 45)
{
    // A normal match exposes a board-shaped pre-game state before its own
    // ROUND 1 mulligan banner. Treating that banner as an edited-video cut
    // produced a second, 20-second pseudo game. A direct-cut split is only
    // useful after enough prior gameplay to represent a real episode; short
    // disconnects/concessions still close through result or absence signals.
    private const double DirectCutStartupGraceSeconds = 90;
    private readonly double _absenceSeconds = Math.Clamp(absenceSeconds, 10, 180);
    private double? _candidateStart, _lastGameAt;
    private int _startVotes, _index, _reliable, _reduced, _skipped;
    private bool _active, _openingRoundSeen;
    public bool IsActive => _active;

    public StreamGameBoundary? Observe(double seconds, StreamFrameDisposition disposition, bool terminalScreen = false,
        bool newGameStart = false)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var inGame = disposition is StreamFrameDisposition.Reliable or StreamFrameDisposition.Reduced;
        if (_active && newGameStart && _candidateStart is { } priorStart &&
            seconds - priorStart >= DirectCutStartupGraceSeconds)
        {
            var prior = Complete(seconds);
            // Edited videos can cut directly from a passed/result frame to the
            // next ROUND 1. Seed the next candidate with this same observation
            // so a short edit does not merge two independent matches.
            if (inGame) { _candidateStart = seconds; _startVotes = 1; }
            return prior;
        }
        if (_active && newGameStart) _openingRoundSeen = true;
        if (!_active)
        {
            if (!inGame) { _candidateStart = null; _startVotes = 0; return null; }
            _candidateStart ??= seconds;
            if (++_startVotes >= 2) { _active = true; _lastGameAt = seconds; Count(disposition); }
            return null;
        }
        if (inGame) { _lastGameAt = seconds; Count(disposition); return null; }
        if (disposition == StreamFrameDisposition.SkippedObscured) _skipped++;
        if (!terminalScreen && _lastGameAt is { } seen && seconds - seen < _absenceSeconds) return null;
        // Opening board setup can precede a long ROUND 1 redraw. Preserve that
        // same episode through the normal absence timeout; an explicit result
        // can still close a genuine quick concession immediately.
        if (!terminalScreen && _openingRoundSeen && _candidateStart is { } start &&
            seconds - start < DirectCutStartupGraceSeconds) return null;
        // Retain the bounded post-game tail so result/rank/MMR readers can use it.
        return Complete(seconds);
    }

    public StreamGameBoundary? Finish(double finalSeconds) => _active ? Complete(Math.Max(finalSeconds, _lastGameAt ?? finalSeconds)) : null;

    private void Count(StreamFrameDisposition disposition)
    {
        if (disposition == StreamFrameDisposition.Reliable) _reliable++;
        else if (disposition == StreamFrameDisposition.Reduced) _reduced++;
    }

    private StreamGameBoundary Complete(double end)
    {
        var result = new StreamGameBoundary(++_index, _candidateStart ?? end, end, _reliable, _reduced, _skipped);
        _active = _openingRoundSeen = false; _candidateStart = null; _lastGameAt = null;
        _startVotes = _reliable = _reduced = _skipped = 0;
        return result;
    }
}

public sealed record StreamDetectedCard(string CardId, string Name, PlayerSide Side, int EvidenceEvents,
    double FirstSeconds, double LastSeconds, string[] Sources, int ObservedCopies = 1);
public sealed record StreamDeckCard(string CardId, string Name, int Copies);
public sealed record StreamDeckEvidence(string? LeaderId, string? LeaderName, string? StratagemId,
    string? StratagemName, StreamDeckCard[] Cards, double ObservedSeconds, bool CompleteEnoughToUse)
{
    public static StreamDeckEvidence FromDeck(DeckDefinition deck, double observedSeconds = 0) => new(null,
        deck.Leader, deck.Stratagem?.Id, deck.Stratagem?.Name,
        deck.Cards.Select(card => new StreamDeckCard(card.Card.Id, card.Card.Name, card.Count)).ToArray(),
        observedSeconds, deck.CardCount >= 25);
}
public sealed record StreamGameRecord(int SchemaVersion, string StreamTag, string SourceKey, Uri SourceUri,
    string? SourceTitle, string? SourceChannel, int GameIndex, double StartSeconds, double EndSeconds, DateTimeOffset ScannedAtUtc,
    double Reliability, int AnalyzedFrames, int ReducedFrames, int SkippedObscuredFrames,
    StreamDetectedCard[] Cards, StreamDeckEvidence? PlayerDeck, int? FinalUserScore = null,
    int? FinalOpponentScore = null, string? Result = null, int? Rank = null, int? Mmr = null,
    string? DetectorVersion = null, DateTimeOffset? SourcePublishedAtUtc = null);

public sealed class StreamGameStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string Save(StreamGameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);
        // Persist a single canonical representation regardless of the caller's
        // local timezone. Legacy records with explicit offsets remain readable.
        game = game with
        {
            ScannedAtUtc = game.ScannedAtUtc.ToUniversalTime(),
            SourcePublishedAtUtc = game.SourcePublishedAtUtc?.ToUniversalTime(),
        };
        var source = game.SourceKey.Replace(':', '-');
        if (source.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidDataException("Unsafe stream source key.");
        var folder = Path.Combine(DirectoryPath, source);
        Directory.CreateDirectory(folder);
        var name = $"game-{game.GameIndex:000}-t{(long)Math.Round(game.StartSeconds):000000}.gvs.json";
        var path = Path.Combine(folder, name);
        var options = new JsonSerializerOptions { WriteIndented = false };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(game, options);
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<StreamGameRecord>(File.ReadAllBytes(path));
            // Rescans improve the deterministic record only when they contain more
            // usable evidence; equal scans from the same detector remain byte-for-byte
            // idempotent. A different detector version is authoritative so a bug fix
            // can replace an equally dense but semantically stale record.
            if (existing is not null && string.Equals(existing.DetectorVersion, game.DetectorVersion,
                    StringComparison.Ordinal) && (existing.AnalyzedFrames > game.AnalyzedFrames ||
                existing.AnalyzedFrames == game.AnalyzedFrames && existing.Reliability >= game.Reliability)) return path;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}
