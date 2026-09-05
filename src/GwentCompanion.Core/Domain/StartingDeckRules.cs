namespace GwentCompanion.Core.Domain;

public sealed record StartingDeckAssessment(
    ConstraintAssessment Shupe,
    ConstraintAssessment Radeyah,
    ConstraintAssessment Renfri,
    ConstraintAssessment GoldenNekker,
    ConstraintAssessment Devotion,
    int? EnslaveValue,
    int ProvisionCapacity,
    int ProvisionUsed)
{
    public int ProvisionRemaining => ProvisionCapacity - ProvisionUsed;
}

public sealed record ObservedStartingDeckAssessment(
    ConstraintAssessment Shupe,
    ConstraintAssessment Radeyah,
    ConstraintAssessment Renfri,
    ConstraintAssessment GoldenNekker,
    ConstraintAssessment Devotion,
    int ProvisionLowerBound,
    int LikelyStartingCardCopies,
    ConstraintAssessment? Musicians = null);

public static class StartingDeckRules
{
    private const int BaseProvisionCapacity = 150;

    public static bool IsStartingCard(CardDefinition card) => card.CanBeInStartingDeck &&
        card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact;

    public static StartingDeckAssessment EvaluateExactDeck(DeckDefinition deck)
    {
        ArgumentNullException.ThrowIfNull(deck);

        var duplicate = deck.Cards.FirstOrDefault(item => item.Count > 1);
        var singletonState = duplicate is null ? ConstraintState.Confirmed : ConstraintState.RuledOut;
        var singletonReason = duplicate is null
            ? "The starting deck contains no duplicate card IDs."
            : $"The starting deck contains {duplicate.Count} copies of {duplicate.Card.Name}.";

        var renfriEligible = deck.UnitCount >= 25;
        var renfri = new ConstraintAssessment(
            "Renfri",
            renfriEligible ? ConstraintState.Confirmed : ConstraintState.RuledOut,
            renfriEligible
                ? $"The starting deck contains {deck.UnitCount} units."
                : $"Renfri requires at least 25 units; this deck contains {deck.UnitCount}.");

        var goldenNekkerViolation = deck.Cards.FirstOrDefault(item =>
            item.Card.Provision >= 10 &&
            !IsGoldenNekkerException(item.Card));
        var goldenNekker = new ConstraintAssessment(
            "Golden Nekker",
            goldenNekkerViolation is null ? ConstraintState.Confirmed : ConstraintState.RuledOut,
            goldenNekkerViolation is null
                ? "No non-exempt starting card costs 10 or more provisions."
                : $"{goldenNekkerViolation.Card.Name} costs {goldenNekkerViolation.Card.Provision} provisions.");

        var neutralViolation = deck.Cards.FirstOrDefault(item =>
            string.Equals(item.Card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase));
        var devotion = new ConstraintAssessment(
            "Devotion",
            neutralViolation is null ? ConstraintState.Confirmed : ConstraintState.RuledOut,
            neutralViolation is null
                ? "The starting deck contains no Neutral cards."
                : $"{neutralViolation.Card.Name} is Neutral.");

        var tactics = deck.Cards
            .Where(item => item.Card.HasCategory("Tactic"))
            .Sum(item => item.Count);
        int? enslaveValue = string.Equals(deck.Leader, "Enslave", StringComparison.OrdinalIgnoreCase)
            ? 3 + (tactics / 4)
            : null;

        return new StartingDeckAssessment(
            new ConstraintAssessment("Shupe", singletonState, singletonReason),
            new ConstraintAssessment("Radeyah", singletonState, singletonReason),
            renfri,
            goldenNekker,
            devotion,
            enslaveValue,
            BaseProvisionCapacity + deck.LeaderProvisionBonus,
            deck.ProvisionTotal);
    }

    public static int ConfirmedProvisionLowerBound(IEnumerable<ObservedCard> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return observations
            .Where(item => item.Provenance == CardProvenance.ConfirmedStartingDeck)
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Sum(group => group.Max(item => item.ObservedCopies) * group.First().Card.Provision);
    }

    public static int ProbableProvisionLowerBound(IEnumerable<ObservedCard> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations
            .Where(item => CountsAgainstStartingDeck(item.Provenance))
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Sum(group => group.Max(item => item.ObservedCopies) * group.First().Card.Provision);
    }

