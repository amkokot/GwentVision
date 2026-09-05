using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record DeckIndexEntry(
    string SourceId,
    string Workbook,
    string Sheet,
    int Row,
    string Faction,
    string Leader,
    string Name,
    Uri DeckUri,
    Uri? AlternateDeckUri,
    Uri? VideoUri,
    string Notes,
    DateTimeOffset? LastEdited,
    int? RankWithinFaction,
    int RecencyRank,
    IReadOnlyList<DeckPatch>? Patches = null, IReadOnlyList<DeckOccurrence>? Occurrences = null);

public sealed record WorkbookDeckIndex(
    string WorkbookPath,
    IReadOnlyList<string> SheetNames,
    IReadOnlyList<DeckIndexEntry> Entries);

public sealed class WorkbookDeckIndexReader
{
    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly Regex DeckUrlPattern = new(
        @"^https?://(?:www\.)?playgwent\.com/(?:[a-z]{2}(?:-[A-Z]{2})?/)?decks(?:/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public WorkbookDeckIndex Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        using var archive = ZipFile.OpenRead(fullPath);

        var workbook = ReadXml(archive, "xl/workbook.xml");
        var workbookRelationships = ReadRelationships(archive, "xl/_rels/workbook.xml.rels");
        var sharedStrings = ReadSharedStrings(archive);
        var sheets = workbook
            .Descendants(Spreadsheet + "sheet")
            .Select((element, index) => new SheetReference(
                element.Attribute("name")?.Value ?? $"Sheet {index + 1}",
                element.Attribute(OfficeRelationships + "id")?.Value ?? string.Empty,
                index))
            .ToArray();

        var entries = new List<DeckIndexEntry>();
        foreach (var sheet in sheets)
        {
            if (!workbookRelationships.TryGetValue(sheet.RelationshipId, out var target))
            {
                continue;
            }

            var worksheetPath = NormalizeWorkbookTarget(target);
            entries.AddRange(ReadSheet(
                archive,
                Path.GetFileName(fullPath),
                sheet,
                worksheetPath,
                sharedStrings));
        }

        // Local, explicit source correction. Freeze the supplied patch rather than rolling
        // historical workbooks forward each time the application starts in a new month.
        var overridePath = Path.Combine(Path.GetDirectoryName(fullPath)!, "deck-patch-overrides.json");
        if (File.Exists(overridePath))
        {
            var overrides = JsonSerializer.Deserialize<Dictionary<string, DeckPatch>>(File.ReadAllText(overridePath));
            var correction = overrides?.FirstOrDefault(item => item.Key.Equals(Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase)).Value;
            if (correction is not null) entries = entries.Select(entry => entry with { Patches = [correction] }).ToList();
        }
        return new WorkbookDeckIndex(
            fullPath,
            sheets.Select(item => item.Name).ToArray(),
            entries);
    }

