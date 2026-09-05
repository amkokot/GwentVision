using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class CompanionCardTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ObservedCard Seen(CardDefinition card, int copies = 1, CardProvenance origin = CardProvenance.ProbableStartingDeck) =>
        new(card, origin, .99, At, "Fixture original play", copies);
    private static CardDefinition Filler(string id, int provision = 4) => new(id, id, "Monsters", CardKind.Unit, provision);

    public static void Run(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(card => card.Id);
        var projector = new OpponentDeckProjector();
        OpponentDeckProjection Project(ObservedCard[] seen, string faction = "Monsters", DeckProjectionEdits? edits = null,
            ObservedStartingDeckAssessment? rules = null, DeckDefinition? pin = null) =>
            projector.Build([], seen, faction, pin, edits, rules, catalog: cards.Values);
        Check(!CompanionCardRules.ThinningPairs.Contains("132301"), "Foglet is a weather Deathwish, not a matching-copy summon.");
        foreach (var id in CompanionCardRules.ThinningPairs)
        {
            var card = cards[id]; var faction = card.Faction == "Neutral" ? "Monsters" : card.Faction;
            Check(card.AbilityText?.Contains("Summon all copies", StringComparison.OrdinalIgnoreCase) == true,
                "Matching-copy registry contains a card without a matching-copy summon: " + card.Name);
            var result = Project([Seen(card)], faction);
            Check(result.UnknownSlots == 24 && result.ObservedCopies == 1,
                "A mechanics hypothesis without a statistical sample must not force copy two: " + card.Name);
            Check(Project([Seen(card, 2)], faction).Slots.Count(slot => slot.Card?.Id == id) == 2, "Two sightings must remain two observed copies.");
            foreach (var origin in Enum.GetValues<CardProvenance>().Where(origin => !StartingDeckRules.CountsAgainstStartingDeck(origin)))
                Check(Project([Seen(card, origin: origin)], faction).UnknownSlots == 25, "Generated/unknown arrival triggered a starting pair.");
        }
        var rider = cards["132310"];
        var evidence = new[] { Seen(rider) };
        var rules = StartingDeckRules.EvaluateObservedDeck(evidence);
        Check(!CompanionCardRules.Active(evidence, rules with { Shupe = new("Shupe", ConstraintState.Confirmed, "fixture") })
            .Any(link => link.Copies == 2), "Singleton registry suggested duplication.");
        Check(!CompanionCardRules.Active(evidence, rules with { Radeyah = new("Radeyah", ConstraintState.Confirmed, "fixture") })
            .Any(link => link.Copies == 2), "Radeyah registry suggested duplication.");
        var edits = new DeckProjectionEdits(); edits.Include(rider, 2);
        Check(Project(evidence, edits: edits).Slots.Single(slot => slot.Card?.Id == rider.Id && slot.Copy == 2).State == DeckSlotState.Selected,
            "An uncertain candidate must remain manually selectable.");
        edits.Exclude(rider, 2);
        Check(Project(evidence, edits: edits).UnknownSlots == 24, "Dismissal lost.");
        Check(Project([Seen(rider, 2)], edits: edits).Slots.Count(slot => slot.Card?.Id == rider.Id && slot.State == DeckSlotState.Observed) == 2,
            "Later physical evidence must replace dismissal.");
        var pin = new DeckDefinition("single", "Explicit singleton reference", "Monsters", "White Frost", 15,
            new[] { new DeckCard(rider) }.Concat(Enumerable.Range(0, 24).Select(i => new DeckCard(Filler("f" + i)))).ToArray());
        Check(Project(evidence, pin: pin).Slots.Count(slot => slot.Card?.Id == rider.Id) == 1, "An explicit reference was overwritten by a pair rule.");
        foreach (var id in new[] { "112202", "112401", "202533", "132218", "202836", "203044" })
            Check(Project([Seen(cards[id])], cards[id].Faction).UnknownSlots == 24, "No-data named/Bonded link forced a slot.");
        foreach (var source in cards.Values.Where(card => card.CanBeInStartingDeck))
        foreach (var link in CompanionCardRules.Active([Seen(source)]))
            Check(cards.TryGetValue(link.TargetId, out var target) && target.CanBeInStartingDeck, "Registry refers to absent/generated-only identity.");
        Check(CompanionCardRules.Active([Seen(cards["202415"])]).Count == 0, "Mentioning Bonded makes Highwaymen a pair.");
        Check(CompanionCardRules.Active([Seen(cards["203044"])]).Any(link => link.TargetId == "203044"), "Little Havfrue audit hypothesis lost.");
        Console.WriteLine("  Mechanics remain auditable; no unsupported forced pairs; manual selection and physical-copy handoff preserved.");
        BoardHandoff(cards);
        Recordings(root);
    }

    private static void BoardHandoff(Dictionary<string, CardDefinition> cards)
    {
        var rider = cards["132310"];
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        CardSighting Sight(double x, PlayerSide side = PlayerSide.Opponent) => new(rider, side, CardSightSource.Board, new(x, .2, x + .04, .3), .1, .6);
        var pair = new[] { Sight(.3), Sight(.4) };
        var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        var copies = new ThinningCopyTracker();
        var preview = new VisionEvidenceEvent(At, Sight(.8) with { Source = CardSightSource.PlayPreview }, "Rider played");
        void Reset(CardProvenance provenance = CardProvenance.ProbableStartingDeck)
        {
            tracker.Reset(); copies.Reset();
            tracker.ConsiderDirectPlay(rider, .99, At, "Original Rider preview", provenance);
            copies.ObserveEvent(preview, provenance);
        }
        bool Frame(int second, CardSighting[]? sights = null, GwentVisualObservation? state = null, bool risk = false, bool scanned = true) =>
            copies.ObserveFrame(At.AddSeconds(second), state ?? screen, sights ?? pair, scanned, [tracker], _ => risk);
        Reset();
        Check(!Frame(1) && !Frame(1), "One/duplicated board frame counted as two observations.");
        Check(Frame(2) && tracker.Observations.Single().ObservedCopies == 2, "Two board copies failed to replace inferred pair.");
        tracker.Reset(); copies.Reset();
        Check(!Frame(1) && Frame(2) && tracker.DeckBuildingObservations.Single().ObservedCopies == 2,
            "Repeated self-thinning pair without a captured preview did not recover both originals.");
        var projected = new OpponentDeckProjector().Build([], tracker.DeckBuildingObservations, "Monsters");
        Check(projected.ObservedCopies == 2 && projected.Slots.Count(slot => slot.Card?.Id == rider.Id) == 2 &&
              projected.Slots.All(slot => slot.Card?.Id != rider.Id || slot.State == DeckSlotState.Observed), "Arrival remained a guess or made a third slot.");
        Check(StartingDeckRules.ProbableProvisionLowerBound(tracker.Observations) == 2 * rider.Provision, "Observed second copy not in provision accounting.");
        tracker.Reset(); copies.Reset(); Frame(1);
        Check(!Frame(2, risk: true) && tracker.DeckBuildingObservations.Count == 0,
            "Board-only self-thinning pair bypassed an observed create/copy risk.");
        foreach (var bad in new[] { new[] { Sight(.3), Sight(.31) }, new[] { Sight(.3) }, new[] { Sight(.3), Sight(.4, PlayerSide.User) },
                     new[] { Sight(.3), Sight(.4), Sight(.5) }, pair.Select(s => s with { Source = CardSightSource.History }).ToArray() })
        {
            Reset(); Frame(1, bad); Check(!Frame(2, bad) && tracker.Observations.Single().ObservedCopies == 1, "Overlap/side/extra/history copy miscount.");
        }
        Reset(); Frame(1); Frame(2, state: screen with { IsCardSelectionOverlay = true });
        Check(!Frame(3), "Overlay did not reset copy consensus.");
        Reset(); Frame(1); Frame(2, state: screen with { HasCardTooltip = true });
        Check(!Frame(3), "Tooltip did not reset copy consensus.");
        Reset(); Frame(1); Check(!Frame(2, risk: true), "Creation risk promoted generated copy.");
        var ordinary = cards["112210"];
        var unrelatedPair = pair.Select(s => s with { Card=ordinary }).ToArray();
        Reset(); Frame(31,unrelatedPair); Check(!Frame(32,unrelatedPair), "Ordinary board pair bypassed the self-thinning identity gate.");
        Reset(); Frame(1); Check(!Frame(2, scanned: false), "Unscanned board promoted a copy.");
        Reset(CardProvenance.Created); Frame(1); Check(!Frame(2,risk:true), "Created Rider became two original cards.");
        Reset(); Frame(1); copies.ObserveEvent(preview with { ObservedAt = At.AddSeconds(1), Sighting = preview.Sighting with { Card = cards["112210"] } }, CardProvenance.ProbableStartingDeck);
        Check(Frame(2), "Independent repeated self-thinning bodies were discarded after an unrelated play.");
        var mutations = new DeckMutationLedger();
        mutations.Record(new(At, "copy", "Fixture copying source", PlayerSide.Opponent, "Another Rider added", rider.Id, rider.Name, 1, true));
        Reset();
        Check(mutations.OriginRisk(pair[0], At, tracker.Observations) is null &&
              mutations.OriginRisk(pair[0], At, tracker.Observations, additionalCopy: true) is not null,
            "One known original must not vouch for every subsequently generated copy.");
        mutations.Reset();
        mutations.Observe(preview with { Sighting = preview.Sighting with { Card = cards["200177"] } }, "Skellige", "Skellige");
        Check(mutations.OriginRisk(pair[0] with { Card = cards["152318"] }, At, [], additionalCopy: true) is not null, "Cerys-spawned Shieldmaiden treated as a second original.");
        Check(mutations.Changes.Count == 0 && mutations.OriginRisk(pair[0] with { Card = cards["152318"] }, At, []) is null,
            "Broad copy-count risk must not create a learned addition or erase independent original identities.");
        var warlord = cards.Values.Single(card => card.Name == "Highland Warlord");
        var warlordTracker = new LiveDeckTracker(PlayerSide.Opponent); warlordTracker.SetFactionPrior("Skellige");
        var warlordSight = preview.Sighting with { Card = warlord };
        var firstWarlord = preview with { Sighting = warlordSight, ObservedAt = At.AddMinutes(1) };
        warlordTracker.ConsiderDirectPlay(warlord, .99, firstWarlord.ObservedAt, "first original", CardProvenance.ProbableStartingDeck);
        copies.Reset();
        Check(!copies.ObserveEvent(firstWarlord, CardProvenance.ProbableStartingDeck, warlordTracker), "One Warlord cast became a pair.");
        Check(copies.ObserveEvent(firstWarlord with { ObservedAt = At.AddMinutes(1).AddSeconds(12) },
            CardProvenance.ProbableStartingDeck, warlordTracker) && warlordTracker.Observations.Single().ObservedCopies == 2,
            "Two distinct original bronze casts did not establish the singleton violation.");
        warlordTracker.Reset(); warlordTracker.SetFactionPrior("Skellige");
        warlordTracker.ConsiderDirectPlay(warlord, .99, firstWarlord.ObservedAt, "first original", CardProvenance.ProbableStartingDeck);
        copies.Reset(); copies.ObserveEvent(firstWarlord, CardProvenance.ProbableStartingDeck, warlordTracker);
        Check(!copies.ObserveEvent(firstWarlord with { ObservedAt = At.AddMinutes(1).AddSeconds(12) },
            CardProvenance.ProbableStartingDeck, warlordTracker, graveReplayRisk: true) && warlordTracker.Observations.Single().ObservedCopies == 1,
            "A graveyard replay was charged as a second starting Warlord.");
        Console.WriteLine("  Rider: 1 observed + 1 inferred → 2 observed; repeated frames, origins, separate sides and copy ceiling verified.");
    }

    private static void Recordings(string root)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };
        var reports = new List<object>();
        foreach (var session in new[] { "20260827-184848" })
        {
            var copies = new ThinningCopyTracker(); var origins = new PlayProvenanceResolver(); var mutations = new DeckMutationLedger();
            var user = new LiveDeckTracker(PlayerSide.User); var opponent = new LiveDeckTracker(PlayerSide.Opponent);
            var frames = 0; var pairFrames = 0; var promotions = new List<string>();
            foreach (var line in File.ReadLines(Path.Combine(root, "GwentCompanion/sessions", session, "vision-observations.jsonl")))
            {
                var frame = JsonSerializer.Deserialize<CardVisionResult>(line, options)!;
                foreach (var evidence in frame.Events)
                {
                    var sight = evidence.Sighting; var tracker = sight.Side == PlayerSide.User ? user : opponent;
                    mutations.Observe(evidence, user.Faction, opponent.Faction);
                    var origin = mutations.OriginRisk(sight, evidence.ObservedAt, tracker.Observations) ?? origins.Observe(evidence);
                    tracker.ConsiderDirectPlay(sight.Card, 1 - sight.Distance, evidence.ObservedAt, evidence.Description, origin.Provenance);
                    copies.ObserveEvent(evidence, origin.Provenance);
                }
                if (frame.Sightings.Any(sight => CompanionCardRules.ThinningPairs.Contains(sight.Card.Id))) pairFrames++;
                if (copies.ObserveFrame(frame.SampledAt, frame.Screen, frame.Sightings, frame.BoardWasScanned, [user, opponent],
                    sight => mutations.OriginRisk(sight, frame.SampledAt, [], additionalCopy: true) is not null || origins.HasCopyRisk(sight, frame.SampledAt)))
                    promotions.Add(frame.SampledAt.ToString("O"));
                foreach (var tracker in new[] { user, opponent })
                {
                    var projection = new OpponentDeckProjector().Build([], tracker.DeckBuildingObservations, tracker.Faction);
                    Check(projection.Slots.Count(slot => slot.State == DeckSlotState.Observed) == projection.ObservedCopies, "Saved replay doubled inferred/observed slots.");
                    Check(StartingDeckRules.ProbableProvisionLowerBound(tracker.DeckBuildingObservations) ==
                        projection.Slots.Where(slot => slot.State == DeckSlotState.Observed).Sum(slot => slot.Card!.Provision), "Saved replay charged guesses against original provision floor.");
                }
                frames++;
            }
            Check(promotions.Count == 0, "Saved Sentinel case includes a leader-created body; two recognized bodies cannot establish two originals.");
            reports.Add(new { Session = session, Frames = frames, FramesWithPairIdentity = pairFrames, PromotionTimes = promotions,
                Note = "Saved recognition snapshots; checks integration/accounting, not new pixel detection. Synthetic tests cover positive Rider arrivals; these recordings do not establish Rider recall." });
            Console.WriteLine($"  Copy replay {session}: {frames} frames, {pairFrames} with a thinning-pair identity, {promotions.Count} corroborated copy promotions.");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.18-copy-replay.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }
}
