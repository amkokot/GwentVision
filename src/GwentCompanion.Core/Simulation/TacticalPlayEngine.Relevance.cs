using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    // Whole-text gates: a balance change invalidates the exemption. These cards remain
    // unsupported when played; only already-present, inactive clauses are excluded.
    private bool DormantInImmediatePlay(PlayRule rule,PlayerSide owner,PlayerSide acting,GamePosition p,string playedId)
    {
        var text=PlayRules.Normalize(rule.Card.AbilityText);
        if(text is "Profit 2. Fee 1: Boost an allied unit by 1." or
            "Resilience. Profit 4. Fee 1: Boost an allied unit by 1. Increase the boost by this card's Fee cost. Each time you pay this card's Fee, increase its cost by 1, up to 9." or
            "Deploy: Poison self. Fee 4: Move Poison from self to another unit. Cooldown: 3" or
            "Resilience. Deploy: Spawn and play a Failed Experiment, Salamandra Abomination, Salamandra Mage, or Salamandra Lackey. Order: Move Poison from an allied unit to another unit." or
            "Intimidate. Fee 1: Damage the highest-power enemy unit by 1.") return true;
        if(text=="Immunity. Profit 2. Fee 1: Poison an allied unit and boost it by 2. Cooldown: 1 Adrenaline 6: At the end of your turn, Purify self.") return owner!=acting;
        if(rule.Reaction=="portal-timer") return owner!=acting; // Its owner's end of turn only.
        if(text=="Deploy (Melee): Clash with the highest-power enemy unit. Might: At the end of your turn, while in hand, gain 1 Armor.") return true;
        if(text=="When Poisoned, Spawn a base copy of self on this row. Counter: 1")
        {
            var played=Find(p,playedId); var ability=played is null ? null : Rule(played.CardId)?.Card.AbilityText;
            if(ability is null || ability.Contains("Poison") || ability.Contains("Create") || ability.Contains("from your deck")) return false;
            return !Board(p).Any(c=>Rule(c.CardId)?.Card.AbilityText is {} t && t.Contains("Poison") &&
                (t.Contains("Whenever") || t.Contains("Deathwish")));
        }
        return false;
    }
}
