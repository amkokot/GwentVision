using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

// Separate from replay rules: extracted actions never certify an omitted condition or hidden pool.
public sealed record ReachProjectionStep(string Kind, int Amount = 0, string? Argument = null,
    PlayRule? Rule = null, BoardRow? Row = null, string? Condition = null,
    ReachProjectionStep? Otherwise = null, ReachProjectionStep? Delayed = null);
public sealed record ReachProjectionTrigger(string Package, bool Opponent, ReachProjectionStep Step);
public sealed record ReachProjectionPlan(ImmutableArray<ReachProjectionStep> Deploy,
    ImmutableArray<ReachProjectionStep> Orders, ImmutableArray<ReachProjectionStep> Automatic,
    ImmutableArray<ReachProjectionStep> Deathwish, ImmutableArray<ReachProjectionTrigger> Triggers,
    ImmutableArray<string> Omitted)
{
    public bool HasActions => Deploy.Length + Orders.Length + Automatic.Length + Deathwish.Length + Triggers.Length > 0;
}

/// <summary>Cached conservative action extraction for the approximate internal player, not a rules oracle.</summary>
internal static partial class ReachProjectionCompiler
{
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static PlayRule Compile(PlayRule source, IReadOnlyList<CardDefinition> catalog)
    {
        if (source.Unmodeled is null && source.UnmodeledDeploy is null) return source;
        var deploy = new List<ReachProjectionStep>(); var orders = new List<ReachProjectionStep>();
        var automatic = new List<ReachProjectionStep>(); var deathwish = new List<ReachProjectionStep>();
        var triggers = new List<ReachProjectionTrigger>(); var omitted = new List<string>();
        var text = PlayRules.Normalize(source.Card.AbilityText);
        // Keep these multi-sentence effects together: their second sentence depends on the chosen card.
        var provisionTutor = Match(text, @"^(?:Echo\. )?(?:Deploy: )?Play a (?<descriptor>non-Neutral unit|unit that isn't from your opponent's starting deck) from (?<owner>your|their) deck with a provision cost of (?<limit>\d+) or less\. Boost it by 1 for each provision below the limit\.$");
        if (provisionTutor.Success)
            return ProjectionOnly(source, new("provision-tutor", int.Parse(provisionTutor.Groups["limit"].Value), provisionTutor.Groups["owner"].Value), "Inferred deck pool; generated-origin exclusion requires starting-deck memory.");
        if (text == "Deploy: Swap this unit's power with an enemy unit. Adrenaline 2: Damage an enemy unit by 4 instead.")
            return ProjectionOnly(source, new("ivar"), "Current powers and hand-count Adrenaline; unread hand count produces alternatives.");
        if (text == "Choose a unit. If it's boosted, damage it by double the amount boosted. If it's damaged, boost it by double the amount damaged.")
            return ProjectionOnly(source, new("dark-mirror"), "Visible base/current power used.");
        if (text.StartsWith("Look at the top 2 cards from your deck, then play one and Banish the other.", StringComparison.Ordinal))
            return ProjectionOnly(source, new("top-play", 2), "Unchosen-card banish excluded from immediate reach.");
        if (text == "Create and play a unit from your opponent's starting deck.")
            return ProjectionOnly(source, new("starting-create", Argument: "opponent;unit"), "Create range over inferred starting-deck units.");
        if (text == "Create and play a bronze card from your opponent's faction.")
            return ProjectionOnly(source, new("faction-create", Argument: "opponent;bronze"), "Opponent faction inferred from leader/deck; unresolved faction is not invented.");
        if (text.StartsWith("Choose a mutagen: Red - Damage an enemy unit by 4.", StringComparison.Ordinal))
            return ProjectionOnly(source, new("mutagens"), "Greedy distinct mutagen choices; current Salamandra count and Poison/Coins.");
        if (text == "Deploy: Choose a bronze allied unit. Timer 2: Spawn a base copy of chosen unit to the right of Megascope.")
            return source with { Effect = PlayEffect.Projection, Unmodeled = null, UnmodeledDeploy = null,
                Partial = "Megascope stores a greedy bronze base-copy choice; observed timers require stored target/count memory.",
                Projection = new([new("timer-arm", 2, "copy")], [], [new("timer-tick", 2, "copy")], [], [], []) };
        var phase = "deploy"; BoardRow? row = null;
        var charge = Match(text, @"\bCharges?: (\d+)");
        var cooldown = Match(text, @"\bCooldown: (\d+)");
        // Do not split a condition away from its listener (e.g. Barricade: At the end ...).
        var pieces = Regex.Split(text, @"(?<=\.)\s+|\s+(?=(?:Deploy(?: \([^)]*\))?:|Order(?: \([^)]*\))?:|Deathwish:|Fee \d+:))");
        foreach (var original in pieces)
        {
            var clause = original.Trim(); if (clause.Length == 0) continue;
            var rowPrefix = Match(clause, @"^(?<row>Melee|Ranged): (?<action>At the end of your turn,.+)$");
            if (rowPrefix.Success) { row = Enum.Parse<BoardRow>(rowPrefix.Groups["row"].Value, true); clause = rowPrefix.Groups["action"].Value; }
            if (Match(clause, @"^(?:Zeal|Formation|Doomed|Veil|Shield|Immunity|Resilience|Defender|Veteran|Echo|Thrive(?: \d+)?|Assimilate(?: \d+)?|Symbiosis(?: \d+)?|Harmony(?: \d+)?|Intimidate(?: \d+)?|Profit \d+)\.?$").Success)
                continue; // original keyword fields already own these
            if (Match(clause, @"^(?:Charges?|Cooldown|Counter): \d+\.?$").Success) continue;
            var timer = Match(clause, @"^Timer (?<turns>\d+): (?<effect>.+)$");
            if (timer.Success && Atom(source.Card, timer.Groups["effect"].Value, catalog) is { } delayed)
            {
                var turns = int.Parse(timer.Groups["turns"].Value);
                deploy.Add(new("timer-arm", turns)); automatic.Add(new("timer-tick", turns, Delayed: delayed));
                phase = "omitted"; continue;
            }
            var autoCondition = Match(clause, @"^(?<condition>Barricade|Dominance|Inspired|Hoard \d+|Bloodthirst \d+): At the end of your turn, (?<action>.+)$");
            if (autoCondition.Success)
            {
                phase = "automatic"; row = null;
                var action = Atom(source.Card, autoCondition.Groups["action"].Value, catalog);
                if (action is not null) automatic.Add(action with { Condition = autoCondition.Groups["condition"].Value });
                else omitted.Add(original);
                continue;
            }
            var header = Match(clause, @"^(?<phase>Deploy|Order|Deathwish)(?: \((?<row>Melee|Ranged)\))?:\s*");
            if (header.Success)
            {
                phase = header.Groups["phase"].Value.ToLowerInvariant();
                row = header.Groups["row"].Success ? Enum.Parse<BoardRow>(header.Groups["row"].Value, true) : null;
                clause = clause[header.Length..];
            }
            const string turn = "At the end of your turn,";
            if (phase == "deathwish" && clause.StartsWith(turn, StringComparison.OrdinalIgnoreCase))
            { phase = "omitted"; omitted.Add(original); continue; } // delayed deathwish needs an explicit queued effect
            if (clause.StartsWith(turn, StringComparison.OrdinalIgnoreCase))
            { phase = "automatic"; if (!rowPrefix.Success) row = null; clause = clause[turn.Length..].Trim(); }
            else if (clause.StartsWith("At ", StringComparison.OrdinalIgnoreCase) || clause.StartsWith("Timer ", StringComparison.OrdinalIgnoreCase) ||
                clause.StartsWith("Fee ", StringComparison.OrdinalIgnoreCase) || clause.StartsWith("Ambush", StringComparison.OrdinalIgnoreCase) ||
                clause.StartsWith("Spring", StringComparison.OrdinalIgnoreCase))
            { phase = "omitted"; omitted.Add(original); continue; }
            var trigger = Match(clause, @"^(?:Whenever|When) (?<owner>you play|your opponent plays) (?:an? |another )?(?<package>[^,]+), (?<effect>.+)$");
            if (trigger.Success)
            {
                phase = "omitted";
                var package = trigger.Groups["package"].Value;
                if (KnownPackage(package, catalog) && Atom(source.Card, trigger.Groups["effect"].Value, catalog) is { } payoff)
                    triggers.Add(new(package, trigger.Groups["owner"].Value.Contains("opponent"), payoff));
                else omitted.Add(original);
                continue;
            }
            if (clause.StartsWith("Whenever ", StringComparison.OrdinalIgnoreCase) || clause.StartsWith("When ", StringComparison.OrdinalIgnoreCase))
            { phase = "omitted"; omitted.Add(original); continue; }
            if (phase == "omitted") { omitted.Add(original); continue; }
            // A conditional replacement selects one action; never add both the base and upgrade.
            var replacement = Match(clause, @"^(?<condition>Devotion|Dominance|Bonded|Barricade|Inspired|Crew|Bloodthirst \d+|Hoard \d+|Adrenaline \d+): (?<effect>.+) instead\.?$");
            var destination = phase switch { "order" => orders, "automatic" => automatic, "deathwish" => deathwish, _ => deploy };
            if (replacement.Success && destination.Count > 0)
            {
                var previous = destination[^1]; var effect = replacement.Groups["effect"].Value;
                // Resolve only unambiguous target shorthand using the preceding action's target.
                if (previous.Rule is { } prior && Match(effect, @"^(Damage|Boost|Heal) by \d+$").Success)
                    effect = Regex.Replace(effect, " by ", prior.Target switch {
                        TargetSide.Enemy => " an enemy unit by ", TargetSide.Allied => " an allied unit by ", _ => " a unit by " });
                var upgraded = Atom(source.Card, effect, catalog);
                if (upgraded is not null && previous.Condition is null)
                {
                    destination[^1] = upgraded with { Condition = replacement.Groups["condition"].Value,
                        Otherwise = previous, Row = previous.Row };
                    continue;
                }
            }
            var atom = Atom(source.Card, clause, catalog);
            if (atom is null) { omitted.Add(original); continue; }
            atom = atom with { Row = row ?? atom.Row };
            (phase switch { "order" => orders, "automatic" => automatic, "deathwish" => deathwish, _ => deploy }).Add(atom);
        }
        var plan = new ReachProjectionPlan(deploy.ToImmutableArray(), orders.ToImmutableArray(), automatic.ToImmutableArray(),
            deathwish.ToImmutableArray(), triggers.ToImmutableArray(), omitted.Distinct().ToImmutableArray());
        return source with
        {
            Effect = PlayEffect.Projection, Projection = plan, Unmodeled = null, UnmodeledDeploy = null,
            Partial = "Approximate greedy projection; " + (omitted.Count == 0 ? "extracted actions are not a whole-card rules proof." :
                "excluded: " + string.Join(" ", omitted)),
            Order = orders.Count > 0 ? "projection" : source.Order,
            InitialCharges = orders.Count > 0 ? charge.Success ? int.Parse(charge.Groups[1].Value) : 1 : source.InitialCharges,
            OrderCooldown = cooldown.Success ? int.Parse(cooldown.Groups[1].Value) : source.OrderCooldown,
            Zeal = source.Zeal,
            Formation = source.Formation,
            Deathwish = deathwish.Count > 0 ? "projection" : source.Deathwish,
        };
    }

