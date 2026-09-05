using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class SummonFocusTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var project = File.Exists(Path.Combine(root, "GwentCompanion.sln")) ? root : Path.Combine(root, "GwentCompanion");
        var cards = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json")).ToDictionary(card => card.Id);
        var at = DateTimeOffset.UnixEpoch;
        var watch = new TacticalWatch(cards.Values);
        TacticalReport Report(string faction, ObservedCard[]? seen = null, DeckDefinition? pin = null, CardDefinition[]? picks = null,
            TriggerOpportunity[]? triggers = null) => watch.Build(faction, null, pin, picks ?? [], seen ?? [], null, [], triggers ?? [], at);
        string Faction(CardDefinition card) => card.Faction == "Neutral" ? "Monsters" : card.Faction;

        string[] automatic = ["112210", "202397", "142211", "202445", "203282", "202879", "202367", "202608", "203217",
            "162310", "202415", "202908", "203096", "203100", "203097", "203224", "203191", "200088", "122318"];
        foreach (var id in automatic)
        {
            var faction = Faction(cards[id]);
            var sections = PlaysSectionBuilder.Build(Report(faction), faction, []);
            Check(sections.Summons.Any(row => row.Rule.Card.Id == id), "Independent trigger watch missing: " + cards[id].Name);
        }

        string[] routine = ["132310", "201559", "202273", "202334", "202521", "202549", "203089", "162301", "112202", "112203", "112204",
            "203223", "203200", "203286", "203145", "202998", "162105", "203055", "203054", "152211", "203075", "202953"];
        foreach (var id in routine)
        {
            var card = cards[id]; var faction = Faction(card);
            var observed = new[] { new ObservedCard(card, CardProvenance.ConfirmedStartingDeck, 1, at, "Confirmed original") };
            var pin = new DeckDefinition("pin", "pin", faction, "", 0, [new(card)]);
            watch.Keep(id, true); watch.Review(id, WatchReview.Arrived, at);
            var report = Report(faction, observed, pin, [card]);
            Check(report.Summons.Any(row => row.Rule.Card.Id == id), "Presentation must not remove the underlying interaction rule: " + card.Name);
            var sections = PlaysSectionBuilder.Build(report, faction, observed, pin, [card]);
            Check(!sections.Summons.Any(row => row.Rule.Card.Id == id), "Routine package returned through pin/seen/priority: " + card.Name);
            var projected = new OpponentDeckProjector().Build([], observed, faction);
            Check(projected.Slots.Any(slot => slot.Card?.Id == id && slot.State == DeckSlotState.Observed), "Hidden package lost its original deck slot.");
            var expansion = new SummonSectionExpansion();
            Check(!expansion.Observe(sections.Summons), "Routine package must not auto-expand Summons.");
            watch.Reset();
        }

        foreach (var id in new[] { "202201", "203257", "200022", "200221", "203090", "122313" })
        {
            var card = cards[id]; var faction = Faction(card);
            Check(!PlaysSectionBuilder.Build(Report(faction), faction, []).Summons.Any(row => row.Rule.Card.Id == id), "Unrelated delayed source clutters default list.");
            var observed = new[] { new ObservedCard(card, CardProvenance.Unknown, .9, at, "Source seen; origin unknown") };
            Check(PlaysSectionBuilder.Build(Report(faction, observed), faction, observed).Summons.Any(row => row.Rule.Card.Id == id), "Observed delayed source should remain: " + card.Name);
            var pin = new DeckDefinition("pin", "pin", faction, "", 0, [new(card)]);
            Check(PlaysSectionBuilder.Build(Report(faction, pin: pin), faction, [], pin).Summons.Any(row => row.Rule.Card.Id == id), "Pinned delayed source should remain.");
            Check(PlaysSectionBuilder.Build(Report(faction, picks: [card]), faction, [], assumed: [card]).Summons.Any(row => row.Rule.Card.Id == id), "Manually assumed delayed source should remain.");
        }
        var monsters = PlaysSectionBuilder.Build(Report("Monsters"), "Monsters", []);
        Check(monsters.Synergies.SelectMany(group => group.Cards).Any(row => row.Rule.Card.Id == "132310"), "Rider's Dominance bonus must survive watch filtering.");
        var scoiatael = PlaysSectionBuilder.Build(Report("Scoia'tael"), "Scoia'tael", []);
        Check(scoiatael.Bonuses.Any(row => row.Rule.Card.Id == "201559"), "Volunteers' board-condition bonus must survive watch filtering.");
        var roach = PlaysSectionBuilder.Build(Report("Monsters", triggers: [new("112210", at, "Gold preview")]), "Monsters", []).Summons.Single(row => row.Rule.Card.Id == "112210");
        Check(roach.State == TacticalState.Pending, "Unverified absence must stay amber, not red or ruled out.");
        watch.Review("202397", WatchReview.Missed, at);
        Check(PlaysSectionBuilder.Build(Report("Monsters"), "Monsters", []).Summons.Single(row => row.Rule.Card.Id == "202397").State != TacticalState.Missed,
            "Random non-arrival must not become negative evidence.");
        watch.Reset();
        foreach (var faction in new[] { "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate" })
        {
            var report = Report(faction);
            var compact = PlaysSectionBuilder.Build(report, faction, []).Summons;
            Check(compact.Count is >= 5 and <= 8, "Reconsider compact presentation if baseline watch count grows: " + faction);
            Check(compact.All(row => FactionCompatibility.IsPlayableBy(row.Rule.Card, faction)), "Off-faction summon in compact buttons: " + faction);
            var dismissal = new TacticalDismissals(); var card = compact.First();
            dismissal.Hide(card);
            Check(dismissal.Contains(card with { State = TacticalState.Seen }), "Hidden buttons returned on state update.");
            dismissal.Restore(compact);
            Check(!dismissal.Contains(card), "Cannot restore a hidden button.");
            Console.WriteLine($"  {faction}: {report.Summons.Count} underlying interactions → {compact.Count} default trigger watches.");
        }
    }
}
