using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public sealed record VisibleZoneInspection(IReadOnlyList<CardDefinition> Cards, string Text);
public static class VisibleZoneInspectionReader
{
    // Exact standalone names only. Ability text mentioning another card is not a sighting.
    public static VisibleZoneInspection? Parse(string text, IEnumerable<CardDefinition> catalog)
    {
        static string Normalize(string value) => Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N}]", "");
        var lines = text.Split('\n').Select(Normalize).Where(line => line.Length >= 5).ToHashSet();
        var matches = catalog.Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .GroupBy(card => Normalize(card.Name)).Where(group => group.Count() == 1 && lines.Contains(group.Key))
            .Select(group => group.Single()).Take(30).ToArray();
        return matches.Length == 0 ? null : new(matches, text);
    }
}
