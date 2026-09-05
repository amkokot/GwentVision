namespace GwentCompanion.Core.Domain;

public enum CardKind
{
    Unknown,
    Unit,
    Special,
    Artifact,
    Stratagem,
    Leader,
}

public enum CardProvenance
{
    Unknown,
    ConfirmedStartingDeck,
    ProbableStartingDeck,
    Spawned,
    Created,
    Copied,
    Transformed,
    Replayed,
    Summoned,
    Stolen,
}

public sealed record CardDefinition(
    string Id,
    string Name,
    string Faction,
    CardKind Kind,
    int Provision,
    int Power = 0,
    bool IsGold = false,
    IReadOnlySet<string>? CardCategories = null,
    Uri? ArtUri = null,
    IReadOnlySet<string>? SecondaryFactionNames = null,
    string? AbilityText = null,
    bool CanBeInStartingDeck = true,
    int? PrintedArmor = null)
{
    public IReadOnlySet<string> Categories { get; init; } =
        CardCategories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> SecondaryFactions { get; init; } =
        SecondaryFactionNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool HasCategory(string category) => Categories.Contains(category);
}

public sealed record DeckCard(CardDefinition Card, int Count = 1);

public sealed record DeckDefinition(
    string Id,
    string Name,
    string Faction,
    string Leader,
    int LeaderProvisionBonus,
    IReadOnlyList<DeckCard> Cards,
    Uri? SourceUri = null,
    DateTimeOffset? LastEdited = null,
    int RecencyRank = 0,
    CardDefinition? Stratagem = null,
    DateTimeOffset? SourceUpdatedAt = null,
    DateTimeOffset? CachedAt = null,
    IReadOnlyList<DeckPatch>? Patches = null,
    IReadOnlyList<DeckOccurrence>? Occurrences = null)
{
    public int CardCount => Cards.Sum(item => item.Count);

    public int UnitCount => Cards
        .Where(item => item.Card.Kind == CardKind.Unit)
        .Sum(item => item.Count);

    public int ProvisionTotal => Cards.Sum(item => item.Card.Provision * item.Count);

    public int CountOf(string cardId) => Cards
        .Where(item => string.Equals(item.Card.Id, cardId, StringComparison.OrdinalIgnoreCase))
        .Sum(item => item.Count);

    public bool ContainsName(string cardName) => Cards.Any(
        item => string.Equals(item.Card.Name, cardName, StringComparison.OrdinalIgnoreCase));
}

public sealed record ObservedCard(
    CardDefinition Card,
    CardProvenance Provenance,
    double Confidence,
    DateTimeOffset ObservedAt,
    string? Evidence = null,
    int ObservedCopies = 1);

public enum ConstraintState
{
    Unknown,
    Possible,
    Likely,
    Confirmed,
    RuledOut,
}

public sealed record ConstraintAssessment(
    string Name,
    ConstraintState State,
    string Reason);
