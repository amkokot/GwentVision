using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GwentCompanion.Core.Data;

/// <summary>Extract links without executing formulas/macros or contacting workbook-specified hosts.</summary>
public static class DeckLinkFileReader
{
    private static readonly Regex Url = new(@"https?://(?:www\.)?playgwent\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?decks/[a-z0-9]+(?:[/?#][^\s<>""']*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeckPath = new(@"^/(?:[a-z]{2}(?:-[a-z]{2})?/)?decks/([a-z0-9]{16,64})/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GuidePath = new(@"^/(?:[a-z]{2}(?:-[a-z]{2})?/)?decks/guides/([0-9]{1,12})/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Uri? CanonicalUrl(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || !(uri.Host.Equals("playgwent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.playgwent.com", StringComparison.OrdinalIgnoreCase))) return null;
        var match = DeckPath.Match(uri.AbsolutePath);
        if (match.Success) return new Uri("https://www.playgwent.com/en/decks/" + match.Groups[1].Value.ToLowerInvariant());
        match = GuidePath.Match(uri.AbsolutePath);
        return match.Success ? new Uri("https://www.playgwent.com/en/decks/guides/" + match.Groups[1].Value) : null;
    }

    public static IReadOnlyList<DeckIndexEntry> FromText(string text, string source = "Pasted link", string sheet = "", int row = 0)
    {
        return Url.Matches(text).Select(match => Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', ';'), UriKind.Absolute, out var uri) ? CanonicalUrl(uri) : null)
            .Where(uri => uri is not null).Distinct().Select((uri, index) => new DeckIndexEntry(
                source + ":" + sheet + ":" + row + ":" + index, source, sheet, row, "", "", "Imported " + uri!.Segments[^1][..Math.Min(8, uri.Segments[^1].Length)],
                uri!, null, null, "Imported link", null, null, row, [DeckPatchMetadata.Current(DateTimeOffset.Now)])).ToArray();
    }

    public static IReadOnlyList<DeckIndexEntry> Read(string path, bool assumeCurrentPatch = true)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Please use a spreadsheet smaller than 32 MB.");
        if (extension is ".csv" or ".tsv" or ".txt") return FromText(File.ReadAllText(path), Path.GetFileName(path));
        if (extension != ".xlsx") throw new InvalidDataException("Choose .xlsx, .csv, .tsv or .txt. Export old .xls files as .xlsx first.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Sum(entry => entry.Length) > 128 * 1024 * 1024)
            throw new InvalidDataException("Spreadsheet expands beyond the 128 MB import limit.");
        var result = new List<DeckIndexEntry>();
        // Preserve dates/names from supported meta-sheet layouts, but don't require their headers.
        try
        {
            foreach (var entry in new WorkbookDeckIndexReader().Read(path).Entries)
            {
                if (CanonicalUrl(entry.DeckUri) is { } uri) result.Add(entry with { DeckUri = uri });
                if (CanonicalUrl(entry.AlternateDeckUri) is { } alternate) result.Add(entry with { DeckUri = alternate, SourceId = entry.SourceId + ":alternate" });
            }
        }
        catch (InvalidDataException) { /* Generic link discovery still handles headerless workbooks. */ }
        // Shared/inline strings, HYPERLINK formulas, and worksheet relationship targets.
        foreach (var part in archive.Entries.Where(entry => entry.FullName == "xl/sharedStrings.xml" ||
                     entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) &&
                     (entry.FullName.EndsWith(".xml") || entry.FullName.EndsWith(".rels"))))
        {
            using var stream = part.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            var values = document.Descendants().SelectMany(element =>
                element.Attributes().Select(attribute => attribute.Value).Concat(element.HasElements ? [] : [element.Value]));
            foreach (var value in values) result.AddRange(FromText(value, Path.GetFileName(path), part.FullName));
        }
        var uploadedAt = DateTimeOffset.Now;
        return result.GroupBy(entry => entry.DeckUri).Select(group =>
        {
            // Structured rows precede generic XML discovery. Do not let fallback discovery
            // falsely attach today's patch to an explicitly historical worksheet entry.
            var primary = group.First();
            var structured = group.Where(entry => entry.Notes != "Imported link").ToArray();
            var patches = DeckPatchMetadata.Merge(structured.SelectMany(entry => entry.Patches ?? []));
            var combined = assumeCurrentPatch ? DeckPatchMetadata.DefaultUpload(primary with { Patches = patches }, uploadedAt) : primary with { Patches = patches };
            return combined with { Occurrences = DeckOccurrences.Merge(
                (structured.Length > 0 ? structured : [combined]).SelectMany(DeckOccurrences.FromIndex)) };
        }).ToArray();
    }
}