    private static IEnumerable<DeckIndexEntry> ReadSheet(
        ZipArchive archive,
        string workbookName,
        SheetReference sheet,
        string worksheetPath,
        IReadOnlyList<string> sharedStrings)
    {
        var document = ReadXml(archive, worksheetPath);
        var hyperlinkTargets = ReadWorksheetHyperlinks(archive, document, worksheetPath);
        var rows = document
            .Descendants(Spreadsheet + "row")
            .Select(element => ReadRow(element, sharedStrings, hyperlinkTargets))
            .Where(row => row.Cells.Count > 0)
            .ToArray();

        var header = rows
            .Take(10)
            .Select(CreateHeader)
            .FirstOrDefault(candidate => candidate.Score >= 2);
        header ??= new HeaderMap(0, new Dictionary<string, string>(), 0);

        var faction = string.Empty;
        foreach (var row in rows.Where(item => item.Number > header.RowNumber))
        {
            var rowFaction = GetByHeader(row, header, "faction");
            if (!string.IsNullOrWhiteSpace(rowFaction))
            {
                faction = rowFaction.Trim();
            }

            var deckCell = FindDeckCell(row, header);
            // Read literal URLs, including embedded text and HYPERLINK formulas;
            // never evaluate a formula. Keep every variant with its sheet/row metadata.
            var rowLinks = row.Cells.Values.SelectMany(cell => DeckLinkFileReader.FromText(
                string.Join("\n", cell.Hyperlink, cell.Text, cell.Formula))).Select(e => e.DeckUri).Distinct().ToArray();
            var deckUrl = DeckLinkFileReader.CanonicalUrl(FindDeckUri(row, deckCell)) ?? rowLinks.FirstOrDefault();
            if (deckUrl is null)
            {
                continue;
            }

            var leader = GetByHeader(row, header, "leader").Trim();
            var primaryName = FirstNonEmpty(
                GetByHeader(row, header, "deck name"),
                GetByHeader(row, header, "description"),
                GetByHeader(row, header, "deck"));
            var linkLabel = deckCell?.Text ?? string.Empty;
            var name = ComposeName(primaryName, linkLabel, deckUrl);
            var alternate = FindUri(GetByHeaderCell(row, header, "alternate version"));
            var video = FindUri(FirstNonNull(
                GetByHeaderCell(row, header, "youtube video"),
                GetByHeaderCell(row, header, "video link")));
            var notes = FirstNonEmpty(
                GetByHeader(row, header, "notes"),
                GetByHeader(row, header, "alternate description"));
            var lastEdited = ParseSpreadsheetDate(GetByHeader(row, header, "last edited"));
            var rank = ParseNullableInt(GetByHeader(row, header, "rank within faction"));

            var entry = new DeckIndexEntry(
                $"{workbookName}|{sheet.Name}|{row.Number}",
                workbookName,
                sheet.Name,
                row.Number,
                faction,
                leader,
                name,
                deckUrl,
                alternate,
                video,
                notes,
                lastEdited,
                rank,
                sheet.Index,
                DeckPatchMetadata.FromSheet(sheet.Name, lastEdited, GetByHeader(row, header, "patch")));
            yield return entry;
            foreach (var extra in rowLinks.Where(uri => uri != deckUrl && uri != DeckLinkFileReader.CanonicalUrl(alternate)))
                yield return entry with { DeckUri = extra, AlternateDeckUri = null, SourceId = entry.SourceId + ":" + extra.Segments[^1] };
        }
    }

    private static HeaderMap CreateHeader(RowData row)
    {
        var columns = row.Cells.Values
            .Where(cell => !string.IsNullOrWhiteSpace(cell.Text))
            .GroupBy(cell => NormalizeHeader(cell.Text))
            .ToDictionary(group => group.Key, group => group.First().Column, StringComparer.OrdinalIgnoreCase);
        var score = columns.Keys.Count(key =>
            key is "faction" or "leader" or "deck" or "deck name" or "description" or "link" or "deck link");
        return new HeaderMap(row.Number, columns, score);
    }

    private static RowData ReadRow(
        XElement row,
        IReadOnlyList<string> sharedStrings,
        IReadOnlyDictionary<string, string> hyperlinkTargets)
    {
        var rowNumber = ParseNullableInt(row.Attribute("r")?.Value) ?? 0;
        var cells = new Dictionary<string, CellData>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var column = GetColumn(reference);
            var type = cell.Attribute("t")?.Value;
            var raw = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
            var text = type switch
            {
                "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                         index >= 0 && index < sharedStrings.Count => sharedStrings[index],
                "inlineStr" => string.Concat(cell.Descendants(Spreadsheet + "t").Select(item => item.Value)),
                _ => raw,
            };

            hyperlinkTargets.TryGetValue(reference, out var hyperlink);
            cells[column] = new CellData(reference, column, text, hyperlink, cell.Element(Spreadsheet + "f")?.Value);
        }

