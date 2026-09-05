using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public enum PlayEffect
{
    None, Damage, Boost, Heal, Reset, Destroy, Banish, Purify, Lock, Poison, Shield,
    Consume, SpawnCopy, SpawnNamed, SummonCopies, Tutor, Resurrect, RowDamage, RowBoost,
    RowReset, DamageThenBoost, BoostArmor, MoveDamage, SelfBoost, GraveConsume, GraveBanish, SpawnPlay, Preparation,
    AdjacentBoost, RowCountBoost, OppositeBaseLoss, Griffin, LockMove, BoostedDamage, TriggerDeathwish, GraveMassBanish, ConsumeMany, BanishSmall, AllOthers,
    BackupPlan, NekkerWarrior, Oakcritters, Agitator, Braenn, Skirmisher, Artorius, Buhurt, RandomSplit, SpendAllCoinsDamage,
    GravePlay, SummonProvision, ClashHighest, Duration, SpawnRowNamed, SetPower,
    SpawnWeather, PlayTop, PlayAllCopies, PlayGoldenNekker, SelfDuration,
    HenGaidth, FrogMatingSeason, Aerondight, Scenario,
    HandBaseCopy, EnemyBronzeBaseCopy, AlliedBronzeBaseCopy, ControlledUnitDamage,
    NatureRebuke, MultiCategoryBoost, StartingDeckBonded, AdjacentTripleBoost,
    CreatePlay, SpawnPlayChoice, StartingDeckTacticSpawns, HandDiscardBoost,
    StartingDeckCategoryBoost, AdjacentTransform, TreantBoar,
    DrawTopUnitsShuffle, EnemyRowCountBoost, ChoiceBuff, Ida, CaravanVanguard, OrchardMantrap,
    VeteranBerserk,
    OrderDeckByProvision, GiveSpying, Fucusya, Artaud, TorresFounder, TorresPriest, Emhyr, Aucwenn,
    BattleStations, Abordage, LippyReach, Birna, Erland, SelfDamage, GeraltProfessional,
    ChampionCharge, CoupDeGrace, Sihil, HaraldGord, HighlandWarlord, BountyBrute, BountyIgnatius, DimunCaptain,
    BloodEagle, BloodthirstDamage, NovigradianJustice, PhilippaBlindFury, GeraltAard,
    GreedyAgent, Projection
}
public sealed record PlayRule(CardDefinition Card, PlayEffect Effect = PlayEffect.None, int Amount = 0,
    TargetSide Target = TargetSide.Any, BoardRow? RequiredRow = null, string? Condition = null,
    string? Argument = null, int Thrive = 0, int Assimilate = 0, int Harmony = 0, int Intimidate = 0,
    int Symbiosis = 0, string? Deathwish = null, string? Reaction = null,
    ImmutableHashSet<CardStatus>? PrintedStatuses = null, bool Echo = false, string? Unmodeled = null,
    string? Order = null, bool Zeal = false, int InitialCharges = 0, int? OrderAmount = null, string? UnmodeledDeploy = null,
    int Profit = 0, int FeeCost = 0, string? FeeAction = null, int FeeAmount = 0, int FeeCooldown = 0, BoardRow? FeeRow = null,
    bool Veteran = false, BoardRow? OrderRow = null, string? PowerInvariant = null, bool Disloyal = false,
    string? Partial = null, bool Formation = false, int OrderCooldown = 0, ReachProjectionPlan? Projection = null);

