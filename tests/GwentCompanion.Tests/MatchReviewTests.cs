using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class MatchReviewTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Filters()
    {
        var cards = Enumerable.Range(0, 25).Select(i => new DeckCard(new CardDefinition("f" + i, "Card " + i, "Scoia'tael", CardKind.Unit, 5))).ToArray();
        var deck = new DeckDefinition("a", "A", "Scoia'tael", "Nature's Gift", 15, cards,
            Patches: [new("14.8", true, "fixture"), new("14.7", false, "fixture")]);
        Check(new DeckSearchFilters(DeckCategoryFilter.Devotion | DeckCategoryFilter.Singleton | DeckCategoryFilter.Renfri | DeckCategoryFilter.GoldenNekker | DeckCategoryFilter.Musicians, "14.7").Matches(deck), "AND filters retain merged patch history.");
        var neutral = deck with { Cards = cards.Select((c, i) => i == 0 ? c with { Card = c.Card with { Faction = "Neutral", Kind = CardKind.Special } } : c).ToArray() };
        Check(!new DeckSearchFilters(DeckCategoryFilter.Devotion).Matches(neutral), "Neutral specials break Devotion.");
        Check(!new DeckSearchFilters(DeckCategoryFilter.Renfri).Matches(neutral), "24 units is not Renfri valid.");
        Check(!new DeckSearchFilters(Patch: "14.6").Matches(deck), "Patch mismatch excluded.");
        Check(new DeckSearchFilters(Patch: "14.8", IncludeUnknown: true).Matches(null), "Unknown patch opt-in.");
        Check(!new DeckSearchFilters(DeckCategoryFilter.Devotion).Matches(null, incomplete: true), "Incomplete lists do not prove requirements.");
        Check(new DeckSearchFilters(DeckCategoryFilter.Devotion, IncludeUnknown: true).Matches(null, incomplete: true), "Incomplete unknown opt-in.");
        Check(new DeckSearchFilters(Completeness: DeckCompletenessFilter.Incomplete).Matches(null, incomplete: true) &&
            !new DeckSearchFilters(Completeness: DeckCompletenessFilter.Complete).Matches(null, incomplete: true), "Completeness distinguishes partials.");
        Check(!new DeckSearchFilters(Completeness: DeckCompletenessFilter.Uncached).Matches(null, incomplete: true), "Partials are not uncached links.");
    }
    public static void Inference(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(c => c.Id);
        var at = DateTimeOffset.UtcNow; var knowledge = new OpponentKnowledge();
        var oak = new ObservedCard(catalog["202680"], CardProvenance.ProbableStartingDeck, .9, at, "Preview");
        Check(knowledge.Assess([oak]).Devotion.State == ConstraintState.Possible, "Oakcritters alone is not proof.");
        knowledge.Suggest(DeckCondition.Devotion, at, "Effect cue");
        Check(knowledge.Assess([oak]).Devotion.State == ConstraintState.Likely, "Cue sets Likely.");
        var neutral = new ObservedCard(catalog["112210"], CardProvenance.ProbableStartingDeck, .9, at, "Original");
        Check(knowledge.Assess([oak, neutral]).Devotion.State == ConstraintState.RuledOut, "Weak cue cannot erase neutral evidence.");
        var fillers = Enumerable.Range(0, 24).Select(i => new DeckCard(new CardDefinition("filler" + i, "Filler " + i, "Scoia'tael", CardKind.Unit, 4))).ToArray();
        var d = new DeckDefinition("dev", "Dev", "Scoia'tael", "", 0, fillers.Prepend(new(catalog["202680"])).ToArray(), LastEdited: at);
        var n = d with { Id = "neutral", Cards = fillers.Skip(1).Concat([new(catalog["202680"]), new(catalog["112210"])]).ToArray() };
        var meta = new DeckMetaAnalyzer().Analyze([d, n], [oak], "Scoia'tael", knowledge.Assess([oak]));
        Check(meta.RankedDecks.All(x => x.Score > 0) && meta.RankedDecks.First().Deck.Id == "dev", "Likely Devotion never removes neutral candidates.");
        var baseline = new DeckMetaAnalyzer().Analyze([d, n], [oak], "Scoia'tael");
        Check(meta.Cards.Single(c => c.Card.Id == "112210").ConditionalPresence < baseline.Cards.Single(c => c.Card.Id == "112210").ConditionalPresence,
            "Archetype evidence must change hypothesized card probabilities, not merely deck badges.");
        knowledge.Resolve(DeckCondition.Devotion, at, "Reviewed effect"); knowledge.Suggest(DeckCondition.Devotion, at, "Later cue");
        Check(knowledge.Assess([oak]).Devotion.State == ConstraintState.Confirmed, "Cue cannot downgrade confirmation.");
        Check(new DeckMetaAnalyzer().Analyze([d, n], [oak], "Scoia'tael", knowledge.Assess([oak])).Cards.Single(c => c.Card.Id == "112210").ConditionalPresence == 0,
            "Confirmed Devotion excludes a neutral hypothesis.");
        var resolver = new PlayProvenanceResolver();
        VisionEvidenceEvent E(string id, int sec, CardSightSource source = CardSightSource.PlayPreview) =>
            new(at.AddSeconds(sec), new(catalog[id], PlayerSide.Opponent, source, new(.8, .1, .9, .4), .1, 1), "Fixture");
        var elf = catalog.Values.First(c => c.Name == "Elven Wardancer").Id;
        foreach (var pair in new[] { ("203279", "200293"), ("203220", elf), ("203047", elf) })
        {
            resolver.Reset(); resolver.Observe(E(pair.Item1, 0));
            Check(resolver.Observe(E(pair.Item2, 4)).Provenance == CardProvenance.Unknown, "Creation not original: " + pair);
            Check(resolver.Observe(E(pair.Item2, 40, CardSightSource.History)).Provenance == CardProvenance.Unknown, "History cannot launder creation origin.");
            Check(resolver.Observe(E(pair.Item2, 70)).Provenance == CardProvenance.Unknown, "Another preview without independent copy evidence cannot erase creation uncertainty.");
        }
        resolver.Reset(); resolver.Observe(E("203279", 0));
        Check(resolver.Observe(E("202473", 45)).Provenance == CardProvenance.Unknown, "Pass Order may create Tempering later.");
        resolver.Reset(); resolver.Observe(E("201615", 0));
        Check(resolver.Observe(E(elf, 5)).Provenance == CardProvenance.ProbableStartingDeck, "Tutor is not creation.");
    }
    public static void DevotionEffects(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(c => c.Id);
        var at = DateTimeOffset.UtcNow; var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        VisionEvidenceEvent E(string id, CardSightSource source = CardSightSource.PlayPreview) => new(at,
            new(cards[id], PlayerSide.Opponent, source, new(.4, .2, .5, .4), .1, 1), "Fixture");
        foreach (var family in EvolvingCardCatalog.Families)
        {
            var detector = new DevotionEvidenceTracker();
            Check(detector.ObserveEvent(E(family.SecondId), cards[family.SecondId].Faction) is null, "Second-stage evolution needs no Devotion.");
            if (family.FinalProvesDevotion)
                Check(detector.ObserveEvent(E(family.FinalId), cards[family.FinalId].Faction) is not null, "Final-stage Devotion clue: " + family.FinalId);
            else
                Check(detector.ObserveEvent(E(family.FinalId), cards[family.FinalId].Faction) is null, "Two-form transformation is not a Devotion clue: " + family.FinalId);
            Check(detector.ObserveEvent(E(family.FinalId), "wrong faction") is null, "Cross-faction copied form is not an opponent Devotion clue.");
            Check(detector.ObserveEvent(E(family.FinalId), cards[family.FinalId].Faction, CardProvenance.Copied) is null, "Known copied final form is not Devotion evidence.");
            var mapped = EvolvingCardCatalog.StartingIdentity(E(family.FinalId).Sighting, new(CardProvenance.Spawned, "NonOwnable"), cards.Values, cards[family.FinalId].Faction);
            Check(mapped.Card.Id == family.StartingId && mapped.Origin.Provenance == CardProvenance.ProbableStartingDeck, "Evolved form maps to one original.");
            mapped = EvolvingCardCatalog.StartingIdentity(E(family.FinalId).Sighting, new(CardProvenance.Created, "Known copy"), cards.Values, cards[family.FinalId].Faction);
            Check(mapped.Origin.Provenance == CardProvenance.Created, "Known creation origin preserved.");
        }
        var conqueror = new DevotionEvidenceTracker(); var sight = E("202614", CardSightSource.Board).Sighting;
        Check(conqueror.ObserveFrame(at, board, [sight], true) is null, "Summoned Conqueror without preview proves nothing.");
        conqueror.Reset(); conqueror.ObserveEvent(E("202614"), "Monsters");
        Check(conqueror.ObserveFrame(at.AddSeconds(1), board, [sight], true) is null, "Deploy animation can show a dying Conqueror.");
        Check(conqueror.ObserveFrame(at.AddSeconds(4), board, [sight], true) is null, "One settled scan not enough.");
        Check(conqueror.ObserveFrame(at.AddSeconds(5), board, [sight], true) is not null, "Repeated post-deploy survival gives Likely.");
        conqueror.Reset(); conqueror.ObserveEvent(E("202614"), "Monsters");
        Check(conqueror.ObserveFrame(at.AddSeconds(4), board, [], true) is null && conqueror.ObserveFrame(at.AddSeconds(20), board, [sight], true) is null, "Death/expired window does not trigger.");
    }
    public static async Task TitlesAsync(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        using var screenReader = new ScreenStateRecognizer(); var reader = new PreviewTitleRecognizer(catalog);
        var folder = Path.Combine(root, "GwentCompanion/sessions/20260827-184848");
        // Every opponent preview event, including duplicate episodes and a hover trap.
        var events = SavedVisionEvents.Read(Path.Combine(folder, "vision-observations.jsonl"), catalog)
            .Where(e => e.Sighting.Side == PlayerSide.Opponent && e.Sighting.Source == CardSightSource.PlayPreview).ToArray();
        var accepted = new List<VisionEvidenceEvent>();
        foreach (var e in events)
        {
            var stamp = e.ObservedAt.ToString("HHmmssfff");
            var frame = OakEffectProbe.Load(Directory.GetFiles(folder, "frame-*-" + stamp + ".jpg").Single());
            var screen = await screenReader.AnalyzeAsync(frame);
            var sightings = await reader.RecognizeAsync(frame, screen, screenReader);
            var want = stamp == "185300786" ? null : e.Sighting.Card.Id;
            var hasTitle = e.Sighting.Evidence?.StartsWith("Exact visible preview title", StringComparison.Ordinal) == true;
            Check(want is null ? sightings.Count == 0 : (!hasTitle && sightings.Count == 0) || sightings.Any(s => s.Card.Id == want && s.Side == PlayerSide.Opponent),
                $"Latest title {stamp}: expected {want ?? "no title"}, got {string.Join(',', sightings.Select(s => s.Card.Name))}");
            if (want is not null) accepted.Add(e);
        }
        Console.WriteLine($"Latest match: {events.Length} fresh preview-title checks including hover false-positive exclusion.");
        var resolver = new PlayProvenanceResolver(); var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        foreach (var e in accepted)
        {
            var origin = resolver.Observe(e);
            tracker.ConsiderDirectPlay(e.Sighting.Card, 1 - e.Sighting.Distance, e.ObservedAt, e.Description + " " + origin.Reason, origin.Provenance);
        }
        foreach (var name in new[] { "Dwarven Agitator", "Tempering", "Dol Blathanna Bomber", "Elven Wardancer" })
            Check(tracker.Observations.Single(c => c.Card.Name == name).Provenance == CardProvenance.Unknown, "Generated-origin exclusion: " + name);
        var report = new { Session = "20260827-184848", PreviewEvents = events.Length, ExcludedHoverEvents = 1,
            StartingIdentities = tracker.DeckBuildingObservations.Count, ProvisionFloor = StartingDeckRules.ProbableProvisionLowerBound(tracker.DeckBuildingObservations),
            Cards = tracker.Observations.Select(c => new { c.Card.Id, c.Card.Name, Provenance = c.Provenance.ToString(), c.ObservedCopies }),
            KnownMisses = new[] { "Miner (202474), visible board by 18:49:57", "Elven Swordmaster (200535), generated after Harvest, board 18:56:53" },
            Scope = "Fresh OCR attribution checks on all 22 saved opponent preview-event frames; artwork-only events reuse saved identity. One verified board-hover title rejected. Provenance replay is not full-match pixel detection or exact copy counts." };
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.13-match-audit.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
