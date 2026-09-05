using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record CardStatEstimate(CardDefinition Card, PointDistribution Distribution, string Label, bool WholePlay,
    int ModeledCards = 0, int PoolCards = 0);
public sealed record ThreatStatContext(string? UserFaction = null, string? OpponentFaction = null, bool UserRunemage = false, bool OpponentRunemage = false);

/// <summary>Offline representative-board values. These are approximate priors, never exact current-board maxima.</summary>
public sealed record CreatePointProfiles(string Fingerprint, string ModelVersion, Dictionary<string, int?[]> Values)
{
    public const string Version = "create-immediate-v3";
    public static string Signature(IEnumerable<CardDefinition> catalog) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Version + typeof(TacticalPlayEngine).Assembly.ManifestModule.ModuleVersionId + JsonSerializer.Serialize(catalog.OrderBy(card => card.Id).Select(card => new { card.Id, card.Power, card.Provision,
            card.AbilityText, card.PrintedArmor, card.Faction, card.Kind, card.IsGold, card.CanBeInStartingDeck, Categories = card.Categories.Order() })))));

    public static CreatePointProfiles LoadOrBuild(string path, IReadOnlyList<CardDefinition> catalog)
    {
        var fingerprint = Signature(catalog);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 10_000_000 &&
                JsonSerializer.Deserialize<CreatePointProfiles>(File.ReadAllText(path)) is { } cached && cached.Fingerprint == fingerprint &&
                cached.ModelVersion == Version && cached.Values is not null && cached.Values.Count > 0 &&
                cached.Values.Values.All(values => values is { Length: 3 } && values.All(value => value is null or >= -10000 and <= 10000))) return cached;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        var result = Build(catalog);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(temporary, JsonSerializer.Serialize(result)); File.Move(temporary, path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Disposable cache; in-memory result remains valid. */ }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return result;
    }

    public static CreatePointProfiles Build(IReadOnlyList<CardDefinition> catalog)
    {
        var dummy = new CardDefinition("profile-body", "Representative body", "Neutral", CardKind.Unit, 4, 4, AbilityText: "", CanBeInStartingDeck: false, PrintedArmor: 0);
        var book = new PlayRuleBook(catalog.Append(dummy)); var engine = new TacticalPlayEngine(book);
        var values = new Dictionary<string, int?[]>();
        foreach (var card in catalog.Where(card => card.CanBeInStartingDeck))
        {
            GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
            var rule = book.Rules[card.Id]; var scores = new int?[3]; var one = new int?[3];
            if (rule.Unmodeled is null && rule.UnmodeledDeploy is null)
            for (var context = 0; context < 3; context++)
            {
                var p = Representative(context, dummy);
                var played = new PositionCard("profile-play", card.Id, card.Power, card.Power, card.PrintedArmor ?? 0, rule.PrintedStatuses, Original: false);
                p = p with { Zones = p.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand ? zone with { Cards = [played], TotalCount = 1 } : zone).ToImmutableArray() };
                scores[context] = engine.ImmediateMaximum(p, played.InstanceId, PlayerSide.User, branchLimit: 512).MaximumPoints;
                if (!card.IsGold && card.Kind == CardKind.Unit)
                {
                    p = p with { Zones = p.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand ? zone with { Cards = [played with { Power = 1, BasePower = 1 }] } : zone).ToImmutableArray() };
                    one[context] = engine.ImmediateMaximum(p, played.InstanceId, PlayerSide.User, branchLimit: 512).MaximumPoints;
                }
            }
            // Separate offline representative-state recipes can value simple non-reacting bodies even when the full
            // engine has no general handler for that ability. They do NOT enable the card in arbitrary live positions.
            for (var context = 0; context < 3; context++) scores[context] ??= DirectTempo(card, context);
            values[card.Id] = scores;
            if (!card.IsGold && card.Kind == CardKind.Unit) values[card.Id + ":one"] = one;
        }
        return new(Signature(catalog), Version, values);
    }
    private static GamePosition Representative(int context, CardDefinition dummy)
    {
        var p = GamePosition.EmptyKnown() with { Round = 1 };
        if (context == 0) return p;
        return p with { Zones = p.Zones.Select(zone => zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee ? zone with
        { Cards = [new($"{zone.Side}-a", dummy.Id, 4, 4, 0, ImmutableHashSet<CardStatus>.Empty),
            new($"{zone.Side}-b", dummy.Id, zone.Side == PlayerSide.Opponent && context == 2 ? 15 : 6, 6, 0, ImmutableHashSet<CardStatus>.Empty)], TotalCount = 2 } : zone).ToImmutableArray() };
    }
    private static int? DirectTempo(CardDefinition card, int context)
    {
        if (card.Kind != CardKind.Unit) return null;
        var text = PlayRules.Normalize(card.AbilityText); var power = card.Power;
        if (text.StartsWith("Order:", StringComparison.Ordinal) && !text.Contains("Zeal") && !text.Contains("Whenever") && !text.Contains("At the") && !text.Contains("When ")) return power;
        if (text is "When this unit is Discarded, Summon it from your graveyard to your Melee row." or
            "Whenever this unit takes damage, Summon all copies of self from your deck to this row." or
            "Whenever Dun Banner receives a boost, Summon all copies of it from your deck to this row." or
            "Melee: Whenever your opponent plays a unit on their side of the battlefield, damage it by 1." or
            "Shield. Whenever this unit loses its Shield, boost self by 2." or
            "Deploy (Melee): Spawn a base copy of a bronze non-Neutral allied unit at the bottom of your deck." or
            "Deploy: Give an enemy unit Bleeding (3). If played from the graveyard, damage an enemy unit by 3 instead.") return power;
        if (text == "Bloodthirst 1: At the end of your turn, boost self by 1.") return power; // Representative targets are not damaged.
        if (text == "Formation. Order: Boost an allied unit by 1. Charges: 3") return power + 3;
        if (text == "Deploy: Damage an enemy unit by 1. Order: Damage an enemy unit by 2.") return power + (context == 0 ? 0 : 1);
        if (text == "Deploy (Melee): Reduce the Cooldown of an allied unit by 1. Deploy (Ranged): Boost an allied unit by 1. Order: Give an allied unit Zeal.") return power + 1;
        if (text == "Deploy: Damage an allied unit by 2.") return Math.Max(0, power - 2);
        if (text == "Zeal. Order (Ranged): Damage a unit by 1. Cooldown: 2 Whenever you play a Beast, reduce the Cooldown by 1.") return power + (context == 0 ? 0 : 1);
        if (text == "Deploy (Melee): Damage an enemy unit by 2. Bloodthirst 2: Also give it Bleeding (2).") return power + (context == 0 ? 0 : 2);
        return null;
    }

    public CardStatEstimate? Estimate(CardDefinition source, GamePosition position, PlayerSide side,
        IReadOnlyList<CardDefinition> catalog, ThreatStatContext? context)
    {
        var text = PlayRules.Normalize(source.AbilityText); if (!text.Contains("Create", StringComparison.Ordinal)) return null;
        // A clicked Order is not immediate reach unless the card has Zeal. Its future activation belongs in Y/Z.
        if (!text.Contains("Deploy", StringComparison.Ordinal) && text.StartsWith("Order", StringComparison.Ordinal) &&
            !text.Contains("Zeal", StringComparison.Ordinal)) return null;
        var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var opponentFaction = enemy == PlayerSide.User ? context?.UserFaction : context?.OpponentFaction;
        var sourceState = side == PlayerSide.User ? position.User : position.Opponent;
        var opponentState = enemy == PlayerSide.User ? position.User : position.Opponent;
        var eligible = catalog.Where(card => card.CanBeInStartingDeck); var onePower = false;
        var recipes = Regex.Matches(text, @"Create(?: and play)? (?<recipe>[^.]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["recipe"].Value)
            .Where(recipe => Regex.IsMatch(recipe, @"^(?:a|an|the|bronze|gold|unit|special)\b", RegexOptions.IgnoreCase)).ToArray();
        // When row placement offers exact-cost versus up-to-cost recipes, the latter contains the former and is the
        // correct maximum-reach pool. Incidental prose such as Runemage's "Create effects show..." is not a recipe.
        var createClause = recipes.FirstOrDefault(recipe => recipe.Contains("or lower", StringComparison.OrdinalIgnoreCase)) ?? recipes.FirstOrDefault() ?? "";
        var faction = Regex.Match(createClause, @"\b(Skellige|Northern Realms|Nilfgaard|Monsters?|Scoia'tael|Syndicate)\b", RegexOptions.IgnoreCase);
        if (faction.Success)
        {
            var factionName = NormalizeFaction(faction.Groups[1].Value);
            eligible = eligible.Where(card => card.Faction.Equals(factionName, StringComparison.OrdinalIgnoreCase));
            if (createClause.Contains("bronze", StringComparison.OrdinalIgnoreCase)) eligible = eligible.Where(card => !card.IsGold);
            if (Regex.IsMatch(createClause, @"\bspecial(?: card)?\b", RegexOptions.IgnoreCase)) eligible = eligible.Where(card => card.Kind == CardKind.Special);
            else if (Regex.IsMatch(createClause, @"\bunit\b", RegexOptions.IgnoreCase)) eligible = eligible.Where(card => card.Kind == CardKind.Unit);
            var category = Regex.Match(createClause, @"\b(Elf|Dwarf|Mage|Wild Hunt|Bandit|Gang|Soldier|Pirate|Ship|Beast|Human)\b", RegexOptions.IgnoreCase);
            if (category.Success) eligible = eligible.Where(card => card.HasCategory(category.Groups[1].Value));
            var currentPower = position.Zone(side, CardZone.Hand).Cards.FirstOrDefault(card => card.CardId == source.Id)?.Power ?? source.Power;
            if (createClause.Contains("equal to, or lower than", StringComparison.OrdinalIgnoreCase) || createClause.Contains("equal to or lower than", StringComparison.OrdinalIgnoreCase))
                eligible = eligible.Where(card => card.Provision <= currentPower);
            else if (createClause.Contains("provision cost equal to this unit's power", StringComparison.OrdinalIgnoreCase))
                eligible = eligible.Where(card => card.Provision == currentPower);
        }
        else if (text == "Create and play a bronze card from your opponent's faction." && opponentFaction is not null)
            eligible = eligible.Where(card => !card.IsGold && card.Faction == opponentFaction);
        else if (text == "Create and play a gold unit from any faction.") eligible = eligible.Where(card => card.IsGold && card.Kind == CardKind.Unit);
        else if (text == "Deploy (Ranged): Create and play a special card from any faction.") eligible = eligible.Where(card => card.Kind == CardKind.Special);
        else if (text == "Create and play a bronze Northern Realms Mage and give it a Shield.")
            eligible = eligible.Where(card => !card.IsGold && card.Faction == "Northern Realms" && card.HasCategory("Mage"));
        else if (text.StartsWith("Deploy: Create and play a bronze Wild Hunt unit.", StringComparison.Ordinal))
            eligible = eligible.Where(card => !card.IsGold && card.Kind == CardKind.Unit && card.HasCategory("Wild Hunt"));
        else if (text == "Deploy: Create and play a bronze Neutral Bandit unit.")
            eligible = eligible.Where(card => !card.IsGold && card.Kind == CardKind.Unit && card.Faction == "Neutral" && card.HasCategory("Bandit"));
        else if (text == "Create and play a unit from your opponent's starting deck." && opponentState.StartingDeckIds is { } enemyDeck)
            eligible = eligible.Where(card => card.Kind == CardKind.Unit && enemyDeck.Contains(card.Id));
        else if (text == "Assimilate. Deploy: Create and play a 1-power copy of a bronze unit from your starting deck." && sourceState.StartingDeckIds is { } ownDeck)
        { eligible = eligible.Where(card => !card.IsGold && card.Kind == CardKind.Unit && ownDeck.Contains(card.Id)); onePower = true; }
        else if (createClause.Equals("a Runestone", StringComparison.OrdinalIgnoreCase))
            eligible = catalog.Where(card => card.Name.EndsWith(" Runestone", StringComparison.Ordinal) && card.AbilityText?.Contains("Create and play", StringComparison.Ordinal) == true);
        else return null;
        var pool = eligible.DistinctBy(card => card.Id).ToArray(); if (pool.Length == 0) return null;
        var enemyCards = position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).ToArray();
        var bucket = enemyCards.Length == 0 ? 0 : enemyCards.Any(card => card.Power >= 10) ? 2 : 1;
        var runemageActive = (side == PlayerSide.User ? context?.UserRunemage : context?.OpponentRunemage) == true || source.Name == "Runemage";
        var options = runemageActive ? 5 : 3;
        int? Score(CardDefinition card)
        {
            if (!card.Name.EndsWith(" Runestone", StringComparison.Ordinal))
                return Values.GetValueOrDefault(card.Id + (onePower ? ":one" : ""))?.ElementAtOrDefault(bucket);
            var innerFaction = Regex.Match(PlayRules.Normalize(card.AbilityText), @"bronze (?<faction>Skellige|Northern Realms|Nilfgaard|Monsters?|Scoia'tael|Syndicate) card", RegexOptions.IgnoreCase);
            if (!innerFaction.Success) return null;
            var factionName = NormalizeFaction(innerFaction.Groups["faction"].Value);
            var inner = catalog.Where(item => item.CanBeInStartingDeck && !item.IsGold && item.Faction.Equals(factionName, StringComparison.OrdinalIgnoreCase))
                .Select(item => Values.GetValueOrDefault(item.Id)?.ElementAtOrDefault(bucket)).ToArray();
            var modeled = inner.Where(value => value.HasValue).Select(value => value!.Value).OrderByDescending(value => value).Take(options).ToArray();
            return modeled.Length == 0 ? null : (int)Math.Round(modeled.Average());
        }
        var scores = pool.Select(Score).ToArray();
        var shift = source.Kind == CardKind.Unit
            ? position.Zone(side, CardZone.Hand).Cards.FirstOrDefault(card => card.CardId == source.Id)?.Power ?? source.Power
            : 0;
        if (onePower) shift++; // Vigo's own Assimilate, once for its generated play.
        var distribution = PointDistribution.BestKnownOffer(scores, options,
            $"Offline representative {(bucket == 0 ? "empty" : bucket == 2 ? "tall-target" : "ordinary-target")} board; {options} distinct uniform options, best immediate choice. " +
            "Known-option floor, not the full Create outcome: unmodeled alternatives may be better. Unresolved mass means no modeled choice offered. " +
            "Actual targets, engines, statuses and pool modifiers can change the result.").Shift(shift);
        return new(source, distribution, $"Create model · {scores.Count(score => score.HasValue)}/{pool.Length} options", true, scores.Count(score => score.HasValue), pool.Length);
    }
    private static string NormalizeFaction(string faction) => faction switch
    {
        "Monster" => "Monsters",
        _ => faction
    };
}
