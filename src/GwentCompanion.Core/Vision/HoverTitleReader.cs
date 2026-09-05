using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public static class HoverTitleReader
{
    public static CardDefinition? Read(string text, IEnumerable<CardDefinition> catalog)
        => new HoverTitleIndex(catalog).Read(text);
}

/// <summary>Compile once. Prefer complete wrapped names over a shorter suffix (False Ciri is not Ciri).</summary>
public sealed class HoverTitleIndex
{
    private readonly Dictionary<string, CardDefinition> _names;
    private readonly Dictionary<string, CardDefinition> _uvNames;
    private readonly Dictionary<string, CardDefinition> _choiceUvNames;
    private static string Key(string value) => Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N}]", "");
    public HoverTitleIndex(IEnumerable<CardDefinition> catalog)
    {
        _names = catalog.DistinctBy(card => card.Id).Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .GroupBy(card => Key(card.Name)).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        _uvNames=_names.Values.GroupBy(card=>Key(card.Name).Replace('U','V'))
            .Where(group=>group.Count()==1 && group.Key.Length>=8 && group.Single().Name.Contains(' '))
            .ToDictionary(group=>group.Key,group=>group.Single());
        _choiceUvNames=_names.Values.GroupBy(card=>Key(card.Name).Replace('U','V'))
            .Where(group=>group.Count()==1 && group.Key.Length>=7)
            .ToDictionary(group=>group.Key,group=>group.Single());
    }
    // Only for a separately validated full bright title line; caller must require
    // temporal confirmation. A font equivalence is not general edit-distance OCR.
    public CardDefinition? ReadGlyphEquivalent(string title) => title.Trim().Contains(' ') &&
        _uvNames.TryGetValue(Key(title).Replace('U','V'),out var card) ? card : null;
    // Selection pop-ups provide a separately located, complete title line. The
    // GWENT capital V is routinely read as U (for example UEREENA). Permit only
    // that font-specific equivalence, only for a unique title of useful length.
    public CardDefinition? ReadChoiceGlyphEquivalent(string title) =>
        _choiceUvNames.TryGetValue(Key(title).Replace('U','V'),out var card) ? card : null;
    public CardDefinition? Read(string text)
    {
        // Exact standalone title near the top only. Never detect names embedded in ability prose.
        var lines = text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Take(6).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i + 1 < lines.Length && _names.TryGetValue(Key(lines[i] + lines[i + 1]), out var wrapped)) return wrapped;
            if (_names.TryGetValue(Key(lines[i]), out var card))
            {
                // A lost prefix must not turn the Human/Agent tooltip into Witcher Ciri.
                if (card.Id == "112101" && i + 1 < lines.Length &&
                    Regex.IsMatch(lines[i + 1], @"\b(Human|Aristocrat|Agent)\b", RegexOptions.IgnoreCase)) return null;
                return card;
            }
        }
        return null;
    }
}
