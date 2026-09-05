using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class OpponentKnowledgeTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CardDefinition Card(string id, int p = 4, string faction = "Northern Realms", bool gold = false) => new(id, id, faction, CardKind.Unit, p, IsGold: gold);
    private static ObservedCard Seen(CardDefinition card, int count = 1) => new(card, CardProvenance.ProbableStartingDeck, .99, At, "test", count);
    private static VisionEvidenceEvent Event(CardDefinition card, PlayerSide side = PlayerSide.Opponent, CardSightSource source = CardSightSource.PlayPreview, int seconds = 1) =>
        new(At.AddSeconds(seconds), new(card, side, source, new(.3, .2, .4, .4), .05, 1, "test"), "test");
    private static GwentVisualObservation Screen(string? header = null, int? hand = null, int? deck = null) => new(GwentViewKind.Board, false, 0, 0, null,
        ScreenHeader: header, OpponentHandCount: hand, OpponentDeckCount: deck, MatchHudVisible: true);
    private static DeckDefinition Deck(string id, params CardDefinition[] cards) => new(id, id, "Northern Realms", "Shieldwall", 15, cards.GroupBy(card => card.Id).Select(group => new DeckCard(group.First(), group.Count())).ToArray());
    private static LearnedOpponentEncounter Encounter(string id, params CardDefinition[] cards) => new(id, At, "Northern Realms", "Shieldwall", 15, null, null, 25,
        cards.Select(card => Seen(card)).ToArray(), [], OpponentProvisionCalculator.Calculate(cards.Select(card => Seen(card)), 165));

    public static void Rules(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        foreach (var rule in DeckInteractionCatalog.All)
            Check(catalog.Single(card => card.Id == rule.CardId).AbilityText!.Contains(rule.AbilityFragment, StringComparison.OrdinalIgnoreCase), "Card text changed; re-review " + rule.Name);
        var index = DeckInteractionIndex.Scan(catalog);
        Check(index.Where(item => item.Tags.Contains("Summon")).All(item => item.ManuallyReviewed), "Every literal Summon interaction in this catalog must have reviewed semantics.");
        Check(catalog.Where(card => card.AbilityText?.Contains("Scenario:", StringComparison.Ordinal) == true)
            .All(card => DeckInteractionCatalog.All.Any(rule => rule.CardId == card.Id)), "Review every scenario, including mixed thinning/generation chapters.");
        Check(index.Count(item => item.ManuallyReviewed) == DeckInteractionCatalog.All.Count, "Reviewed nontechnical card text must not vanish from the interaction index.");
        var stripMarkup = File.ReadAllText(Path.Combine(root, "GwentCompanion", "src", "GwentCompanion.App", "Controls", "DeckCardList.xaml"));
        Check(stripMarkup.Contains("ToolTipService.IsEnabled=\"False\"") && !stripMarkup.Contains("ToolTip=\"{Binding Tooltip}\""), "Card rows must not restore the intrusive hover popup.");
        Check(index.Any(item => item.Id == "200022" && item.Tags.Contains("Reveal / inspect")) && index.Any(item => item.Id == "202334" && item.Tags.Contains("Summon")), "Catalog-wide scan must cover reveal and bronze summons.");
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.9-interaction-index.json"), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Catalog scan: {catalog.Count} identities, {index.Count} interaction entries, {index.Count(item => item.ManuallyReviewed)} reviewed rules.");
        var knowledge = new OpponentKnowledge();
        var radeyah = catalog.Single(card => card.Id == "202478");
        var before = knowledge.Assess([Seen(radeyah)]);
        Check(before.Shupe.State == ConstraintState.Likely && before.Radeyah.State == ConstraintState.Likely, "Radeyah and Shupe share a condition, not proof of activation.");
        knowledge.Resolve(DeckCondition.Singleton, At, "User verified created stratagem");
        Check(knowledge.Assess([Seen(radeyah)]).Shupe.State == ConstraintState.Confirmed, "Verified resolution confirms singleton.");
        Check(knowledge.Assess([Seen(Card("bronze"), 2)]).Shupe.State == ConstraintState.Unknown, "Conflicting duplicate evidence must request review, not silently overrule the resolved effect.");
        knowledge.ClearResolution(DeckCondition.Singleton);
        Check(knowledge.Assess([Seen(Card("bronze"), 2)]).Radeyah.State == ConstraintState.RuledOut, "Undo must restore ordinary copy-based singleton assessment.");
        knowledge.Resolve(DeckCondition.GoldenNekker, At, "Verified deploy chain");
        var gn = catalog.Single(card => card.Id == "203123");
        var nova = catalog.Single(card => card.Id == "201626") with { Provision = 12 };
        Check(knowledge.Assess([Seen(gn), Seen(nova)]).GoldenNekker.State == ConstraintState.Confirmed, "BOTH GN and Nova are provision exceptions, even after provision changes.");
        Check(knowledge.Assess([Seen(Card("big", 12))]).GoldenNekker.State == ConstraintState.Unknown, "Conflicting high-cost origin requires review.");
        knowledge.Reset(); knowledge.Resolve(DeckCondition.Renfri, At, "Verified leader replacement");
        var special = Card("special", 6) with { Kind = CardKind.Special };
        Check(knowledge.MinimumSize([Seen(special)]) == 26, "Renfri with a starting special implies at least 26 cards, not an impossible Renfri deck.");
        knowledge.Reset(); knowledge.Resolve(DeckCondition.Musicians, At, "User verified setup");
        var musicians = catalog.Single(card => card.Id == "202200");
        Check(knowledge.Assess([Seen(musicians)]).Musicians!.State == ConstraintState.Confirmed, "Musicians itself is exempt from no-other-4p.");
        Check(knowledge.Assess([Seen(Card("four"))]).Musicians!.State == ConstraintState.Unknown, "Other 4p originals conflict.");
        knowledge.Reset(); knowledge.ObserveScreen(Screen("ROUND 1"), At); knowledge.ObserveScreen(Screen("ROUND 1", 10, 17), At.AddSeconds(1));
        knowledge.ObserveOpeningCounts("Nilfgaard");
        Check(knowledge.StartingSizeMinimum == 25, "Daerlan additions must not make 27 visible cards a 27-card starting list.");
        knowledge.ObserveOpeningCounts("Scoia'tael"); Check(knowledge.StartingSizeMinimum == 27, "A non-NG verified opening count supports a larger list.");
        knowledge.ObserveScreen(Screen("FINAL ROUND"), At.AddSeconds(2)); knowledge.ObserveScreen(Screen("FINAL ROUND"), At.AddSeconds(3));
        Check(knowledge.Round == 3, "The actual game heading is FINAL ROUND, not ROUND 3.");
        knowledge.ObserveScreen(Screen("ROUND 1"), At.AddSeconds(4)); knowledge.ObserveScreen(Screen("ROUND 1"), At.AddSeconds(5));
        Check(knowledge.Round == 3, "Do not roll backward / combine another game's observations automatically.");
        knowledge.Reset(); knowledge.ObserveScreen(Screen("ROUND 1", deck: 18), At);
        knowledge.ObserveScreen(Screen("ROUND 1", hand: 10), At.AddSeconds(4));
        knowledge.ObserveOpeningCounts("Scoia'tael");
        Check(knowledge.StartingSizeMinimum == 25, "Do not combine hand and draw-pile readings from different moments.");
        knowledge.ObserveScreen(Screen(hand: 10), At.AddSeconds(7));
        Check(knowledge.OpponentDrawPile is null, "Old draw-pile readings must expire, like hand counts.");
        knowledge.Reset(); knowledge.Observe(Event(special));
        knowledge.ObserveScreen(Screen(hand: 10, deck: 15), At.AddSeconds(2));
        Check(!knowledge.ObserveStartingConservation(At.AddSeconds(2), "Northern Realms", 1), "Pre-resolution hand counter became an exact 26-card deck.");
        knowledge.ObserveScreen(Screen(hand: 9, deck: 15), At.AddSeconds(5));
        Check(!knowledge.ObserveStartingConservation(At.AddSeconds(5), "Northern Realms", 1), "One conserved snapshot became exact size.");
        knowledge.ObserveScreen(Screen(hand: 9, deck: 15), At.AddSeconds(6));
        Check(knowledge.ObserveStartingConservation(At.AddSeconds(6), "Northern Realms", 1) && knowledge.StartingSize == 25 &&
            knowledge.Assess([Seen(special)]).Renfri.State == ConstraintState.RuledOut,
            "A recording begun after the first play did not recover 25-card conservation / Renfri exclusion.");
        knowledge.Reset(); knowledge.Observe(Event(special)); knowledge.ObserveScreen(Screen(hand: 9, deck: 18), At.AddSeconds(5));
        knowledge.ObserveStartingConservation(At.AddSeconds(5), "Nilfgaard", 1); knowledge.ObserveScreen(Screen(hand: 9, deck: 18), At.AddSeconds(6));
        Check(!knowledge.ObserveStartingConservation(At.AddSeconds(6), "Nilfgaard", 1) && knowledge.StartingSize is null,
            "Nilfgaard setup additions were mistaken for an exact conserved starting size.");
        knowledge.Reset();
        var roach = catalog.Single(card => card.Id == "112210");
        knowledge.Observe(Event(Card("gold", 9, gold: true), source: CardSightSource.History));
        Check(knowledge.Opportunities.Count == 0, "History is not a live trigger clock.");
        knowledge.Observe(Event(Card("gold", 9, gold: true)));
        var clues = knowledge.Clues([], Deck("roach", roach), null, At.AddSeconds(9));
        Check(clues.Single().Text.Contains("not detected") && !clues.Single().Text.Contains("in hand"), "Missed detection is not reviewed absence.");
        knowledge.Opportunity(new(roach.Id, At, "gold", true, true));
        Check(knowledge.Clues([], Deck("roach", roach), null, At.AddSeconds(9)).Single().Text.Contains("possibly in hand"), "A verified trigger absence is conditional hand information only.");
        Check(knowledge.BoardOrigin(Event(roach, source: CardSightSource.Board, seconds: 5))?.Provenance == CardProvenance.ProbableStartingDeck, "Corroborated Roach summon is probable original evidence.");
        knowledge.Reset();
        var king = catalog.Single(card => card.Id == "203100");
        Check(knowledge.BoardOrigin(Event(king, source: CardSightSource.Board, seconds: 5))?.Provenance == CardProvenance.ProbableStartingDeck,
            "An inherent self-summoner seen without its easily missed trigger must still debit one probable original and its provisions.");
        var redanian = catalog.Single(card => card.Id == "202367");
        Check(knowledge.BoardOrigin(Event(redanian, source: CardSightSource.Board, seconds: 5)) is null,
            "A deck-or-graveyard return must remain origin-ambiguous without a corroborating trigger.");
        var knickers = catalog.Single(card => card.Id == "202397");
        Check(knowledge.BoardOrigin(Event(knickers, source: CardSightSource.Board, seconds: 5))?.Provenance == CardProvenance.ProbableStartingDeck,
            "Knickers automatic arrival must debit one probable original and its provisions.");
        var skirmisher = catalog.Single(card => card.Id == "152313");
        Check(knowledge.BoardOrigin(Event(skirmisher, source: CardSightSource.Board, seconds: 5))?.Provenance == CardProvenance.ProbableStartingDeck,
            "Tuirseach Skirmisher discard summon must debit one probable original and its provisions.");
        knowledge.Opportunity(new(roach.Id, At, "gold", true, true));
        knowledge.Observe(Event(roach, source: CardSightSource.Board, seconds: 5));
        Check(knowledge.Clues([], Deck("roach", roach), null, At.AddSeconds(9)).Count == 0, "Seen arrival removes missing-trigger clue.");
    }

    public static void Budgets()
    {
        var cards = Enumerable.Range(0, 24).Select(i => Seen(Card("known" + i, i < 12 ? 7 : 6))).ToArray(); //156p
        var result = OpponentProvisionCalculator.Calculate(cards, 165, round: 3, hand: 1);
        Check(result.ObservedFloor == 156 && result.SingleUnknownCardCeiling == 9 && result.BigCardCeiling == 0 && result.UnknownStartingSlots == 1, "Tight final original-card budget must rule out 10+p.");
        Check(result.Summary.Contains("ROUND 3") && result.Detail.Contains("replayed"), "Final-card caveats must be visible.");
        var generated = new ObservedCard(Card("created", 20), CardProvenance.Created, 1, At);
        Check(OpponentProvisionCalculator.Calculate(cards.Append(generated), 165).ObservedFloor == 156, "Generated provisions never debit originals.");
        Check(OpponentProvisionCalculator.Calculate(cards.Concat(cards), 165).ObservedFloor == 156, "Repeated frames do not double debit.");
        Check(OpponentProvisionCalculator.Calculate(cards, null).UnaccountedCeiling is null, "No capacity means no fabricated remaining budget.");
        Check(OpponentProvisionCalculator.Calculate(cards, 158).Conflict, "Insufficient minimum-slot budget is a conflict, not clamped zero.");
        var larger = OpponentProvisionCalculator.Calculate(cards, 165, minimumSize: 26);
        Check(larger.UnknownStartingSlots == 2 && larger.SingleUnknownCardCeiling == 5, "Reserve provisions for every unknown original, including ones still in deck.");
    }

    public static void Memory(string root)
    {
        var a = Card("a", 7); var b = Card("b", 6); var c = Card("c", 5); var d = Card("d", 9, gold: true);
        var store = new OpponentDeckMemoryStore();
        var first = store.Record(Encounter("game1", a, b, c), "Partial");
        var next = Encounter("game2", a, b, c, d);
        var match = store.FindMatches(next).Single();
        Check(match.Possible && match.NewCopies == 1 && match.SharedCopies == 3, "Missing entries in a partial record are not contradictions.");
        Check(store.FindMatches(Encounter("unseen", d)).Single().Possible, "No shared recorded cards is still possible, just low support.");
        var merged = store.Record(next, "Partial", first.Id);
        Check(merged.Cards.Count == 4 && merged.Encounters.Count == 2 && !merged.Complete, "Approved encounter union fills missing identities without upgrading completeness.");
        var repeat = store.Record(next, "Renamed", first.Id);
        Check(repeat.Encounters.Count == 2 && repeat.Cards.Count == 4 && repeat.Name == "Renamed", "Same-session save is idempotent and supports renaming.");
        var complete = merged with { Complete = true };
        Check(!OpponentDeckMemoryStore.Match(complete, Encounter("extra", a, Card("different"))).Possible, "Missing from a complete record IS a contradiction.");
        var differentLeader = next with { StartingLeader = "Inspired Zeal" };
        Check(!store.FindMatches(differentLeader).Single().Possible, "Confirmed leader variants must remain separate.");
        var uncertain = next with { StartingLeader = null, StratagemId = null };
        Check(store.FindMatches(uncertain).Single().Possible, "Unknown headers never conflict with recorded ones.");
        var other = store.Record(differentLeader, "Variant", variantOf: first.Id);
        Check(other.VariantOf == first.Id && store.Records.Count == 2, "Related variants are separate records, not destructive merges.");
        var directory = Path.Combine(root, "GwentCompanion", "diagnostics", "opponent-memory-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json"); store.Save(path);
        var loaded = OpponentDeckMemoryStore.Load(path);
        Check(loaded.Records.Count == 2 && loaded.Records.Single(item => item.Id == first.Id).Cards.Count == 4, "Learned evidence and variants round-trip.");
        loaded.Save(path); Check(File.Exists(path + ".bak"), "Memory saves preserve a recovery backup.");
        var blocked = false;
        try { loaded.Record(Encounter("bad", a) with { Cards = [Seen(a) with { Provenance = CardProvenance.Created }] }, "bad"); }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Created/guessed cards must never enter learned original composition.");
        var baseCards = Enumerable.Range(0, 25).Select(i => Card("v" + i)).ToArray();
        var v1 = Deck("v1", baseCards); var v2 = Deck("v2", baseCards.Take(24).Append(d).ToArray());
        Check(DeckVariants.CompatibleFamily(v1, [v1, v2], [Seen(baseCards[0])]).Count == 2, "Shared cards keep both one-card variants possible.");
        Check(DeckVariants.CompatibleFamily(v1, [v1, v2], [Seen(d)]).Single().Id == "v2", "Variant-specific original evidence removes the wrong complete list.");
        Check(DeckVariants.CompatibleFamily(v1, [v1, v2], [Seen(d) with { Provenance = CardProvenance.Created }]).Count == 2, "Generated copies cannot eliminate variants.");
    }

    public static void Mutations(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var temple = catalog.Single(card => card.Id == "203198"); var henry = catalog.Single(card => card.Id == "203275");
        var card = Card("legend", 13, gold: true);
        var ledger = new DeckMutationLedger();
        ledger.Observe(Event(temple), "Skellige", "Northern Realms");
        Check(ledger.Changes.Single().AffectedSide == PlayerSide.Opponent && !ledger.Changes.Single().Confirmed, "Temple preview records a potential own-deck addition, not guessed identities.");
        Check(ledger.OriginRisk(Event(card, seconds: 3).Sighting, At.AddSeconds(3), [])?.Provenance == CardProvenance.Unknown, "Later Temple-compatible cards are acquisition-ambiguous.");
        Check(ledger.OriginRisk(Event(card, seconds: 3).Sighting, At.AddSeconds(3), [Seen(card)]) is null, "Later copies do not erase independent earlier original evidence.");
        Check(ledger.OriginRisk(Event(Card("bronze"), seconds: 3).Sighting, At.AddSeconds(3), []) is null, "Temple does not make all bronze cards generated.");
        ledger.Reset(); ledger.Observe(Event(henry), "Skellige", "Nilfgaard");
        Check(ledger.Changes.Select(item => item.AffectedSide).Distinct().Count() == 2, "Henry affects BOTH players.");
        ledger.Record(new(At, henry.Id, henry.Name, PlayerSide.Opponent, "User read description", card.Id, card.Name, 1, true));
        Check(ledger.OriginRisk(Event(card).Sighting, At.AddSeconds(2), [])?.Provenance == CardProvenance.Created, "Verified added identity is tracked separately from originals.");
        ledger.Reset();
        var torres = catalog.Single(item => item.Id == "203192");
        var braathens = catalog.Single(item => item.Id == "202658");
        var griffinFromOpponentDeck = catalog.Single(item => item.Id == "132307");
        ledger.Observe(Event(torres), "Monsters", "Nilfgaard");
        Check(ledger.Changes.Single().CandidateFaction == "Monsters", "Torres acquisition risk did not retain the opposing deck faction.");
        Check(ledger.OriginRisk(Event(braathens, seconds: 2).Sighting, At.AddSeconds(2), []) is null,
            "Torres incorrectly made a later native Nilfgaard unit look acquired from a Monsters deck.");
        Check(ledger.OriginRisk(Event(griffinFromOpponentDeck, seconds: 2).Sighting, At.AddSeconds(2), [])?.Provenance == CardProvenance.Unknown,
            "Torres-compatible Monsters unit was incorrectly trusted as an original Nilfgaard card.");
        var parsed = CreatedCardDescriptionReader.Parse("Temple of Melitele: Congregation\nCreated cards:\nPrince Anséis\nKing Foltest", catalog);
        Console.WriteLine("  Description parser: " + (parsed is null ? "no source/result" : parsed.SourceName + " => " + string.Join(", ", parsed.Candidates.Select(item => item.Name))));
        Check(parsed is not null && parsed.Candidates.Select(item => item.Name).ToHashSet().SetEquals(["Prince Anséis", "King Foltest"]), "Description names are suggestions, not play events.");
        Check(CreatedCardDescriptionReader.Parse("Prince Anseis\nKing Foltest", catalog) is null, "No source title means no automatic acquisition attribution.");
        Check(CreatedCardDescriptionReader.Parse(temple.Name + "\n" + temple.AbilityText, catalog) is null, "Static printed ability text alone must not invent created identities.");
        var reveal = CreatedCardDescriptionReader.Parse("Summoning Circle\nChosen unit: Sewer Raiders", catalog);
        Check(reveal is { IsDeckReveal: true } && reveal.Candidates.Single().Id == "202334", "Circle target is a current-deck reveal, not an added card or played unit.");
        ledger.Reset(); ledger.Record(new(At, "200022", "Summoning Circle", PlayerSide.Opponent, "Current deck reveal", card.Id, card.Name, 1, true, IsDeckReveal: true));
        Check(ledger.OriginRisk(Event(card).Sighting, At.AddSeconds(2), []) is null, "A reveal alone does not make its target created.");
        ledger.Reset(); ledger.Observe(Event(catalog.Single(item => item.Id == "203226")), "Skellige", "Monsters");
        var kikimore = catalog.First(item => item.Name == "Kikimore Warrior");
        Check(ledger.Changes.Single().CreatesOutsideDeck && ledger.OriginRisk(Event(kikimore).Sighting, At.AddSeconds(2), [])?.Provenance == CardProvenance.Unknown,
            "Hive Mind makes later Kikimore origins ambiguous, not new starting cards or deck additions.");
        Check(ledger.OriginRisk(Event(kikimore, PlayerSide.User).Sighting, At.AddSeconds(2), []) is null, "Creation risks must stay on the correct side.");
        Check(ledger.OriginRisk(Event(kikimore).Sighting, At.AddSeconds(2), [Seen(kikimore)]) is null, "A generated match does not erase earlier independent original evidence.");
        ledger.Observe(Event(catalog.Single(item => item.Name == "Squirrel"), PlayerSide.User, seconds: 3), "Skellige", "Monsters");
        Check(ledger.Changes.All(item => item.AddedCardId is null && !item.Confirmed), "Generator presence / Squirrel do not promise or materialize unobserved outputs.");
        Check(ledger.OriginRisk(Event(kikimore, seconds: 4).Sighting, At.AddSeconds(4), [])?.Provenance == CardProvenance.Unknown,
            "Squirrel's unknown target cannot erase possible past generation or prove a later Kikimore is an original.");
        ledger.Reset(); ledger.Observe(Event(catalog.Single(item => item.Id == "202544")), "Skellige", "Nilfgaard");
        var fangs = catalog.Single(item => item.Name == "Fangs of the Empire");
        Check(ledger.OriginRisk(Event(fangs).Sighting, At.AddSeconds(2), [])?.Provenance == CardProvenance.Unknown,
            "Scenario Spawn-and-play previews must not be treated as proven originals.");
        Check(ledger.OriginRisk(Event(fangs).Sighting, At.AddSeconds(20), [], additionalCopy: true,
                  independentHandPlay: true)?.Provenance == CardProvenance.Unknown,
            "A nearby trigger-card hand decrement laundered a generated scenario payoff into an original.");
        ledger.Reset();
        var siege = catalog.Single(item => item.Id == "202504");
        var bombardment = catalog.Single(item => item.Name == "Bombardment");
        ledger.Observe(Event(siege), "Skellige", "Northern Realms");
        Check(ledger.OriginRisk(Event(bombardment, seconds: 70).Sighting, At.AddSeconds(70), [], additionalCopy: true,
                  independentHandPlay: true)?.Provenance == CardProvenance.Unknown,
            "Siege's delayed Bombardment chapter was charged to the starting deck after hand corroboration.");
        ledger.Reset(); ledger.Observe(Event(catalog.Single(item => item.Id == "203075")), "Skellige", "Northern Realms");
        var knight = catalog.First(item => item.CanBeInStartingDeck && !item.IsGold && item.HasCategory("Knight"));
        Check(ledger.OriginRisk(Event(knight).Sighting, At.AddSeconds(2), []) is null, "Damsel's summoned Knight is not a generated payoff.");
        var charge = catalog.Single(item => item.Name == "Mad Charge");
        Check(ledger.OriginRisk(Event(charge).Sighting, At.AddSeconds(2), [])?.Provenance == CardProvenance.Unknown, "Damsel's generated Mad Charge is a different origin.");
        ledger.Reset(); ledger.Observe(Event(catalog.Single(item => item.Id == "203080")), "Skellige", "Monsters");
        var griffin = catalog.Single(item => item.Name == "Griffin");
        Check(ledger.OriginRisk(Event(griffin).Sighting, At.AddSeconds(2), []) is null, "Manor's played-from-deck target is not blanket-classified as generated.");
        ledger.Reset();
        var foltest = catalog.Single(item => item.Id == "202886");
        var scout = catalog.Single(item => item.Id == "122310");
        var commando = catalog.Single(item => item.Id == "122311");
        ledger.Observe(Event(foltest), "Scoia'tael", "Northern Realms");
        Check(ledger.OriginRisk(Event(commando, seconds: 40).Sighting, At.AddSeconds(40), []) is null &&
              ledger.OriginRisk(Event(commando, seconds: 40).Sighting, At.AddSeconds(40), [], additionalCopy: true) is not null,
            "Foltest's copy route suppressed the first proven template or failed to protect additional copies.");
        ledger.Observe(Event(scout, seconds: 50), "Scoia'tael", "Northern Realms");
        Check(ledger.OriginRisk(Event(commando, seconds: 60).Sighting, At.AddSeconds(60), []) is null &&
              ledger.OriginRisk(Event(commando, seconds: 60).Sighting, At.AddSeconds(60), [], additionalCopy: true) is not null,
            "Blue Stripes Scout's copy route poisoned a first identity or lost duplicate ambiguity.");
        ledger.Reset();
        var pavetta = catalog.Single(item => item.Id == "122210");
        ledger.Observe(Event(pavetta), "Scoia'tael", "Northern Realms");
        Check(ledger.OriginRisk(Event(commando, seconds: 20).Sighting, At.AddSeconds(20), []) is null &&
              ledger.OriginRisk(Event(commando, seconds: 20).Sighting, At.AddSeconds(20), [], additionalCopy: true) is not null,
            "Pavetta's return route suppressed first identity evidence or failed to protect repeats.");
        ledger.Reset();
        var assire = catalog.Single(item => item.Id == "162202");
        var laterOpponentCard = catalog.Single(item => item.Id == "202431");
        var laterUserCard = catalog.Single(item => item.Id == "202436");
        ledger.Observe(Event(assire), "Scoia'tael", "Nilfgaard");
        Check(ledger.Changes.Count == 2 && ledger.Changes.All(item => item.CopyCountOnly),
            "Assire's visible return risk must remain in the mutation log as copy-only evidence.");
        Check(ledger.OriginRisk(Event(laterOpponentCard, seconds: 3).Sighting, At.AddSeconds(3), []) is null &&
              ledger.OriginRisk(Event(laterUserCard, PlayerSide.User, seconds: 3).Sighting, At.AddSeconds(3), []) is null,
            "Assire must not poison the first sighting of every later card on either side.");
        Check(ledger.OriginRisk(Event(laterOpponentCard, seconds: 3).Sighting, At.AddSeconds(3), [], additionalCopy: true) is not null,
            "Assire must preserve uncertainty for a later additional copy whose returned identity was not resolved.");
        var repeatedGold = catalog.Single(item=>item.Id=="203242");
        Check(ledger.OriginRisk(Event(repeatedGold,seconds:4).Sighting,At.AddSeconds(4),[],additionalCopy:true) is not null &&
              ledger.OriginRisk(Event(laterOpponentCard,seconds:5).Sighting,At.AddSeconds(5),[],additionalCopy:true) is null,
            "A repeated gold did not bind Assire's single unresolved return and release unrelated later identities.");
        ledger.Reset();
        var ramon = catalog.Single(item => item.Id == "202446");
        var lightCavalry = catalog.Single(item => item.Id == "202452");
        ledger.Observe(Event(ramon), "Scoia'tael", "Nilfgaard");
        Check(ledger.OriginRisk(Event(lightCavalry, seconds: 2).Sighting, At.AddSeconds(2), [], additionalCopy: true,
                  independentHandPlay: true) is not null,
            "A later paid play of Ramon's retained hand template must not be counted as a second original copy.");
        Check(ledger.OriginRisk(Event(catalog.Single(item=>item.Id=="202450"),seconds:3).Sighting,At.AddSeconds(3),[],additionalCopy:true,
                  independentHandPlay:true) is null,
            "Ramon copy ambiguity remained broad after its immediate copied identity was observed.");
        ledger.Reset();
        var slaveDriver = catalog.Single(item => item.Id == "201612");
        var crossbowman = catalog.Single(item => item.Id == "202450");
        ledger.Observe(Event(slaveDriver), "Scoia'tael", "Nilfgaard");
        Check(ledger.OriginRisk(Event(crossbowman, seconds: 2).Sighting, At.AddSeconds(2), [], additionalCopy: true,
                  independentHandPlay: true) is null,
            "A later independently paid card may establish another original after an ordinary board-copy effect.");
        var provenance = new PlayProvenanceResolver();
        var nauzicaa = catalog.Single(item => item.Id == "162309");
        var preparation = catalog.Single(item => item.Id == "202454");
        provenance.Observe(Event(nauzicaa));
        Check(provenance.Observe(Event(preparation, seconds: 2)).Provenance == CardProvenance.Spawned,
            "A named Spawn-and-play result immediately following its played Deploy source must not spend provisions.");
        provenance.Reset();
        var baccala = catalog.Single(item => item.Id == "203239");
        var marine = catalog.Single(item => item.Id == "203244");
        provenance.Observe(Event(baccala));
        Check(provenance.Observe(Event(marine, source: CardSightSource.Board, seconds: 2)).Provenance == CardProvenance.Spawned,
            "A named Spawn body first recognized on board must retain generated provenance.");
        var boardKnowledge = new OpponentKnowledge();
        Check(boardKnowledge.BoardOrigin(Event(baccala, source: CardSightSource.Board), "Nilfgaard")?.Provenance == CardProvenance.ProbableStartingDeck,
            "A repeatedly recognized faction-compatible gold board identity must recover one probable original after its preview was missed.");
        Check(boardKnowledge.BoardOrigin(Event(marine, source: CardSightSource.Board), "Nilfgaard") is null,
            "Generic bronze board presence remains controller evidence only because copy/spawn routes are common.");
    }

    public static async Task HudAsync(string root)
    {
        using var reader = new ScreenStateRecognizer();
        var deckReads = 0;
        foreach (var (file, expectedHand, expectedDeck) in new[] { ("frame-000770-101455988.jpg", 9, 15),
            ("frame-001292-101548200.jpg", 8, 13), ("frame-005164-102215401.jpg", 6, 7), ("frame-006487-102427703.jpg", 2, 7) })
        {
        var hud = new OpponentHudRecognizer();
        var path = Path.Combine(root, "GwentCompanion", "sessions", "20260827-101339", file);
        using var input = File.OpenRead(path);
        var image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var frame = BitmapFrameAdapter.ToPixelFrame(image);
        var first = await hud.ReadAsync(frame, Screen(), At, reader);
        var second = await hud.ReadAsync(frame, Screen(), At.AddSeconds(1), reader);
        Check(first.OpponentHandCount is null && first.OpponentDeckCount is null, "One OCR sample cannot establish counters.");
        Console.WriteLine($"  Recorded HUD {file}: hand {second.OpponentHandCount}, draw pile {second.OpponentDeckCount} (expected {expectedHand},{expectedDeck})");
        Check(second.OpponentHandCount == expectedHand, "Visible hand counters must match manually inspected values.");
        Check(second.OpponentDeckCount is null || second.OpponentDeckCount == expectedDeck, "Deck counter must read correctly or abstain, never invent a number.");
        if (second.OpponentDeckCount == expectedDeck) deckReads++;
        }
        Check(deckReads >= 2, "Deck-counter coverage unexpectedly regressed below two of four inspected frames.");
        Console.WriteLine($"  HUD coverage: 4/4 hand counts; {deckReads}/4 draw-pile counts; remaining readings withheld.");
        var legacyPath = Path.Combine(root, "GwentCompanion", "cache", "video-scan", "shinmiri", "windows",
            "QO9Q1Cmjc1s-hive-mind-resolution-2515", "frame-002535267.jpg");
        if (File.Exists(legacyPath))
        {
            using var input = File.OpenRead(legacyPath);
            var image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var frame = BitmapFrameAdapter.ToPixelFrame(image); var hud = new OpponentHudRecognizer();
            await hud.ReadAsync(frame, Screen(), At, reader);
            var confirmed = await hud.ReadAsync(frame, Screen(), At.AddSeconds(1), reader);
            Check(confirmed.OpponentScore == 39 && confirmed.UserScore == 23,
                "Readable score glyphs were discarded when a legacy/obscured hand counter was unavailable.");
            Console.WriteLine("  Shinmiri legacy HUD: opponent 39, player 23; score accounting remains available independently of /10 OCR.");
        }
        Check(OpponentHudRecognizer.ParseHand("19/10") is null && OpponentHudRecognizer.ParseDeck("15 damage") is null, "Reject counters contaminated by body text.");
    }

    public static void Recordings(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var reports = new List<object>();
        foreach (var (session, inputs) in new[] { ("20260827-101339", new[] { "v0.1.3-verified-streaming.json" }),
                     ("20260827-081435", new[] { "../tests/recording-events-20260827-081435-reviewed.json", "v0.1.2-verified-titles.json" }) })
        {
            var events = inputs.SelectMany(input => SavedVisionEvents.Read(Path.Combine(root, "GwentCompanion", "diagnostics", input), catalog, session)).OrderBy(item => item.ObservedAt).ToArray();
            var user = new LiveDeckTracker(PlayerSide.User); var opponent = new LiveDeckTracker(PlayerSide.Opponent);
            var knowledge = new OpponentKnowledge(); var mutations = new DeckMutationLedger(); var resolver = new PlayProvenanceResolver();
            foreach (var evidence in events)
            {
                var sight = evidence.Sighting; var tracker = sight.Side == PlayerSide.User ? user : opponent;
                mutations.Observe(evidence, user.Faction, opponent.Faction);
                var normal = resolver.Observe(evidence);
                var origin = mutations.OriginRisk(sight, evidence.ObservedAt, tracker.Observations) ?? knowledge.BoardOrigin(evidence) ?? normal;
                if (!sight.Card.CanBeInStartingDeck) origin = normal;
                knowledge.Observe(evidence);
                tracker.ConsiderDirectPlay(sight.Card, 1 - sight.Distance, evidence.ObservedAt, evidence.Description + " " + origin.Reason, origin.Provenance);
            }
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "GwentCompanion", "tests", "opponent-inventory-" + session + ".json")));
            var expected = inventory.RootElement.GetProperty("Cards").EnumerateArray().Select(item => item.GetProperty("Id").GetString()!).ToHashSet();
            Check(expected.SetEquals(opponent.Observations.Select(item => item.Card.Id)), "Interaction inference must preserve all verified visible identities: " + session);
            var evidenceCards = opponent.DeckBuildingObservations.ToArray();
            var budget = OpponentProvisionCalculator.Calculate(evidenceCards, 170, knowledge.MinimumSize(evidenceCards));
            var projection = new OpponentDeckProjector().Build([], evidenceCards, opponent.Faction, constraints: knowledge.Assess(evidenceCards));
            Check(projection.ObservedCopies == evidenceCards.Sum(item => item.ObservedCopies), "Only original evidence reaches slots.");
            Check(budget.ObservedFloor == projection.Slots.Where(item => item.State == DeckSlotState.Observed).Sum(item => item.Card!.Provision), "Budget and composition agree after replay.");
            var log = Path.Combine(root, "GwentCompanion", "sessions", session, "vision-observations.jsonl");
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            if (File.Exists(log)) foreach (var line in File.ReadLines(log))
            {
                using var document = JsonDocument.Parse(line);
                var screen = document.RootElement.GetProperty("Screen").Deserialize<GwentVisualObservation>(options)!;
                knowledge.ObserveScreen(screen, document.RootElement.GetProperty("SampledAt").GetDateTimeOffset());
            }
            if (session == "20260827-101339") Check(knowledge.Round == 3 && knowledge.Ended, "Saved FINAL ROUND and VICTORY headings must drive endgame state.");
            reports.Add(new { Session = session, VisibleIdentities = opponent.Observations.Count, OriginalCopies = projection.ObservedCopies,
                budget, knowledge.Round, knowledge.Ended, Mutations = mutations.Changes,
                Note = "New inference replay of saved recognition output; HUD is tested separately on pixels. Not a fresh all-frame vision accuracy measurement." });
            Console.WriteLine($"  {session}: {opponent.Observations.Count}/{expected.Count} visible IDs; {projection.ObservedCopies} original-evidence copies; {budget.ObservedFloor}p; round={knowledge.Round}; ended={knowledge.Ended}");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.9-opponent-replay.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }
}