/// <summary>Whole-clause compilation, not a permissive natural-language interpreter. Unknown remainder is retained.</summary>
public static class PlayRules
{
    public static string Normalize(string? text) => Regex.Replace(text?.Trim() ?? "", @"\s+", " ");
    public static PlayRule Compile(CardDefinition card)
    {
        var text = Normalize(card.AbilityText);
        var rule = new PlayRule(card, PrintedStatuses: ImmutableHashSet<CardStatus>.Empty);
        if (card.AbilityText is null) return rule with { Unmodeled = "Missing ability text" };
        if (text == "No ability.") text = "";
        while (true)
        {
            // Gwent's catalogue omits the punctuation after some standalone keywords
            // (notably Aucwenn's `Symbiosis\nDeploy`).  Keep the token boundary strict,
            // while accepting either the printed full stop, the next clause, or EOF.
            var keyword = Regex.Match(text,
                @"^(Thrive|Assimilate|Harmony|Intimidate|Symbiosis)(?: (\d+))?(?:\. ?| (?=[A-Z])|$)");
            if (keyword.Success)
            {
                var value = keyword.Groups[2].Success ? int.Parse(keyword.Groups[2].Value) : 1;
                rule = keyword.Groups[1].Value switch
                {
                    "Thrive" => rule with { Thrive = value }, "Assimilate" => rule with { Assimilate = value },
                    "Harmony" => rule with { Harmony = value }, "Intimidate" => rule with { Intimidate = value },
                    _ => rule with { Symbiosis = value }
                };
                text = text[keyword.Length..]; continue;
            }
            var status = Regex.Match(text, @"^(Shield|Doomed|Immunity|Veil|Resilience|Defender)\. ?");
            if (status.Success)
            {
                rule = rule with { PrintedStatuses = rule.PrintedStatuses!.Add(status.Groups[1].Value == "Immunity" ? CardStatus.Immune : Enum.Parse<CardStatus>(status.Groups[1].Value)) };
                text = text[status.Length..]; continue;
            }
            if (text.StartsWith("Echo. ")) { rule = rule with { Echo = true }; text = text[6..]; continue; }
            if (text == "Veteran." || text.StartsWith("Veteran. "))
            {
                rule = rule with { Veteran = true };
                text = text.Length == 8 ? "" : text[9..]; continue;
            }
            if (text.StartsWith("Zeal. ")) { rule = rule with { Zeal = true }; text = text[6..]; continue; }
            if (text.StartsWith("Formation. ")) { rule = rule with { Formation = true }; text = text[11..]; continue; }
            if (text.StartsWith("Disloyal. ")) { rule = rule with { Disloyal = true }; text = text[10..]; continue; }
            var profit = Regex.Match(text, @"^Profit (\d+)\. ?");
            if (profit.Success)
            {
                rule = rule with { Profit = int.Parse(profit.Groups[1].Value) };
                text = text[profit.Length..]; continue;
            }
            break;
        }
        if (text.Length == 0) return rule;
        if (text.Contains("Scenario: Progress whenever you play", StringComparison.Ordinal))
            return rule with { Effect = PlayEffect.Scenario, Reaction = "scenario", InitialCharges = 0,
                Partial = card.Id == "203080" ? null :
                    "Scenario chapters are executed from board state; chapter-specific persistent infusions and uncommon generated-card clauses may remain conditional." };
        // The complete residual text must match. A patch changing an ability invalidates its handler automatically.
        rule = text switch
        {
            "When this unit takes damage, Summon all copies of self from your deck to this row." =>
                rule with { Reaction = "shieldmaiden" },
            "Whenever a unit gains Poison, gain 2 Coins." => rule with { Reaction = "roland-poison" },
            "Whenever you gain Coins, boost self by 1." => rule with { Reaction = "townsfolk" },
            "Whenever an enemy unit receives a status, boost self by 1." => rule with { Reaction = "thirsty-dame" },
            "Order (Predator): Consume a Token. If it was allied, double the boost from it." =>
                rule with { Order = "striga-token", InitialCharges = 1 },
            "Damage an enemy unit by 5, then Spawn and play a base copy of the stored unit and give it Doomed. Deathblow: Banish the unit and store its soul in the Hen Gaidth Sword." =>
                rule with { Effect = PlayEffect.HenGaidth, Amount = 5, Target = TargetSide.Enemy },
            "Choose 2 allied units, then give them Vitality (4) and Spawn a Frog on each side of the chosen units." =>
                rule with { Effect = PlayEffect.FrogMatingSeason, Amount = 4, Target = TargetSide.Allied },
            "Damage an enemy unit by 0, then boost the lowest-power allied unit by the excess damage dealt. At the end of your turn, if you have more points than your opponent, increase the damage by 1, wherever this card is." =>
                rule with { Effect = PlayEffect.Aerondight, Target = TargetSide.Enemy, Reaction = "aerondight" },
            "At the end of your turn, trigger Vitality for adjacent units. Order: Move self to the other row." =>
                rule with { Reaction = "frog", Order = "move", InitialCharges = 1 },
            "This unit may raid the battlefield to aid you in battle." => rule,
            "Hoard 4: At the end of your turn, boost self by 1." => rule with { Reaction = "peaches" },
            "Whenever this unit's Thrive is triggered, Spawn a Drone on this row. Adrenaline 4: Whenever this unit's Thrive is triggered, Spawn an Endrega Larva on this row instead." =>
                rule with { Reaction = "koshchey" },
            "Order: Play a bronze unit from your hand, then draw a card. Cooldown: 5 Crew: Whenever you play a unit next to this card, damage a random enemy unit by 2. Mages contribute to this card's Crew ability." =>
                rule with { Order = "raffard-hand", InitialCharges = 1, OrderCooldown = 5, Reaction = "raffard-crew" },
            "Order: Reset the power of a unit. Inspired: If it was boosted, give it Bleeding equal to the amount of boost it lost." =>
                rule with { Order = "baron-reset", InitialCharges = 1 },
            "Order: Damage a unit by 2. Charge: 1 Whenever you play a Tactic, gain 1 Charge." =>
                rule with { Order = "damage", OrderAmount = 2, InitialCharges = 1, Reaction = "tactic-charge" },
            "Order: Damage a unit by 1. Charge: 1 Whenever you play a card with an Order, gain 1 Charge ." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, Reaction = "order-charge" },
            "Order: Damage a unit by 1. Charge: 1 Whenever you play a Tactic, gain 1 Charge." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, Reaction = "tactic-charge" },
            "Whenever you use an Order ability, damage a random enemy unit by 1." =>
                rule with { Reaction = "order-damage" },
            "Resupply. Order: Damage a unit by 1. Cooldown: 1" =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, OrderCooldown = 1, Reaction = "resupply" },
            "Deploy: Spawn a 1-power base copy of a non-Neutral unit with a provision cost of 10 or less from your hand on this row." =>
                rule with { Effect = PlayEffect.HandBaseCopy, Amount = 1, Argument = "non-neutral;max-provision:10" },
            "Deploy: Spawn and play a base copy of a non-Disloyal bronze enemy unit." =>
                rule with { Effect = PlayEffect.EnemyBronzeBaseCopy },
            "Spawn and play a base copy of a bronze allied unit." =>
                rule with { Effect = PlayEffect.AlliedBronzeBaseCopy },
            "Deploy: Damage an enemy unit by the number of units you control." =>
                rule with { Effect = PlayEffect.ControlledUnitDamage, Target = TargetSide.Enemy },
            "Damage an enemy unit by 5. Deathblow: Boost a random allied Treant by 2." =>
                rule with { Effect = PlayEffect.NatureRebuke, Amount = 5, Target = TargetSide.Enemy },
            "Deploy: Boost an allied Elf, Dwarf, and Dryad by 3." =>
                rule with { Effect = PlayEffect.MultiCategoryBoost, Amount = 3, Argument = "Elf;Dwarf;Dryad", Target = TargetSide.Allied },
            "Deploy: Spawn and play a Bonded unit from your starting deck. Order: Spawn and play Golden Froth." =>
                rule with { Effect = PlayEffect.StartingDeckBonded, Order = "spawn-play:Golden Froth", InitialCharges = 1 },
            "Boost 3 adjacent units by 2." =>
                rule with { Effect = PlayEffect.AdjacentTripleBoost, Amount = 2 },
            "Deploy (Melee): Create and play a Scoia'tael special card with a provision cost equal to this unit's power. Deploy (Ranged): Create and play a Scoia'tael special card with a provision cost equal to or lower than this unit's power." =>
                rule with { Effect = PlayEffect.CreatePlay, Argument = "faction:Scoia'tael;special;row-power" },
            "Create and play a bronze Scoia'tael Elf, then, depending on the position of the chosen unit, boost the leftmost, a random, or the rightmost unit in your hand by 2." =>
                rule with { Effect = PlayEffect.CreatePlay, Argument = "faction:Scoia'tael;unit;bronze;category:Elf;harvest" },
            "Deploy: For the rest of the game, your Create effects show you 5 options instead of 3. Create and play a Runestone." =>
                rule with { Effect = PlayEffect.CreatePlay, Argument = "runestone" },
            "Order: Create and play a bronze Scoia'tael special card with a provision cost equal to, or lower than, this card's power." =>
                rule with { Order = "create-play:faction:Scoia'tael;special;bronze;up-to-power", InitialCharges = 1 },
            "Deploy: Spawn and play Ace up the Sleeve for each 3 Tactics in your starting deck." =>
                rule with { Effect = PlayEffect.StartingDeckTacticSpawns, Argument = "Ace up the Sleeve" },
            "Deploy (Melee): Draw a card, then Discard a card. If the Discarded card was a unit, boost self by its power." =>
                rule with { Effect = PlayEffect.HandDiscardBoost, RequiredRow = BoardRow.Melee },
            "Deploy: Transform adjacent units into random units that cost 1 provision more." =>
                rule with { Effect = PlayEffect.AdjacentTransform, Amount = 1 },
            "Deploy: Play a bronze Tactic from your graveyard and give it Doomed. At the end of your turn, boost self by 1. Grace 8: Move self to the opposite row and Purify self." =>
                rule with { Effect = PlayEffect.GravePlay, Argument = "special;bronze;category:Tactic;doomed", Reaction = "false-ciri" },
            "Boost an allied unit by 0. Increase the boost by 1 for each Nature card other than Spring Equinox in your starting deck." =>
                rule with { Effect = PlayEffect.StartingDeckCategoryBoost, Argument = "Nature", Target = TargetSide.Allied },
            "At the end of the round, move self to your opponent's graveyard. At the end of your turn, while in the graveyard, Banish the lowest-cost card from it. If Vypper is the only card in the graveyard, give it Doomed and Spying, then Summon self to a random enemy row." =>
                rule with { Reaction = "vypper" },
            "Deploy: Spawn and play a Griffin Witcher Adept, Griffin Witcher, Griffin Witcher Mentor, or Griffin Witcher Ranger. Order: Boost a unit in your deck by 3." =>
                rule with { Effect = PlayEffect.SpawnPlayChoice, Argument = "Griffin Witcher Adept|Griffin Witcher|Griffin Witcher Mentor|Griffin Witcher Ranger", Order = "boost-deck", OrderAmount = 3, InitialCharges = 1 },
            "Order: Transform an allied Witcher into a base copy of Griffin Witcher Adept." =>
                rule with { Order = "transform-adept", InitialCharges = 1, Target = TargetSide.Allied },
            "Deploy: Draw your top unit and boost it by 1, then shuffle a card from your hand back into your deck. Adrenaline 4: Draw your top 2 units and boost them by 1, then shuffle 2 cards from your hand back into your deck instead." =>
                rule with { Effect = PlayEffect.DrawTopUnitsShuffle, Amount = 1 },
            "Deploy: Boost self by 1 for each unit on an enemy row. Order: Transfer all boost from self to an allied unit." =>
                rule with { Effect = PlayEffect.EnemyRowCountBoost, Amount = 1, Order = "transfer-boost", InitialCharges = 1, Target = TargetSide.Allied },
            "Whenever your Symbiosis is triggered, give the unit to the left Vitality (2). Order: Remove all Vitality from an allied unit, then boost self by the same amount." =>
                rule with { Reaction = "symbiosis-left", Order = "consume-vitality", InitialCharges = 1, Target = TargetSide.Allied },
            "Deploy (Melee): Consume an allied unit. This unit triggers Thrive an additional time. Deploy (Ranged): Infuse 2 allied units with \"Thrive\"." =>
                rule with { Effect = PlayEffect.OrchardMantrap },
            "If you have no other 4-provision cost cards in your starting deck, start on your Ranged row." => rule,
            "Choose one: Boost an allied unit by 7 and give it Veil. Boost an allied unit by 9. Boost an allied unit by 6 and give it Vitality (6)." =>
                rule with { Effect = PlayEffect.ChoiceBuff, Target = TargetSide.Allied },
            "Deploy (Melee): Purify a unit. Deploy (Ranged): Give an allied unit Vitality (3)." =>
                rule with { Effect = PlayEffect.Ida },
            "Deploy (Melee): Boost self by 3. Deploy (Ranged): Spawn a base copy of self on this row. Bonded: Combine both abilities instead." =>
                rule with { Effect = PlayEffect.CaravanVanguard, Amount = 3 },
            "Deploy: Poison an enemy unit. Order (Melee): Poison an enemy unit." =>
                rule with { Effect = PlayEffect.Poison, Target = TargetSide.Enemy, Order = "poison", InitialCharges = 1, OrderRow = BoardRow.Melee },
            "Order: Summon a bronze Human unit from your graveyard to this row and give it Doomed. Cooldown: 7" =>
                rule with { Order = "summon-grave:unit;bronze;category:Human;doomed", InitialCharges = 1, OrderCooldown = 7 },
            "Order (Ranged): Damage a unit by 1. If you control only non-Neutral units, damage by 2 instead. Cooldown: 1" =>
                rule with { Order = "pavko", InitialCharges = 1, OrderCooldown = 1, OrderRow = BoardRow.Ranged },
            "Order: Heal an allied unit by 4 and reset its Cooldown. Inspired: Boost an allied unit by 4 and reset its Cooldown instead." =>
                rule with { Order = "priscilla", InitialCharges = 1, Target = TargetSide.Allied },
            "Resupply. Order: Spawn a Volunteer on this row. Cooldown: 2 Crew: Set the Cooldown to 1." =>
                rule with { Order = "frigate", InitialCharges = 1, OrderCooldown = 2, Reaction = "resupply-frigate" },
            "Deploy: Damage self by 3. Berserk 3: Heal self." =>
                rule with { Effect = PlayEffect.VeteranBerserk, Amount = 3 },
            "At the end of your turn, move self to the other row and damage a random enemy unit on the opposite row by 1. Adrenaline 3: Damage it by 2 instead." =>
                rule with { Reaction = "cat-witcher" },
            "When Poisoned, Spawn a base copy of self on this row. Counter: 1" =>
                rule with { Reaction = "mutant", InitialCharges = 1 },
            "Whenever you target an allied unit with a special, give it Vitality equal to the special's provision cost. Whenever you target an enemy unit with a special, give it Bleeding equal to the special's provision cost." =>
                rule with { Reaction = "prism" },
            "Deploy: Give an enemy unit Spying." =>
                rule with { Effect = PlayEffect.GiveSpying, Target = TargetSide.Enemy },
            "Order: Damage an enemy unit by 1. Charge: 1 Deathblow: Gain 1 Armor. Whenever an enemy unit gains Spying, gain 1 Charge." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, Zeal = true, Reaction = "spying-charge" },
            "Order: Banish a bronze unit from your graveyard and boost self by its power, then Summon a copy of it from your deck to this row. Sabbath: Play the copy instead." =>
                rule with { Order = "mammuna", InitialCharges = 1, Zeal = true },
            "Deploy: Play a non-Neutral unit from your graveyard with a provision cost of 10 or less and give it Doomed, then Spawn Rain on the opposite row with a duration equal to unused provisions." =>
                rule with { Effect = PlayEffect.Fucusya },
            "Deploy: Spawn and play a base copy of any non-Disloyal unit you gave Spying to during this game, excluding self." =>
                rule with { Effect = PlayEffect.Artaud },
            "Deploy: Give Spying to 3 units with provision costs of 10 or less from your opponent's deck, then Spawn base copies of them into your deck. Boost self by 1 for each provision below the limit. Order: Banish up to 3 cards from your starting deck from your deck. While in hand or deck, evolve after you win a round." =>
                rule with { Effect = PlayEffect.TorresFounder, Order = "torres-banish", InitialCharges = 1 },
            "Deploy: Move a non-Doomed allied unit that is not from your starting deck, excluding self, back to your hand, boost it by 2 and give it Doomed, then play a card. If there is no valid target, Create and play a bronze unit from your opponent's faction and boost it by 2 instead." =>
                rule with { Effect = PlayEffect.TorresPriest },
            "Deploy: Play a bronze Soldier or Aristocrat from your hand, then draw a card. Whenever your opponent plays a unit, give it Spying. Order: Seize a 1-power enemy unit with Spying. Devotion: At the end of your turn, refresh this card's Order." =>
                rule with { Effect = PlayEffect.Emhyr, Reaction = "emhyr", Order = "emhyr-seize", InitialCharges = 1 },
            "Deploy: Infuse all your Naiads with the Nature category, wherever they are. Whenever you Spawn a Wandering Treant, give it Vitality equal to the number of Dryads you control." =>
                rule with { Effect = PlayEffect.Aucwenn, Reaction = "aucwenn" },
            "Play up to 2 bronze cards from your hand, then draw as many cards." =>
                rule with { Effect = PlayEffect.BattleStations },
            "Damage an enemy unit by 2. Bloodthirst 2: Also play a Pirate from your hand, then draw a card." =>
                rule with { Effect = PlayEffect.Abordage, Amount = 2, Target = TargetSide.Enemy },
            "Deploy (Ranged): Swap your graveyard with your deck." =>
                rule with { Effect = PlayEffect.LippyReach, RequiredRow = BoardRow.Ranged },
            "Deploy (Melee): Shuffle a card from your opponent's graveyard into their deck. Deploy (Ranged): Shuffle a card from your graveyard into your deck." =>
                rule,
            "Deploy: Draw up to 2 cards, then Discard the same number of cards." =>
                rule with { Effect = PlayEffect.Birna },
            "Order: Draw up to 2 cards, then shuffle the same number of cards back into your deck. Whenever you draw a card, boost self by 2." =>
                rule with { Order = "snowdrop-draw", InitialCharges = 1, Zeal = true, Reaction = "draw-boost" },
            "Order: Draw a card, then Discard a card. Whenever you Discard a card, damage a random enemy unit by 2." =>
                rule with { Order = "coral-discard", InitialCharges = 1, Zeal = true, Reaction = "discard-damage" },
            "Deploy: Boost all units in your deck by 1. Adrenaline 3: Also gain Immunity. Order: Remove boosts from all units in your deck, then boost this unit by the same amount." =>
                rule with { Effect = PlayEffect.Erland, Order = "erland-cashout", InitialCharges = 1 },
            "Order: Play a boosted unit from your deck. While in deck, whenever this unit receives a boost from other abilities, boost self by the same amount." =>
                rule with { Order = "tutor-boosted", InitialCharges = 1 },
            "Deploy: Draw up to 2 Bonded units, then shuffle the same number of cards back into your deck. Order: Replay an allied Bonded unit." =>
                rule,
            "Order: Damage an enemy unit by 1. Deathblow: Shuffle self back into your deck. Whenever an enemy unit moves during your turn, Summon self from your deck to the opposite row, then damage it by 1." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, Zeal = true, Reaction = "milva" },
            "Deploy: Give an enemy unit Bleeding (4). At the end of your turn, give Bleeding (2) to a random enemy unit without Bleeding. Devotion: Bleeding on enemy units also triggers at the end of your turn." =>
                rule with { Effect = PlayEffect.Duration, Amount = 4, Argument = "Bleeding", Target = TargetSide.Enemy, Reaction = "unseen-elder" },
            "Tribute 3: Remove Poison from an allied unit, then boost self by 5. Whenever your opponent plays a unit, Poison it and remove a Counter. When the Counter reaches 0, the Tribute becomes a Fee. Counter: 2" =>
                rule with { Reaction = "manual-engine-2" },
            "Order (Melee): Damage an enemy unit by 3. At the end of your turn, if the Order is not used, boost a random non-Neutral unit in your hand by 1." =>
                rule with { Order = "damage", OrderAmount = 3, InitialCharges = 1, Zeal = true, OrderRow = BoardRow.Melee, Reaction = "dunca" },
            "Deploy: Damage self by 6. Whenever an enemy unit takes damage, Heal self by 1." =>
                rule with { Effect = PlayEffect.SelfDamage, Amount = 6, Reaction = "greatsword" },
            "Order: Reduce an allied unit's Cooldown by 1. When you play a Siege Engine, Summon self from your hand to the left of it, then draw a card." =>
                rule with { Reaction = "siege-master" },
            "Deploy: Damage 3 units by 1. If you control another Witcher, also gain Zeal. Order: Damage an enemy unit by 3. If its power is a multiple of 3, destroy it instead." =>
                rule with { Effect = PlayEffect.GeraltProfessional, Order = "professional", InitialCharges = 1, Target = TargetSide.Enemy },
            "Order (Melee): Split 3 damage randomly between enemy units on a row. At the end of your turn, if the Order is not used, damage a random enemy unit by 1." =>
                rule with { Order = "random-split-3", InitialCharges = 1, Zeal = true, OrderRow = BoardRow.Melee, Reaction = "patience-damage-1" },
            "Order: Lose all Armor, then boost self by that amount. Whenever an allied unit uses its Order, gain 1 Armor." =>
                rule with { Order = "armor-boost", InitialCharges = 1, Zeal = true, Reaction = "order-armor" },
            "Order (Melee): Duel an enemy unit." =>
                rule with { Order = "duel", InitialCharges = 1, OrderRow = BoardRow.Melee, Target = TargetSide.Enemy },
            "Deploy (Bloodthirst 2): Gain Zeal. Order: Damage a unit by 1. Charges: 3" =>
                rule with { Effect = PlayEffect.DimunCaptain, Order = "damage", OrderAmount = 1, InitialCharges = 3, Condition = "bloodthirst:2" },
            "Deploy (Bloodthirst 3): Gain Zeal. Order: Damage a unit by 2." =>
                rule with { Effect = PlayEffect.DimunCaptain, Order = "damage", OrderAmount = 2, InitialCharges = 1, Condition = "bloodthirst:3" },
            "Damage a unit by 5. Bloodthirst 3: Destroy a unit instead." =>
                rule with { Effect = PlayEffect.ChampionCharge, Amount = 5, Target = TargetSide.Any },
            "Damage a unit by 4. Bloodthirst 2: Damage by 6 instead." =>
                rule with { Effect = PlayEffect.BloodthirstDamage, Amount = 4, Argument = "6", Condition = "bloodthirst:2", Target = TargetSide.Any },
            "Damage an enemy unit by 2, then play a Warrior from your deck with a provision cost of 7 or less. Deathblow: Play a Warrior from your deck instead. Bloodthirst 3: Always trigger the Deathblow ability." =>
                rule with { Effect = PlayEffect.BloodEagle, Amount = 2, Target = TargetSide.Enemy },
            "Play a bronze Dwarf or Crownsplitter unit from your deck. If you already control a Dwarf or Crownsplitter, also Spawn a Cleaver's Muscle on your Melee row." =>
                rule with { Effect = PlayEffect.NovigradianJustice },
            "Deploy: Damage an enemy unit by 4, then damage a random enemy unit by 3, then 2, then 1." =>
                rule with { Effect = PlayEffect.PhilippaBlindFury, Target = TargetSide.Enemy },
            "Deploy (Melee): Damage 3 enemy units by 2, then move them to the Ranged row." =>
                rule with { Effect = PlayEffect.GeraltAard, RequiredRow = BoardRow.Melee, Target = TargetSide.Enemy },
            "Deploy: Create and play a bronze Disloyal unit from your starting deck." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "braathens" },
            "Look at the top card from your deck plus an additional card for each Coin you have. Play the top card for free, or play another card for a Coin cost equal to its distance from the top, then shuffle the remaining cards back into your deck." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "vivaldi-bank" },
            "Damage an enemy unit by 2. Deathblow: Play a 4-provision cost Warrior from your graveyard and give it Doomed. Devotion: Always trigger the Deathblow ability." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "war-of-clans", Amount = 2, Target = TargetSide.Enemy },
            "Deploy: Play an Alchemy card from your graveyard with a provision cost 4 or less. Increase the cost by the total duration of Rain and Storm on your opponent's side." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "bride-of-the-sea" },
            "Deploy: Summon all copies of self from your graveyard to this row. If you have an Alchemy card in your hand, also Summon all copies from your deck." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "crow-messenger" },
            "Deploy: Play a Tactic from your hand, then draw a card." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "hand-play-draw:Tactic" },
            "Deploy: Play a Bomb from your hand, then draw a card. Barricade: Whenever you play a Bomb, damage a random enemy unit by 1." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "hand-play-draw:Bomb", Reaction = "bomb-agent" },
            "Deploy: Damage an enemy unit by 2. Deathblow: Also Summon all copies of self from your deck to this row." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "brokilon-sentinel", Amount = 2, Target = TargetSide.Enemy },
            "Deploy (Melee): Create and play a bronze special card from your opponent's faction. Deploy (Ranged): Play a bronze special card from your opponent's graveyard." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "lydia" },
            "Sabbath: At the end of your turn, Spawn a Gernichora's Fruit on each sides of this unit and transform self into Gernichora without changing power." =>
                rule with { Reaction = "bloody-mistress" },
            "At the end of your turn, transform self into Bloody Mistress without changing power. Sabbath: At the end of your turn, boost self by 1 for each allied Gernichora's Fruit instead." =>
                rule with { Reaction = "gernichora" },
            "Deploy: Consume an allied unit. Order: Spawn and play Arachas Nest. Deathwish: Spawn a base copy of the Consumed unit on this row." =>
                rule with { Effect = PlayEffect.Consume, Target = TargetSide.Allied, Order = "spawn-play:Arachas Nest",
                    InitialCharges = 1, Deathwish = "consumed-copy" },
            "Spawn Rain on an enemy row for 2 turns, then Spawn a Deafening Siren on the opposite row." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "tears-of-siren" },
            "Damage a unit by 4 and give it Veil." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "damage-veil", Amount = 4, Target = TargetSide.Any },
            "Deploy: Move a unit to the other row." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "move-unit", Target = TargetSide.Any },
            "Deploy (Melee): Boost an allied unit by 2. Deploy (Ranged): Heal an allied unit by 4." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "hawker-healer", Target = TargetSide.Allied },
            "Deploy (Ranged): Damage an enemy unit by 3 if it is the only unit on its row." =>
                rule with { Effect = PlayEffect.GreedyAgent, Argument = "lone-row-damage", RequiredRow = BoardRow.Ranged,
                    Amount = 3, Target = TargetSide.Enemy },
            "Deathwish: Spawn 3 Drones on this row." => rule with { Deathwish = "drones3" },
            "At the end of your turn, if Poisoned, boost self by 2." => rule with { Reaction = "poisoned2" },
            "Damage an enemy unit by 3. Deathblow: Spawn and play a base copy of it. Conspiracy: Always trigger the Deathblow ability." =>
                rule with { Effect = PlayEffect.CoupDeGrace, Amount = 3, Target = TargetSide.Enemy },
            "Damage an enemy unit by 1. Deathblow: Play a bronze card from your hand, then move Sihil back to your hand. Initiative: Also increase the damage by 1 for the rest of the game. If the damage was 3, transform into Masterful Sihil instead." =>
                rule with { Effect = PlayEffect.Sihil, Amount = 1, Target = TargetSide.Enemy },
            "Deploy: Boost self by 0. Increase the boost by 1 for each special card you played this game. The boost cannot exceed 12." =>
                rule with { Effect = PlayEffect.HaraldGord },
            "Deploy: Increase the damage of your Raid cards by 1 for the rest of the game." =>
                rule with { Effect = PlayEffect.HighlandWarlord },
            "Deploy (Melee): Play a Raid from your deck." =>
                rule with { Effect = PlayEffect.Tutor, Argument = "category:Raid", RequiredRow = BoardRow.Melee },
            "Melee: At the end of your turn, boost a random unit in your hand by 1." =>
                rule with { Reaction = "dunca", RequiredRow = BoardRow.Melee },
            "When this unit is Discarded, Summon it from your graveyard to your Melee row." =>
                rule with { Reaction = "discard-summon" },
            "When this unit enters your graveyard during the round, Summon it to your Melee row and give it Doomed." =>
                rule with { Reaction = "grave-summon-doomed" },
            "Deploy: Boost self by boost. The boost is equal to the base power of the highest-power enemy unit with a Bounty you destroyed this game. Whenever you place a Bounty on an enemy unit, increase the Profit by 1." =>
                rule with { Effect = PlayEffect.BountyBrute },
            "At the start of the game, or when Spawned, set own power to 1. While in hand, deck, or on the battlefield, whenever an enemy unit with a Bounty is destroyed, Heal self by its base power." =>
                rule with { Effect = PlayEffect.BountyIgnatius },
            "Deploy: If you control a Dryad, gain Zeal. Order (Melee): Move self to the Ranged row, then Heal self. Order (Ranged): Move self to the Melee row, then damage an enemy unit by 2. Cooldown: 1" =>
                rule with { Effect = PlayEffect.TreantBoar, Order = "treant-boar", OrderAmount = 2, InitialCharges = 1, OrderCooldown = 1 },
            "Resupply. Order (Ranged): Damage an enemy unit by 2. Cooldown: 3 Crew: At the end of your turn, gain 1 Armor." =>
                rule with { Order = "damage", OrderAmount = 2, InitialCharges = 1, OrderCooldown = 3, OrderRow = BoardRow.Ranged, Reaction = "resupply-carro" },
            "Order (Melee): Damage an enemy unit by 1. Cooldown: 1 Adrenaline 3: At the end of your turn, damage a random enemy unit by 3 and Lock self." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, OrderCooldown = 1, OrderRow = BoardRow.Melee, Reaction = "griffin-witcher" },
            "Order (Ranged): Damage a unit by 1. Cooldown: 2 Whenever you play a Beast, reduce the Cooldown by 1." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, OrderCooldown = 2, OrderRow = BoardRow.Ranged, Reaction = "beast-cooldown" },
            "Order (Melee): Damage an enemy unit by 1. Cooldown: 2 Whenever you play an Elf, decrease the Cooldown by 1." =>
                rule with { Order = "damage", OrderAmount = 1, InitialCharges = 1, OrderCooldown = 2, OrderRow = BoardRow.Melee, Reaction = "elf-cooldown" },
            "Order: Halve own power and destroy an enemy unit with power up to the power removed from self." =>
                rule with { Order = "damsel", InitialCharges = 1, Target = TargetSide.Enemy },
            "Split 4 damage randomly between all enemy units. Increase the damage by 1 for each Siege Engine you control." => rule with { Effect = PlayEffect.RandomSplit, Amount = 4, Argument = "engines" },
            "Spawn Frost on an enemy row for 3 turns." => rule with { Effect = PlayEffect.SpawnWeather, Amount = 3, Argument = "Frost", Target = TargetSide.Enemy },
            "Spawn Fog on an enemy row for 3 turns." => rule with { Effect = PlayEffect.SpawnWeather, Amount = 3, Argument = "Fog", Target = TargetSide.Enemy },
            "Spawn Rain on an enemy row for 3 turns." => rule with { Effect = PlayEffect.SpawnWeather, Amount = 3, Argument = "Rain", Target = TargetSide.Enemy },
            "Play all copies of Biting Frost, Impenetrable Fog, or Torrential Rain from your deck." => rule with { Effect = PlayEffect.PlayAllCopies, Argument = "weather" },
            "Deploy: Play all copies of a bronze special card from your deck." => rule with { Effect = PlayEffect.PlayAllCopies, Argument = "bronze-special" },
            "Deploy: Play the top 2 cards from your deck." => rule with { Effect = PlayEffect.PlayTop, Amount = 2, Argument = "any" },
            "Deploy: Play the top non-Disloyal unit from your deck and boost it by 8." => rule with { Effect = PlayEffect.PlayTop, Amount = 8, Argument = "non-disloyal-unit" },
            "Deploy: If your starting deck does not have cards with a provision cost of 10 or more, play the top unit, special, and artifact from your deck. Ciri: Nova and Golden Nekker are excluded from this condition." =>
                rule with { Effect = PlayEffect.PlayGoldenNekker },
            "Deploy: Gain Vitality (5). At the start of the round, while in your graveyard, Banish self, then Spawn Phoenix Hatchling on a random allied row." =>
                rule with { Effect = PlayEffect.SelfDuration, Amount = 5, Argument = "Vitality", Reaction = "phoenix-round" },
            "Order: Transform into Phoenix." => rule with { Order = "transform:Phoenix", InitialCharges = 1 },
            "At the end of your turn, damage self and a random enemy unit by 1. Berserk 3: Transform into a Bear Abomination." =>
                rule with { Reaction = "drummond-berserker" },
            "Deploy (Melee): Split 3 damage randomly between all enemy units." => rule with { Effect = PlayEffect.RandomSplit, Amount = 3, RequiredRow = BoardRow.Melee },
            "Deathwish: Damage a random enemy unit by 5." => rule with { Deathwish = "random-damage", Amount = 5 },
            "Deploy: Create and play a 1-power copy of a bronze unit from your starting deck." => rule with { Effect = PlayEffect.Artorius },
            "Boost an enemy unit by 3, then an allied unit by 9." => rule with { Effect = PlayEffect.Buhurt },
            "Spend all your Coins and damage an enemy unit by the same amount." => rule with { Effect = PlayEffect.SpendAllCoinsDamage, Target = TargetSide.Enemy },
            "Fee 2: Boost adjacent units by 1. If this reduced your Coins to 0, boost by 2 instead." =>
                rule with { FeeCost = 2, FeeAction = "guard", FeeAmount = 1 },
            "Fee 2: Boost self by 2. Hoard 7: Boost by 3 instead." =>
                rule with { FeeCost = 2, FeeAction = "self-hoard", FeeAmount = 2 },
            "Fee 1: Damage the highest-power enemy unit by 1." =>
                rule with { FeeCost = 1, FeeAction = "highest-damage", FeeAmount = 1 },
            "Fee 1: Give an enemy unit Bleeding (1). If it has a Bounty, damage it by 1 instead." =>
                rule with { FeeCost = 1, FeeAction = "bounty-bleed", FeeAmount = 1 },
            "Fee 1: Poison an allied unit and boost it by 2. Cooldown: 1 Adrenaline 6: At the end of your turn, Purify self." =>
                rule with { FeeCost = 1, FeeAction = "poison-boost", FeeAmount = 2, FeeCooldown = 1 },
            "Fee 2: Damage an enemy unit by 3. Cooldown: 1 Whenever you play a Crime, reduce the Cooldown by 1." =>
                rule with { FeeCost = 2, FeeAction = "damage", FeeAmount = 3, FeeCooldown = 1 },
            "Fee 3: Damage an enemy unit by 3, then destroy self. At the end of your turn, reduce the Fee cost by 1. If the Fee cost was 0, destroy self instead." =>
                rule with { FeeCost = 3, FeeAction = "damage-destroy-self", FeeAmount = 3 },
            "Fee 1: Spawn a Firesworn Zealot on this row. Cooldown: 1" =>
                rule with { FeeCost = 1, FeeAction = "spawn:Firesworn Zealot", FeeCooldown = 1 },
            "At the end of your turn, boost the lowest-power unit you control by 1. Might: Boost all lowest-power units you control by 1 instead." => rule with { Reaction = "tugo" },
            "Deploy: Sort the cards in your deck from the highest to the lowest provision cost. At the start of the game, move self up by one position in the deck for each Tactic in your starting deck." =>
                rule with { Effect = PlayEffect.OrderDeckByProvision },
            "Deploy (Ranged): Replace your leader ability with a base copy of your opponent's leader ability." =>
                rule with { UnmodeledDeploy = "Leader replacement unresolved; no in-play passive" },
            "If your starting deck has no duplicates, Deploy (Melee): Spawn your faction stratagem on this row. Deploy (Ranged): Create a Neutral stratagem on this row." =>
                rule with { UnmodeledDeploy = "Singleton Stratagem choice unresolved; no in-play passive" },
            "Deploy: If your opponent has not passed and their hand is not full, Create a legendary unit from your faction that was not in your starting deck, add it to your opponent's hand, move the rest to the bottom of your opponent's deck, then Create a legendary unit from your opponent's faction that was not in their starting deck, add it to your hand, put the rest on the bottom of your deck." =>
                rule with { UnmodeledDeploy = "Created hand/deck choices unresolved; no in-play passive" },
            "Play a non-Neutral unit from your deck and boost it by 1." => rule with { Effect = PlayEffect.Tutor, Argument = "non-neutral-unit", Amount = 1 },
            "Deploy: Play an Echo card from your deck." => rule with { Effect = PlayEffect.Tutor, Argument = "echo" },
            "Look at a random Dwarf, Dryad, and Elf from your deck, then play one and boost it by 2." => rule with { Effect = PlayEffect.Tutor, Argument = "council", Amount = 2 },
            "Damage the last unit played by your opponent by 2 and Create a bronze Scoia'tael Elf with a Deploy ability that was not in your starting deck." => rule with { Effect = PlayEffect.BackupPlan },
            "Order: Consume an allied unit. Deathwish: Boost adjacent allied units by 2." => rule with { Order = "consume", InitialCharges = 1, Deathwish = "adjacent2" },
            "Deploy: Create a Scoia'tael Dwarf and give it 4 Armor. Order: Spawn and play Tempering." => rule with { UnmodeledDeploy = "Create Dwarf choice not implemented", Order = "tempering", InitialCharges = 1 },
            "Deploy: Summon a random 4-provision cost unit from your deck to the left of this card. Timer 3: Summon a random 4-provision cost unit from your deck to the right of this card." =>
                rule with { Effect = PlayEffect.SummonProvision, Amount = 4, Reaction = "portal-timer", InitialCharges = 1 },
            "Deploy: If this card would not trigger any of your Thrive units, damage self by 3 and Infuse self with \"Thrive\"." => rule with { Effect = PlayEffect.NekkerWarrior },
            "Deploy (Melee): Give an enemy unit Bleeding (4). Deploy (Ranged): Spawn a base copy of self on this row. Devotion: Combine both abilities instead." => rule with { Effect = PlayEffect.Oakcritters },
            "Deploy: Give 1 Armor to 2 Dwarves in your hand. Whenever you play a Dwarf, give it 1 Armor." => rule with { Effect = PlayEffect.Agitator, Reaction = "agitator" },
            "Deploy: If you control 2 or more allied Dryads, gain Zeal. Order: Damage an enemy unit by 1. Charges: 3" => rule with { Effect = PlayEffect.Braenn, Order = "damage", InitialCharges = 3, OrderAmount = 1, Target = TargetSide.Enemy },
            "Deploy (Melee): Damage an enemy unit by 3. If it survived, gain 1 Armor. Order (Barricade): Damage an enemy unit by 3." => rule with { Effect = PlayEffect.Skirmisher, Order = "barricade-damage", InitialCharges = 1, Target = TargetSide.Enemy },
            "Order: Boost an allied unit by 2. If it's a Dwarf, also give it 4 Armor." => rule with { Order = "miner", InitialCharges = 1, Target = TargetSide.Allied },
            "Deploy: Spawn a base copy of self on this row." => rule with { Effect = PlayEffect.SpawnCopy },
            "Deploy (Dominance): Summon all copies of self from your deck to this row." => rule with { Effect = PlayEffect.SummonCopies, Condition = "dominance" },
            "Deploy: If you control a Dwarf, Summon all copies of self from your deck to this row." => rule with { Effect = PlayEffect.SummonCopies, Condition = "dwarf" },
            "Deploy (Hoard 4): Summon all copies of self from your deck to this row." => rule with { Effect = PlayEffect.SummonCopies, Condition = "hoard4" },
            "Deploy: If your opponent has won a round this game, Spawn and play Battle Preparation." =>
                rule with { Effect = PlayEffect.SpawnPlay, Condition = "lost-round", Argument = "Battle Preparation" },
            "If your opponent has won a round this game, Summon self from your deck to a random allied row when you play a Soldier." => rule with { Reaction = "brigade" },
            "Boost an allied unit by 4 and give it 2 Armor. If it's a Soldier, boost by 6 and give 2 Armor instead." => rule with { Effect = PlayEffect.Preparation, Target = TargetSide.Allied },
            "Deploy: Consume an allied unit." => rule with { Effect = PlayEffect.Consume, Target = TargetSide.Allied },
            "Deploy: Consume 3 allied units." => rule with { Effect = PlayEffect.ConsumeMany, Target = TargetSide.Allied, Amount = 3 },
            "Deploy: Decrease own base power by 1 for each unit on the opposite row." => rule with { Effect = PlayEffect.OppositeBaseLoss },
            "Deploy: Destroy an allied unit on this row. If there are no targets, destroy self." => rule with { Effect = PlayEffect.Griffin },
            "Deploy: Lock a unit and move it to the other row." => rule with { Effect = PlayEffect.LockMove },
            "Deploy: Damage an enemy unit by 1. If Elven Wardancer is already boosted, damage by 3 instead." => rule with { Effect = PlayEffect.BoostedDamage, Target = TargetSide.Enemy },
            "Deploy: Boost adjacent units by 2." => rule with { Effect = PlayEffect.AdjacentBoost, Amount = 2 },
            "Deploy: Boost adjacent Dwarves by 2." => rule with { Effect = PlayEffect.AdjacentBoost, Amount = 2, Argument = "Dwarf" },
            "Deploy: Boost self by 1 for each allied unit on this row." => rule with { Effect = PlayEffect.RowCountBoost, Amount = 1 },
            "Deploy: Trigger an allied unit's Deathwish ability." => rule with { Effect = PlayEffect.TriggerDeathwish },
            "Deploy: Trigger a bronze allied unit's Deathwish ability." => rule with { Effect = PlayEffect.TriggerDeathwish, Argument = "bronze" },
            "Deploy: Banish all units in your graveyard, then boost self by 1 for each." => rule with { Effect = PlayEffect.GraveMassBanish },
            "Deploy: Banish a unit with power 4 or less." => rule with { Effect = PlayEffect.BanishSmall, Amount = 4 },
            "Deploy (Melee): Damage all other units by 2. Deploy (Ranged): Boost all other units by 2." => rule with { Effect = PlayEffect.AllOthers, Amount = 2 },
            "Order: Destroy all other units with power equal to Schirrú." => rule with { Order = "schirru", InitialCharges = 1 },
            "Order: Consume an allied unit. Charges: 2" => rule with { Order = "consume", InitialCharges = 2 },
            "Order: Consume an allied unit." => rule with { Order = "consume", InitialCharges = 1 },
            "Deathwish: Destroy the lowest-power enemy unit." => rule with { Deathwish = "manticore" },
            "Dominance: At the end of your turn, boost self by 1." => rule with { Reaction = "hound" },
            "Bloodthirst 1: At the end of your turn, boost self by 1." => rule with { Reaction = "warcrier" },
            "Deploy: Spawn 2 Rats on this row. Deathwish: Spawn 2 Rats on this row." => rule with { Effect = PlayEffect.SpawnNamed, Amount = 2, Argument = "Rat", Deathwish = "rats" },
            "Deploy (Melee): Consume a bronze unit in your graveyard." => rule with { Effect = PlayEffect.GraveConsume, RequiredRow = BoardRow.Melee, Argument = "bronze-own" },
            "Deploy (Melee): Consume a unit from your opponent's graveyard. Deploy (Ranged): Consume a unit from your graveyard." => rule with { Effect = PlayEffect.GraveConsume, Argument = "ozzrel" },
            "Deploy: Banish a card from your opponent's graveyard." => rule with { Effect = PlayEffect.GraveBanish },
            "Play a unit from your deck." => rule with { Effect = PlayEffect.Tutor, Argument = "unit" },
            "Play any card from your deck." => rule with { Effect = PlayEffect.Tutor, Argument = "any" },
            "Play a special card from your deck." => rule with { Effect = PlayEffect.Tutor, Argument = "special" },
            "Play a Deathwish unit from your deck." => rule with { Effect = PlayEffect.Tutor, Argument = "deathwish-unit" },
            "Summon a non-Neutral unit from your graveyard to an allied row and give it Doomed." => rule with { Effect = PlayEffect.Resurrect, Argument = "faction" },
            "Deploy: Destroy an enemy unit with 9 or more power." => rule with { Effect = PlayEffect.Destroy, Amount = 9, Target = TargetSide.Enemy },
            "Banish a unit or an artifact." => rule with { Effect = PlayEffect.Banish },
            "Deploy: Move an enemy unit to the other row and damage it by 2." => rule with { Effect = PlayEffect.MoveDamage, Amount = 2, Target = TargetSide.Enemy },
            "When you play a gold card, Summon self from your deck to a random allied row." => rule with { Reaction = "roach" },
            "At the end of your turn, if you control 5 or more Elves, Summon self from your deck to your Melee row." => rule with { Reaction = "aelirenn" },
            "Hoard 9: At the end of your turn, Summon self from your deck or graveyard to a random allied row." => rule with { Reaction = "redanian" },
            "At the end of your turn, if there is Frost on both enemy rows, Summon self from your deck to your Ranged row. Devotion: Once both players have passed, boost self by 2 for each turn of Frost remaining on your opponent's side." =>
                rule with { Reaction = "winter-queen" },
            "At the end of your turn, if there is Rain or Storm on both enemy rows, Summon self from your deck to your Ranged row. At the end of your turn, if neither enemy row has Rain or Storm, move self to the bottom of your deck." =>
                rule with { Reaction = "anglerfish" },
            "Deploy: Boost self by 5. When you play a Soldier, Summon self from your graveyard to a random allied row." => rule with { Effect = PlayEffect.SelfBoost, Amount = 5, Reaction = "ronvid" },
            "Deploy: Consume an allied unit. When you play a Deathwish unit on your Ranged row, Summon self from your graveyard to the same row, Consume it and gain Doomed." => rule with { Effect = PlayEffect.Consume, Target = TargetSide.Allied, Reaction = "toad" },
            "Deploy: Consume an allied unit. Order (Dominance): Consume an allied unit." => rule with { Effect = PlayEffect.Consume, Target = TargetSide.Allied, Order = "consume-dominance", InitialCharges = 1 },
            "Order: Trigger a bronze allied unit's Deathwish ability. Whenever you play a bronze unit, Summon a random copy of it from your graveyard to the same row and give it Doomed." =>
                rule with { Reaction = "tome", Order = "deathwish", InitialCharges = 1 },
            "Fee 3: Gain a Shield. Whenever your opponent plays a unit, boost self by 1. Bonded: Whenever your opponent plays a card, boost self by 1." =>
                rule with { Reaction = "seductress", Order = "shield-fee" },
            "Whenever you play an Alchemy card, boost self by 1. Bonded: Boost by 2 instead." => rule with { Reaction = "preacher" },
            "When you play a special card, Spawn a base copy of self on this row and remove a Counter. Counter: 1" =>
                rule with { Reaction = "whisperer", InitialCharges = 1 },
            "At the end of your turn, if this unit has Vitality, boost self by 1." => rule with { Reaction = "hamadryad" },
            "Deathwish: Spawn a Harpy on this row." => rule with { Deathwish = "harpy" },
            "Deathwish: Summon all copies of self from your deck to this row." => rule with { Deathwish = "copies" },
            "Deathwish: Boost the lowest-power allied unit by 4." => rule with { Deathwish = "lowest4" },
            "Deathwish: Summon 2 random bronze Deathwish units from your deck to this row." => rule with { Deathwish = "brewess" },
            "Deathwish: Your opponent Summons the highest-power unit from their deck to the opposite row." => rule with { Deathwish = "golyat" },
            "Melee: Whenever an enemy unit moves, damage it by 1. Ranged: Whenever an allied unit moves, boost it by 1." => rule with { Reaction = "sentry" },
            "Boost an allied unit by 4 and give it a Shield. While in your graveyard, when you play a unit, give it a Shield, then Banish self." => rule with { Effect = PlayEffect.Shield, Amount = 4, Target = TargetSide.Allied, Reaction = "wyvern-shield" },
            "Boost an allied unit by 5. If it's a Dwarf, also give it 2 Armor." => rule with { Effect = PlayEffect.BoostArmor, Amount = 5, Target = TargetSide.Allied },
            "Deploy (Melee): Clash with the highest-power enemy unit. Might: At the end of your turn, while in hand, gain 1 Armor." =>
                rule with { Effect = PlayEffect.ClashHighest, RequiredRow = BoardRow.Melee },
            "Deploy (Melee): Consume a unit with 4 or less power." =>
                rule with { Effect = PlayEffect.Consume, Amount = 4, RequiredRow = BoardRow.Melee },
            "Deploy (Melee): Spawn a Drummond Shieldmaiden on this row. Deploy (Ranged): Spawn a Drummond Queensguard on this row." =>
                rule with { Effect = PlayEffect.SpawnRowNamed, Argument = "Melee:Drummond Shieldmaiden;Ranged:Drummond Queensguard" },
            "Damage a unit by 1 six times or split 6 damage randomly between all units on an enemy row." =>
                rule with { Effect = PlayEffect.RandomSplit, Amount = 6 },
            "Play a bronze unit from your graveyard and give it Doomed." =>
                rule with { Effect = PlayEffect.GravePlay, Argument = "unit;bronze;doomed" },
            "Play a bronze non-Neutral unit from your graveyard and give it Doomed." =>
                rule with { Effect = PlayEffect.GravePlay, Argument = "unit;bronze;non-neutral;doomed" },
            "Deploy: Play a bronze Nature card from your graveyard." =>
                rule with { Effect = PlayEffect.GravePlay, Argument = "special;bronze;category:Nature" },
            "Deploy: Play a bronze Warrior from your graveyard, give it Doomed and damage self by 2. At the start of the round, while in hand or deck, evolve." =>
                rule with { Effect = PlayEffect.GravePlay, Argument = "unit;bronze;category:Warrior;doomed;self-damage:2" },
            "This unit's power is always equal to its Armor." => rule with { PowerInvariant = "armor" },
            "This card's power cannot be changed by other abilities." => rule with { PowerInvariant = "fixed" },
            "Set an enemy unit's power to 1." => rule with { Effect = PlayEffect.SetPower, Amount = 1, Target = TargetSide.Enemy },
            "Reset a unit." => rule with { Effect = PlayEffect.Reset },
            _ => rule with { Unmodeled = text }
        };
        if (rule.Unmodeled is null) return rule;
        var createFaction = Regex.Match(text, @"^Create and play a bronze (Skellige|Northern Realms|Nilfgaard|Monsters|Scoia'tael|Syndicate) card\.$");
        if (createFaction.Success) return rule with { Unmodeled = null, Effect = PlayEffect.CreatePlay,
            Argument = "faction:" + createFaction.Groups[1].Value + ";bronze" };
        var simple = Regex.Match(text, @"^(?:Deploy(?: \((Melee|Ranged)\))?: )?(Damage|Boost|Heal) (?:a|an) (?:(enemy|allied) )?unit by (\d+)\.$");
        if (simple.Success) return rule with { Unmodeled = null,
            Effect = Enum.Parse<PlayEffect>(simple.Groups[2].Value), Amount = int.Parse(simple.Groups[4].Value),
            RequiredRow = simple.Groups[1].Success ? Enum.Parse<BoardRow>(simple.Groups[1].Value) : null,
            Target = simple.Groups[3].Value switch { "enemy" => TargetSide.Enemy, "allied" => TargetSide.Allied, _ => TargetSide.Any } };
        var statusEffect = Regex.Match(text, @"^(?:Deploy: )?(Lock|Purify|Reset the power of|Poison|Destroy|Give) (?:a|an) (?:(enemy|allied) )?unit(?: a (Shield))?\.$");
        if (statusEffect.Success) return rule with { Unmodeled = null,
            Effect = statusEffect.Groups[3].Success ? PlayEffect.Shield : statusEffect.Groups[1].Value == "Reset the power of" ? PlayEffect.Reset : Enum.Parse<PlayEffect>(statusEffect.Groups[1].Value),
            Target = statusEffect.Groups[2].Value switch { "enemy" => TargetSide.Enemy, "allied" => TargetSide.Allied, _ => TargetSide.Any } };
        var duration = Regex.Match(text, @"^(?:Deploy: )?Give (?:a|an) (enemy|allied) unit (Bleeding|Vitality) \((\d+)\)\.$");
        if (duration.Success) return rule with { Unmodeled = null, Effect = PlayEffect.Duration,
            Target = duration.Groups[1].Value == "enemy" ? TargetSide.Enemy : TargetSide.Allied,
            Argument = duration.Groups[2].Value, Amount = int.Parse(duration.Groups[3].Value) };
        var rowEffect = Regex.Match(text, @"^(?:Deploy: )?(Damage|Boost) all (?:(enemy|allied) )?units on a row by (\d+)\.$");
        if (rowEffect.Success) return rule with { Unmodeled = null, Effect = rowEffect.Groups[1].Value == "Damage" ? PlayEffect.RowDamage : PlayEffect.RowBoost,
            Amount = int.Parse(rowEffect.Groups[3].Value), Target = rowEffect.Groups[2].Value switch { "enemy" => TargetSide.Enemy, "allied" => TargetSide.Allied, _ => TargetSide.Any } };
        var self = Regex.Match(text, @"^Deploy: Boost self by (\d+)\.$");
        if (self.Success) return rule with { Unmodeled = null, Effect = PlayEffect.SelfBoost, Amount = int.Parse(self.Groups[1].Value) };
        var damageBoost = Regex.Match(text, @"^Damage a unit by (\d+), then boost it by (\d+)\.$");
        if (damageBoost.Success) return rule with { Unmodeled = null, Effect = PlayEffect.DamageThenBoost,
            Amount = int.Parse(damageBoost.Groups[1].Value), Argument = damageBoost.Groups[2].Value };
        var tutor = Regex.Match(text, @"^(?:Deploy(?: \((Melee|Ranged)\))?: )?Play (?:a|an) (.+) from your deck\.$");
        if (tutor.Success)
        {
            var kind = tutor.Groups[2].Value;
            // Category labels must be singular catalog categories, not arbitrary clauses such as "random ...".
            if (kind is "Warfare card" or "Tactic card" or "Organic card" or "Nature card" or "Alchemy card" or "Crime card" or "Crime" or "Raid card" or "Elf" or "Dwarf" or "Witcher" or "artifact")
                return rule with { Unmodeled = null, Effect = PlayEffect.Tutor, Argument = kind == "artifact" ? "artifact" : "category:" + kind.Replace(" card", ""),
                    RequiredRow = tutor.Groups[1].Success ? Enum.Parse<BoardRow>(tutor.Groups[1].Value) : null };
        }
        var gravePlay = Regex.Match(text,
            @"^(?:Deploy: )?Play (?:a )?(?<descriptor>Bronze unit|bronze unit|unit|bronze non-Neutral unit|bronze [A-Za-z'’ -]+ card) from (?<owner>your|your opponent's) graveyard(?: with a provision cost of (?<max>\d+) or less)?(?<doomed> and give it Doomed)?\.$");
        if (gravePlay.Success)
        {
            var descriptor = gravePlay.Groups["descriptor"].Value;
            var arguments = new List<string>
            {
                descriptor.EndsWith("unit", StringComparison.OrdinalIgnoreCase) ? "unit" : "special"
            };
            if (descriptor.StartsWith("bronze", StringComparison.OrdinalIgnoreCase)) arguments.Add("bronze");
            if (descriptor.Contains("non-Neutral", StringComparison.OrdinalIgnoreCase)) arguments.Add("non-neutral");
            var category = Regex.Match(descriptor, @"^bronze (?<category>[A-Za-z'’ -]+) card$", RegexOptions.IgnoreCase);
            if (category.Success) arguments.Add("category:" + category.Groups["category"].Value);
            if (gravePlay.Groups["owner"].Value.Contains("opponent", StringComparison.OrdinalIgnoreCase)) arguments.Add("opponent");
            if (gravePlay.Groups["max"].Success) arguments.Add("max-provision:" + gravePlay.Groups["max"].Value);
            if (gravePlay.Groups["doomed"].Success) arguments.Add("doomed");
            return rule with { Unmodeled = null, Effect = PlayEffect.GravePlay, Argument = string.Join(';', arguments) };
        }
        var spawn = Regex.Match(text, @"^Deploy: Spawn (?:a |an |(\d+) )?([A-Za-z' ]+) on this row\.$");
        if (spawn.Success) return rule with { Unmodeled = null, Effect = PlayEffect.SpawnNamed, Amount = spawn.Groups[1].Success ? int.Parse(spawn.Groups[1].Value) : 1,
            Argument = spawn.Groups[2].Value };
        var feeTarget = Regex.Match(text, @"^Fee (\d+)(?: \((Melee|Ranged)\))?: (Damage|Boost) (?:a|an) (enemy|allied) unit by (\d+)\.$");
        if (feeTarget.Success) return rule with { Unmodeled = null, FeeCost = int.Parse(feeTarget.Groups[1].Value),
            FeeRow = feeTarget.Groups[2].Success ? Enum.Parse<BoardRow>(feeTarget.Groups[2].Value) : null,
            FeeAction = feeTarget.Groups[3].Value.ToLowerInvariant(), FeeAmount = int.Parse(feeTarget.Groups[5].Value),
            Target = feeTarget.Groups[4].Value == "enemy" ? TargetSide.Enemy : TargetSide.Allied };
        var feeSelf = Regex.Match(text, @"^Fee (\d+): Boost self by (\d+)\.$");
        if (feeSelf.Success) return rule with { Unmodeled = null, FeeCost = int.Parse(feeSelf.Groups[1].Value),
            FeeAction = "self", FeeAmount = int.Parse(feeSelf.Groups[2].Value) };
        var feeSpawn = Regex.Match(text, @"^Fee (\d+): Spawn (?:a|an) ([A-Za-z' ]+) on this row\.$");
        if (feeSpawn.Success) return rule with { Unmodeled = null, FeeCost = int.Parse(feeSpawn.Groups[1].Value),
            FeeAction = "spawn:" + feeSpawn.Groups[2].Value };
        var feeDuration = Regex.Match(text, @"^Fee (\d+): Give (?:a|an) (enemy|allied) unit (Bleeding|Vitality) \((\d+)\)\.$");
        if (feeDuration.Success) return rule with { Unmodeled = null, FeeCost = int.Parse(feeDuration.Groups[1].Value),
            FeeAction = feeDuration.Groups[3].Value.ToLowerInvariant(), FeeAmount = int.Parse(feeDuration.Groups[4].Value),
            Target = feeDuration.Groups[2].Value == "enemy" ? TargetSide.Enemy : TargetSide.Allied };
        var combined = Regex.Match(text, @"^(Deploy: (?:Damage|Boost) (?:a|an) (?:enemy|allied) unit by \d+\.) (Order: (?:Damage|Boost) (?:a|an) (?:enemy|allied) unit by \d+\.)$");
        if (combined.Success)
        {
            var deploy = Compile(card with { AbilityText = combined.Groups[1].Value });
            var action = Compile(card with { AbilityText = combined.Groups[2].Value });
            if (deploy.Unmodeled is null && action.Unmodeled is null && deploy.Target == action.Target)
                return deploy with { Card = card, Order = action.Order, OrderAmount = action.Amount, InitialCharges = 1 };
        }
        var order = Regex.Match(text, @"^Order(?: \((Melee|Ranged)\))?: (Damage|Boost) (?:a|an) (?:(enemy|allied) )?unit by (\d+)\.(?: Charges?: (\d+))?$");
        if (order.Success) return rule with { Unmodeled = null, Order = order.Groups[2].Value.ToLowerInvariant(), Amount = int.Parse(order.Groups[4].Value),
            Target = order.Groups[3].Value switch { "enemy" => TargetSide.Enemy, "allied" => TargetSide.Allied, _ => TargetSide.Any },
            OrderRow = order.Groups[1].Success ? Enum.Parse<BoardRow>(order.Groups[1].Value) : null,
            InitialCharges = order.Groups[5].Success ? int.Parse(order.Groups[5].Value) : 1 };
        var statusOrder = Regex.Match(text, @"^Order(?: \((Melee|Ranged)\))?: (Lock|Purify|Poison) (?:a|an) (?:(enemy|allied) )?unit\.$");
        if (statusOrder.Success) return rule with { Unmodeled = null, Order = statusOrder.Groups[2].Value.ToLowerInvariant(),
            Target = statusOrder.Groups[3].Value switch { "enemy" => TargetSide.Enemy, "allied" => TargetSide.Allied, _ => TargetSide.Any },
            OrderRow = statusOrder.Groups[1].Success ? Enum.Parse<BoardRow>(statusOrder.Groups[1].Value) : null,
            InitialCharges = 1 };
        return rule;
    }

    // Unknown abilities in the draw pile need not block unrelated plays; explicit off-board or global text does.
    public static bool HasOffBoardEffect(CardDefinition card) => Regex.IsMatch(card.AbilityText ?? "",
        @"\b(self|this card|this unit)\b.*\b(deck|graveyard|hand)\b|\b(in|from|into) (your |the )?(deck|graveyard|hand)\b.*\b(self|this card|this unit)\b|rest of the game|start of the game",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
}

