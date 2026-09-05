using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record PointContribution(string Source, int Minimum, int Maximum, string Reason, bool Deferred = false,
    bool RequiresAction = false)
{
    public string RangeText => Minimum == Maximum ? $"{Maximum:+0;-0;0}" : $"{Minimum:+0;-0;0}–{Maximum:+0;-0;0}";
}

public sealed record BoardInteractionEstimate(int Minimum, int Maximum, IReadOnlyList<PointContribution> Contributions,
    IReadOnlyList<string> Assumptions)
{
    public bool HasValue => Contributions.Count > 0;
}

/// <summary>
/// Portable play-event valuation for engines already present in a supplied position. This intentionally does not
/// interpret the candidate's Deploy text; <see cref="ApproximatePointModel"/> owns that direct value. Keeping the
/// two concerns separate prevents an unsupported card from erasing otherwise readable board interactions.
/// </summary>
public sealed class BoardInteractionPointModel
{
    private static readonly Regex DirectPlayPayoff = new(
        @"(?:Whenever|When) you play (?:(?:an?|another) )?(?<package>unique special card|unit with (?:the )?Deathwish ability|unit with Deploy|special card|card|unit|[A-Za-z'’ -]+?)(?: card)?,\s*(?:then )?(?<effect>boost self|boost it|damage (?:a )?random enemy unit|boost (?:a )?random allied unit|heal (?:a )?(?:random )?allied unit) by (?<amount>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ChargePayoff = new(
        @"Order(?:\s*\([^)]*\))?:\s*(?<effect>Damage|Boost)[^.]*? by (?<order>\d+).*?Whenever you play (?:an? )?(?<package>Tactic|Warrior|card with an Order)(?: card)?,?\s*gain (?<charges>\d+) Charge",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CooldownDamagePayoff = new(
        @"Order(?:\s*\([^)]*\))?:\s*Damage (?:an enemy |a |an |the )?unit by (?<damage>\d+).*?Cooldown:\s*(?<cooldown>\d+).*?Whenever you play (?:an? )?(?<package>Elf|Vampire|Beast|Ship|Cursed)(?: card)?,?\s*(?:reduce|decrease) the Cooldown by (?<reduce>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex OpponentPlayPayoff = new(
        @"Whenever your opponent plays (?:a )?(?<package>unit|special card|card),\s*(?<effect>damage it|boost self) by (?<amount>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, CardDefinition> _cards;
    private readonly IReadOnlyDictionary<string, PlayRule> _rules;

    public BoardInteractionPointModel(IEnumerable<CardDefinition> catalog)
    {
        var cards = catalog.DistinctBy(card => card.Id).ToArray();
        _cards = cards.ToDictionary(card => card.Id);
        _rules = cards.ToDictionary(card => card.Id, PlayRules.Compile);
    }

    public BoardInteractionEstimate Estimate(CardDefinition candidate, GamePosition position, PlayerSide side)
    {
        var other = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var contributions = new List<PointContribution>();
        var assumptions = new HashSet<string>();
        var own = Units(position, side).ToArray();
        var enemy = Units(position, other).ToArray();
        var handCard = position.Zone(side, CardZone.Hand).Cards.FirstOrDefault(card => card.CardId == candidate.Id);
        var candidatePower = handCard?.Power ?? candidate.Power;
        var origin = handCard?.Original;
        var ownRowsComplete = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board).All(zone => zone.Complete);

        void Add(string source, int low, int high, string reason, bool deferred = false, bool requiresAction = false)
        {
            if (low > high) (low, high) = (high, low);
            if (low == 0 && high == 0) return;
            contributions.Add(new(source, low, high, reason, deferred, requiresAction));
        }
        void Active(PositionCard card, string source, int value, string reason, bool deferred = false)
        {
            if (card.Statuses?.Contains(CardStatus.Locked) == true) return;
            if (card.Statuses is null)
            {
                Add(source, Math.Min(0, value), Math.Max(0, value), reason + "; Lock status unread.", deferred);
                assumptions.Add("Unread Lock statuses make some engine contributions ranges.");
            }
            else Add(source, value, value, reason, deferred);
        }
        bool HasPackage(string package) => candidate.HasCategory(package);

        var triggeredThrive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listener in own)
        {
            if (!_rules.TryGetValue(listener.CardId, out var rule)) continue;
            var name = rule.Card.Name;
            var active = listener.Statuses?.Contains(CardStatus.Locked) != true;
            var possibleActive = listener.Statuses is null || active;
            if (!possibleActive) continue;

            var thrive = rule.Thrive + listener.ExtraThrive;
            if (candidate.Kind == CardKind.Unit && thrive > 0)
            {
                if (listener.Power is { } enginePower && candidatePower > enginePower)
                {
                    Active(listener, name, thrive, $"Thrive {thrive}: {candidate.Name} enters at {candidatePower} power.");
                    if (active) triggeredThrive.Add(listener.InstanceId);
                }
                else if (listener.Power is null)
                {
                    Add(name, 0, thrive, $"Thrive {thrive}: engine power is unread, so the comparison with {candidatePower} is unresolved.");
                    assumptions.Add("Unread engine power can change Thrive activation.");
                }
            }

            if (rule.Assimilate > 0 && origin != true)
            {
                var amount = rule.Assimilate;
                if (origin == false) Active(listener, name, amount, $"Assimilate {amount}: candidate is known to be generated.");
                else
                {
                    Add(name, 0, amount, $"Assimilate {amount}: candidate origin is unknown.");
                    assumptions.Add("Candidate origin is needed to resolve Assimilate.");
                }
            }

            if (rule.Harmony > 0 && candidate.Kind == CardKind.Unit && candidate.Faction == "Scoiatael" && candidate.Categories.Count > 0)
            {
                var unique = candidate.Categories.Any(category => !own.Any(card =>
                    _cards.GetValueOrDefault(card.CardId) is { Faction: "Scoiatael" } definition && definition.HasCategory(category)));
                if (unique)
                {
                    var low = ownRowsComplete ? rule.Harmony : 0;
                    if (listener.Statuses is null) low = 0;
                    Add(name, low, rule.Harmony, $"Harmony {rule.Harmony}: {candidate.Name} supplies a currently unique Scoia'tael category." +
                        (ownRowsComplete ? "" : " Hidden row identities could already have that category."));
                }
            }

            if (rule.Intimidate > 0 && HasPackage("Crime"))
            {
                var amount = rule.Intimidate;
                if (listener.CardId == "202890")
                {
                    var row = position.Zones.First(zone => zone.Side == side && zone.Zone == CardZone.Board && zone.Cards.Contains(listener));
                    var index = row.Cards.IndexOf(listener);
                    if (index > 0 && _cards.GetValueOrDefault(row.Cards[index - 1].CardId)?.HasCategory("Crownsplitters") == true) amount++;
                    if (index >= 0 && index + 1 < row.Cards.Length && _cards.GetValueOrDefault(row.Cards[index + 1].CardId)?.HasCategory("Crownsplitters") == true) amount++;
                }
                Active(listener, name, amount, $"Intimidate {amount}: {candidate.Name} is a Crime" +
                    (amount == rule.Intimidate ? "." : "; adjacent Crownsplitters increase Cleaver's trigger."));
            }

            if (rule.Reaction == "preacher" && HasPackage("Alchemy"))
            {
                var bonded = own.Count(card => card.CardId == listener.CardId) >= 2;
                Active(listener, name, bonded ? 2 : 1, bonded ? "Alchemy reaction with Bonded active." : "Alchemy reaction.");
            }

            // Portable functional engines cover literal play-package payoffs without requiring the whole card to compile.
            // Skip reactions already represented by the strict keyword fields above.
            foreach (Match match in DirectPlayPayoff.Matches(rule.Card.AbilityText ?? ""))
            {
                var package = match.Groups["package"].Value;
                var eligible = MatchesPlayPackage(candidate, package);
                if (eligible == false || package.Equals("Crime", StringComparison.OrdinalIgnoreCase) && rule.Intimidate > 0 ||
                    package.Equals("Alchemy", StringComparison.OrdinalIgnoreCase) && rule.Reaction == "preacher") continue;
                var amount = int.Parse(match.Groups["amount"].Value);
                var text = rule.Card.AbilityText ?? "";
                var instead = Regex.Match(text, @"Bonded:\s*(?:Boost|Damage|Heal)[^.]*? by (?<amount>\d+) instead", RegexOptions.IgnoreCase);
                if (instead.Success && own.Count(card => card.CardId == listener.CardId) >= 2)
                    amount = int.Parse(instead.Groups["amount"].Value);
                var effect = match.Groups["effect"].Value;
                var range = effect.StartsWith("damage", StringComparison.OrdinalIgnoreCase)
                    ? RandomDamage(enemy, amount, position.Zones.Where(zone => zone.Side == other && zone.Zone == CardZone.Board).All(zone => zone.Complete))
                    : effect.StartsWith("heal", StringComparison.OrdinalIgnoreCase) ? (Minimum: 0, Maximum: amount)
                    : (Minimum: amount, Maximum: amount);
                var low = eligible is null || listener.Statuses is null ? Math.Min(0, range.Minimum) : range.Minimum;
                var high = listener.Statuses?.Contains(CardStatus.Locked) == true ? 0 : range.Maximum;
                Add(name, low, high, $"{package} play reaction: {effect} contributes {low:+0;-0;0} to {high:+0;-0;0} immediate points." +
                    (eligible is null ? " The play-history condition is unread." : ""));
            }

            foreach (Match match in ChargePayoff.Matches(rule.Card.AbilityText ?? ""))
            {
                var package = match.Groups["package"].Value;
                if (MatchesPlayPackage(candidate, package) != true) continue;
                var amount = int.Parse(match.Groups["order"].Value) * int.Parse(match.Groups["charges"].Value);
                var ready = listener.Cooldown == 0 && listener.Charges is not null;
                Add(name, 0, amount, $"{package} grants charge reach worth up to {amount}; conversion depends on Order readiness and a legal target.",
                    requiresAction: true);
                if (!ready) assumptions.Add("A gained Charge is only upper-bound reach when the receiving Order is not known ready.");
            }

            foreach (Match match in CooldownDamagePayoff.Matches(rule.Card.AbilityText ?? ""))
            {
                var package = match.Groups["package"].Value;
                if (!HasPackage(package) || listener.Cooldown == 0) continue;
                var reduction = int.Parse(match.Groups["reduce"].Value);
                var damage = int.Parse(match.Groups["damage"].Value);
                if (listener.Cooldown is { } current && current > reduction) continue;
                var reach = BestDamage(enemy, damage, position.Zones.Where(zone => zone.Side == other && zone.Zone == CardZone.Board).All(zone => zone.Complete));
                var low = listener.Cooldown is null || listener.Statuses is null ? 0 : reach.Minimum;
                Add(name, low, reach.Maximum,
                    $"{package} play reduces Cooldown by {reduction}; " +
                    (listener.Cooldown is null ? "readiness is unread, so the newly enabled Order is bounded." :
                        $"current Cooldown {listener.Cooldown} reaches 0 and enables its {damage}-damage Order this turn."),
                    requiresAction: true);
            }

            if (HasPackage("Warfare") && Regex.IsMatch(rule.Card.AbilityText ?? "", @"\bResupply\b", RegexOptions.IgnoreCase))
            {
                var orderDamage = Regex.Match(rule.Card.AbilityText ?? "", @"Order(?:\s*\([^)]*\))?:\s*Damage (?:an enemy |a |an )?unit by (?<damage>\d+)", RegexOptions.IgnoreCase);
                if (orderDamage.Success && listener.Cooldown != 0 && listener.Cooldown is null or <= 1)
                {
                    var damage = int.Parse(orderDamage.Groups["damage"].Value);
                    var reach = BestDamage(enemy, damage, position.Zones.Where(zone => zone.Side == other && zone.Zone == CardZone.Board).All(zone => zone.Complete));
                    Add(name, listener.Cooldown is null || listener.Statuses is null ? 0 : reach.Minimum, reach.Maximum,
                        $"Resupply reduces Cooldown by 1 and can make the {damage}-damage Order ready this turn.", requiresAction: true);
                    if (listener.Cooldown is null) assumptions.Add("Resupply receiver Cooldown is unread, so enabled Order damage is upper-bound reach.");
                }
            }

            if (rule.Reaction == "whisperer" && candidate.Kind == CardKind.Special)
            {
                var row = position.Zones.First(zone => zone.Side == side && zone.Zone == CardZone.Board && zone.Cards.Contains(listener));
                var room = row.Cards.Length + (candidate.Kind == CardKind.Unit ? 1 : 0) < GwentRules.MaximumRowSize;
                if (room && listener.Charges != 0)
                {
                    var value = _cards.GetValueOrDefault(listener.CardId)?.Power ?? listener.BasePower ?? 0;
                    var definite = listener.Charges is > 0 && listener.Statuses is not null ? value : 0;
                    Add(name, row.Complete ? definite : 0, value,
                        "Special-card play spends Whisperer's remaining Counter and spawns one base-power copy; row capacity and Counter state are respected.");
                    if (listener.Charges is null) assumptions.Add("Whisperer's Counter is unread, so its copy is upper-bound reach.");
                }
            }
        }

        // Raffard's Vengeance is placement-sensitive: the entering unit must sit beside Raffard and complete
        // Crew with a Soldier/Mage on the other side. Cooldown/Order readiness is deliberately irrelevant to
        // this automatic Crew shot. Enumerate the at-most-20 legal insertion slots once instead of treating each
        // Raffard independently, which would over-count layouts that one played card cannot satisfy.
        if (RaffardCrew(candidate, position, side, enemy) is { } raffard)
            Add("Raffard's Vengeance", raffard.Minimum, raffard.Maximum,
                "Candidate can be placed beside Raffard while two adjacent Soldiers/Mages satisfy Crew; the random 2-damage shot is bounded by current enemy Armor, Shield, power and row completeness.");

        // Sly Seductress is an opponent-side reaction and therefore reduces the acting side's swing.
        foreach (var listener in enemy.Where(card => _rules.GetValueOrDefault(card.CardId)?.Reaction == "seductress"))
        {
            var bonded = enemy.Count(card => card.CardId == listener.CardId) >= 2;
            if (candidate.Kind == CardKind.Unit || bonded)
                Active(listener, _cards.GetValueOrDefault(listener.CardId)?.Name ?? listener.CardId, -1,
                    bonded ? "Opponent Bonded Seductress reacts to the played card." : "Opponent Seductress reacts to the played unit.");
        }
        foreach (var listener in enemy)
        {
            if (!_rules.TryGetValue(listener.CardId, out var rule) || listener.Statuses?.Contains(CardStatus.Locked) == true) continue;
            if (rule.Reaction == "seductress") continue; // Bonded/unit distinction is handled above.
            foreach (Match match in OpponentPlayPayoff.Matches(rule.Card.AbilityText ?? ""))
            {
                if (MatchesPlayPackage(candidate, match.Groups["package"].Value) != true) continue;
                var amount = int.Parse(match.Groups["amount"].Value);
                if (match.Groups["effect"].Value.StartsWith("boost", StringComparison.OrdinalIgnoreCase))
                    Active(listener, rule.Card.Name, -amount, $"Opponent {match.Groups["package"].Value} reaction boosts its engine by {amount}.");
                else
                {
                    var shielded = handCard?.Statuses?.Contains(CardStatus.Shield) == true;
                    var armor = handCard?.Armor ?? candidate.PrintedArmor ?? 0;
                    var loss = shielded ? 0 : Math.Min(candidatePower, Math.Max(0, amount - armor));
                    Add(rule.Card.Name, listener.Statuses is null ? -loss : -loss, listener.Statuses is null ? 0 : -loss,
                        $"Opponent unit-play reaction damages the entering card by {amount}; current Armor/Shield limits the score loss to {loss}.");
                }
            }
        }

        var resources = side == PlayerSide.User ? position.User : position.Opponent;
        var freeSlots = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board)
            .Sum(zone => Math.Max(0, 9 - zone.Cards.Length));
        var rowCapacityCertain = ownRowsComplete;
        if (HasPackage("Nature"))
        {
            var symbiosis = own.Where(card => card.Statuses?.Contains(CardStatus.Locked) != true)
                .Sum(card => _rules.GetValueOrDefault(card.CardId)?.Symbiosis ?? 0) + (resources.CurrentLeaderId == "200165" ? 1 : 0);
            if (symbiosis > 0 && freeSlots > (candidate.Kind is CardKind.Unit or CardKind.Artifact ? 1 : 0))
            {
                Add("Symbiosis", rowCapacityCertain ? symbiosis : 0, symbiosis,
                    $"Nature play spawns a {symbiosis}-power Wandering Treant; row capacity" + (rowCapacityCertain ? " is known." : " is not fully known."));
                var dryads = own.Count(card => _cards.GetValueOrDefault(card.CardId)?.HasCategory("Dryad") == true);
                if (dryads > 0 && own.Any(card => card.CardId == "203149" && card.Statuses?.Contains(CardStatus.Locked) != true))
                    Add("Aucwenn", 1, 1, $"The spawned Treant receives Vitality from {dryads} current Dryad{(dryads == 1 ? "" : "s")}; one end-turn tick is immediate reach.", true);
                var fledglings = own.Where(card => card.CardId == "203152" && card.Statuses?.Contains(CardStatus.Locked) != true).Count(card =>
                {
                    var row = position.Zones.First(zone => zone.Side == side && zone.Zone == CardZone.Board && zone.Cards.Contains(card));
                    return row.Cards.IndexOf(card) > 0;
                });
                if (fledglings > 0)
                    Add("Naiad Fledgling", rowCapacityCertain ? fledglings : 0, fledglings,
                        $"{fledglings} Fledgling{(fledglings == 1 ? "" : "s")} give the target to the left Vitality; one end-turn tick per target is immediate reach.", true);
            }
        }
        if (HasPackage("Organic") && resources.CurrentLeaderId == "201743" && freeSlots > (candidate.Kind is CardKind.Unit or CardKind.Artifact ? 1 : 0))
            Add("Arachas Swarm", rowCapacityCertain ? 1 : 0, 1, "Organic play spawns a 1-power Drone from the active leader passive.");
        if (HasPackage("Crime") && resources.CurrentLeaderId == "122105")
        {
            var coins = resources.Coins;
            Add("Lined Pockets", coins is null ? 0 : coins < 9 ? 1 : 0, coins == 9 ? 0 : 1,
                coins is null ? "Crime can gain 1 Coin; current pouch size is unread." : coins < 9 ? "Crime gains 1 Coin of immediate reach." : "Coin pouch is full.");
        }
        if (HasPackage("Alchemy") && resources.CurrentLeaderId == "200159")
        {
            var damaged = own.Any(card => card.Power is { } power && card.BasePower is { } basis && power < basis);
            var unread = own.Any(card => card.Power is null || card.BasePower is null);
            Add("Battle Trance", damaged ? 1 : 0, damaged || unread ? 1 : 0,
                damaged ? "Alchemy heals a currently damaged allied unit by 1." : "Alchemy heal needs a damaged allied unit." );
        }

