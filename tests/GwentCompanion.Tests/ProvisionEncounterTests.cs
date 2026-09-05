using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class ProvisionEncounterTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        CardDefinition Card(string name) => catalog.First(c => c.Name == name);
        var at = DateTimeOffset.Parse("2026-08-28T15:00:00-04:00");
        var reference = new DeckDefinition("own", "Brewess pair", "Monsters", "Fruits of Ysgith", 15,
            [new(Card("Brewess: Ritual")), new(Card("Rotfiend"), 2), new(Card("Fiend"), 2)]);
        var player = new LiveDeckTracker(PlayerSide.User); var copies = new ThinningCopyTracker();
        var state = new GameStateTracker(); state.Reset("copies", reference); var values = new LiveValueLedger();
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        var sightings = new[] { new CardSighting(Card("Rotfiend"), PlayerSide.User, CardSightSource.Board, new(.4,.69,.46,.86), .1, 1, "fixture"),
            new CardSighting(Card("Rotfiend"), PlayerSide.User, CardSightSource.Board, new(.52,.69,.58,.86), .1, 1, "fixture") };
        var palePair = screen with { HasCardTooltip = true, TooltipConfidence = .2, TooltipRegion = new(.4,.70,.6,.87) };
        Check(!BoardOcclusionVerifier.Refine(palePair, sightings, null).HasCardTooltip, "Pale pair incorrectly masks the board");
        Check(BoardOcclusionVerifier.Refine(palePair, [sightings[0], sightings[0]], null).HasCardTooltip &&
            BoardOcclusionVerifier.Refine(palePair, sightings, Card("Rotfiend")).HasCardTooltip &&
            BoardOcclusionVerifier.Refine(palePair with { TooltipConfidence = .4 }, sightings, null).HasCardTooltip,
            "Duplicate templates / confirmed tooltip / strong paper panel incorrectly dismissed");
        void Tick(DateTimeOffset time, IReadOnlyList<CardSighting> seen, bool risk = false)
        {
            copies.ObserveFrame(time, screen, seen, true, [player], _ => risk, reference);
            values.Observe(state.Observe(new(time, screen, seen, [], true)), catalog, reference.Cards.Select(c => c.Card), []);
        }
        Tick(at, sightings); Check(player.Observations.Count == 0, "One scan became a confirmed player copy");
        Tick(at.AddSeconds(1), sightings);
        Check(player.Observations.Single().ObservedCopies == 2, "Brewess's simultaneous bronze summons not accounted for without a play preview");
        var usage = LiveValueLedger.Provisions(player.Observations, reference, null, values.Spent(PlayerSide.User), values.SpentCopies(PlayerSide.User));
        Check(usage.SpentFloor == 10 && usage.CommittedCards == 2 && usage.UnaccountedCards == 3 && usage.RemainingCeiling == 19,
            "Two 5p Rotfiends not charged as two distinct original slots");
        Check(Math.Abs(usage.ProvisionsPerCard!.Value - 19d / 3) < .001 && !usage.AssumedSize, "Known deck average wrong");
        Tick(at.AddSeconds(20), sightings); Tick(at.AddSeconds(21), sightings);
        Check(LiveValueLedger.Provisions(player.Observations, reference, null, values.Spent(PlayerSide.User), values.SpentCopies(PlayerSide.User)).SpentFloor == 10,
            "Reacquired artwork spent Rotfiends twice");
        player.Reset(); copies.Reset(); Tick(at.AddSeconds(22), sightings, true); Tick(at.AddSeconds(23), sightings, true);
        Check(player.Observations.Count == 0, "Known creation route mistaken for additional originals");
        player.Reset(); copies.Reset(); Tick(at.AddSeconds(24), sightings); Tick(at.AddSeconds(25), [sightings[0]]);
        Check(player.Observations.Single().ObservedCopies == 1, "Sequential sightings synthesized simultaneous copies");
        player.Reset(); copies.Reset(); Tick(at.AddSeconds(40), sightings); Tick(at.AddSeconds(43), []);
        Tick(at.AddSeconds(52), sightings);
        Check(player.Observations.Single().ObservedCopies == 2, "Intermittent partial scans erased a corroborated simultaneous pair");
        player.Reset(); copies.Reset(); Tick(at.AddSeconds(60), sightings); Tick(at.AddSeconds(90), sightings);
        Check(player.Observations.Count == 0, "Stale two-copy claim crossed the rolling window");
        player.Reset(); copies.Reset();
        var preview = new CardSighting(Card("Griffin"), PlayerSide.Opponent, CardSightSource.PlayPreview, new(.815,.137,.914,.399), .1, 1);
        Tick(at.AddSeconds(100), [..sightings,preview]); Tick(at.AddSeconds(101), [..sightings,preview]);
        Check(player.Observations.Single().ObservedCopies == 2, "Unrelated opponent preview suppressed unobscured player summons");
        player.Reset(); copies.Reset();
        preview = preview with { Side=PlayerSide.User, Region=new(.815,.413,.914,.665) };
        var covered = sightings.Select(s => s with {Region = s.Region with {Top=.50,Bottom=.65,Left=s.Region.Left+.15,Right=s.Region.Right+.15}}).ToArray();
        Tick(at.AddSeconds(105), [..covered,preview]); Tick(at.AddSeconds(106), [..covered,preview]);
        Check(!player.Observations.Any(o=>o.ObservedCopies>1), "Preview-covered artwork became a corroborated pair");
        var staleCost = reference.Cards.First(c=>c.Card.Name=="Rotfiend").Card with { Provision=7 };
        var consistent = LiveValueLedger.Provisions([new(staleCost,CardProvenance.ConfirmedStartingDeck,1,at,ObservedCopies:2)],reference,null);
        Check(consistent.SpentFloor==10 && consistent.Total==29, "Mixed card-cost snapshots corrupted reference provision accounting");
        var oneEventTwoPhysical = LiveValueLedger.Provisions([new(Card("Rotfiend"),CardProvenance.ConfirmedStartingDeck,1,at,ObservedCopies:2)],
            reference,null,new HashSet<string>{Card("Rotfiend").Id},new Dictionary<string,int>{{Card("Rotfiend").Id,1}});
        Check(oneEventTwoPhysical.SpentFloor==10 && oneEventTwoPhysical.CommittedCards==2,
            "A corroborated second original copy was discarded because the generic event ledger saw one preview");
        var unknown = LiveValueLedger.Provisions([], null, 165);
        Check(unknown.AssumedSize && unknown.UnaccountedCards == 25 && unknown.ProvisionsPerCard == 6.6, "Unknown-size assumption hidden");
        var all = LiveValueLedger.Provisions(reference.Cards.Select(c => new ObservedCard(c.Card, CardProvenance.ConfirmedStartingDeck, 1, at, ObservedCopies:c.Count)), reference, null);
        Check(all.UnaccountedCards == 0 && all.ProvisionsPerCard is null, "Empty remainder divides by zero");
        var committedWithoutTracker = LiveValueLedger.Provisions([], reference, null,
            new HashSet<string> { reference.Cards[0].Card.Id }, new Dictionary<string, int> { [reference.Cards[0].Card.Id] = 1 });
        Check(committedWithoutTracker.SpentFloor == reference.Cards[0].Card.Provision && committedWithoutTracker.CommittedCards == 1,
            "Known player reference failed to bridge a commitment that arrived ahead of provenance tracking");
        var tyr = Card("Tyr: Slayer of Yngvar"); var evolvedTyr = Card("Tyr: Master of An Skellig");
        Check(EvolvingCardCatalog.StartingId(evolvedTyr.Id) == tyr.Id, "Tyr's transformed artwork lost its starting identity");
        var evolutionLedger = new LiveValueLedger(); var beforeEvolution = new GameStateTracker().Current;
        var afterEvolution = beforeEvolution with { SessionId = "evolution", At = at, Phase = GamePhase.Playing };
        evolutionLedger.Observe(new(beforeEvolution, afterEvolution,
            [new("tyr-play", at, "PlayPreview", PlayerSide.Opponent, evolvedTyr.Id, null, "fixture")], true), catalog, [], []);
        var tyrUsage = LiveValueLedger.Provisions([new(tyr, CardProvenance.ProbableStartingDeck, 1, at)], null, 168,
            evolutionLedger.Spent(PlayerSide.Opponent), evolutionLedger.SpentCopies(PlayerSide.Opponent));
        Check(tyrUsage.SpentFloor == tyr.Provision && evolutionLedger.Spent(PlayerSide.Opponent).Contains(tyr.Id),
            "Transformed Tyr did not charge the original starting slot/provisions");
        var sunset = Card("Sunset Wanderers"); values.ConfirmWanderersInHand(PlayerSide.Opponent, sunset);
        Check(values.Growth(PlayerSide.Opponent, sunset.Id)?.Minimum == 1, "Sunset printed baseline");
        values.ObserveWanderersMovement("shift-1", PlayerSide.Opponent, sunset);
        values.ObserveWanderersMovement("shift-1", PlayerSide.Opponent, sunset);
        Check(values.Growth(PlayerSide.Opponent, sunset.Id) is { Minimum: 2, Maximum: null }, "Repeated frames counted twice or unknown earlier movement invented");
        values.RecordGrowth(new(PlayerSide.Opponent, sunset.Id, sunset.Name, 8, 8, "power", "Reviewed"));
        values.ObserveWanderersMovement("shift-2", PlayerSide.Opponent, sunset);
        Check(values.Growth(PlayerSide.Opponent, sunset.Id) is { Minimum: 9, Maximum: 9 }, "Sunset exact reading not advanced by verified shift");
        values.ForgetGrowth(PlayerSide.Opponent, sunset.Id); Check(values.Growth(PlayerSide.Opponent, sunset.Id) is null, "Sunset undo failed");

        // A real 25-card library list, with synthetic IDs only for deliberately incompatible decoys.
        var saved = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(root, "GwentCompanion/sessions/20260828-154538/game-state-final.json")), GameStateJournal.Json)!;
        var target = saved.User.StartingDeckReference!;
        var selected = target.Cards.Take(11).Select(c => new ObservedCard(c.Card, CardProvenance.ProbableStartingDeck, .9, at, "observed", c.Count)).ToArray();
        var decoys = Enumerable.Range(0, 8).Select(i => target with { Id = "decoy" + i,
            Cards = target.Cards.Select(c => new DeckCard(c.Card with { Id = c.Card.Id + "-" + i }, c.Count)).ToArray() });
        var library = new[] { target }.Concat(decoys).ToArray();
        LearnedOpponentEncounter Encounter(string id, IReadOnlyList<ObservedCard>? cards = null) => new(id, at, target.Faction, target.Leader,
            target.LeaderProvisionBonus, null, 25, 25, cards ?? selected, [], OpponentProvisionCalculator.Calculate(cards ?? selected, 165, 25),
            MatchMmr:2406, PostMatchMmr:new(2406, null, true, "Faction", 2431));
        var memory = new OpponentDeckMemoryStore();
        var reviewedStore = new OpponentDeckMemoryStore();
        var completeEncounter = Encounter("reviewed") with { Cards = target.Cards.Select(c => new ObservedCard(c.Card,
            CardProvenance.ConfirmedStartingDeck, 1, at, ObservedCopies:c.Count)).ToArray(), StratagemId = target.Stratagem?.Id };
        var reviewedRecord = reviewedStore.Record(completeEncounter, "Existing reviewed identity", reviewedComplete:true);
        var reviewedAgain = OpponentEncounterPipeline.Capture(reviewedStore, Encounter("reviewed-again"), library);
        Check(reviewedStore.Records.Count == 1 && reviewedAgain.Record.Id == reviewedRecord.Id && reviewedAgain.Record.EncounterCount == 2,
            "Existing reviewed complete identity duplicated instead of receiving a new encounter");
        var captured = OpponentEncounterPipeline.Capture(memory, Encounter("first"), library);
        Check(captured.LibraryObservation is not null && !captured.Record.Complete && !captured.Record.NeedsReview && captured.Record.Cards.Count == selected.Length,
            "Strong association failed or unseen library cards became observations");
        var deckLibrary = new DeckLibrary(); deckLibrary.Merge(library); deckLibrary.Merge([captured.LibraryObservation!]);
        var repeated = OpponentEncounterPipeline.Capture(memory, Encounter("first") with { At = at.AddDays(3) }, library);
        deckLibrary.Merge([repeated.LibraryObservation!]);
        Check(memory.Records.Count == 1 && repeated.Record.EncounterCount == 1 && repeated.Record.FirstSeen == at &&
            deckLibrary.Find(target.Id)!.Deck.Occurrences!.Count(o => o.Id == "match-first") == 1, "MMR repeated capture inflated/rejuvenated match");
        var next = OpponentEncounterPipeline.Capture(memory, Encounter("second") with { At = at.AddMonths(1) }, library);
        Check(next.Record.EncounterCount == 2 && next.Record.Encounters.Select(e => e.Patch).Distinct().Count() == 2, "Frequency or patch history lost");
        Check(new OpponentEncounterPrior(memory.Records, 2406, library:library).Weight(target, at.AddMonths(1)) > 1,
            "Inferred encounter never contributes to recommendation prevalence");
        Check(new OpponentEncounterPrior(memory.Records, 2406, "first", library).Weight(target, at) < new OpponentEncounterPrior(memory.Records, 2406, library:library).Weight(target, at), "Current match leaked into own prior");
        var rawCount = memory.Records[0].Encounters[0].Cards.Count;
        memory.Review(captured.Record.Id, "My corrected deck", selected.Skip(1).ToArray(), true);
        deckLibrary.RemoveInferredEncounters(["first", "second"]);
        Check(memory.Records[0].Cards.Count == selected.Length - 1 && memory.Records[0].Encounters[0].Cards.Count == rawCount &&
            !deckLibrary.Find(target.Id)!.Deck.Occurrences!.Any(o => o.Id == "match-first"), "Correction destroyed raw evidence or failed to retract association");
        OpponentEncounterPipeline.Capture(memory, Encounter("first"), library);
        Check(memory.Records[0].Cards.Count == selected.Length - 1, "Result reread undid reviewed correction");
        var ambiguous = target with { Id = "variant", Cards = target.Cards.Select((c,i) => i == target.Cards.Count - 1 ?
            new DeckCard(Card("Wild Hunt Rider"), c.Count) : c).ToArray() };
        var uncertain = OpponentEncounterPipeline.Capture(new(), Encounter("ambiguous"), library.Append(ambiguous).ToArray());
        Check(uncertain.LibraryObservation is null && uncertain.Record.NeedsReview, "Unseen variant guessed as exact observed identity");
        var expanded = target.Cards.SelectMany(card => Enumerable.Repeat(card.Card, card.Count)).ToArray();
        var alternatives = catalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact &&
            FactionCompatibility.IsPlayableBy(card, target.Faction) && target.CountOf(card.Id) == 0).Take(3).ToArray();
        var closeCards = expanded.Take(18).Concat(alternatives).GroupBy(card => card.Id)
            .Select(group => new ObservedCard(group.First(), CardProvenance.ProbableStartingDeck, .95, at, "fixture", group.Count())).ToArray();
        var close = OpponentEncounterPipeline.Capture(new(), Encounter("close-analogue", closeCards), library);
        var closeOverlap = new DeckVariationSimilarity(library, closeCards.Select(c => c.Card)).Compare(closeCards.Select(c => new DeckCard(c.Card, c.ObservedCopies)), target.Cards);
        var closeCoverage = closeOverlap.SharedProvisions / (double)closeOverlap.LeftProvisions;
        Check(closeCoverage < .9 && close.LibraryObservation is null && close.Record.NeedsReview,
            "An 18/21 copy match with too much unmatched provision spend was associated as a close analogue.");
        Console.WriteLine($"  Former 18/21 synthetic analogue: {closeOverlap.SharedProvisions}/{closeOverlap.LeftProvisions}p ({closeCoverage:P1}); correctly remains partial under 90% spend coverage.");
        var cheapAlternative = catalog.First(card => card.CanBeInStartingDeck && card.Kind == CardKind.Unit && card.Provision == 4 &&
            FactionCompatibility.IsPlayableBy(card, target.Faction) && target.CountOf(card.Id) == 0);
        var cheapCards = expanded.Take(20).Append(cheapAlternative).GroupBy(card => card.Id)
            .Select(g => new ObservedCard(g.First(), CardProvenance.ProbableStartingDeck, .95, at, "cheap swap fixture", g.Count())).ToArray();
        var cheapCapture = OpponentEncounterPipeline.Capture(new(), Encounter("cheap-analogue", cheapCards), library);
        Check(cheapCapture.LibraryObservation is not null && !cheapCapture.Record.Complete && cheapCapture.Record.AssociationReason!.Contains("observed spend"),
            "A dominant inexpensive substitution was not retained as a provision-weighted partial analogue.");
        var liveMemoryPath = Path.Combine(root, "GwentCompanion/cache/opponent-memory.json");
        var latestSession = "20260829-130240";
        if (File.Exists(liveMemoryPath) && OpponentDeckMemoryStore.Load(liveMemoryPath).Records
            .SelectMany(record => record.Encounters).FirstOrDefault(encounter => encounter.SessionId == latestSession) is { } latestEncounter)
        {
            var liveLibrary = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json")).Decks;
            var latestCapture = OpponentEncounterPipeline.Capture(new(), latestEncounter, liveLibrary);
            var latestMetric = new DeckVariationSimilarity(liveLibrary, latestEncounter.Cards.Select(c => c.Card));
            var latestCards = latestEncounter.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)).ToArray();
            var bestSpend = liveLibrary.Where(d => (latestEncounter.Faction is null || d.Faction == latestEncounter.Faction) &&
                    (latestEncounter.StartingLeader is null || d.Leader == latestEncounter.StartingLeader))
                .Select(d => latestMetric.Compare(latestCards, d.Cards)).Select(o => o.SharedProvisions / (double)o.LeftProvisions).DefaultIfEmpty(0).Max();
            Check(latestCapture.Record.Cards.Sum(c => c.ObservedCopies) == latestEncounter.Cards.Sum(c => c.ObservedCopies) && !latestCapture.Record.Complete &&
                (bestSpend < .9 ? latestCapture.LibraryObservation is null : true), "Recorded partial evidence was promoted/lost or linked below the provision boundary.");
            Console.WriteLine($"  Recorded Nature's Gift example: best observed-spend overlap {bestSpend:P1}; association {(latestCapture.LibraryObservation is null ? "withheld" : "retained")}; raw cards unchanged.");
        }
        var partialStore = new OpponentDeckMemoryStore();
        var partial = OpponentEncounterPipeline.Capture(partialStore, Encounter("p1"), []);
        var extended = selected.Append(new ObservedCard(target.Cards[12].Card, CardProvenance.ProbableStartingDeck, .9, at)).ToArray();
        var merged = OpponentEncounterPipeline.Capture(partialStore, Encounter("p2", extended), []);
        Check(partialStore.Records.Count == 1 && merged.Record.Cards.Count == extended.Length && merged.Record.EncounterCount == 2, "Compatible partial observations did not fill gaps");
        var sharedCheap = Enumerable.Range(0, 11).Select(i => new ObservedCard(new CardDefinition("partial-cheap-" + i,
            "Partial bronze " + i, target.Faction, CardKind.Unit, 5), CardProvenance.ProbableStartingDeck, 1, at)).ToArray();
        var expensiveA = new ObservedCard(new CardDefinition("partial-gold-a", "Partial gold A", target.Faction, CardKind.Unit, 15, IsGold: true), CardProvenance.ProbableStartingDeck, 1, at);
        var expensiveB = expensiveA with { Card = expensiveA.Card with { Id = "partial-gold-b", Name = "Partial gold B" } };
        var distinctPartials = new OpponentDeckMemoryStore();
        OpponentEncounterPipeline.Capture(distinctPartials, Encounter("weighted-partial-a", sharedCheap.Append(expensiveA).ToArray()), []);
        OpponentEncounterPipeline.Capture(distinctPartials, Encounter("weighted-partial-b", sharedCheap.Append(expensiveB).ToArray()), []);
        Check(distinctPartials.Records.Count == 2, "11/12 shared cheap copies hid an expensive disagreement and merged distinct partials.");
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.30-memory-tests"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "memory.json"); partialStore.Save(path);
        var loaded = OpponentDeckMemoryStore.Load(path);
        Check(loaded.Records.Single().NeedsReview && loaded.Records.Single().EncounterCount == 2 && loaded.Records.Single().Encounters.All(e => e.PostMatchMmr?.SeasonPeak == 2431), "Pending review/MMR not durable");
        var originalCards = loaded.Records.Single().Cards.Count;
        var suggested = loaded.Suggest(loaded.Records.Single().Id, [new(Card("Wild Hunt Rider"), 2)]);
        Check(suggested.Cards.Count == originalCards && suggested.DraftCards.Any(c => c.Card.Name == "Wild Hunt Rider" && c.Provenance == CardProvenance.Unknown),
            "Quiet-cache guesses contaminated observed composition");
        loaded.Save(path); loaded = OpponentDeckMemoryStore.Load(path);
        Check(loaded.Records.Single().SuggestedCards?.Single().Count == 2, "Quiet-cache suggestions not persisted");
        var ui = File.ReadAllText(Path.Combine(root,"GwentCompanion/src/GwentCompanion.App/MainWindow.xaml"));
        Check(ui.Contains("ReviewNewDecksChoice") && ui.Contains("Review new opponent decks after matches"), "Review preference missing");
        Console.WriteLine("PASS provision copies/averages, Sunset growth, MMR capture, inferred prior, idempotency, variants, partial merge, corrections and persistence.");
    }

    public static void Pixels(string root, bool baseline, string session = "20260828-154538", string? fromStamp = null, string? toStamp = null)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var directory = Path.Combine(root, "GwentCompanion/sessions", session);
        var saved = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(directory, "game-state-final.json")), GameStateJournal.Json)!;
        var reference = saved.User.StartingDeckReference!;
        var records = File.ReadLines(Path.Combine(directory, "vision-observations.jsonl")).Select(s => JsonSerializer.Deserialize<CardVisionResult>(s, GameStateJournal.Json)!).ToArray();
        var refs = VisionReferenceLibrary.Load(catalog, cache); Console.WriteLine($"Loading {refs.Count} artwork references");
        using var detector = new FeatureCardRecognizer(refs, Path.Combine(cache,"recognition-features"));
        if (!baseline) detector.SetKnownPlayerDeck(reference.Cards.Select(c => c.Card.Id));
        var fallback = new CardFrameRecognizer(new CardArtMatcher(detector.ArtReferences));
        var fileLookup = Directory.GetFiles(directory, "frame-*.jpg").ToDictionary(p => Path.GetFileNameWithoutExtension(p).Split('-')[2]);
        var pixelRows = new List<object>();
        var tracker = new LiveDeckTracker(PlayerSide.User); var copies = new ThinningCopyTracker(); var origins = new PlayProvenanceResolver();
        var state = new GameStateTracker(); state.Reset("recorded-provisions", reference); var ledger = new LiveValueLedger();
        var afterSummons = 0;
        var powerReader = new BoardPowerReader();
        foreach (var original in records)
        {
            var result = original; var stamp = result.SampledAt.ToString("HHmmssfff");
            if (fromStamp is not null && string.CompareOrdinal(stamp, fromStamp) < 0 ||
                toStamp is not null && string.CompareOrdinal(stamp, toStamp) > 0) continue;
            // Re-read every scheduled board scan, excluding the two training-crop seconds.
            // Other frames retain original OCR/preview evidence rather than inventing unsaved detections.
            if (result.BoardWasScanned && !stamp.StartsWith("155006") && !stamp.StartsWith("154850"))
            {
                using var source = File.OpenRead(fileLookup[stamp]);
                var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
                var features = detector.Recognize(frame, result.Screen);
                var fresh = features.Concat(fallback.Recognize(frame, result.Screen, true, features)).ToArray();
                result = result with { Sightings = fresh, Screen = BoardOcclusionVerifier.Refine(result.Screen, fresh, result.HoveredCard) };
                result = result with { CardMeasurements=powerReader.Observe(frame,result.SampledAt,fresh,result.Screen) };
                pixelRows.Add(new { stamp, result.Screen.HasCardTooltip, Cards = fresh.Select(s => new { s.Side, s.Card.Name, s.Region, s.Distance }).ToArray() });
                Console.WriteLine(stamp + " " + string.Join(", ", fresh.Where(s => s.Side == PlayerSide.User).Select(s => s.Card.Name)));
            }
            foreach (var action in result.Events.Where(e => e.Sighting.Side == PlayerSide.User))
            {
                var origin = origins.Observe(action, reference);
                tracker.ConsiderDirectPlay(action.Sighting.Card, 1-action.Sighting.Distance, action.ObservedAt, action.Description + " " + origin.Reason, origin.Provenance);
            }
            copies.ObserveFrame(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, [tracker],
                s => origins.HasCopyRisk(s, result.SampledAt), reference);
            ledger.Observe(GameStateVisionAdapter.Apply(state, result), catalog, reference.Cards.Select(c => c.Card), []);
            if (string.CompareOrdinal(stamp,"155020000") >= 0 && afterSummons == 0)
                afterSummons = tracker.Observations.FirstOrDefault(c => c.Card.Name == "Rotfiend")?.ObservedCopies ?? 0;
        }
        var usage = LiveValueLedger.Provisions(tracker.Observations, reference, null, ledger.Spent(PlayerSide.User), ledger.SpentCopies(PlayerSide.User));
        var output = Path.Combine(root, session=="20260828-154538" ? "GwentCompanion/diagnostics/v0.1.30-provision-" + (baseline ? "baseline" : "pixels") + ".json" :
            "GwentCompanion/diagnostics/v0.1.31-provision-pixels" + (fromStamp is null ? "" : "-window") + ".json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { Scope = "All original records; scheduled board scans re-read except training-crop seconds. Original OCR/previews retained, not a complete streaming rerun", afterSummons, usage,
            Cards = tracker.Observations.Select(c => new { c.Card.Name, c.ObservedCopies, c.Provenance }), pixelRows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Rotfiend copies after Brewess resolution: {afterSummons}; spend floor {usage.SpentFloor}/{usage.Total}p; {output}");
        if (!baseline && session=="20260828-154538") Check(afterSummons == 2, "Held-out Brewess sequence still misses the two summoned Rotfiends");
    }

    public static void NeighborProbe(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache"); var catalog = GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var refs = VisionReferenceLibrary.Load(catalog, cache).Select(r =>
        {
            using var file = File.OpenRead(r.Path);
            return new CardArtReference(r.Card, VisualDescriptor.Create(BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0])));
        }).ToArray();
        var fallback = new CardFrameRecognizer(new CardArtMatcher(refs));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"GwentCompanion/diagnostics/v0.1.30-provision-pixels.json")));
        var folder = Path.Combine(root,"GwentCompanion/sessions/20260828-154538");
        foreach (var row in json.RootElement.GetProperty("pixelRows").EnumerateArray())
        {
            var stamp = row.GetProperty("stamp").GetString()!;
            var sightings = row.GetProperty("Cards").EnumerateArray().Select(c => new CardSighting(catalog.First(d => d.Name == c.GetProperty("Name").GetString()),
                (PlayerSide)c.GetProperty("Side").GetInt32(), CardSightSource.Board, c.GetProperty("Region").Deserialize<NormalizedRegion>(), .1,1)).ToArray();
            using var file = File.OpenRead(Directory.GetFiles(folder,"frame-*-" + stamp + ".jpg").Single());
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
            var result = fallback.Recognize(frame, new(GwentViewKind.Board,false,0,0,null), true, sightings);
            Console.WriteLine(stamp + " extra: " + string.Join(", ", result.Select(s => s.Card.Name + " " + s.Distance.ToString("F2"))));
        }
    }
}
