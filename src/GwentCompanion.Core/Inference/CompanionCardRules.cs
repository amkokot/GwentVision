using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record CompanionLink(string SourceId, string TargetId, int Copies, bool AutoSuggest, string Reason);

/// <summary>Mechanics registry for copy detection and statistical audits, not likelihood boosts.
/// AutoSuggest marks a mechanical pairing hypothesis; it does not authorize automatic slots.</summary>
public static class CompanionCardRules
{
    public static IReadOnlyList<string> ThinningPairs { get; } =
    ["122311", "122313", "132310", "152318", "200038", "201559", "202273", "202334", "202368", "202521", "202549", "203089"];

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