    private static bool KnownPackage(string text, IReadOnlyList<CardDefinition> catalog) =>
        text is "card" or "unit" or "special card" or "gold card" or "bronze card" ||
        catalog.Any(card => card.HasCategory(text.Replace(" card", "")));

    private static PlayRule ProjectionOnly(PlayRule source, ReachProjectionStep step, string assumption) => source with
    {
        Effect = PlayEffect.Projection, Unmodeled = null, UnmodeledDeploy = null, Partial = "Approximate projection: " + assumption,
        Projection = new([step], [], [], [], [], [assumption]),
    };

    private static ReachProjectionStep? Atom(CardDefinition source, string input, IReadOnlyList<CardDefinition> catalog)
    {
        var text = input.Trim().TrimEnd('.');
        if (text.EndsWith(" instead", StringComparison.OrdinalIgnoreCase)) return null; // replacement, not an additive second effect
        var conditional = Match(text, @"^(?<condition>Devotion|Dominance|Bonded|Barricade|Inspired|Bloodthirst \d+|Hoard \d+|Adrenaline \d+): (?<effect>.+)$");
        if (conditional.Success)
        {
            var child = Atom(source, conditional.Groups["effect"].Value.Replace(" instead", ""), catalog);
            return child is null ? null : child with { Condition = conditional.Groups["condition"].Value };
        }
        var selfCondition = Match(text, @"^if (?:this unit|self) (?:is |has )(?<condition>boosted|damaged|Vitality|Bleeding|Poison), (?<effect>.+)$");
        if (selfCondition.Success)
        {
            var child = Atom(source, selfCondition.Groups["effect"].Value, catalog);
            return child is null ? null : child with { Condition = selfCondition.Groups["condition"].Value };
        }
        // Do not strip an arbitrary condition, target qualifier or suffix to manufacture a legal action.
        if (text.StartsWith("If ", StringComparison.OrdinalIgnoreCase)) return null;
        var spawnRandom = Match(text, @"^Spawn and play (?:a )?random (?<descriptor>.+)$");
        if (spawnRandom.Success && Pool(spawnRandom.Groups["descriptor"].Value, catalog) is { } randomPool)
            return new("create", Argument: string.Join('|', randomPool));
        var startingCopy = Match(text, @"^Spawn and play a base copy of a (?<descriptor>.+) from your starting deck$");
        if (startingCopy.Success && Pool(startingCopy.Groups["descriptor"].Value, catalog) is { } startingPool)
            return new("starting-create", Argument: "own;" + string.Join('|', startingPool));
        foreach (var prefix in new[] { "", "Deploy: " })
        {
            var compiled = PlayRules.Compile(source with { AbilityText = prefix + text + "." });
            if (compiled.Unmodeled is null && compiled.UnmodeledDeploy is null && compiled.Effect != PlayEffect.None)
                return new("rule", Rule: compiled);
        }
        var self = Match(text, @"^(?<action>Boost|Damage|Heal) self by (?<amount>\d+)$");
        if (self.Success) return new(self.Groups["action"].Value.ToLowerInvariant() + "-self", int.Parse(self.Groups["amount"].Value));
        if (text.Equals("Poison self", StringComparison.OrdinalIgnoreCase)) return new("poison-self");
        if (text.Equals("Purify self", StringComparison.OrdinalIgnoreCase)) return new("purify-self");
        if (text.Equals("Gain Zeal", StringComparison.OrdinalIgnoreCase)) return new("zeal");
        var duration = Match(text, @"^Gain (?<status>Vitality|Bleeding) \((?<amount>\d+)\)$");
        if (duration.Success) return new("duration-self", int.Parse(duration.Groups["amount"].Value), duration.Groups["status"].Value);
        var multipleDamage = Match(text, @"^Damage (?<count>\d+) (?<random>random )?(?<side>enemy )?units by (?<amount>\d+)$");
        if (multipleDamage.Success) return new("multiple-damage", int.Parse(multipleDamage.Groups["amount"].Value),
            multipleDamage.Groups["count"].Value + ";" + multipleDamage.Groups["random"].Success + ";" + multipleDamage.Groups["side"].Success);
        if (text.Equals("Choose a unit", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("Heal an allied unit", StringComparison.OrdinalIgnoreCase)) return new("heal-full");
        if (text.Equals("Transform unit to the right into base copy of self", StringComparison.OrdinalIgnoreCase)) return new("transform-right");
        var drain = Match(text, @"^Drain an enemy unit by (?<amount>\d+)$");
        if (drain.Success) return new("drain", int.Parse(drain.Groups["amount"].Value));
        var gainCount = Match(text, @"^Boost self by (?<amount>\d+) for each (?<side>allied|enemy)? ?unit (?:you control|on this row)$");
        if (gainCount.Success) return new("count-boost", int.Parse(gainCount.Groups["amount"].Value), text.Contains("this row") ? "row" : gainCount.Groups["side"].Value);
        if (text.Equals("damage self by 1 then damage a random enemy unit by 1", StringComparison.OrdinalIgnoreCase)) return new("berserker-tick");
        var adjacent = Match(text, @"^Boost (?:(?:an?|the) )?unit to the right by (?<amount>\d+)$");
        if (adjacent.Success) return new("boost-right", int.Parse(adjacent.Groups["amount"].Value));
        var poisonBoost = Match(text, @"^Poison an allied unit and boost it by (?<amount>\d+)$");
        if (poisonBoost.Success) return new("poison-boost", int.Parse(poisonBoost.Groups["amount"].Value));
        var halfRow = Match(text, @"^(?<action>Damage|Boost) all (?<allied>allied )?units (?:on an enemy row|on a row) by half their base power$");
        if (halfRow.Success) return new("half-row", Argument: halfRow.Groups["action"].Value.ToLowerInvariant());
        var chained = Match(text, @"^(?<first>Damage an enemy unit by \d+),? then (?<second>boost (?:a random )?(?:non-Neutral )?unit in your hand by \d+)$");
        if (chained.Success)
        {
            // A random hand recipient changes carryover distribution, not its total.
            var first = Atom(source, chained.Groups["first"].Value, catalog);
            var second = Atom(source, Regex.Replace(chained.Groups["second"].Value, "a random ", "a ", RegexOptions.IgnoreCase), catalog);
            if (first is not null && second is not null) return new("damage-carryover", first.Rule!.Amount, second.Amount + ";" + second.Argument);
        }
        var profit = Match(text, @"^Gain (?<amount>\d+) Coins?$");
        if (profit.Success) return new("coins", int.Parse(profit.Groups["amount"].Value));
        var draw = Match(text, @"^Draw (?<amount>a|\d+) cards?$");
        if (draw.Success) return new("draw", draw.Groups["amount"].Value == "a" ? 1 : int.Parse(draw.Groups["amount"].Value));
        var spawn = Match(text, @"^Spawn (?:(?<count>\d+) |an? )?(?<name>.+?) on (?<where>this row|an allied row|each allied row|each side of this card)$");
        if (spawn.Success)
        {
            var name = spawn.Groups["name"].Value;
            var target = catalog.FirstOrDefault(card => card.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                catalog.FirstOrDefault(card => card.Name.Equals(name.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
            if (target is not null) return new("spawn", spawn.Groups["count"].Success ? int.Parse(spawn.Groups["count"].Value) : 1,
                target.Id + "|" + spawn.Groups["where"].Value);
        }
        var choice = Match(text, @"^Spawn and play (?:an? )?(?<names>.+)$");
        if (choice.Success)
        {
            var names = Regex.Split(choice.Groups["names"].Value, @",? or |, ").Select(name => Regex.Replace(name, @"^an? ", "")).ToArray();
            var pool = names.Select(name => catalog.FirstOrDefault(card => card.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (pool.Length > 0 && pool.All(card => card is not null)) return new("create", Argument: string.Join('|', pool.Select(card => card!.Id)));
        }
        var create = Match(text, @"^Create(?: and play)? (?:an? )?(?<descriptor>.+?)(?: and give it (?:a Shield|\d+ Armor))?$");
        if (create.Success && Pool(create.Groups["descriptor"].Value, catalog) is { } created)
            return new("create", Argument: string.Join('|', created));
        var play = Match(text, @"^Play (?:an? |any )?(?<descriptor>.+?) from (?<owner>your|your opponent's|their) (?<zone>deck|graveyard|hand)(?: and give it Doomed)?$");
        if (play.Success && Pool(play.Groups["descriptor"].Value, catalog) is { } fetched)
            return new("pool-play", Argument: play.Groups["owner"].Value + ";" + play.Groups["zone"].Value + ";" +
                (text.EndsWith("Doomed", StringComparison.OrdinalIgnoreCase) ? "doomed" : "") + ";" + string.Join('|', fetched));
        var top = Match(text, @"^Look at (?:the )?(?:top|first) (?<count>\d+) cards (?:from the top of |from |in )?your deck(?:,? then| and) play one(?: of them)?$");
        if (top.Success) return new("top-play", int.Parse(top.Groups["count"].Value));
        var random = Match(text, @"^(?<action>Damage|Boost|Heal) (?:a )?random (?<side>enemy|allied) unit by (?<amount>\d+)$");
        if (random.Success) return new("random-" + random.Groups["action"].Value.ToLowerInvariant(), int.Parse(random.Groups["amount"].Value), random.Groups["side"].Value);
        var extrema = Match(text, @"^(?<action>Destroy|Boost|Damage|Heal) (?:the )?(?<extreme>highest|lowest)-power (?<side>enemy |allied )?unit(?: you control)?(?: by (?<amount>\d+))?$");
        if (extrema.Success) return new("extreme-" + extrema.Groups["action"].Value.ToLowerInvariant(),
            extrema.Groups["amount"].Success ? int.Parse(extrema.Groups["amount"].Value) : 0,
            extrema.Groups["extreme"].Value + ";" + extrema.Groups["side"].Value.Trim());
        var all = Match(text, @"^(?<action>Boost|Damage|Heal) (?:all|each) (?<other>other )?(?<side>allied|enemy)? ?(?<category>[A-Za-z' -]*?)units?(?: on (?:your side of )?the battlefield)? by (?<amount>\d+)$");
        if (all.Success && (all.Groups["category"].Value.Trim().Length == 0 || catalog.Any(card => card.HasCategory(all.Groups["category"].Value.Trim()))))
            return new("all-" + all.Groups["action"].Value.ToLowerInvariant(), int.Parse(all.Groups["amount"].Value),
                all.Groups["side"].Value + ";" + all.Groups["category"].Value.Trim() + ";" + all.Groups["other"].Value);
        var purifyBoost = Match(text, @"^Purify an allied unit and boost it by (?<amount>\d+)$");
        if (purifyBoost.Success) return new("purify-boost", int.Parse(purifyBoost.Groups["amount"].Value));
        var weather = Match(text, @"^Spawn (?<name>Frost|Rain|Fog|Storm|Cataclysm|Blood Moon) on both enemy rows for (?<amount>\d+) turns?$");
        if (weather.Success) return new("weather-both", int.Parse(weather.Groups["amount"].Value), weather.Groups["name"].Value);
        var carry = Match(text, @"^Boost (?<count>a|an|\d+|all) (?<descriptor>.+?) in your (?<zone>deck|hand) by (?<amount>\d+)$");
        if (carry.Success && Pool(carry.Groups["descriptor"].Value.TrimEnd('s'), catalog) is { } boosted)
            return new("carryover", int.Parse(carry.Groups["amount"].Value), carry.Groups["count"].Value + ";" + carry.Groups["zone"].Value + ";" + string.Join('|', boosted));
        if (Match(text, @"^Damage an enemy unit by (?:the power of your highest-power unit|its provision cost)$").Success)
            return new("dynamic-damage", Argument: text.Contains("provision") ? "provision" : "highest-ally");
        if (text.Equals("Heal an allied unit, then boost it by the amount it was Healed", StringComparison.OrdinalIgnoreCase)) return new("restore");
        if (text.Equals("Boost an allied unit by its provision cost, or damage an enemy unit by its provision cost", StringComparison.OrdinalIgnoreCase)) return new("triangle");
        if (text.Equals("Set an allied unit's power to match the highest-power enemy unit", StringComparison.OrdinalIgnoreCase)) return new("match-highest");
        if (text.Equals("Each player destroys their lowest-power unit", StringComparison.OrdinalIgnoreCase)) return new("predatory-dive");
        if (text.Equals("Remove 2 Armor from all units on a row and damage them by 2", StringComparison.OrdinalIgnoreCase)) return new("surrender");
        if (text.Equals("Remove an enemy unit's Shield and damage it by 4, or give an allied unit a Shield and boost it by 4", StringComparison.OrdinalIgnoreCase)) return new("tourney-joust", 4);
        if (text.Equals("Destroy a Doomed unit", StringComparison.OrdinalIgnoreCase)) return new("destroy-filter", Argument: "doomed");
        if (text.Equals("Destroy a 4-provision cost unit", StringComparison.OrdinalIgnoreCase)) return new("destroy-filter", 4, "provision");
        var armor = Match(text, @"^Give (?:an allied unit|self) (?<amount>\d+) Armor$");
        if (armor.Success) return new(text.Contains("self") ? "armor-self" : "armor", int.Parse(armor.Groups["amount"].Value));
        return AdditionalAtom(source, text, catalog);
    }

    // Unknown descriptors do NOT become unrestricted pools. Identities are indexed once and
    // intersected with the actual inferred zone during play, avoiding repeated catalog scans.
    private static string[]? Pool(string descriptor, IReadOnlyList<CardDefinition> catalog)
    {
        var text = descriptor.Replace(" from any faction", "", StringComparison.OrdinalIgnoreCase).Trim(); IEnumerable<CardDefinition> pool = catalog.Where(card => card.CanBeInStartingDeck &&
            card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact);
        var max = Match(text, @"(?:with )?(?:a )?provision cost of (?<amount>\d+) or less");
        if (max.Success) { pool = pool.Where(card => card.Provision <= int.Parse(max.Groups["amount"].Value)); text = text.Remove(max.Index, max.Length).Trim(); }
        var provision = Match(text, @"(?<amount>\d+)-provision cost ");
        if (provision.Success) { pool = pool.Where(card => card.Provision == int.Parse(provision.Groups["amount"].Value)); text = text.Remove(provision.Index, provision.Length); }
        foreach (var color in new[] { "bronze", "gold", "legendary" })
            if (Match(text, @"\b" + color + @"\b").Success)
            { pool = pool.Where(card => card.IsGold == (color != "bronze")); text = Regex.Replace(text, @"\b" + color + @"\b", "", RegexOptions.IgnoreCase).Trim(); }
        if (text.Contains("non-Neutral", StringComparison.OrdinalIgnoreCase))
        { pool = pool.Where(card => card.Faction != "Neutral"); text = text.Replace("non-Neutral", "", StringComparison.OrdinalIgnoreCase).Trim(); }
        foreach (var faction in catalog.Select(card => card.Faction).Distinct().OrderByDescending(value => value.Length))
            if (text.Contains(faction, StringComparison.OrdinalIgnoreCase))
            { pool = pool.Where(card => card.Faction == faction); text = text.Replace(faction, "", StringComparison.OrdinalIgnoreCase).Trim(); break; }
        if (text.EndsWith(" with a Fee", StringComparison.OrdinalIgnoreCase))
        { pool = pool.Where(card => card.AbilityText?.Contains("Fee ") == true); text = text[..^11].Trim(); }
        if (text.Contains("an Craite", StringComparison.OrdinalIgnoreCase))
        { pool = pool.Where(card => card.Name.StartsWith("An Craite", StringComparison.OrdinalIgnoreCase)); text = text.Replace("an Craite", "", StringComparison.OrdinalIgnoreCase).Trim(); }
        if (text.Contains("Crew", StringComparison.OrdinalIgnoreCase))
        { pool = pool.Where(card => card.AbilityText?.Contains("Crew", StringComparison.OrdinalIgnoreCase) == true); text = text.Replace("Crew", "", StringComparison.OrdinalIgnoreCase).Trim(); }
        foreach (var category in catalog.SelectMany(card => card.Categories).Distinct().OrderByDescending(value => value.Length))
            if (Match(text, @"\b" + Regex.Escape(category) + @"\b").Success)
            { pool = pool.Where(card => card.HasCategory(category)); text = Regex.Replace(text, @"\b" + Regex.Escape(category) + @"\b", "", RegexOptions.IgnoreCase).Trim(); }
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text is "unit" or "units") pool = pool.Where(card => card.Kind == CardKind.Unit);
        else if (text is "special" or "special card") pool = pool.Where(card => card.Kind == CardKind.Special);
        else if (text is "artifact") pool = pool.Where(card => card.Kind == CardKind.Artifact);
        else if (text is not ("" or "card" or "cards")) return null;
        return pool.Select(card => card.Id).ToArray();
    }
}