    public static ObservedStartingDeckAssessment EvaluateObservedDeck(IEnumerable<ObservedCard> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var likely = observations
            .Where(item => CountsAgainstStartingDeck(item.Provenance))
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(item => item.ObservedCopies)!)
            .ToArray();
        var highProvision = likely.FirstOrDefault(item =>
            item.Card.Provision >= 10 && !IsGoldenNekkerException(item.Card));
        var neutral = likely.FirstOrDefault(item =>
            string.Equals(item.Card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase));
        var observedUnits = likely
            .Where(item => item.Card.Kind == CardKind.Unit)
            .Sum(item => item.ObservedCopies);
        var observedDuplicate = likely.FirstOrDefault(item => item.ObservedCopies > 1);
        var sawSingletonPayoff = likely.FirstOrDefault(item =>
            IsNamed(item.Card, "Shupe's Day Off") || IsNamed(item.Card, "Radeyah"));
        var sawRenfri = likely.FirstOrDefault(item => IsNamed(item.Card, "Renfri"));
        var sawGoldenNekkerPayoff = likely.FirstOrDefault(item =>
            IsNamed(item.Card, "Golden Nekker") || IsNamed(item.Card, "Ciri: Nova"));

        var singletonState = observedDuplicate is not null
            ? ConstraintState.RuledOut
            : sawSingletonPayoff is null
                ? ConstraintState.Possible
                : ConstraintState.Likely;
        var singletonReason = observedDuplicate is not null
            ? $"At least {observedDuplicate.ObservedCopies} likely starting-deck copies of {observedDuplicate.Card.Name} were observed."
            : sawSingletonPayoff is not null
            ? $"{sawSingletonPayoff.Card.Name} was observed; its starting-deck condition makes a singleton list likely, but a created copy remains possible."
            : likely.Length == 0
                ? "No likely starting-deck cards have been confirmed."
                : $"No duplicate has been established among {likely.Length} unique observed cards; unseen duplicates remain possible.";
        var renfriState = sawRenfri is not null || observedUnits >= 25
            ? ConstraintState.Likely
            : ConstraintState.Possible;
        var renfriReason = sawRenfri is not null
            ? "Renfri was observed; a starting deck with at least 25 units is likely, though created copies remain possible."
            : $"{observedUnits} likely starting-deck unit(s) observed; Renfri requires at least 25 units in the full deck.";
        var goldenNekkerState = highProvision is not null
            ? ConstraintState.RuledOut
            : sawGoldenNekkerPayoff is not null
                ? ConstraintState.Likely
                : ConstraintState.Possible;
        var goldenNekkerReason = highProvision is not null
            ? $"{highProvision.Card.Name} costs {highProvision.Card.Provision} provisions."
            : sawGoldenNekkerPayoff is not null
                ? $"{sawGoldenNekkerPayoff.Card.Name} was observed; no non-exempt 10+ provision starting card should be present if its condition resolved."
                : "No confirmed non-exempt 10+ provision card has been observed.";
        return new ObservedStartingDeckAssessment(
            new ConstraintAssessment("Shupe", singletonState, singletonReason),
            new ConstraintAssessment("Radeyah", singletonState, singletonReason),
            new ConstraintAssessment(
                "Renfri",
                renfriState,
                renfriReason),
            new ConstraintAssessment(
                "Golden Nekker",
                goldenNekkerState,
                goldenNekkerReason),
            new ConstraintAssessment(
                "Devotion",
                neutral is null ? ConstraintState.Possible : ConstraintState.RuledOut,
                neutral is null
                    ? "No likely starting-deck Neutral card has been observed."
                    : $"{neutral.Card.Name} is Neutral."),
            ProbableProvisionLowerBound(likely),
            likely.Sum(item => item.ObservedCopies));
    }

    public static bool CountsAgainstStartingDeck(CardProvenance provenance) => provenance is
        CardProvenance.ConfirmedStartingDeck or CardProvenance.ProbableStartingDeck;

    private static bool IsGoldenNekkerException(CardDefinition card) =>
        string.Equals(card.Name, "Golden Nekker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(card.Name, "Ciri: Nova", StringComparison.OrdinalIgnoreCase);

    private static bool IsNamed(CardDefinition card, string name) =>
        string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase);
}
