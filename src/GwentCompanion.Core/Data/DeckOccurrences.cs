using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public static class DeckOccurrences
{
    // Merge is the lossless source journal. Its length is NOT an observation count:
    // independent libraries can assign different IDs/rows to the same deck and patch.
    public static DeckOccurrence[] Merge(params IEnumerable<DeckOccurrence>?[] sources) => sources
        .Where(s => s is not null).SelectMany(s => s!).GroupBy(o => o.Id, StringComparer.Ordinal)
        // Re-saving an observation must not move its patch or timestamp forward.
        .Select(g => g.OrderBy(o => o.At ?? DateTimeOffset.MinValue).ThenBy(o => o.Patch, StringComparer.Ordinal).First())
        .OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Unique evidence for ONE deck composition, not a collection of unrelated decks.</summary>
    public static DeckOccurrence[] Evidence(IEnumerable<DeckOccurrence>? occurrences) => Merge(occurrences)
        .GroupBy(o => IsMatch(o.Kind) ? "match:" + o.Id : "library:" + PatchKey(o.Patch), StringComparer.Ordinal)
        // Source IDs, URLs, names and save times cannot make another independent library sighting.
        // Prefer explicit patch evidence, then a deterministic original record; retain all originals in Merge.
        .Select(g => g.OrderBy(o => o.Inferred).ThenBy(o => o.At ?? DateTimeOffset.MinValue)
            .ThenBy(o => o.Id, StringComparer.Ordinal).First())
        .Select(o => o with { Patch = PatchKey(o.Patch) })
        .OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();

    private static bool IsMatch(string? kind) => kind is "OpponentMatch" or "OpponentMatchInferred" or "OpponentMatchAnalogueInferred";
    private static string PatchKey(string? patch) => Inference.PatchRecency.TryIndex(patch?.Trim(), out var index)
        ? Inference.PatchRecency.Label(index) : (patch ?? "").Trim().ToLowerInvariant();

    public static DeckDefinition Record(DeckDefinition deck, string eventId, string kind, DateTimeOffset at, string source)
    {
        var patch = DeckPatchMetadata.Current(at).Label;
        var occurrence = new DeckOccurrence(eventId, patch, kind, source, at);
        var occurrences = Merge(deck.Occurrences, [occurrence]);
        return deck with { Occurrences = occurrences, Patches = DeckPatchMetadata.Merge(deck.Patches, Tags(occurrences)) };
    }

    public static DeckPatch[] Tags(IEnumerable<DeckOccurrence>? occurrences) => (occurrences ?? [])
        .Select(o => new DeckPatch(o.Patch, o.Inferred, "Occurrence " + o.Kind + ": " + o.Id)).ToArray();

    public static DeckOccurrence[] FromIndex(DeckIndexEntry entry)
    {
        // Import source IDs/rows make repeated reads idempotent. Distinct rows,
        // sources and patches remain in the source journal. Evidence collapses library copies per patch.
        return (entry.Patches ?? []).GroupBy(p => (p.Source, p.Inferred)).SelectMany(g =>
        {
            var tags = g.ToArray();
            if (g.Key.Inferred && tags.Length > 1 && tags.All(p => Inference.PatchRecency.TryIndex(p.Label, out _)))
                tags = [tags.MaxBy(p => { Inference.PatchRecency.TryIndex(p.Label, out var i); return i; })!];
            return tags.Select(p => new DeckOccurrence("import-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{entry.SourceId}|{entry.Workbook}|{entry.Sheet}|{entry.Row}|{entry.DeckUri}|{p.Label}"))),
                p.Label, "Import", $"{entry.Workbook} / {entry.Sheet} / row {entry.Row}", entry.LastEdited, p.Inferred));
        }).ToArray();
    }

    public static string Describe(IEnumerable<DeckOccurrence>? occurrences)
    {
        var groups = Evidence(occurrences).GroupBy(o => PatchKey(o.Patch)).OrderByDescending(g =>
            Inference.PatchRecency.TryIndex(g.Key, out var i) ? i : -1).ToArray();
        return groups.Length == 0 ? "" : "Observations: " + string.Join(", ", groups.Take(6)
            .Select(g => g.Key + " ×" + g.Count().ToString(CultureInfo.InvariantCulture))) + (groups.Length > 6 ? " …" : "");
    }
}
