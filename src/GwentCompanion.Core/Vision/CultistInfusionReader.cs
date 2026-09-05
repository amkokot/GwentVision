using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

/// <summary>Each repeated clause is a separate Eclipse infusion, not a shared faction counter.</summary>
public sealed record CultistInfusionReading(string CardId, bool HasCultistCategory, ImmutableArray<int> Boosts)
{
    public bool SameAs(CultistInfusionReading? other) => other is not null && CardId == other.CardId &&
        HasCultistCategory == other.HasCultistCategory && Boosts.SequenceEqual(other.Boosts);
}

public static class CultistInfusionReader
{
    private static readonly Regex Boost = new(
        @"Whenever\s+you\s+play\s+a\s+Cultist\s*,?\s*boost\s+self\s+by\s+(?<value>\d+)\s*,?\s*then\s+increase\s+this\s+value\s+by\s+1\s+if\s+it\s+was\s+a\s+bronze\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Category = new(
        @"^(?:[A-Za-z][A-Za-z '\-]*,\s*)*Cultist(?:\s*,\s*[A-Za-z][A-Za-z '\-]*)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CultistInfusionReading? Read(CardDefinition? card, string? text)
    {
        // In particular, never read the scenario's quoted grant as a unit's current boost.
        if (card?.Kind != CardKind.Unit || string.IsNullOrWhiteSpace(text)) return null;
        var matches = Boost.Matches(text);
        var amounts = ImmutableArray.CreateBuilder<int>();
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["value"].Value, out var value) || value is < 1 or > 999) return null;
            amounts.Add(value);
        }
        var cultist = card.HasCategory("Cultist") || text.Split('\n').Take(6)
            .Any(line => Category.IsMatch(line.Trim()));
        return amounts.Count == 0 && !cultist ? null : new(card.Id, cultist, amounts.ToImmutable());
    }
}
