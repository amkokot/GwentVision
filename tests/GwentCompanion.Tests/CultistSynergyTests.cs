using System.Collections.Immutable;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class CultistSynergyTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static string Clause(int amount) => $"Whenever you play a Cultist, boost self by {amount}, then increase this value by 1 if it was a bronze.";

    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var eclipse = catalog.Single(c => c.Id == CultistSynergyTracker.ScenarioId);
        var initiate = catalog.Single(c => c.Id == "203066");
        var deacon = catalog.Single(c => c.Id == "203065");
        var prophet = catalog.Single(c => c.Id == "203111");
        var master = catalog.Single(c => c.Id == "203064");
        var plain = new CardDefinition("plain-gold", "Plain gold", "Nilfgaard", CardKind.Unit, 8, 5, true);
        var cards = catalog.Append(plain).ToArray();
        Check(eclipse.AbilityText!.Contains("Chapter 1: Infuse all your non-Disloyal Cultists") &&
            eclipse.AbilityText.Contains("increase this value by 1 if it was a bronze"), "Cached scenario rule changed; review the tracker.");

        Check(CultistInfusionReader.Read(eclipse, eclipse.AbilityText) is null, "Scenario prose became a recipient.");
        Check(CultistInfusionReader.Read(initiate, initiate.AbilityText) is { Boosts.Length: 0 }, "Enemy damage infusion became boost.");
        Check(CultistInfusionReader.Read(plain, "Plain gold\nHuman, Cultist\n" + Clause(3) + "\n" + Clause(1)) is
            { HasCultistCategory: true, Boosts: var boosts } && boosts.SequenceEqual([3, 1]), "Independent stacked infusion parsing.");
        Check(CultistInfusionReader.Read(plain, "Plain gold\nDeploy: Infuse a unit with Cultist category.") is null,
            "Mentioning Cultist in an ability became an infused category.");
        Check(CultistInfusionReader.Read(plain, Clause(0)) is null && CultistInfusionReader.Read(plain, Clause(1000)) is null,
            "Invalid OCR was accepted.");

        var f = new Fixture(cards);
        f.Add(initiate, "initiate"); f.Play(eclipse, "scenario");
        Check(!f.Read().Activated && f.Meter().Active is null, "Prologue alone activated Cultists.");
        f.Play(plain, "plain");
        Check(!f.Read().Activated, "An uninfused gold non-Cultist advanced Eclipse.");
        f.Play(deacon, "early-bronze");
        Check(!f.Read().Activated, "Bronze Cultist advanced the scenario.");
        f.Add(deacon, "hidden", zone: CardZone.Hand);
        f.Play(prophet, "gold");
        Check(f.Read() is { Activated: true, Boost: 3 } && f.Meter().Active == true,
            "Chapter 1 did not grant 1 separately to all three board Cultists (including the activating gold).");
        f.Play(initiate, "bronze");
        Check(f.Read().Boost == 7, "Bronze should grow three existing listeners, with a later entrant starting at 1, not triggering itself.");
        f.Play(master, "second-gold");
        Check(f.Read().Boost == 8, "Gold should not grow existing infusion values.");
        f.Play(deacon, "chapter-two-deacon");
        Check(f.Read().Boost == 13, "Chapter 2 Deacon should trigger growth but not inherit Chapter 1 infusion.");
        f.Move("hidden", CardZone.Board); f.Step();
        Check(f.Read().Boost == 14, "Infusion in hand grew off-board or was lost on entry.");
        f.Add(initiate, "spawn-only"); f.Step(kind: "BoardEvidence", card: initiate);
        Check(f.Read().Boost == 14, "Spawn/summon was counted as play or received a historical board grant.");
        f.Step(kind: "HistoricalAction", card: initiate);
        Check(f.Read().Boost == 14, "History replay increased the counter.");
        Check(f.Read().Approximate && f.Meter().Value.StartsWith("≈+"), "Inferred hidden-zone grants must be visibly approximate.");
        f.NextRound();
        Check(f.Read() is { Activated: true, Boost: 0 } && f.Meter().Active == true,
            "Round reset either retained old board points or forgot Chapter 1 activation.");
        f.ResetMatch();
        Check(!f.Read().Activated && f.Read().Boost == 0, "Match reset retained the prior activation.");

        // Direct observations reconcile independent per-unit/per-copy counters.
        var exact = new Fixture(cards);
        exact.Add(prophet, "a"); exact.Add(master, "b"); exact.Step();
        Check(exact.Hover(prophet, [3, 1]) && exact.Hover(master, [2]) && exact.Read().Boost == 6,
            "Different and stacked infusion amounts were flattened.");
        Check(!exact.Read().Approximate && exact.Meter() is { Value: "+6", Active: true, Minimum: 6, Maximum: 6 },
            "Complete measured recipients did not produce an exact current total.");
        exact.Hover(prophet, [3, 1]);
        Check(exact.Read().Boost == 6, "Repeated tooltip added another infusion instead of reconciling.");
        exact.Play(deacon, "new", origin: CardProvenance.Created);
        Check(exact.Read().Boost == 9, "Each stacked infusion must grow independently after a bronze play.");
        exact.Hover(prophet, [4, 2]); exact.Hover(master, [3]);
        Check(exact.Read().Boost == 9, "Post-play runtime text double-counted growth.");
        exact.SetStatus("a", CardStatus.Locked, true); exact.Step();
        Check(exact.Read().Boost == 3, "Locked recipients still contributed boost.");
        exact.Play(initiate, "bronze-two", origin: CardProvenance.Created);
        Check(exact.Read().Boost == 4, "Locked infusions continued to trigger/grow.");
        exact.SetStatus("a", CardStatus.Locked, false); exact.Step();
        Check(exact.Read().Boost == 10, "Unlock should resume the frozen 4+2 values, not catch up on missed plays.");
        exact.SetStatus("a", CardStatus.Infused, false); exact.Step();
        Check(exact.Read().Boost == 4, "Purify did not clear all stacked infusions.");
        exact.Move("b", CardZone.Graveyard); exact.Step();
        Check(exact.Read().Boost == 0 && exact.Read().Activated, "Removed recipient still contributed points.");

        var dynamic = new Fixture(cards);
        dynamic.Add(initiate, "engine"); dynamic.Play(eclipse, "scenario");
        dynamic.Play(plain, "infused-gold");
        Check(!dynamic.Read().Activated, "Unknown category was guessed before observation.");
        dynamic.Step(); // Repeated tooltip is normally a later frame than the play preview.
        dynamic.Hover(plain, [1], category: true);
        Check(dynamic.Read().Activated && dynamic.Read().Boost == 2, "Observed Cultist category did not advance pending gold play.");
        dynamic.Play(deacon, "bronze", side: PlayerSide.Opponent, origin: CardProvenance.Created);
        Check(dynamic.Read().Boost == 2 && !dynamic.Read(PlayerSide.Opponent).Activated, "Opponent play grew friendly counters.");
        var repeatedId = "same-play";
        dynamic.Step(kind: "PlayPreview", card: initiate, eventId: repeatedId);
        var afterOne = dynamic.Read().Boost;
        dynamic.Step(kind: "PlayPreview", card: initiate, eventId: repeatedId);
        Check(dynamic.Read().Boost == afterOne, "Duplicate event ID grew infusions twice.");
        Check(!dynamic.Hover(initiate, [20], inHand: true) && dynamic.Read().Boost == afterOne,
            "Hand tooltip overwrote a same-identity board recipient.");
        dynamic.Add(initiate, "duplicate", side: PlayerSide.Opponent); dynamic.Step();
        Check(!dynamic.Hover(initiate, [20]), "Ambiguous same-art cards on opposite sides accepted one tooltip.");

        var removedScenario = new Fixture(cards);
        removedScenario.Play(eclipse, "scenario"); removedScenario.Move("scenario", CardZone.Graveyard); removedScenario.Step();
        removedScenario.Play(prophet, "gold");
        Check(!removedScenario.Read().Activated, "Destroyed scenario activated from later gold Cultist.");
        var blockedGrant = new Fixture(cards);
        blockedGrant.Add(initiate, "veil"); blockedGrant.SetStatus("veil", CardStatus.Veil, true);
        var spy = plain with { Id = "disloyal", AbilityText = "Disloyal.", Categories = new HashSet<string> { "Cultist" } };
        blockedGrant.Add(spy, "spy"); blockedGrant.Play(eclipse, "scenario"); blockedGrant.Play(prophet, "gold");
        Check(blockedGrant.Read().Boost == 1, "Veil or Disloyal unit received scenario infusion.");

        var stacks = new Fixture(cards);
        stacks.Play(eclipse, "first-scenario"); stacks.Play(prophet, "first-gold");
        stacks.Play(eclipse, "second-scenario"); stacks.Play(master, "second-gold");
        Check(stacks.Read().Boost == 3, "Second Chapter 1 replaced, rather than stacked with, the older infusion.");
        stacks.Play(deacon, "generated-deacon");
        Check(stacks.Read().Boost == 6, "Generated Deacon should grow all three infusion layers, without gaining a historical grant.");
        stacks.Play(deacon, "hand-deacon");
        Check(stacks.Read().Boost == 11, "One generated Deacon exclusion must not exclude the next hand Deacon, which may carry both Chapter 1 grants.");
        var enemy = new Fixture(cards);
        enemy.Play(eclipse, "scenario", PlayerSide.Opponent); enemy.Play(prophet, "gold", PlayerSide.Opponent);
        Check(enemy.Read(PlayerSide.Opponent) is { Activated: true, Boost: 1 } && !enemy.Read().Activated,
            "Opponent's scenario did not activate its own independent counter.");

        // Ensure both streaming result paths carry the new repeated text observation.
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 1, 0, null);
        var reading = new CultistInfusionReading(prophet.Id, true, [4]);
        var prepared = new PreparedVisionFrame(new PixelFrame(1, 1, new byte[4]), DateTimeOffset.UnixEpoch, screen, [])
            { CultistInfusion = reading };
        Check(prepared.TextResult.CultistInfusion == reading, "Text-only frame lost the infusion reading.");
        var ui = File.ReadAllText(Path.Combine(root, "GwentCompanion/src/GwentCompanion.App/MainWindow.TacticalWatch.cs"));
        Check(FactionSynergyCatalog.ForFaction("Nilfgaard").Contains("NG Cultist") &&
            ui.Contains("_cultists.Read(_gameState.Current, PlayerSide.User)") &&
            ui.Contains("_cultists.Read(_gameState.Current, PlayerSide.Opponent)") && ui.Contains("Text = reading.Reason"),
            "Cultist meter is not wired to both sides with visible explanation.");
        Console.WriteLine("PASS NG Cultist: Chapter 1 activation, per-unit/stacked growth, gold/bronze, hidden zones, generated Deacon, locks/Purify, side isolation, OCR and reset.");
    }

    private sealed class Fixture(CardDefinition[] catalog)
    {
        private readonly Dictionary<string, GameCardInstance> _cards = [];
        private int _tick;
        private int _round = 1;
        private string _session = "cultist";
        private readonly CultistSynergyTracker _tracker = new();
        private GameStateSnapshot _state = new GameStateTracker().Current;
        private DateTimeOffset NextAt => DateTimeOffset.UnixEpoch.AddSeconds(_tick + 1);
        public void Add(CardDefinition card, string id, PlayerSide side = PlayerSide.User, CardZone zone = CardZone.Board,
            CardProvenance origin = CardProvenance.Unknown)
        {
            _cards[id] = new(id, card, new(new(side, zone, BoardRow.Melee, null), NextAt, 1, EvidenceKind.Visual, "fixture"),
                NextAt, NextAt, CardPresence.Visible, Origin: new(origin, NextAt, 1, EvidenceKind.Reviewed, "fixture"));
        }
        public void Move(string id, CardZone zone) => _cards[id] = _cards[id] with
        { Location = _cards[id].Location with { Value = _cards[id].Location.Value with { Zone = zone }, At = NextAt } };
        public void SetStatus(string id, CardStatus status, bool value)
        {
            var prior = _cards[id].Statuses.IsDefault ? [] : _cards[id].Statuses;
            _cards[id] = _cards[id] with { Statuses = prior.Where(s => s.Status != status)
                .Append(new StatusFact(status, new(value, NextAt, 1, EvidenceKind.Visual, "fixture"))).ToImmutableArray() };
        }
        public void Play(CardDefinition card, string id, PlayerSide side = PlayerSide.User, CardProvenance origin = CardProvenance.Unknown)
        { Add(card, id, side, origin: origin); Step("PlayPreview", card, side, id); }
        public void Step(string? kind = null, CardDefinition? card = null, PlayerSide side = PlayerSide.User,
            string? instanceId = null, string? eventId = null)
        {
            var at = NextAt; _tick++;
            foreach (var key in _cards.Keys.ToArray()) _cards[key] = _cards[key] with { LastSeen = at };
            var next = _state with
            {
                SessionId = _session, At = at, Revision = _tick, Phase = GamePhase.Playing,
                Round = new(_round, at, 1, EvidenceKind.Reviewed, "fixture"), Cards = _cards.Values.ToImmutableArray(),
                Rows = Enum.GetValues<PlayerSide>().SelectMany(s => Enum.GetValues<BoardRow>().Select(row =>
                    new GameRowState(s, row, RowCoverage.Complete, at,
                        _cards.Values.Where(c => c.Location.Value.Controller == s && c.Location.Value.Zone == CardZone.Board &&
                            c.Location.Value.Row == row).Select(c => c.InstanceId).ToImmutableArray(), []))).ToImmutableArray()
            };
            ImmutableArray<GameStateEvent> events = kind is null ? [] : [new(eventId ?? $"event-{_tick}", at, kind, side, card?.Id, instanceId, "fixture")];
            _tracker.Observe(new(_state, next, events, true), catalog); _state = next;
        }
        public bool Hover(CardDefinition card, ImmutableArray<int> boosts, bool category = true, bool inHand = false) =>
            _tracker.ObserveHover(_state, new(card.Id, category, boosts), _state.At!.Value, inHand);
        public CultistSynergyEstimate Read(PlayerSide side = PlayerSide.User) => _tracker.Read(_state, side);
        public LiveSynergyReading Meter() => LiveSynergyMeter.Read(_state, PlayerSide.User, "NG Cultist", cultist: Read());
        public void NextRound() { _round++; _cards.Clear(); Step(); }
        public void ResetMatch() { _session = "next-match"; _round = 1; _cards.Clear(); Step(); }
    }
}
