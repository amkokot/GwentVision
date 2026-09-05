using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public sealed record EvolvingCardFamily(string StartingId, string SecondId, string FinalId, bool FinalProvesDevotion = true);
public static class EvolvingCardCatalog
{
    public static IReadOnlyList<EvolvingCardFamily> Families { get; } = [
        new("202603", "202604", "202605"), new("202615", "202616", "202617"),
        new("202629", "202630", "202631"), new("202642", "202643", "202644"),
        new("202655", "202656", "202657"), new("202670", "202671", "202672"),
        // Sacred and Profane introduced one-step transformations. Their alternate
        // artwork is still the same starting-deck card, but unlike the original
        // three-stage cycle it is not, by itself, a Devotion proof.
        new("203192", "203204", "203204", false), // Torres
        new("203194", "203205", "203205", false), // Tyr
        new("203195", "203206", "203206", false), // Dana
        new("203198", "203207", "203207", false), // Temple
        new("203199", "203208", "203208", false), // Dagon
        new("203201", "203209", "203209", false), // Saint Gregory
        // Saov transforms on entering the graveyard and later returns in its Unity
        // form. Unity is the same physical starting card, not a 7p generated slot.
        new("202986", "203021", "203021", false)
    ];
    public static bool IsEvolved(string id) => Families.Any(f => f.SecondId == id || f.FinalId == id);
    public static bool IsFinal(string id) => Families.Any(f => f.FinalProvesDevotion && f.FinalId == id);
    public static string StartingId(string id) => Families.FirstOrDefault(f => f.SecondId == id || f.FinalId == id)?.StartingId ?? id;
    public static (CardDefinition Card, PlayOriginAssessment Origin) StartingIdentity(CardSighting sight,
        PlayOriginAssessment origin, IEnumerable<CardDefinition> catalog, string? faction)
    {
        var family = Families.FirstOrDefault(f => f.SecondId == sight.Card.Id || f.FinalId == sight.Card.Id);
        var original = family is null ? null : catalog.FirstOrDefault(c => c.Id == family.StartingId);
        if (original is null) return (sight.Card, origin);
        // Evolved identities are marked NonOwnable, but are not inherently new copies.
        // Do not erase an explicit creation/theft risk or infer origin from controller alone.
        if (origin.Provenance == CardProvenance.Spawned)
            origin = new(sight.Source != CardSightSource.Board && FactionCompatibility.IsPlayableBy(original, faction ?? "")
                ? CardProvenance.ProbableStartingDeck : CardProvenance.Unknown, "Evolved form; original membership is a hypothesis.");
        return (original, origin with { Reason = $"Observed {sight.Card.Name}; grouped with {original.Name}, not an additional deck copy. " + origin.Reason });
    }
}
