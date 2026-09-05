using System.Globalization;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record PatchPredictionContext(string TargetPatch, double HalfLifePatches = 2, bool StrictHistory = false);
public sealed record PatchEvidence(double Weight, int? Distance, string? Label, bool Inferred,
    bool DateFallback, bool Available = true, bool Legacy = false);

/// <summary>Patch-first priors. Monthly patch distance is supported from 12.1 onward.</summary>
public static class PatchRecency
{
    public const double BackgroundFloor = .02;
    public static PatchPredictionContext Current(DateTimeOffset now) => new(DeckPatchMetadata.Current(now).Label);

    public static bool TryIndex(string? label, out int index)
    {
        index = -1;
        var m = Regex.Match(label ?? "", @"^(\d{1,2})\.([1-9]|1[0-2])(?:\.\d+)?$", RegexOptions.CultureInvariant);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) || major < 12) return false;
        index = (major - 12) * 12 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
        return true;
    }
    public static string Label(int index) => $"{12 + index / 12}.{index % 12 + 1}";
    public static DateTimeOffset Start(string label) => TryIndex(label, out var index)
        ? new DateTimeOffset(2024 + index / 12, index % 12 + 1, 1, 0, 0, 0, TimeSpan.Zero)
        : throw new ArgumentException("Use a monthly patch from 12.1 onward, e.g. 14.8.", nameof(label));

    public static PatchEvidence Resolve(DeckDefinition deck, PatchPredictionContext context)
    {
        if (!TryIndex(context.TargetPatch, out var target) || !double.IsFinite(context.HalfLifePatches) || context.HalfLifePatches <= 0)
            throw new ArgumentException("A valid target patch and positive patch half-life are required.");
        double Weight(int distance) => Math.Max(BackgroundFloor, Math.Pow(.5, distance / context.HalfLifePatches));
        var tags = DeckPatchMetadata.Merge(deck.Patches, DeckOccurrences.Tags(deck.Occurrences));
        var known = tags.Where(p => TryIndex(p.Label, out _)).ToArray();
        // Two inferred months from the same worksheet are an ambiguous range, not
        // proof that the deck was already present in its earliest possible month.
        var safe = known.GroupBy(p => (p.Source, p.Inferred)).SelectMany(g =>
            g.Key.Inferred && g.Select(p => { TryIndex(p.Label, out var i); return i; }).Distinct().Count() > 1 &&
            g.Any(p => { TryIndex(p.Label, out var i); return i > target; }) ? Enumerable.Empty<DeckPatch>() : g).ToArray();
        var past = safe.Select(p => { TryIndex(p.Label, out var i); return (Patch: p, Index: i); }).Where(p => p.Index <= target)
            .OrderByDescending(p => p.Index).ThenBy(p => p.Patch.Inferred).ToArray();
        if (past.Length > 0)
        {
            var p = past[0]; var distance = target - p.Index;
            return new(Weight(distance), distance, p.Patch.Label, p.Patch.Inferred, false);
        }
        // Pre-maintenance numbering was not uniformly monthly. Keep it as a dated
        // legacy background bucket; never invent exact distances between 7.x/8.x/etc.
        if (tags.Any(p => Regex.IsMatch(p.Label, @"^(?:[1-9]|10|11)\.", RegexOptions.CultureInvariant)))
            return new(Weight(target + 1), target + 1, "before 12.1", true, false, Legacy: true);
        if (known.Length > 0) return new(0, null, null, true, false, Available: false);
        if (tags.Length > 0) return Unknown(context); // Malformed metadata must not be refreshed by a server timestamp.
        var date = DeckMetaAnalyzer.SourceDate(deck);
        if (date is null) return Unknown(context);
        if (date.Value.Year < 2024) return new(Weight(target + 1), target + 1, "before 12.1", true, true, Legacy: true);
        TryIndex(DeckPatchMetadata.Current(date.Value).Label, out var dated);
        if (dated > target) return new(0, null, null, true, true, Available: false);
        return new(Weight(target - dated), target - dated, Label(dated), true, true);
    }
    private static PatchEvidence Unknown(PatchPredictionContext context) => new(context.StrictHistory ? 0 : .15,
        null, null, true, false, Available: !context.StrictHistory);

    public static int[] SupportedPatchIndices(DeckDefinition deck, PatchPredictionContext context)
    {
        if (!TryIndex(context.TargetPatch, out var target)) throw new ArgumentException("Invalid target patch.");
        var tags = DeckPatchMetadata.Merge(deck.Patches, DeckOccurrences.Tags(deck.Occurrences));
        if (tags.Length == 0)
        {
            var fallback = Resolve(deck, context);
            return fallback.Available && TryIndex(fallback.Label, out var index) ? [index] : [];
        }
        // One ambiguous multi-month source supplies one conservative occurrence at
        // its last possible patch, not several independent historical sightings.
        return tags.Where(p => TryIndex(p.Label, out _)).GroupBy(p => (p.Source, p.Inferred)).SelectMany(g =>
        {
            var indices = g.Select(p => { TryIndex(p.Label, out var i); return i; }).Distinct().ToArray();
            return g.Key.Inferred && indices.Length > 1
                ? indices.Max() <= target ? new[] { indices.Max() } : []
                : indices.Where(i => i <= target).ToArray();
        }).Distinct().Order().ToArray();
    }
}
