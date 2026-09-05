using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public enum PointEstimateQuality
{
    StateAware,
    Bounded,
    BaselineOnly,
}

/// <summary>
/// A deliberately non-authoritative direct-tempo range. It is used only when the strict play engine cannot
/// prove a maximum. Future engines, hidden choices and long play chains are excluded and remain visible in Notes.
/// </summary>
public sealed record ApproximatePointEstimate(int Minimum, int Maximum, PointEstimateQuality Quality,
    IReadOnlyList<string> Notes, bool FavorableRandomness = false)
{
    public string RangeText => Minimum == Maximum ? $"{Maximum:+0;-0;0}" : $"{Minimum:+0;-0;0}–{Maximum:+0;-0;0}";
}

public sealed class ApproximatePointModel
{
    private static readonly Regex NumberedDamage = new(@"Damage (?<count>\d+) (?:random )?enemy units? by (?<amount>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SingleDamage = new(@"Damage (?:an |a |the |target )?(?<side>enemy|allied)? ?unit(?:s)? by (?<amount>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SingleBoost = new(@"Boost (?:(?<side>an enemy|an allied|a|the) )?unit by (?<amount>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SelfBoost = new(@"Boost self by (?<amount>\d+)(?! for each)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StatusDuration = new(@"Give (?:an )?(?<side>enemy|allied) unit (?<status>Bleeding|Vitality) \((?<amount>\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Profit = new(@"\bProfit (?<amount>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SpawnCopies = new(@"Spawn (?:(?<count>\d+) )?(?:a |an )?(?:Doomed )?(?:base )?cop(?:y|ies) of (?<name>self|[^.;]+?)(?: on| to| and|\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SpawnNamed = new(@"Spawn(?: and play)? (?:(?<count>\d+) )?(?:a |an )?(?:Doomed )?(?:base )?(?:copy of )?(?<name>[^.;]+?)(?: on| to| and give|\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, CardDefinition> _cards;
    private readonly PlayRuleBook _book;
    private readonly BoardInteractionPointModel _interactions;

    public ApproximatePointModel(IEnumerable<CardDefinition> catalog)
    {
        var cards = catalog.DistinctBy(card => card.Id).ToArray();
        _cards = cards.ToDictionary(card => card.Id);
        _book = new(cards);
        _interactions = new(cards);
    }

    public ApproximatePointEstimate? Estimate(CardDefinition card, GamePosition position, PlayerSide side)
        => EstimateCore(card, position, side, includeInteractions: true, includeDeferredTicks: true);

    /// <summary>Card body/direct text only: no existing board engines and no future Vitality/Bleeding/weather tick.</summary>
    public ApproximatePointEstimate? EstimateDirect(CardDefinition card, GamePosition position, PlayerSide side)
        => EstimateCore(card, position, side, includeInteractions: false, includeDeferredTicks: false);

    private ApproximatePointEstimate? EstimateCore(CardDefinition card, GamePosition position, PlayerSide side,
        bool includeInteractions, bool includeDeferredTicks)
    {
        var text = PlayRules.Normalize(card.AbilityText);
        if (card.AbilityText is null) return null;
        var notes = new HashSet<string>();
        var disloyal = text.Contains("Disloyal", StringComparison.OrdinalIgnoreCase);
        var body = card.Kind == CardKind.Unit ? CurrentBody(card, position, side) * (disloyal ? -1 : 1) : 0;
        var minimum = body; var maximum = body; var modeled = false; var bounded = false; var random = false;

        if (card.Id == "203102")
        {
            var value = position.CardValues?.FirstOrDefault(item => item.Side == side && item.CardId == card.Id &&
                item.Kind.Equals("damage", StringComparison.OrdinalIgnoreCase));
            var lowDamage = value?.Minimum ?? 0; var highDamage = value?.Maximum;
            if (highDamage is null)
            {
                notes.Add("Aerondight's last known damage is a lower bound; hover OCR or reviewed value is needed for an upper reach value.");
                return new(lowDamage, lowDamage, PointEstimateQuality.Bounded, notes.ToArray());
            }
            var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            var targets = TargetableEnemies(position, enemy).ToArray();
            int Swing(int amount)
            {
                if (targets.Length == 0) return 0;
                return targets.Select(target =>
                {
                    var direct = DamageSwing(target, amount);
                    var shielded = target.Statuses?.Contains(CardStatus.Shield) == true;
                    var excess = shielded || !Units(position, side).Any() ? 0 :
                        Math.Max(0, amount - (target.Power ?? 0) - (target.Armor ?? 0));
                    return direct + excess;
                }).DefaultIfEmpty(0).Max();
            }
            notes.Add($"Uses tracked Aerondight damage {lowDamage}–{highDamage}; overkill is transferred to the lowest allied unit when one exists.");
            return new(Swing(lowDamage), Swing(highDamage.Value), lowDamage == highDamage ? PointEstimateQuality.StateAware : PointEstimateQuality.Bounded,
                notes.ToArray());
        }

        if (card.Id == "202192")
        {
            var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            var damage = TargetableEnemies(position, enemy).Select(unit => DamageSwing(unit, 5)).DefaultIfEmpty(0).Max();
            var storedId = position.CardValues?.FirstOrDefault(item => item.Side == side && item.CardId == card.Id &&
                item.Kind.Equals("stored-soul", StringComparison.OrdinalIgnoreCase))?.StoredCardId;
            var stored = storedId is null ? null : _cards.GetValueOrDefault(storedId);
            notes.Add(stored is null ? "No stored Hen Gaidth soul is currently known; only the 5-damage play is counted." :
                $"Adds the stored base copy of {stored.Name}; its supported Deploy/play reactions are resolved by the strict engine when the board is complete.");
            return new(damage + (stored?.Power ?? 0), damage + (stored?.Power ?? 0),
                PointEstimateQuality.StateAware, notes.ToArray());
        }

        if (card.Id == "203150" && !includeDeferredTicks)
        {
            var targets = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board)
                .SelectMany(zone => zone.Cards.Select(unit => (Zone: zone, Unit: unit)))
                .Where(item => IsUnit(item.Unit)).ToArray();
            if (targets.Length < 2) return new(0, 0, PointEstimateQuality.StateAware,
                ["Frog Mating Season has fewer than two legal allied unit targets."]);
            var best = 0;
            for (var first = 0; first < targets.Length; first++)
            for (var second = first + 1; second < targets.Length; second++)
            {
                var selected = new[] { targets[first], targets[second] };
                var frogs = selected.GroupBy(item => item.Zone.Row).Sum(group =>
                {
                    var zone = group.First().Zone;
                    return Math.Min(group.Count() * 2, Math.Max(0, 9 - zone.Cards.Length));
                });
                best = Math.Max(best, frogs);
            }
            return new(best, best, PointEstimateQuality.StateAware,
                [$"Card-only value is {best} immediately spawned Frog bod{(best == 1 ? "y" : "ies")}; Vitality and adjacent Frog triggers start in Y."]);
        }

        // Profit is immediate reach. Only newly gained coins count, matching TacticalPlayEngine.ReachSwing.
        if (Profit.Match(text) is { Success: true } profit)
        {
            var amount = int.Parse(profit.Groups["amount"].Value);
            var coins = side == PlayerSide.User ? position.User.Coins : position.Opponent.Coins;
            if (coins is { } known) minimum += Math.Max(0, Math.Min(9, known + amount) - known);
            else { bounded = true; notes.Add("Coin count unread; Profit shown from 0 to its uncapped value."); }
            maximum += coins is { } value ? Math.Max(0, Math.Min(9, value + amount) - value) : amount;
            modeled = true;
        }

        var deployRanges = new List<(int Low, int High, bool Bounded, bool Random, string? Note)>();
        foreach (var clause in ImmediateClauses(card))
        {
            var range = Clause(clause, card, position, side, includeDeferredTicks);
            if (range is not null) deployRanges.Add(range.Value);
        }
        if (deployRanges.Count > 0)
        {
            // Separate Deploy (Melee)/(Ranged) clauses are alternatives. A single clause may itself contain several effects.
            var best = deployRanges.OrderByDescending(range => range.High).ThenByDescending(range => range.Low).First();
            minimum += best.Low; maximum += best.High; bounded |= best.Bounded; random |= best.Random; modeled = true;
            if (best.Note is not null) notes.Add(best.Note);
        }

        var interactions = includeInteractions ? _interactions.Estimate(card, position, side) : new BoardInteractionEstimate(0, 0, [], []);
        if (includeInteractions && interactions.HasValue)
        {
            minimum += interactions.Minimum;
            maximum += interactions.Maximum;
            modeled = true;
            bounded |= interactions.Minimum != interactions.Maximum;
            foreach (var contribution in interactions.Contributions)
                notes.Add($"{contribution.Source} {contribution.RangeText}: {contribution.Reason}");
            foreach (var assumption in interactions.Assumptions) notes.Add(assumption);
        }

        if (HasUncountedValue(text, modeled))
        {
            bounded = true;
            notes.Add("Future engines, Orders without Zeal, hidden choices and unparsed chained effects are excluded.");
        }
        if (position.Zones.Any(zone => zone.Zone == CardZone.Board && !zone.Complete))
        {
            bounded = true;
            notes.Add("Visible-board estimate; unread units or statuses can change the range.");
        }

        if (!modeled)
        {
            if (card.Kind != CardKind.Unit) return null;
            notes.Add(text.Length == 0 ? "Printed/current unit body." : "Direct body only; ability value is not included.");
            return new(body, body, PointEstimateQuality.BaselineOnly, notes.ToArray());
        }
        notes.Add(includeInteractions && interactions.HasValue
            ? "Board-aware one-card estimate; direct effects and readable play-event reactions are combined, while unresolved chains stay bounded."
            : includeInteractions ? "Direct one-card estimate; strict simulation was unavailable."
            : "Card-only value; existing board engines and future timed ticks are excluded.");
        return new(minimum, maximum, bounded ? PointEstimateQuality.Bounded : PointEstimateQuality.StateAware, notes.ToArray(), random);
    }

    private int CurrentBody(CardDefinition card, GamePosition position, PlayerSide side)
    {
        var staged = position.Zone(side, CardZone.Hand).Cards.FirstOrDefault(item => item.CardId == card.Id);
        var rule = _book.Rules.GetValueOrDefault(card.Id);
        if (rule?.PowerInvariant == "armor") return staged?.Armor ?? card.PrintedArmor ?? 0;
        var veteran = rule?.Veteran == true && position.Round is >= 2 ? position.Round.Value - 1 : 0;
        return staged?.Power ?? card.Power + veteran;
    }

    private IEnumerable<string> ImmediateClauses(CardDefinition card)
    {
        var original = card.AbilityText ?? "";
        var lines = original.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var deploy = lines.Where(line => line.StartsWith("Deploy", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (deploy.Length > 0)
        {
            foreach (var line in deploy)
            {
                var colon = line.IndexOf(':');
                if (colon >= 0 && colon + 1 < line.Length) yield return line[(colon + 1)..].Trim();
            }
            yield break;
        }
        foreach (var line in lines)
        {
            var value = line.Trim();
            if (value.StartsWith("Profit", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Whenever", StringComparison.OrdinalIgnoreCase) || value.StartsWith("At the ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("After ", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Timer", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Cooldown", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Deathwish", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Fee", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.StartsWith("Order", StringComparison.OrdinalIgnoreCase))
            {
                if (!original.Contains("Zeal", StringComparison.OrdinalIgnoreCase)) continue;
                var colon = value.IndexOf(':'); value = colon >= 0 ? value[(colon + 1)..].Trim() : value;
            }
            if (value.StartsWith("Zeal.", StringComparison.OrdinalIgnoreCase))
            {
                var order = value.IndexOf("Order", StringComparison.OrdinalIgnoreCase);
                var colon = order < 0 ? -1 : value.IndexOf(':', order);
                if (colon >= 0) value = value[(colon + 1)..].Trim(); else continue;
            }
            yield return value;
        }
    }

    private (int Low, int High, bool Bounded, bool Random, string? Note)? Clause(string clause, CardDefinition source,
        GamePosition position, PlayerSide side, bool includeDeferredTicks)
    {
        var enemy = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var allies = Units(position, side).ToArray(); var enemies = TargetableEnemies(position, enemy).ToArray();
        var low = 0; var high = 0; var found = false; var bounded = false; var random = false; string? note = null;

        foreach (Match match in NumberedDamage.Matches(clause))
        {
            var count = int.Parse(match.Groups["count"].Value); var amount = int.Parse(match.Groups["amount"].Value);
            var values = enemies.Select(unit => DamageSwing(unit, amount)).OrderByDescending(value => value).Take(count).ToArray();
            high += values.Sum(); low += enemies.Length == 0 ? 0 : values.Sum(); found = true;
            if (enemies.Any(unit => unit.Power is null || unit.Armor is null || unit.Statuses is null)) bounded = true;
        }
        if (!NumberedDamage.IsMatch(clause))
        foreach (Match match in SingleDamage.Matches(clause))
        {
            var amount = int.Parse(match.Groups["amount"].Value); var allied = match.Groups["side"].Value.Equals("allied", StringComparison.OrdinalIgnoreCase);
            var targets = allied ? allies : enemies; var values = targets.Select(unit => DamageSwing(unit, amount)).ToArray();
            if (allied) { low -= values.DefaultIfEmpty(amount).Max(); high -= values.DefaultIfEmpty(0).Min(); }
            else { high += values.DefaultIfEmpty(amount).Max(); low += values.DefaultIfEmpty(0).Min(); }
            bounded |= targets.Length == 0 || targets.Any(unit => unit.Power is null || unit.Armor is null || unit.Statuses is null); found = true;
        }
        if (clause.Contains("Split", StringComparison.OrdinalIgnoreCase) && clause.Contains("random", StringComparison.OrdinalIgnoreCase))
        { random = true; bounded = true; note = "Range assumes favorable allocation of random damage; histogram remains the probability view."; }

        foreach (Match match in SelfBoost.Matches(clause))
        { var amount = int.Parse(match.Groups["amount"].Value); low += amount; high += amount; found = true; }
        foreach (Match match in SingleBoost.Matches(clause))
        {
            var amount = int.Parse(match.Groups["amount"].Value); var enemyTarget = match.Groups["side"].Value.Contains("enemy", StringComparison.OrdinalIgnoreCase);
            if (enemyTarget) { low -= amount; high -= amount; }
            else { high += allies.Length > 0 ? amount : 0; low += allies.Length > 0 ? amount : 0; if (allies.Length == 0) bounded = true; }
            found = true;
        }
        var adjacentBoost = Regex.Match(clause, @"Boost (?<count>\d+) adjacent units by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (adjacentBoost.Success)
        {
            var count = int.Parse(adjacentBoost.Groups["count"].Value); var amount = int.Parse(adjacentBoost.Groups["amount"].Value);
            var value = Math.Min(count, allies.Length) * amount; low += value; high += value; found = true;
        }

        var allEnemy = Regex.Match(clause, @"(?:Damage|Deal) all (?:other )?enemy units by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (allEnemy.Success)
        {
            var amount = int.Parse(allEnemy.Groups["amount"].Value); var value = enemies.Sum(unit => DamageSwing(unit, amount));
            low += value; high += value; found = true;
        }
        var enemyRow = Regex.Match(clause, @"Damage all units on an enemy row by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (enemyRow.Success)
        {
            var amount = int.Parse(enemyRow.Groups["amount"].Value);
            var values = position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board)
                .Select(zone => zone.Cards.Where(IsUnit).Sum(unit => DamageSwing(unit, amount))).ToArray();
            var value = values.DefaultIfEmpty(0).Max(); low += value; high += value; found = true;
        }
        var allAlliedBoost = Regex.Match(clause, @"Boost all (?:other )?allied units by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (allAlliedBoost.Success)
        {
            var amount = int.Parse(allAlliedBoost.Groups["amount"].Value); var value = allies.Length * amount;
            low += value; high += value; found = true;
        }
        var alliedRowBoost = Regex.Match(clause, @"Boost all units on an allied row by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (alliedRowBoost.Success)
        {
            var amount = int.Parse(alliedRowBoost.Groups["amount"].Value);
            var count = position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board)
                .Select(zone => zone.Cards.Count(IsUnit)).DefaultIfEmpty(0).Max();
            low += count * amount; high += count * amount; found = true;
        }

        if (clause.Contains("Destroy", StringComparison.OrdinalIgnoreCase) || clause.Contains("Banish", StringComparison.OrdinalIgnoreCase))
        {
            var threshold = Regex.Match(clause, @"with (?<amount>\d+) or more power", RegexOptions.IgnoreCase);
            var legal = enemies.Where(unit => !threshold.Success || unit.Power >= int.Parse(threshold.Groups["amount"].Value)).ToArray();
            if (clause.Contains("enemy", StringComparison.OrdinalIgnoreCase) && legal.Length > 0)
            { var value = legal.Max(unit => unit.Power ?? 0); high += value; low += value; found = true; }
        }
        if (Regex.IsMatch(clause, @"Destroy (?:all )?(?:the )?(?:highest|lowest)-power unit", RegexOptions.IgnoreCase))
        {
            var all = allies.Concat(enemies).Where(unit => unit.Power is not null).ToArray();
            if (all.Length > 0)
            {
                var lowest = clause.Contains("lowest", StringComparison.OrdinalIgnoreCase);
                var selectedPower = lowest ? all.Min(unit => unit.Power!.Value) : all.Max(unit => unit.Power!.Value);
                var selected = all.Where(unit => unit.Power == selectedPower).ToArray();
                var value = selected.Where(enemies.Contains).Sum(unit => unit.Power ?? 0) - selected.Where(allies.Contains).Sum(unit => unit.Power ?? 0);
                low += value; high += value; found = true; bounded = true;
            }
        }
        if (clause.Contains("reset its power", StringComparison.OrdinalIgnoreCase) || clause.Contains("Reset a unit", StringComparison.OrdinalIgnoreCase))
        {
            var values = allies.Concat(enemies).Select(unit =>
            {
                var definition = _cards.GetValueOrDefault(unit.CardId); if (unit.Power is null || definition is null) return 0;
                var rule = _book.Rules.GetValueOrDefault(unit.CardId);
                var resetPower = rule?.PowerInvariant switch
                {
                    "fixed" => unit.Power.Value,
                    "armor" => unit.Armor ?? unit.Power.Value,
                    _ => unit.BasePower ?? definition.Power
                };
                var delta = resetPower - unit.Power.Value;
                return allies.Contains(unit) ? delta : -delta;
            }).ToArray();
            var value = values.DefaultIfEmpty(0).Max(); low += value; high += value; found = true;
        }
        var setPower = Regex.Match(clause, @"Set (?:an? |the )?(?<side>enemy|allied)? ?unit's power to (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (setPower.Success)
        {
            var amount = int.Parse(setPower.Groups["amount"].Value);
            var targetSide = setPower.Groups["side"].Value;
            var values = Enumerable.Empty<int>();
            if (!targetSide.Equals("enemy", StringComparison.OrdinalIgnoreCase))
                values = values.Concat(allies.Select(unit => SetPowerSwing(unit, amount, allied: true)));
            if (!targetSide.Equals("allied", StringComparison.OrdinalIgnoreCase))
                values = values.Concat(enemies.Select(unit => SetPowerSwing(unit, amount, allied: false)));
            var value = values.DefaultIfEmpty(0).Max();
            low += value; high += value; found = true; bounded = true;
        }
        var seize = Regex.Match(clause, @"Seize it|Seize an enemy unit", RegexOptions.IgnoreCase);
        if (seize.Success)
        {
            var threshold = Regex.Match(clause, @"with (?<amount>\d+) or less power", RegexOptions.IgnoreCase);
            var legal = enemies.Where(unit => !threshold.Success || unit.Power <= int.Parse(threshold.Groups["amount"].Value)).ToArray();
            var value = 2 * legal.Select(unit => unit.Power ?? 0).DefaultIfEmpty(0).Max(); low += value; high += value; found = true;
        }
        if (clause.Contains("Move an enemy unit to the top of your deck", StringComparison.OrdinalIgnoreCase))
        {
            var value = enemies.Select(unit => unit.Power ?? 0).DefaultIfEmpty(0).Max(); low += value; high += value; found = true;
        }
        var damageIt = Regex.Match(clause, @"damage it by (?<amount>\d+)", RegexOptions.IgnoreCase);
        if (damageIt.Success && !SingleDamage.IsMatch(clause))
        {
            var amount = int.Parse(damageIt.Groups["amount"].Value); var value = enemies.Select(unit => DamageSwing(unit, amount)).DefaultIfEmpty(amount).Max();
            low += 0; high += value; found = true; bounded = true;
        }
        var status = StatusDuration.Match(clause);
        if (status.Success)
        {
            var enemyTarget = status.Groups["side"].Value.Equals("enemy", StringComparison.OrdinalIgnoreCase);
            var bleeding = status.Groups["status"].Value.Equals("Bleeding", StringComparison.OrdinalIgnoreCase);
            // ImmediateMaximum advances one end-turn tick; remaining duration belongs to carryover, not this swing.
            var tick = bleeding ? (enemyTarget ? 1 : -1) : (enemyTarget ? -1 : 1);
            if (includeDeferredTicks) { low += tick; high += tick; }
            found = true; note = includeDeferredTicks
                ? "Counts the first end-turn Bleeding/Vitality tick only; later ticks belong to carryover."
                : "Vitality/Bleeding starts after the immediate card-only horizon.";
        }

        foreach (Match match in SpawnCopies.Matches(clause))
        {
            var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
            var name = match.Groups["name"].Value.Trim(); var spawned = name.Equals("self", StringComparison.OrdinalIgnoreCase) ? source : FindByName(name);
            if (spawned is null) { bounded = true; continue; }
            var value = count * (spawned.Kind == CardKind.Unit ? spawned.Power : 0); low += value; high += value; found = true;
        }
        foreach (Match match in SpawnNamed.Matches(clause))
        {
            var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
            if (match.Groups["name"].Value.Trim().Equals("self", StringComparison.OrdinalIgnoreCase)) continue;
            var spawned = FindByName(match.Groups["name"].Value.Trim()); if (spawned is null) continue;
            var value = count * (spawned.Kind == CardKind.Unit ? spawned.Power : 0); low += value; high += value; found = true;
            if (clause.Contains("and play", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(spawned.AbilityText)) bounded = true;
        }

        if (clause.Contains("Consume", StringComparison.OrdinalIgnoreCase) &&
            clause.Contains("graveyard", StringComparison.OrdinalIgnoreCase))
        {
            var opponentGraveyard = clause.Contains("opponent's graveyard", StringComparison.OrdinalIgnoreCase);
            var owner = opponentGraveyard ? enemy : side;
            var graveyard = position.Zone(owner, CardZone.Graveyard);
            var choices = graveyard.Cards.Where(IsUnit).ToArray();
            if (clause.Contains("bronze", StringComparison.OrdinalIgnoreCase))
                choices = choices.Where(item => _cards.GetValueOrDefault(item.CardId)?.IsGold == false).ToArray();
            var value = choices.Select(item => item.Power ?? item.BasePower ?? _cards.GetValueOrDefault(item.CardId)?.Power ?? 0)
                .DefaultIfEmpty(0).Max();
            low += value; high += value; found = true; bounded |= !graveyard.Complete;
            note = choices.Length == 0
                ? "No eligible identity is currently known in the selected graveyard."
                : $"Consumes the highest-power eligible known graveyard unit ({value}); the zone inventory" +
                  (graveyard.Complete ? " is complete." : " is partial and a manually reviewed unseen target could be higher.");
        }

        var zonePlay = Regex.Match(clause, @"\bPlay (?:a|an|the) .* from (?<owner>your|your opponent's) (?<zone>deck|graveyard)", RegexOptions.IgnoreCase);
        if (clause.Contains("Summon", StringComparison.OrdinalIgnoreCase) || zonePlay.Success)
        {
            var sourceZone = zonePlay.Success && zonePlay.Groups["zone"].Value.Equals("graveyard", StringComparison.OrdinalIgnoreCase) ? CardZone.Graveyard : CardZone.Deck;
            var owner = zonePlay.Success && zonePlay.Groups["owner"].Value.Contains("opponent", StringComparison.OrdinalIgnoreCase) ? enemy : side;
            var deck = position.Zone(owner, sourceZone); var candidates = deck.Cards.AsEnumerable();
            if (Regex.IsMatch(clause, @"\bunits?\b", RegexOptions.IgnoreCase)) candidates = candidates.Where(IsUnit);
            else if (Regex.IsMatch(clause, @"\bspecial(?: card)?\b", RegexOptions.IgnoreCase)) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.Kind == CardKind.Special);
            else if (Regex.IsMatch(clause, @"\bartifact\b", RegexOptions.IgnoreCase)) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.Kind == CardKind.Artifact);
            if (clause.Contains("bronze", StringComparison.OrdinalIgnoreCase)) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.IsGold == false).ToArray();
            if (clause.Contains("non-Neutral", StringComparison.OrdinalIgnoreCase)) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.Faction != "Neutral");
            var category = Regex.Match(clause, @"\b(?<category>Deathwish|Alchemy|Nature|Organic|Tactic|Warfare|Crime|Raid) (?:unit|special|card)", RegexOptions.IgnoreCase);
            if (category.Success) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.HasCategory(category.Groups["category"].Value) == true);
            var provisionLimit = Regex.Match(clause, @"provision cost (?:of )?(?<amount>\d+) or less", RegexOptions.IgnoreCase);
            if (provisionLimit.Success) candidates = candidates.Where(item => _cards.GetValueOrDefault(item.CardId)?.Provision <= int.Parse(provisionLimit.Groups["amount"].Value)).ToArray();
            var named = _cards.Values.Where(card => clause.Contains(card.Name, StringComparison.OrdinalIgnoreCase)).Select(card => card.Id).ToHashSet();
            if (named.Count > 0) candidates = candidates.Where(item => named.Contains(item.CardId)).ToArray();
            var candidateArray = candidates.ToArray();
            if (clause.Contains("all copies", StringComparison.OrdinalIgnoreCase) || named.Count > 1)
            { var value = candidateArray.Sum(unit => unit.Power ?? _cards.GetValueOrDefault(unit.CardId)?.Power ?? 0); low += value; high += value; }
            else
            { var value = candidateArray.Select(unit => unit.Power ?? _cards.GetValueOrDefault(unit.CardId)?.Power ?? 0).DefaultIfEmpty(0).Max(); low += value; high += value; }
            found = true; bounded |= !deck.Complete;
            note = deck.Complete ? $"Known-{sourceZone.ToString().ToLowerInvariant()} summon/tutor body included; fetched Deploy effects are excluded." :
                $"Known-{(owner == side ? "own" : "opponent")}-{sourceZone.ToString().ToLowerInvariant()} summon/tutor floor; hidden identities can increase the value.";
        }

        var weather = Regex.Match(clause, @"Spawn ?(?<weather>Frost|Fog|Rain|Storm|Cataclysm|Blood Moon) on an enemy row", RegexOptions.IgnoreCase);
        if (weather.Success)
        {
            var packet = weather.Groups["weather"].Value.ToLowerInvariant() switch { "cataclysm" => 3, "storm" => 3, "blood moon" => 1, _ => 2 };
            var value = enemies.Select(unit => DamageSwing(unit, packet)).DefaultIfEmpty(0).Max();
            if (includeDeferredTicks) { low += 0; high += value; }
            found = true; bounded = includeDeferredTicks;
            note = includeDeferredTicks ? "Weather range includes at most its first favorable tick; later turns belong to carryover."
                : "Weather starts after the immediate card-only horizon.";
        }

        var perUnit = Regex.Match(clause, @"Boost self by (?<amount>\d+) for each (?<which>allied|enemy) unit(?: on this row)?", RegexOptions.IgnoreCase);
        if (perUnit.Success)
        {
            var amount = int.Parse(perUnit.Groups["amount"].Value); var enemyCount = perUnit.Groups["which"].Value.Equals("enemy", StringComparison.OrdinalIgnoreCase);
            var count = enemyCount ? Units(position, enemy).Count() : position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board)
                .Select(zone => zone.Cards.Count(IsUnit)).DefaultIfEmpty(0).Max();
            low += count * amount; high += count * amount; found = true;
        }

        if (clause.Contains("Clash with the highest-power enemy unit", StringComparison.OrdinalIgnoreCase))
        {
            var clashEnemies = Units(position, enemy).ToArray();
            var highestPower = clashEnemies.Where(unit => unit.Power is not null).Select(unit => unit.Power!.Value).DefaultIfEmpty().Max();
            var highest = clashEnemies.Where(unit => unit.Power == highestPower).ToArray();
            if (highest.Length > 0)
            {
                var staged = position.Zone(side, CardZone.Hand).Cards.FirstOrDefault(item => item.CardId == source.Id);
                var sourcePower = staged?.Power ?? source.Power;
                var sourceArmor = staged?.Armor ?? source.PrintedArmor ?? 0;
                var sourceShield = staged?.Statuses?.Contains(CardStatus.Shield) == true;
                var swings = highest.Select(target =>
                {
                    var enemyLoss = DamageSwing(target, sourcePower);
                    var ownLoss = sourceShield ? 0 : Math.Min(sourcePower, Math.Max(0, highestPower - sourceArmor));
                    return enemyLoss - ownLoss;
                }).ToArray();
                low += swings.Min(); high += swings.Max(); found = true;
                bounded |= highest.Length > 1 || position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board).Any(zone => !zone.Complete);
                note = "Clash uses the current highest enemy power and both units' visible Armor/Shield; tied highest targets produce a range.";
            }
            else
            {
                found = true;
                if (position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board).Any(zone => !zone.Complete))
                { bounded = true; note = "No visible Clash target; an incomplete enemy row can raise the result."; }
            }
        }

        return found ? (low, high, bounded, random, note) : null;
    }

    private IEnumerable<PositionCard> Units(GamePosition position, PlayerSide side) => position.Zones
        .Where(zone => zone.Side == side && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards).Where(IsUnit);
    private bool IsUnit(PositionCard card) => _cards.GetValueOrDefault(card.CardId)?.Kind == CardKind.Unit;
    private IEnumerable<PositionCard> TargetableEnemies(GamePosition position, PlayerSide enemy)
    {
        foreach (var row in position.Zones.Where(zone => zone.Side == enemy && zone.Zone == CardZone.Board))
        {
            var units = row.Cards.Where(IsUnit).Where(card => card.Statuses?.Contains(CardStatus.Immune) != true).ToArray();
            var defenders = units.Where(card => card.Statuses?.Contains(CardStatus.Defender) == true).ToArray();
            foreach (var unit in defenders.Length > 0 ? defenders : units) yield return unit;
        }
    }
    private int DamageSwing(PositionCard card, int amount)
    {
        var invariant = _book.Rules.GetValueOrDefault(card.CardId)?.PowerInvariant;
        if (invariant == "fixed") return 0;
        if (card.Statuses?.Contains(CardStatus.Shield) == true) return 0;
        var armor = card.Armor ?? 0; var power = card.Power ?? amount;
        if (invariant == "armor") return Math.Min(power, Math.Min(armor, amount));
        return Math.Min(power, Math.Max(0, amount - armor));
    }
    private int SetPowerSwing(PositionCard card, int amount, bool allied)
    {
        if (_book.Rules.GetValueOrDefault(card.CardId)?.PowerInvariant is "fixed" or "armor") return 0;
        var delta = amount - (card.Power ?? amount);
        return allied ? delta : -delta;
    }
    private CardDefinition? FindByName(string value)
    {
        value = Regex.Replace(value, @"^(?:a |an |the )", "", RegexOptions.IgnoreCase).Trim(' ', '.', ',');
        return _cards.Values.FirstOrDefault(card => card.Name.Equals(value, StringComparison.OrdinalIgnoreCase)) ??
            (value.EndsWith('s') ? _cards.Values.FirstOrDefault(card => card.Name.Equals(value[..^1], StringComparison.OrdinalIgnoreCase)) : null);
    }
    private static bool HasUncountedValue(string text, bool modeled) => text.Length > 0 &&
        (text.Contains("Order", StringComparison.OrdinalIgnoreCase) || text.Contains("Whenever", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Create", StringComparison.OrdinalIgnoreCase) || text.Contains("Deathwish", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("At the start", StringComparison.OrdinalIgnoreCase) || text.Contains("At the end", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Timer", StringComparison.OrdinalIgnoreCase) || text.Contains("Fee", StringComparison.OrdinalIgnoreCase) || !modeled);
}
