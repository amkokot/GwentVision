using System.Globalization;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public static class DeckPatchMetadata
{
    // Worksheet dates describe when a list was used. Server payloads may all be
    // rewritten together by balance changes; do not turn old sheets into new meta.
    public static DateTimeOffset? HistoricalDate(IEnumerable<DeckPatch>? patches, DateTimeOffset? fallback)
    {
        var dates = (patches ?? []).Where(p => p.Source.StartsWith("Worksheet", StringComparison.Ordinal) || p.Source == "Patch column")
            .Select(p => Regex.Match(p.Label, @"^(\d+)\.(\d+)$"))
            .Where(m => m.Success).Select(m => (Major: int.Parse(m.Groups[1].Value), Month: int.Parse(m.Groups[2].Value)))
            .Where(v => v.Major >= 12 && v.Month is >= 1 and <= 12)
            .Select(v => new DateTimeOffset(v.Major + 2012, v.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddTicks(-1)).ToArray();
        if (dates.Length == 0)
        {
            // Pre-maintenance patch numbering did not match calendar months.
            // Only claim the known upper date bound, not an invented exact month.
            var historical = (patches ?? []).Any(p => (p.Source.StartsWith("Worksheet", StringComparison.Ordinal) || p.Source == "Patch column") &&
                Regex.IsMatch(p.Label, @"^(?:[1-9]|10|11)\.\d+$"));
            if (!historical) return null;
            var bound = new DateTimeOffset(2023, 12, 31, 23, 59, 59, TimeSpan.Zero);
            return fallback is { } old && old < bound ? old : bound;
        }
        var last = dates.Max();
        return fallback is { } date && date < last ? date : last;
    }
    // Post-2023 monthly convention: January 2024 = 12.1, August 2026 = 14.8.
    // Keep inferred labels distinct; this is not a live server-version check.
    public static DeckPatch Current(DateTimeOffset at) =>
        new($"{at.Year - 2012}.{at.Month}", true, $"Upload month {at:yyyy-MM}");

    public static DeckPatch[] FromSheet(string sheet, DateTimeOffset? rowDate = null, string? explicitLabel = null)
    {
        foreach (var text in new[] { explicitLabel, sheet })
        {
            var version = Regex.Match(text ?? "", @"^\s*(?:patch\s*)?v?\s*(\d{1,2}\.\d{1,2}(?:\.\d+)?)\s*$", RegexOptions.IgnoreCase);
            if (version.Success) return [new(version.Groups[1].Value, false, explicitLabel == text ? "Patch column" : "Worksheet " + sheet)];
        }
        var year = Regex.Match(sheet, @"\b(20\d{2})\b");
        if (year.Success && int.Parse(year.Value, CultureInfo.InvariantCulture) is var y && y >= 2024)
        {
            var months = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames.Take(12)
                .Select((name, i) => (name, month: i + 1))
                .Where(item => sheet.Contains(item.name, StringComparison.OrdinalIgnoreCase)).Select(item => item.month).ToArray();
            if (rowDate is { } date && date.Year == y && months.Contains(date.Month)) months = [date.Month];
            if (months.Length > 0) return months.Select(m => new DeckPatch($"{y - 2012}.{m}", true, "Worksheet month " + sheet)).ToArray();
        }
        if (rowDate is { Year: >= 2024 } edited)
            return [new($"{edited.Year - 2012}.{edited.Month}", true, $"Spreadsheet row date {edited:yyyy-MM-dd}")];
        return [];
    }

    public static DeckPatch[] Merge(params IEnumerable<DeckPatch>?[] sources) => sources
        .Where(source => source is not null).SelectMany(source => source!)
        .Distinct().OrderBy(patch => patch.Label, StringComparer.Ordinal).ThenBy(patch => patch.Inferred).ToArray();

    public static DeckIndexEntry DefaultUpload(DeckIndexEntry entry, DateTimeOffset at) =>
        entry.Patches is { Count: > 0 } ? entry : entry with { Patches = [Current(at)] };

    public static string Describe(IEnumerable<DeckPatch>? patches)
    {
        var labels = (patches ?? []).GroupBy(p => p.Label).Select(group => group.Key + (group.All(p => p.Inferred) ? "~" : "")).ToArray();
        return labels.Length == 0 ? "Patch unknown" : "Patch " + string.Join(", ", labels);
    }
}