        // Known off-board identities are real snapshot information, not deck-list assumptions. Their automatic
        // play reactions belong in both player and opponent reach and are skipped when the relevant zone does not
        // actually contain the card.
        if (HasPackage("Alchemy"))
        {
            foreach (var crowmother in position.Zone(side, CardZone.Graveyard).Cards.Where(card => card.CardId == "202514"))
            {
                var value = _cards.GetValueOrDefault(crowmother.CardId)?.Power ?? crowmother.BasePower ?? 0;
                Add("Crowmother", freeSlots > (candidate.Kind is CardKind.Unit or CardKind.Artifact ? 1 : 0) ? value : 0, value,
                    "Known Crowmother in the graveyard summons on an Alchemy play; incomplete/full row capacity is represented as a range.");
            }
        }
        if (HasPackage("Bomb"))
        {
            var madocs = position.Zone(side, CardZone.Deck).Cards.Concat(position.Zone(side, CardZone.Graveyard).Cards)
                .Where(card => card.CardId == "202879").Take(1);
            foreach (var madoc in madocs)
            {
                var value = _cards.GetValueOrDefault(madoc.CardId)?.Power ?? madoc.BasePower ?? 0;
                Add("Madoc", freeSlots > (candidate.Kind is CardKind.Unit or CardKind.Artifact ? 1 : 0) ? value : 0, value,
                    "Known Madoc in deck/graveyard summons on a Bomb play; no unseen Madoc is invented.");
            }
        }

