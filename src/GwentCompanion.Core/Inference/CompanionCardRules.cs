using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record CompanionLink(string SourceId, string TargetId, int Copies, bool AutoSuggest, string Reason);

/// <summary>Mechanics registry for copy detection and statistical audits, not likelihood boosts.
/// AutoSuggest marks a mechanical pairing hypothesis; it does not authorize automatic slots.</summary>
public static class CompanionCardRules
{
    public static IReadOnlyList<string> ThinningPairs { get; } =
    ["122311", "122313", "132310", "152318", "200038", "201559", "202273", "202334", "202368", "202521", "202549", "203089"];

    /// <summary>
    /// True only for the narrow printed Deploy pattern that creates a base copy
    /// of the played unit on its row. Two corroborated bodies then establish one
    /// original, never two, when the enlarged play preview was missed.
    /// </summary>
    public static bool SpawnsBaseCopyOfSelfOnDeploy(CardDefinition card) =>
        System.Text.RegularExpressions.Regex.IsMatch(card.AbilityText ?? "",
            @"\bDeploy\s*:\s*Spawn a base copy of (?:self|this card)\b[^.\n]*\bon (?:this|that) row\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Cards whose own printed mechanic can put that identity onto the board from
    /// the deck without a play preview. Graveyard alternatives are deliberately
    /// excluded because a pile decrement cannot establish their origin.
    /// </summary>
    public static bool IsInherentDeckArrival(CardDefinition card)
    {
        if (card.Id == "202397") return true; // Knickers uses non-technical flavour text.
        return HasExplicitInherentDeckArrival(card);
    }

    /// <summary>
    /// True when the public rules text itself names the card's deck-to-board route.
    /// Unlike the broader inherent-arrival predicate, this excludes deliberately
    /// vague text such as Knickers and can therefore support identity persistence
    /// without a separately observed pile decrement or exact tooltip title.
    /// </summary>
    public static bool HasExplicitInherentDeckArrival(CardDefinition card)
    {
        var ability = card.AbilityText ?? "";
        return !ability.Contains("deck or graveyard", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(ability,
                @"\bSummon (?:self|this card) from (?:your |the )?deck\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// A played card can arm a matching-copy arrival for a different identity.
    /// This widens candidate retention and allows the ledger to join a repeated
    /// exact target title to a matching multi-card deck departure.
    /// </summary>
    public static IReadOnlyList<string> TriggeredAutomaticPairTargets(CardDefinition played)
    {
        var ability = played.AbilityText ?? "";
        // Highwaymen listens for any played card with the Bonded keyword. Whether
        // another copy was actually controlled is resolved by the later pixels,
        // not assumed from the trigger text.
        return System.Text.RegularExpressions.Regex.IsMatch(ability,
            @"\bBonded(?:\s*\([^)]*\))?\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? ["202415"] : [];
    }

    /// <summary>
    /// Extracts an explicit ordered summon group from printed rules text. Besides
    /// the usual "Summon X and Y from your deck" form, this understands cards such
    /// as Madam Marquise Serenity which put each named target in a separate row
    /// branch and then explicitly say "Summon both instead". Separate clauses are
    /// never combined without that aggregate instruction, so mutually exclusive
    /// row choices cannot manufacture a two-card summon.
    /// </summary>
    public static IReadOnlyList<string> NamedDeckSummonTargetNames(CardDefinition source)
    {
        var ability = source.AbilityText ?? "";
        var matches = System.Text.RegularExpressions.Regex.Matches(ability,
            @"\bSummon\s+(?<targets>[^.\r\n]{2,120}?)\s+from your deck\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var clause = match.Groups["targets"].Value.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(clause, @"\s+and\s+",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase) || UnsafeNamedClause(clause)) continue;
            var combined = SplitNamedTargets(clause);
            if (ValidNamedGroup(combined)) return combined;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(ability,
                @"\bSummon\s+(?:both|all)(?:\s+of them)?\s+instead\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return [];
        var separate = matches.Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Groups["targets"].Value.Trim())
            .Where(clause => !UnsafeNamedClause(clause) &&
                !System.Text.RegularExpressions.Regex.IsMatch(clause, @"\s+and\s+",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(NormalizeNamedTarget).Where(name => name.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ValidNamedGroup(separate) ? separate : [];
    }

    /// <summary>True only when printed alternative summon clauses may resolve one-by-one or together.</summary>
    public static bool AllowsIndependentNamedDeckSummons(CardDefinition source)
    {
        var ability = source.AbilityText ?? "";
        var namedClauses = System.Text.RegularExpressions.Regex.Matches(ability,
            @"\bSummon\s+[^.\r\n]{2,120}?\s+from your deck\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        return namedClauses >= 2 && System.Text.RegularExpressions.Regex.IsMatch(ability,
            @"\bSummon\s+(?:both|all)(?:\s+of them)?\s+instead\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Resolves the parsed names to visually searchable starting-deck identities.
    /// The returned order is printed resolution order; each new Gwent arrival is
    /// inserted beside the source, making settled nearest-to-farthest order reverse.
    /// </summary>
    public static IReadOnlyList<string> NamedDeckSummonTargets(CardDefinition source,
        IEnumerable<CardDefinition> catalog)
    {
        var names = NamedDeckSummonTargetNames(source);
        if (names.Count == 0) return [];
        var cards = catalog.ToArray();
        var ids = new List<string>();
        foreach (var name in names)
        {
            var target = cards.SingleOrDefault(card => card.CanBeInStartingDeck &&
                card.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.Id == source.Id) return [];
            ids.Add(target.Id);
        }
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Count ? ids : [];
    }

    private static bool UnsafeNamedClause(string clause) =>
        System.Text.RegularExpressions.Regex.IsMatch(clause,
            @"\b(?:random|self|this card|both|copies?|units?|cards?|specials?|artifacts?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string[] SplitNamedTargets(string clause) =>
        System.Text.RegularExpressions.Regex.Split(clause, @",\s*(?:and\s+)?|\s+and\s+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(NormalizeNamedTarget).Where(name => name.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string NormalizeNamedTarget(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"^(?:a|an)\s+", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static bool ValidNamedGroup(IReadOnlyList<string> names) => names.Count is >= 2 and <= 4;

    /// <summary>
    /// Returns only visually searchable targets of a printed recurring random
    /// deck summon. This is a candidate index, not evidence that any target was
    /// present. The later artwork, board timing and origin gates remain mandatory.
    /// </summary>
    public static IReadOnlyList<string> RecurringSummonPool(CardDefinition source,
        IEnumerable<CardDefinition> catalog, string? controllerFaction = null)
    {
        var ability = source.AbilityText ?? "";
        var saskia = System.Text.RegularExpressions.Regex.IsMatch(ability,
            @"\bSummon a random bronze non-Neutral unit\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(ability,
            @"\bTimer\s*3\s*:\s*Repeat (?:the )?Deploy ability\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var portal = System.Text.RegularExpressions.Regex.Match(ability,
            @"\bDeploy\s*:\s*Summon a random (?<cost>\d+)-provision cost unit from your deck to the left of this card\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!saskia && (!portal.Success || !System.Text.RegularExpressions.Regex.IsMatch(ability,
                @"\bTimer\s*\d+\s*:\s*Summon a random \d+-provision cost unit from your deck to the right of this card\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return [];
        var faction = string.Equals(source.Faction, "Neutral", StringComparison.OrdinalIgnoreCase)
            ? controllerFaction : source.Faction;
        var provision = portal.Success ? int.Parse(portal.Groups["cost"].Value,
            System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        return catalog.Where(card => card.Id != source.Id && StartingDeckRules.IsStartingCard(card) &&
                card.Kind == CardKind.Unit &&
                (!saskia || !card.IsGold && !string.Equals(card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase)) &&
                (provision is null || card.Provision == provision) &&
                (string.IsNullOrWhiteSpace(faction) || FactionCompatibility.IsPlayableBy(card, faction)))
            .Select(card => card.Id).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Printed source is persistent and can summon unrelated deck identities more than once.</summary>
    public static bool IsRecurringDeckSummonSource(CardDefinition source) =>
        System.Text.RegularExpressions.Regex.IsMatch(source.AbilityText ?? "",
            @"\bSummon a random bronze non-Neutral unit\b[^\n]*\n?[^\n]*\bTimer\s*3\s*:\s*Repeat (?:the )?Deploy ability\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(source.AbilityText ?? "",
            @"\bDeploy\s*:\s*Summon a random \d+-provision cost unit from your deck to the left of this card\b[\s\S]*\bTimer\s*\d+\s*:\s*Summon a random \d+-provision cost unit from your deck to the right of this card\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns an explicit source-relative destination only when the printed rule
    /// itself fixes that destination. Ordinary summons continue to use their
    /// general arrival geometry.
    /// </summary>
    public static string? ExplicitDeckSummonPlacement(CardDefinition source, CardDefinition target)
    {
        if (!RecurringSummonPool(source, [target]).Contains(target.Id)) return null;
        var ability = source.AbilityText ?? "";
        return System.Text.RegularExpressions.Regex.IsMatch(ability,
                   @"\bfrom your deck to the left of this card\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
               System.Text.RegularExpressions.Regex.IsMatch(ability,
                   @"\bfrom your deck to the right of this card\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? "left-or-right" : null;
    }

    private static IEnumerable<CompanionLink> NamedLinks()
    {
        foreach (var group in new[] { new[] { "112202", "112203", "112204" }, new[] { "112401", "112402" },
            new[] { "202533", "202534" }, new[] { "132206", "132207", "132208" },
            new[] { "162208", "162209" }, new[] { "202307", "202309" }, new[] { "201626", "203123" } })
        foreach (var source in group)
        foreach (var target in group.Where(id => id != source))
            yield return new(source, target, 1, group[0] is "112202" or "112401" or "202533", "Named companion synergy");
        yield return new("132218", "132408", 1, true, "Old Speartip: Asleep's named deck target");
        yield return new("162101", "162208", 1, false, "Letho's optional Auckes hand synergy");
        yield return new("162101", "162209", 1, false, "Letho's optional Serrit hand synergy");
        yield return new("203286", "202308", 1, false, "Serenity's melee target; row/Tribute choice is unresolved");
        yield return new("203286", "202561", 1, false, "Serenity's ranged target; row/Tribute choice is unresolved");
        // These have several useful configurations or alternative targets. Prefer a
        // supported cached package; do not auto-fill every possible target or both copies.
        foreach (var (source, target, reason) in new[]
        {
            ("200218", "132212", "Jotunn's Ice Giant starting-deck payoff"),
            ("132405", "201647", "Frenzied D'ao's Rock Barrage deck target"),
            ("113208", "202220", "Vaedermakar's optional Scepter of Storms bonus"),
            ("200083", "112215", "Iris' Companions' named Iris hand bonus"),
            ("200083", "202399", "Iris' Companions' named Iris hand bonus"),
            ("202237", "202238", "Palmerin's optional Milton board bonus"),
            ("202596", "202601", "Allgod's Offering starting-deck bonus"),
            ("203051", "202920", "Alumni's Ban Ard Student damage scaling"),
            ("203051", "202992", "Alumni's Aretuza Student boost scaling"),
            ("203058", "203057", "Octavia's possible son: The Brute"),
            ("203058", "202998", "Octavia's possible son: Scoundrel"),
            ("203058", "202999", "Octavia's possible son: Ignatius Hale"),
            ("203058", "202927", "Octavia's possible son: Fabian Hale"),
            ("203162", "202553", "Sigi Reuven: Mastermind's Collusion option"),
            ("203277", "152310", "Otkell's Freya's Blessing graveyard targets"),
            ("202537", "202535", "Vernossiel's Commando's optional Vernossiel bonus"),
        }) yield return new(source, target, 1, false, reason);
    }

    public static IReadOnlyList<CompanionLink> Active(IEnumerable<ObservedCard> observations,
        ObservedStartingDeckAssessment? constraints = null)
    {
        var observed = observations.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) && item.Card.CanBeInStartingDeck)
            .GroupBy(item => item.Card.Id).ToDictionary(group => group.Key, group => group.MaxBy(item => item.ObservedCopies)!);
        var singleton = constraints?.Shupe.State is ConstraintState.Likely or ConstraintState.Confirmed ||
            constraints?.Radeyah.State is ConstraintState.Likely or ConstraintState.Confirmed;
        var links = new List<CompanionLink>();
        foreach (var item in observed.Values)
        {
            if (singleton || item.ObservedCopies >= 2 || item.Card.IsGold) continue;
            if (ThinningPairs.Contains(item.Card.Id))
                links.Add(new(item.Card.Id, item.Card.Id, 2, true, "Matching-copy thinning package"));
            else if (item.Card.Id != "162301" && (System.Text.RegularExpressions.Regex.IsMatch(item.Card.AbilityText ?? "", @"\bBonded(?:\s*\([^)]*\))?\)?:") || item.Card.Id == "202836"))
                links.Add(new(item.Card.Id, item.Card.Id, 2, false, "Same-name synergy; created copies can supply the partner"));
        }
        links.AddRange(NamedLinks().Where(link => observed.ContainsKey(link.SourceId) &&
            (observed.GetValueOrDefault(link.TargetId)?.ObservedCopies ?? 0) < link.Copies));
        return links.DistinctBy(link => (link.SourceId, link.TargetId, link.Copies)).ToArray();
    }

}