public static class ScenarioRules
{
    public static bool Matches(CardDefinition scenario, PlayRule played)
    {
        var text = PlayRules.Normalize(scenario.AbilityText);
        var match = Regex.Match(text, @"Scenario: Progress whenever you play (?:an? )?(?<gold>gold )?(?<kind>unit )?(?:with )?(?<trigger>[A-Za-z'’ -]+?)\. Prologue:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups["gold"].Success && !played.Card.IsGold ||
            match.Groups["kind"].Success && played.Card.Kind != CardKind.Unit) return false;
        var trigger = Regex.Replace(match.Groups["trigger"].Value.Trim(), @" on your side of the battlefield$", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (trigger.Equals("Thrive", StringComparison.OrdinalIgnoreCase)) return played.Thrive > 0;
        if (trigger.Equals("Harmony", StringComparison.OrdinalIgnoreCase)) return played.Harmony > 0;
        if (trigger.Equals("Deathwish", StringComparison.OrdinalIgnoreCase)) return played.Deathwish is not null ||
            played.Card.AbilityText?.Contains("Deathwish:", StringComparison.OrdinalIgnoreCase) == true;
        if (trigger.Equals("unit", StringComparison.OrdinalIgnoreCase)) return played.Card.Kind == CardKind.Unit;
        return played.Card.HasCategory(trigger) || played.Card.HasCategory(trigger + "s");
    }
}

/// <summary>Immutable shared definitions compiled once. Search state remains private to each engine.</summary>
public sealed class PlayRuleBook
{
    internal IReadOnlyDictionary<string, PlayRule> Rules { get; }
    internal IReadOnlyDictionary<string, string> Names { get; }
    private readonly Lazy<PlayRuleBook>? _projection;
    internal bool IsProjection { get; }
    internal PlayRuleBook ForProjection => _projection?.Value ?? this;
    public PlayRuleBook(IEnumerable<CardDefinition> catalog)
    {
        Rules = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id, PlayRules.Compile);
        Names = Rules.Values.GroupBy(rule => rule.Card.Name).ToDictionary(group => group.Key, group => group.First().Card.Id);
        _projection = new(() =>
        {
            var cards = Rules.Values.Select(item => item.Card).ToArray();
            return new PlayRuleBook(Rules.Values.Select(rule => ReachProjectionCompiler.Compile(rule, cards)).ToArray());
        });
    }
    private PlayRuleBook(PlayRule[] rules)
    {
        Rules = rules.ToDictionary(rule => rule.Card.Id);
        Names = rules.GroupBy(rule => rule.Card.Name).ToDictionary(group => group.Key, group => group.First().Card.Id);
        IsProjection = true;
    }
}