        return new RowData(rowNumber, cells);
    }

    private static IReadOnlyDictionary<string, string> ReadWorksheetHyperlinks(
        ZipArchive archive,
        XDocument worksheet,
        string worksheetPath)
    {
        var relationshipPath = GetWorksheetRelationshipPath(worksheetPath);
        var relationships = archive.GetEntry(relationshipPath) is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadRelationships(archive, relationshipPath);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hyperlink in worksheet.Descendants(Spreadsheet + "hyperlink"))
        {
            var reference = hyperlink.Attribute("ref")?.Value;
            var relationshipId = hyperlink.Attribute(OfficeRelationships + "id")?.Value;
            if (!string.IsNullOrWhiteSpace(reference) &&
                !string.IsNullOrWhiteSpace(relationshipId) &&
                relationships.TryGetValue(relationshipId, out var target))
            {
                result[reference] = target;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadRelationships(ZipArchive archive, string path)
    {
        var document = ReadXml(archive, path);
        return document
            .Descendants(PackageRelationships + "Relationship")
            .Where(element => element.Attribute("Id") is not null && element.Attribute("Target") is not null)
            .ToDictionary(
                element => element.Attribute("Id")!.Value,
                element => element.Attribute("Target")!.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        if (archive.GetEntry("xl/sharedStrings.xml") is null)
        {
            return Array.Empty<string>();
        }

        var document = ReadXml(archive, "xl/sharedStrings.xml");
        return document
            .Descendants(Spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ??
                    throw new InvalidDataException($"The workbook does not contain '{path}'.");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static CellData? FindDeckCell(RowData row, HeaderMap header)
    {
        var preferred = FirstNonNull(
            GetByHeaderCell(row, header, "deck link"),
            GetByHeaderCell(row, header, "link"));
        if (FindDeckUri(row, preferred) is not null)
        {
            return preferred;
        }

        return row.Cells.Values.FirstOrDefault(cell =>
        {
            var uri = FindUri(cell);
            return uri is not null && DeckUrlPattern.IsMatch(uri.AbsoluteUri);
        });
    }

    private static Uri? FindDeckUri(RowData row, CellData? preferred)
    {
        var preferredUri = FindUri(preferred);
        if (preferredUri is not null && DeckUrlPattern.IsMatch(preferredUri.AbsoluteUri))
        {
            return preferredUri;
        }

        foreach (var cell in row.Cells.Values)
        {
            var uri = FindUri(cell);
            if (uri is not null && DeckUrlPattern.IsMatch(uri.AbsoluteUri))
            {
                return uri;
            }
        }

        return null;
    }

    private static Uri? FindUri(CellData? cell)
    {
        if (cell is null)
        {
            return null;
        }

        return FindUri(FirstNonEmpty(cell.Hyperlink, cell.Text));
    }

    private static Uri? FindUri(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    private static CellData? GetByHeaderCell(RowData row, HeaderMap header, string name) =>
        header.Columns.TryGetValue(name, out var column) && row.Cells.TryGetValue(column, out var cell)
            ? cell
            : null;

    private static string GetByHeader(RowData row, HeaderMap header, string name) =>
        GetByHeaderCell(row, header, name)?.Text ?? string.Empty;

    private static string ComposeName(string primary, string linkLabel, Uri deckUri)
    {
        primary = primary.Trim();
        linkLabel = linkLabel.Trim();
        if (string.IsNullOrWhiteSpace(primary))
        {
            return !string.IsNullOrWhiteSpace(linkLabel) && FindUri(linkLabel) is null
                ? linkLabel
                : deckUri.Segments.Last().Trim('/');
        }

        if (!string.IsNullOrWhiteSpace(linkLabel) &&
            FindUri(linkLabel) is null &&
            !string.Equals(primary, linkLabel, StringComparison.OrdinalIgnoreCase))
        {
            return $"{primary} — {linkLabel}";
        }

        return primary;
    }

    private static DateTimeOffset? ParseSpreadsheetDate(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Local));
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
    }

    private static int? ParseNullableInt(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (int)Math.Round(number)
            : null;
    }

    private static string NormalizeWorkbookTarget(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
    }

    private static string GetWorksheetRelationshipPath(string worksheetPath)
    {
        var directory = Path.GetDirectoryName(worksheetPath)?.Replace('\\', '/') ?? "xl/worksheets";
        var file = Path.GetFileName(worksheetPath);
        return $"{directory}/_rels/{file}.rels";
    }

    private static string GetColumn(string cellReference)
    {
        var length = 0;
        while (length < cellReference.Length && char.IsLetter(cellReference[length]))
        {
            length++;
        }

        return cellReference[..length].ToUpperInvariant();
    }

    private static string NormalizeHeader(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static T? FirstNonNull<T>(params T?[] values) where T : class =>
        values.FirstOrDefault(value => value is not null);

    private sealed record SheetReference(string Name, string RelationshipId, int Index);
    private sealed record CellData(string Reference, string Column, string Text, string? Hyperlink, string? Formula);
    private sealed record RowData(int Number, IReadOnlyDictionary<string, CellData> Cells);
    private sealed record HeaderMap(int RowNumber, IReadOnlyDictionary<string, string> Columns, int Score);
}
