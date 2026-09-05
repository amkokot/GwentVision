using System.IO;
using System.Xml.Linq;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;

internal static class PlaysSectionTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var at = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var watch = new TacticalWatch(cards);
        var redanian = cards.Single(card => card.Name == "The Flying Redanian");
        watch.Keep(redanian.Id, true);
        TacticalReport Report(string? faction = "Monsters") => watch.Build(faction, null, null, [], [], null, [], [], at);
        PlaysSections Sections(string? faction = "Monsters") => PlaysSectionBuilder.Build(Report(faction), faction, []);
        Check(Sections().Summons.All(row => row.Rule.Card.Id != redanian.Id), "Stale kept SY summon must not clutter MO.");
        Check(Sections().Summons.Any(row => row.Rule.Card.Name == "Winter Queen"), "MO should include Winter Queen.");
        Check(Sections().Summons.Any(row => row.Rule.Card.Name == "Roach"), "Neutral summons are available to every faction.");
        Check(Sections(null).Summons.All(row => row.Rule.Card.Faction == "Neutral"), "Unknown faction must not flood every faction.");
        Check(Sections().Synergies.Any(group => group.Name == "Dominance") && Sections().Synergies.All(group => group.Name != "Hoard"), "MO keyword filtering.");
        Check(Sections("Skellige").Synergies.Any(group => group.Name == "Bloodthirst"), "SK Bloodthirst grouping.");
        var expectedSynergies = new Dictionary<string, string[]>
        {
            ["Monsters"] = ["Deathwish", "Dominance", "Organic", "Thrive"],
            ["Nilfgaard"] = ["Assimilate", "Flanking", "NG Cultist", "Tactic"],
            ["Northern Realms"] = ["Crew", "Grace", "Inspired"],
            ["Scoia'tael"] = ["Barricade", "Harmony", "Symbiosis"],
            ["Skellige"] = ["Alchemy", "Berserk", "Bloodthirst", "Pirate Armor", "Raid"],
            ["Syndicate"] = ["Crime", "Hoard", "Intimidate", "Tribute"],
        };
        foreach (var pair in expectedSynergies)
            Check(FactionSynergyCatalog.ForFaction(pair.Key).SequenceEqual(pair.Value), $"{pair.Key} live synergy routing.");
        var routed = expectedSynergies.Keys.SelectMany(FactionSynergyCatalog.ForFaction).ToArray();
        Check(routed.Length == routed.Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
            routed.OrderBy(name => name).SequenceEqual(FactionSynergyCatalog.All),
            "Every tracked Live synergy must have exactly one owning faction.");
        Check(FactionSynergyCatalog.ForFaction("Monsters").Contains("Organic") &&
            !FactionSynergyCatalog.ForFaction("Monsters").Contains("Alchemy") &&
            FactionSynergyCatalog.ForFaction("Skellige").Contains("Alchemy") &&
            !FactionSynergyCatalog.ForFaction("Skellige").Contains("Organic"),
            "Organic belongs to MO and Alchemy belongs to SK.");
        Check(FactionSynergyCatalog.ForFaction("Nilfgaard").Contains("Flanking") &&
            FactionSynergyCatalog.ForFaction("Scoia'tael").Contains("Barricade") &&
            !FactionSynergyCatalog.All.Contains("Warrior"),
            "Broad current card-pool packages are routed correctly; niche/redundant meters stay hidden.");
        var rules = TacticalSynergies.Rules(cards).ToArray();
        var blood1 = rules.First(rule => rule.Synergy == "Bloodthirst" && rule.Requirement.StartsWith("Bloodthirst 1"));
        var blood3 = rules.First(rule => rule.Synergy == "Bloodthirst" && rule.Requirement.StartsWith("Bloodthirst 3"));
        watch.SetRuleCondition(blood1, true, at);
        Check(Report("Skellige").Plays.Single(row => row.Rule.Key == blood1.Key).State == TacticalState.Ready, "Reviewed card-local condition.");
        Check(Report("Skellige").Plays.Single(row => row.Rule.Key == blood3.Key).State != TacticalState.Ready, "Bloodthirst 1 cannot enable Bloodthirst 3.");
        var expired = watch.Build("Skellige", null, null, [], [], null, [], [], at.AddSeconds(31));
        Check(expired.Plays.Single(row => row.Rule.Key == blood1.Key).State == TacticalState.Unknown, "Local review expires.");
        var expansion = new SummonSectionExpansion();
        Check(!expansion.Observe(Sections().Summons), "Unknown watches must not auto-expand.");
        var roach = cards.Single(card => card.Name == "Roach");
        watch.Review(roach.Id, WatchReview.Arrived, at);
        Check(expansion.Observe(Sections().Summons), "Newly seen summon watch expands.");
        Check(!expansion.Observe(Sections().Summons), "Manual collapse survives redraw.");
        expansion.Reset(); Check(expansion.Observe(Sections().Summons), "New match resets expansion notification.");
        var dismissal = new TacticalDismissals();
        var roachRow = Sections().Summons.Single(row => row.Rule.Card.Id == roach.Id);
        dismissal.Hide(roachRow);
        Check(dismissal.Contains(roachRow) && Sections().Summons.Single(row => row.Rule.Card.Id == roach.Id).State == TacticalState.Seen,
            "Hiding a watch must not delete arrival evidence.");
        Check(dismissal.Contains(roachRow with { State = TacticalState.Pending }), "Dismissal survives redraw/state changes for this match.");
        dismissal.Restore([roachRow]); Check(!dismissal.Contains(roachRow), "Restore hidden watch.");
        dismissal.Hide(roachRow); dismissal.Reset(); Check(!dismissal.Contains(roachRow), "New match clears dismissals.");
        var doc = XDocument.Load(Path.Combine(root, "GwentCompanion", "src", "GwentCompanion.App", "MainWindow.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var names = doc.Descendants().Select(el => (string?)el.Attribute(x + "Name")).Where(name => name is not null).ToArray();
        Check(Array.IndexOf(names, "SummonButtons") >= 0 && Array.IndexOf(names, "SummonButtons") < Array.IndexOf(names, "SynergyButtons") &&
              Array.IndexOf(names, "SynergyButtons") > Array.IndexOf(names, "SummonButtons") && !names.Contains("BonusesExpander") && !names.Contains("ThreatsExpander"), "Compact Live section order.");
        Check(!names.Contains("SummonPopup"), "No legacy summon dropdown.");
    }
}
