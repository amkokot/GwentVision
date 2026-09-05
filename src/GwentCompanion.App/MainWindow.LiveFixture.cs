using System.Collections.Immutable;
using System.IO;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private void LoadLiveInsightsFixture(IReadOnlyList<CardDefinition> catalog)
    {
        // Only reachable through explicit OFFLINE REVIEW; no game discovery, input, or real-match writes.
        _suspendReach = true; // Background library projection must not replace this deterministic UI fixture.
        _candidateCatalog = catalog;
        CardDefinition Card(string name) => catalog.First(card => card.Name == name);
        var at = DateTimeOffset.Now;
        _liveValues.Add(new("fixture-truffle", PlayerSide.Opponent, "203061", "The Mushy Truffle", PendingValueKind.LocationOrder, 0, 6,
            "Golden Froth · up to three adjacent targets", at));
        _liveValues.Add(new("fixture-pass", PlayerSide.Opponent, "203279", "Mahakam Pass", PendingValueKind.LocationOrder, 0, 5,
            "Tempering · armor is not additional raw points", at));
        _liveValues.Add(new("fixture-boost", PlayerSide.User, "202596", "Allgod", PendingValueKind.DeckBoost, 4, 4,
            "Reviewed remaining deck boost · two targets", at));
        _liveValues.RecordGrowth(new(PlayerSide.User, "203102", "Aerondight", 9, 9, "damage", "Fixture: reviewed current damage"));
        _liveValues.RecordGrowth(new(PlayerSide.Opponent, "132205", "Morvudd", 15, null, "power", "Hypothetical off-board growth · incomplete turn coverage"));
        _liveValues.Store(PlayerSide.User, Card("Nekker Warrior"), "Fixture: captured unit, not carryover");
        RenderTacticalWatch(); RenderLiveValues();
        var bomb = Card("Bombardment"); var p = GamePosition.EmptyKnown();
        p = p with { Zones = p.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand ? zone with
            { Cards = [new("hover", bomb.Id, 0, 0, 0, ImmutableHashSet<CardStatus>.Empty)], TotalCount = 1 } :
            zone.Side == PlayerSide.Opponent && zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee ? zone with
            { Cards = [new("a", Card("Volunteer").Id, 2, 2, 0, ImmutableHashSet<CardStatus>.Empty),
                new("b", Card("Volunteer").Id, 8, 2, 2, ImmutableHashSet<CardStatus>.Empty.Add(CardStatus.Shield))], TotalCount = 2 } : zone).ToImmutableArray() };
        var analyzer = new ThreatAnalyzer(catalog, () => CreatePointProfiles.LoadOrBuild(Path.Combine(FindDataRoot(), "cache/create-point-profiles.json"), catalog));
        var report = analyzer.Analyze(new(p, "hover", ["Explicit synthetic UI fixture, not recognition"], new("Northern Realms", "Monsters")), 1,
            [new(Card("Old Speartip"), .8, "Fixture"), new(Card("Devana Runestone"), .6, "Fixture")]);
        if (Environment.GetCommandLineArgs().Contains("--review-hover-banner"))
        { _liveHover = bomb; _lastHoverAt = at; ShowHoverThreats.IsChecked = true; }
        if (Environment.GetCommandLineArgs().Contains("--review-reach-progress"))
        {
            report = report with { Complete = false, Reach = null };
            _reachProgress = new(2, 8, report);
        }
        RenderThreatReport(bomb, report);
        ShowPage(UiPage.Plays);
        GameStatusText.Text = "OFFLINE REVIEW · synthetic Live insights fixture";
        if (Environment.GetCommandLineArgs().Contains("--review-encounter-editor"))
        {
            var cards = new[] { "Brewess: Ritual", "Rotfiend", "Siren" }.Select(name =>
                new ObservedCard(Card(name), CardProvenance.ProbableStartingDeck, 1, at, "Offline fixture", name == "Rotfiend" ? 2 : 1)).ToArray();
            var memory = new OpponentDeckMemoryStore();
            var encounter = new LearnedOpponentEncounter("offline-fixture", at, "Monsters", "Fruits of Ysgith", 15, null, 25, 25,
                cards, [], OpponentProvisionCalculator.Calculate(cards, 165, 25), MatchMmr:2406, PostMatchMmr:new(2406,null,true,"Faction",2431));
            var record = OpponentEncounterPipeline.Capture(memory, encounter, []).Record;
            var editor = new OpponentReviewWindow(record, catalog, new DeckLibrary(),
                Path.Combine(FindDataRoot(), "diagnostics/opponent-editor-fixture-library.json"), () => { }, (draft, later) =>
            {
                memory.Review(record.Id, draft.Name, OpponentReviewDraft.Cards(record, draft), later, OpponentReviewDraft.Header(draft, catalog));
                memory.Save(Path.Combine(FindDataRoot(), "diagnostics/v0.1.30-editor-fixture.json"));
            }) { Owner = this, Title = "OFFLINE REVIEW · opponent editor" };
            editor.Show();
        }
    }
}