        // Current functional engines whose payoff is caused by the keyword trigger rather than the card play directly.
        if (triggeredThrive.Count > 0 || HasPackage("Organic"))
        {
            foreach (var koshchey in own.Where(card => card.CardId == "202832" && triggeredThrive.Contains(card.InstanceId)))
            {
                var value = Math.Min(1, Math.Max(0, 9 - (position.Zones.First(zone => zone.Side == side &&
                    zone.Zone == CardZone.Board && zone.Cards.Contains(koshchey)).Cards.Length + (candidate.Kind == CardKind.Unit ? 1 : 0))));
                Add("Koshchey", rowCapacityCertain ? value : 0, value, "Koshchey's own Thrive trigger can spawn one 1-power body on its row.");
            }
            foreach (var queen in own.Where(card => card.CardId == "202435" && card.Statuses?.Contains(CardStatus.Locked) != true))
            {
                var queenRow = position.Zones.First(zone => zone.Side == side && zone.Zone == CardZone.Board && zone.Cards.Contains(queen));
                var insectoids = queenRow.Cards.Count(card => _cards.GetValueOrDefault(card.CardId)?.HasCategory("Insectoid") == true);
                var triggers = triggeredThrive.Contains(queen.InstanceId) || HasPackage("Organic") ? 1 : 0;
                var value = triggers * insectoids;
                Add("Kikimore Queen", queenRow.Complete ? value : 0, value,
                    (HasPackage("Organic") && !triggeredThrive.Contains(queen.InstanceId) ? "Organic triggers the Queen's own Thrive and boosts " : "The Queen's own Thrive boosts ") +
                    $"{insectoids} known Insectoid{(insectoids == 1 ? "" : "s")} on its row.");
            }
        }

        var minimum = contributions.Sum(item => item.Minimum);
        var maximum = contributions.Sum(item => item.Maximum);
        return new(minimum, maximum, contributions, assumptions.Order().ToArray());
    }

    private (int Minimum, int Maximum)? RaffardCrew(CardDefinition candidate, GamePosition position, PlayerSide side,
        PositionCard[] enemies)
    {
        if (candidate.Kind != CardKind.Unit || !CrewContributor(candidate)) return null;
        var rows = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board).ToArray();
        if (!rows.SelectMany(row => row.Cards).Any(IsRaffard)) return null;

        var definite = 0; var possible = 0;
        foreach (var row in rows.Where(row => row.Cards.Length < GwentRules.MaximumRowSize))
        {
            for (var slot = 0; slot <= row.Cards.Length; slot++)
            {
                var entered = row.Cards.Insert(slot, new PositionCard("reach-candidate", candidate.Id, candidate.Power,
                    candidate.Power, candidate.PrintedArmor ?? 0, PlayRules.Compile(candidate).PrintedStatuses));
                var low = 0; var high = 0;
                for (var index = 0; index < entered.Length; index++)
                {
                    var listener = entered[index];
                    if (!IsRaffard(listener) || Math.Abs(index - slot) != 1 || index == 0 || index == entered.Length - 1) continue;
                    if (!CrewContributor(entered[index - 1]) || !CrewContributor(entered[index + 1])) continue;
                    if (listener.Statuses?.Contains(CardStatus.Locked) != true) high++;
                    if (listener.Statuses is not null && !listener.Statuses.Contains(CardStatus.Locked)) low++;
                }
                definite = Math.Max(definite, low); possible = Math.Max(possible, high);
            }
            if (!row.Complete)
            {
                // Hidden ordering can supply the other Crew neighbor, but one inserted card can border at most two engines.
                possible = Math.Max(possible, Math.Min(2, row.Cards.Count(IsRaffard)));
                definite = 0;
            }
        }
        if (possible == 0) return null;
        var enemyRowsComplete = position.Zones.Where(zone => zone.Side != side && zone.Zone == CardZone.Board).All(zone => zone.Complete);
        var damage = RandomDamage(enemies, 2, enemyRowsComplete);
        var minimum = definite > 1 ? 0 : definite * damage.Minimum; // repeated random shots can consume the first low target
        return (minimum, possible * damage.Maximum);
    }

    private bool IsRaffard(PositionCard card) => card.CardId == "203050" &&
        PlayRules.Normalize(_cards.GetValueOrDefault(card.CardId)?.AbilityText).Contains("Mages contribute to this card's Crew ability", StringComparison.Ordinal);

    private bool CrewContributor(PositionCard card) => _cards.GetValueOrDefault(card.CardId) is { } definition && CrewContributor(definition);
    private static bool CrewContributor(CardDefinition card) => card.HasCategory("Soldier") || card.HasCategory("Mage");

    private static (int Minimum, int Maximum) RandomDamage(PositionCard[] enemies, int amount, bool complete)
    {
        if (enemies.Length == 0) return complete ? (0, 0) : (0, amount);
        var values = enemies.Select(enemy =>
        {
            if (enemy.Statuses?.Contains(CardStatus.Shield) == true) return (Minimum: 0, Maximum: 0);
            if (enemy.Power is null || enemy.Armor is null || enemy.Statuses is null) return (Minimum: 0, Maximum: amount);
            var loss = Math.Min(enemy.Power.Value, Math.Max(0, amount - enemy.Armor.Value));
            return (Minimum: loss, Maximum: loss);
        }).ToArray();
        var low = values.Min(value => value.Minimum); var high = values.Max(value => value.Maximum);
        return complete ? (low, high) : (0, Math.Max(amount, high));
    }

    private static (int Minimum, int Maximum) BestDamage(PositionCard[] enemies, int amount, bool complete)
    {
        if (enemies.Length == 0) return complete ? (0, 0) : (0, amount);
        var values = enemies.Select(enemy =>
        {
            if (enemy.Statuses?.Contains(CardStatus.Shield) == true) return 0;
            if (enemy.Power is null || enemy.Armor is null || enemy.Statuses is null) return amount;
            return Math.Min(enemy.Power.Value, Math.Max(0, amount - enemy.Armor.Value));
        }).ToArray();
        var best = values.Max();
        return complete ? (best, best) : (0, Math.Max(amount, best));
    }

    private static bool? MatchesPlayPackage(CardDefinition candidate, string raw)
    {
        var package = raw.Trim();
        if (package.Equals("card", StringComparison.OrdinalIgnoreCase)) return true;
        if (package.Equals("unit", StringComparison.OrdinalIgnoreCase)) return candidate.Kind == CardKind.Unit;
        if (package.Equals("special card", StringComparison.OrdinalIgnoreCase)) return candidate.Kind == CardKind.Special;
        if (package.Equals("unique special card", StringComparison.OrdinalIgnoreCase))
            return candidate.Kind == CardKind.Special ? null : false; // prior-play identities are not present in GamePosition
        if (package.Equals("unit with Deploy", StringComparison.OrdinalIgnoreCase))
            return candidate.Kind == CardKind.Unit && candidate.AbilityText?.Contains("Deploy", StringComparison.OrdinalIgnoreCase) == true;
        if (package.Equals("card with an Order", StringComparison.OrdinalIgnoreCase))
            return candidate.AbilityText?.Contains("Order", StringComparison.OrdinalIgnoreCase) == true;
        if (package.Equals("unit with the Deathwish ability", StringComparison.OrdinalIgnoreCase) ||
            package.Equals("unit with Deathwish ability", StringComparison.OrdinalIgnoreCase))
            return candidate.Kind == CardKind.Unit && candidate.AbilityText?.Contains("Deathwish", StringComparison.OrdinalIgnoreCase) == true;
        var categories = package.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return categories.Any(candidate.HasCategory);
    }

    private IEnumerable<PositionCard> Units(GamePosition position, PlayerSide side) => position.Zones
        .Where(zone => zone.Side == side && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards)
        .Where(card => _cards.GetValueOrDefault(card.CardId)?.Kind == CardKind.Unit);
}
