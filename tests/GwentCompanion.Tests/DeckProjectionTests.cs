using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

internal static class DeckProjectionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static CardDefinition Card(string id, int provision = 4, bool gold = false, string faction = "Nilfgaard") =>
        new(id, id, faction, CardKind.Unit, provision, IsGold: gold);
    private static ObservedCard Seen(CardDefinition card, CardProvenance provenance = CardProvenance.ProbableStartingDeck, int copies = 1) =>
        new(card, provenance, .99, At, "Fixture evidence", copies);
    private static DeckDefinition Deck(string id, IEnumerable<CardDefinition> cards, int recency = 0) =>
        new(id, id, "Nilfgaard", "Enslave", 15, cards.GroupBy(card => card.Id).Select(group => new DeckCard(group.First(), group.Count())).ToArray(),
            LastEdited: At.AddDays(-recency), RecencyRank: recency);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Projection()
    {
        var projector = new OpponentDeckProjector();
        var empty = projector.Build([], [], null);
        Check(empty.Slots.Count == 25 && empty.UnknownSlots == 25, "Empty evidence must leave 25 unknown slots.");
        var cards = Enumerable.Range(0, 23).Select(i => Card("card" + i, 4 + i % 4)).ToArray();
        var bronze = Card("bronze", 4);
        var pin = Deck("pin", cards.Concat([bronze, bronze]));
        var exact = projector.Build([], [Seen(cards[0])], null, pin);
        Check(exact.Slots.Count == 25 && exact.UnknownSlots == 0 && exact.ObservedCopies == 1, "Pin must fill all 25 physical slots even without corpus support.");
        Check(exact.Slots.Count(row => row.State == DeckSlotState.Pinned) == 24, "Unseen reference cards must never be labeled observed.");
        Check(exact.Slots.Where(row => row.Card?.Id == bronze.Id).Select(row => row.Copy).SequenceEqual([1, 2]), "Bronze copies occupy separate slots.");
        Check(exact.Slots.Select(row => row.Card!.Provision).SequenceEqual(exact.Slots.Select(row => row.Card!.Provision).OrderDescending()), "Rows must descend by provision.");
        Check(exact.Devotion.State == ConstraintState.Possible && exact.DevotionAssumption.Contains("Pinned list: Devotion"), "A Devotion pin is an assumption, not observed proof.");

        var replacement = Card("replacement", 6, true);
        var corroborator = Card("corroborator", 5, true);
        var alternatives = Enumerable.Range(0, 6).Select(i => Deck("alt" + i, cards.Skip(4).Concat([bronze, bronze, replacement, corroborator, Card("variantA" + i), Card("variantB" + i)]), i)).ToArray();
        var profileSeen = cards.Skip(4).Take(4).Select(c => Seen(c)).Prepend(Seen(replacement)).ToArray();
        var violated = projector.Build(alternatives, profileSeen, "Nilfgaard", pin);
        Check(violated.PinDeviations == 1 && violated.PinInfluence < exact.PinInfluence && violated.PinInfluence > 0, "A deviation softens rather than erases a legal pin.");
        Check(violated.Slots.Any(row => row.Card?.Id == replacement.Id && row.State == DeckSlotState.Observed && row.DeviatesFromPin), "Conflicting observations must survive.");
        Check(violated.Slots.Any(row => row.Card?.Id == corroborator.Id && row.State == DeckSlotState.Predicted), "Strong co-occurrence can substitute a pin-exclusive card.");
        Check(violated.Slots.Where(row => row.Card is not null).Sum(row => row.Card!.Provision) + violated.UnknownSlots * 4 <= 165, "Suggestions must leave a provision budget for unknowns under a pin.");
        var repeated = projector.Build([], [Seen(bronze), Seen(bronze, copies: 2)], "Nilfgaard");
        Check(repeated.ObservedCopies == 2 && repeated.Slots.Count(row => row.Card?.Id == bronze.Id) == 2, "Repeated snapshots use max copy evidence, not sum.");
        var oversized = Deck("large", cards.Concat([bronze, bronze, Card("extra")]));
        Check(projector.Build([], [], "Nilfgaard", oversized).Slots.Count == 26, "25 is a minimum layout, not a hard deck limit.");
        Check(projector.Build([], Enumerable.Range(0, 27).Select(i => Seen(Card("seen" + i))), "Nilfgaard").Slots.Count == 27, "Never truncate observations to 25.");
        Check(projector.Build(alternatives, [], null).UnknownSlots == 25, "Unresolved faction must not populate a mixed-faction deck.");
        Check(projector.Build([pin], [], "Nilfgaard").UnknownSlots == 25, "A lone cached list is not enough evidence to auto-fill a deck.");
        var poorFit = projector.Build(alternatives, [Seen(replacement), Seen(Card("absent1")), Seen(Card("absent2"))], "Nilfgaard");
        Check(poorFit.Meta.BestMatchedIdentities == 1 && poorFit.Slots.Count(s => s.State == DeckSlotState.Predicted) <= 8 &&
            poorFit.Slots.Where(s => s.State == DeckSlotState.Predicted).All(s => s.Reason.Contains("Weak absolute cache fit")), "Poor-fit fallback must be bounded and explicitly tentative.");

        var otherFaction = Deck("monsters", [Card("mo", faction: "Monsters")]) with { Faction = "Monsters" };
        var pinPrior = projector.Build([otherFaction, pin], [], null, pin);
        Check(pinPrior.Meta.CorpusDecks == 1 && pinPrior.Slots.All(row => row.Card?.Id != "mo"), "A pin supplies a provisional faction, not a cross-faction mixture.");
        var conflictFaction = projector.Build([otherFaction], [], "Monsters", pin);
        Check(conflictFaction.PinInfluence == 0, "Stable observed faction takes precedence over a conflicting pin.");

        var neutralSpecial = Card("Neutral special", faction: "Neutral") with { Kind = CardKind.Special };
        var broken = projector.Build([], [Seen(neutralSpecial)], "Nilfgaard", pin);
        Check(broken.Devotion.State == ConstraintState.RuledOut, "Neutral specials, not just units, break Devotion.");
        Check(projector.Build([], [Seen(neutralSpecial, CardProvenance.Created)], "Nilfgaard").Devotion.State == ConstraintState.Possible, "Generated neutrals do not break Devotion.");
        Check(projector.Build([], [Seen(neutralSpecial, CardProvenance.Unknown)], "Nilfgaard").ObservedCopies == 0, "Uncertain ownership cannot populate starting-deck slots.");
        var gn = Card("gn", 14, true, "Neutral") with { Name = "Golden Nekker", Kind = CardKind.Special };
        var invalidGn = Deck("invalid-gn", [gn, Card("big", 12, true)]);
        var noLegal = projector.Build([invalidGn], [Seen(gn)], "Nilfgaard", invalidGn);
        Check(noLegal.PinInfluence == 0 && noLegal.Slots.All(row => row.Card?.Id != "big") && noLegal.Meta.Cards.All(signal => signal.ConditionalPresence == 0), "No compatible GN corpus must abstain, not normalize forbidden decks back to probability one.");
        var known = OpponentDeckProjector.Reference(pin, [Seen(bronze)]);
        Check(known.Count == 25 && known.Count(row => row.State == DeckSlotState.Observed) == 1 && known.Count(row => row.State == DeckSlotState.Reference) == 24, "Known own deck remains reference data, not inferred discoveries.");

        var edits = new DeckProjectionEdits();
        var automatic = projector.Build(alternatives, profileSeen, "Nilfgaard", edits: edits);
        var suggestion = automatic.Slots.First(row => row.State == DeckSlotState.Predicted);
        edits.Exclude(suggestion.Card!, suggestion.Copy);
        var dismissed = projector.Build(alternatives, profileSeen, "Nilfgaard", edits: edits);
        Check(dismissed.Slots.All(row => row.Card?.Id != suggestion.Card!.Id || row.Copy != suggestion.Copy), "Dismissed auto-fill must not bounce back on the next render.");
        edits.Include(suggestion.Card!, suggestion.Copy);
        var manual = projector.Build(alternatives, profileSeen, "Nilfgaard", edits: edits);
        Check(manual.Slots.Any(row => row.Card?.Id == suggestion.Card!.Id && row.Copy == suggestion.Copy && row.State == DeckSlotState.Selected), "Clicking a candidate back in should create an explicit manual assumption.");
        Check(manual.ObservedCopies == automatic.ObservedCopies,
            "Manual choices must never be counted as observations.");
        Check(manual.Meta.CorpusDecks == automatic.Meta.CorpusDecks && manual.Meta.ObservedIdentities == automatic.Meta.ObservedIdentities &&
            manual.Meta.Cards.All(card => card.SupportingDecks == automatic.Meta.Cards.Single(other => other.Card.Id == card.Card.Id).SupportingDecks),
            "Manual priors may change recommendations, but not observed evidence or empirical sample counts.");
        edits.Exclude(replacement, 1);
        Check(projector.Build(alternatives, [Seen(replacement)], "Nilfgaard", edits: edits).Slots.Any(row => row.Card?.Id == replacement.Id && row.State == DeckSlotState.Observed), "Dismissal cannot override a later observed play.");
        edits.Include(neutralSpecial, 1);
        var manualNeutral = projector.Build([], [], "Nilfgaard", edits: edits);
        Check(manualNeutral.Devotion.State == ConstraintState.Possible && manualNeutral.ObservedCopies == 0, "Manually assumed neutrals cannot rule out observed Devotion.");
        Check(projector.Build([], [], "Nilfgaard", pin, edits).Slots.All(row => row.State != DeckSlotState.Selected), "Full-deck pin temporarily supersedes individual assumptions.");
        edits.Clear();
        Check(edits.Included.Count == 0 && edits.Excluded.Count == 0, "New match clears both manual choices and dismissal memory.");
        edits.Include(bronze, 2);
        var twoManual = projector.Build([], [], "Nilfgaard", edits: edits);
        Check(twoManual.Slots.Count(row => row.State == DeckSlotState.Selected) == 2, "Selecting a second copy must retain the prerequisite first copy even without auto-fill support.");
        edits.Exclude(bronze, 2);
        Check(projector.Build([], [], "Nilfgaard", edits: edits).Slots.Count(row => row.State == DeckSlotState.Selected) == 1, "Returning one duplicate must preserve the other copy.");
    }

    public static void Meta()
    {
        DeckDefinition Complete(string id, IEnumerable<CardDefinition> input, int days = 0)
        {
            var values = input.ToArray();
            return Deck(id, values.Concat(Enumerable.Range(0, Math.Max(0, 25 - values.Length)).Select(i => Card(id + " filler " + i))), days);
        }
        var anchor = Card("observed engine"); var partner = Card("synergy payoff", 8, true);
        var staple = Card("universal staple", 5, true); var recent = Card("newly popular", 7, true);
        var decks = Enumerable.Range(0, 12).Select(i => Complete("sample" + i,
            new[] { staple }.Concat(i is 2 or 3 or 5 ? [anchor, partner] : []).Concat(i < 4 ? [recent] : []), i < 4 ? i : 40 + i)).ToArray();
        var analyzer = new DeckMetaAnalyzer();
        var report = analyzer.AnalyzeAt(decks.Concat([decks[0]]), [Seen(anchor)], "Nilfgaard", null, null, At);
        var synergy = report.Cards.Single(item => item.Card.Id == partner.Id);
        var popular = report.Cards.Single(item => item.Card.Id == staple.Id);
        var rising = report.Cards.Single(item => item.Card.Id == recent.Id);
        Check(report.CorpusDecks == 12 && report.RecentDecks == 4 && report.OlderDecks == 8, "Deduplicate repeated deck links and define recent/older windows.");
        Check(synergy.StrategyLinked && synergy.AssociationLift > 1.35 && synergy.Associations.Single().JointDecks == 3, "Specific repeated co-occurrence should produce a strategy signal.");
        Check(popular.RecentPrevalence == 1 && Math.Abs(popular.AssociationLift - 1) < .001 && !popular.StrategyLinked, "Universal staples must not masquerade as specific synergy.");
        Check(rising.Rising && rising.RecentPrevalence == 1 && rising.OlderPrevalence == 0, "Newer sample prevalence must be compared with older lists.");
        Check(analyzer.Analyze(decks, [], "Nilfgaard").Cards.All(item => !item.StrategyLinked), "No plays means no match-specific strategy claim.");
        Check(analyzer.Analyze(decks.Take(2), [Seen(anchor)], "Nilfgaard").Cards.All(item => !item.Rising), "Insufficient samples must not claim a rising trend.");
        var doubles = new[] { Complete("copies1", [anchor, anchor]), Complete("copies2", [anchor]), Complete("copies3", [staple]) };
        var copy = analyzer.Analyze(doubles, [], "Nilfgaard").Cards.Single(item => item.Card.Id == anchor.Id);
        Check(copy.SupportingDecks == 2 && copy.CopyPresence[0] > copy.CopyPresence[1], "Presence counts decks, not copies; second-copy likelihood stays separate.");
        var foreign = Complete("foreign", [staple]) with { Faction = "Monsters", LastEdited = At.AddDays(1) };
        var global = analyzer.AnalyzeAt(decks.Append(foreign), [], "Monsters", null, null, At);
        var globalStaple = global.Cards.Single(s => s.Card.Id == staple.Id).GlobalRecentPrevalence;
        Check(global.CorpusDecks == 1 && globalStaple == 1,
            $"Global recent baseline must remain available across faction filtering (corpus={global.CorpusDecks}, global={globalStaple:F3}).");
        var many = analyzer.Analyze(decks, Enumerable.Range(0, 500).Select(i => Seen(Card("absent" + i))), "Nilfgaard");
        Check(many.Cards.All(item => double.IsFinite(item.ConditionalPresence)) && Math.Abs(many.RankedDecks.Sum(item => item.Score) - 1) < 1e-8, "Log-weight normalization must survive many mismatches without NaNs.");
        var gn = Card("gn", 14, true, "Neutral") with { Name = "Golden Nekker" };
        var numerous = Enumerable.Range(0, 500).Select(i => Card("many" + i)).ToArray();
        var requiredGn = StartingDeckRules.EvaluateObservedDeck([Seen(gn)]);
        var valid = Complete("legal", [gn]); var invalid = Deck("forbidden", numerous.Append(Card("big", 15)));
        var underflow = analyzer.Analyze([valid, invalid], numerous.Select(card => Seen(card)), "Nilfgaard", requiredGn);
        Check(underflow.RankedDecks.Single(item => item.Deck.Id == "legal").Score == 1, "Forbidden high-weight decks must not cause legal weights to underflow.");
    }

    public static void Persistence(string root)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var legacy = JsonSerializer.Deserialize<ObservedDeckRecord>("""{"SessionId":"old","Side":"User","RecordedAt":"2026-08-01T00:00:00Z","Faction":"Nilfgaard","CompatibleCachedDecks":0,"BestCachedDeckId":null,"Cards":[]}""", options)!;
        Check(legacy.Devotion == ConstraintState.Unknown && !legacy.TrainingOnly, "Old records must remain readable without inventing new evidence.");
        var record = legacy with { TrainingOnly = true, Devotion = ConstraintState.Possible, DevotionEvidence = "No neutral observed", PinnedDeckId = "ref" };
        var restored = JsonSerializer.Deserialize<ObservedDeckRecord>(JsonSerializer.Serialize(record, options), options)!;
        Check(restored.TrainingOnly && restored.Devotion == ConstraintState.Possible && restored.PinnedDeckId == "ref", "Record new evidence and reference metadata without losing side/training flags.");
        var path = Directory.GetFiles(Path.Combine(root, "GwentCompanion", "cache", "decks"), "*.json").First();
        var id = Path.GetFileNameWithoutExtension(path);
        var entry = new DeckIndexEntry("test", "test", "test", 1, "", "", "Newest spreadsheet entry", new Uri("https://www.playgwent.com/en/decks/" + id), null, null, "", At, null, 8);
        var old = entry with { Name = "Old entry", LastEdited = At.AddDays(-10), RecencyRank = 0 };
        var cache = new PlayGwentDeckCacheService().LoadCached([old, entry], Path.GetDirectoryName(path)!, 1);
        Check(cache.Single().LastEdited == At && cache.Single().Name == entry.Name, "Latest spreadsheet timestamp must survive cache loading and duplicate link selection.");
    }

    public static void Recordings(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion", "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var index = Directory.GetFiles(Path.Combine(root, "notes"), "*.xlsx").SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries).ToArray();
        var decks = new PlayGwentDeckCacheService().LoadCached(index, Path.Combine(cache, "decks"), int.MaxValue);
        var reports = new List<object>();
        foreach (var (session, inputs) in new[]
        {
            ("20260827-101339", new[] { "v0.1.3-verified-streaming.json" }),
            ("20260827-081435", new[] { "../tests/recording-events-20260827-081435-reviewed.json", "v0.1.2-verified-titles.json" }),
        })
        {
            var events = inputs.SelectMany(input => SavedVisionEvents.Read(Path.Combine(root, "GwentCompanion", "diagnostics", input), catalog, session))
                .OrderBy(item => item.ObservedAt).ToArray();
            var user = new LiveDeckTracker(PlayerSide.User); var opponent = new LiveDeckTracker(PlayerSide.Opponent);
            var origin = new PlayProvenanceResolver(); var projector = new OpponentDeckProjector();
            foreach (var evidence in events)
            {
                var result = origin.Observe(evidence);
                var tracker = evidence.Sighting.Side == PlayerSide.User ? user : opponent;
                tracker.ConsiderDirectPlay(evidence.Sighting.Card, 1 - evidence.Sighting.Distance, evidence.ObservedAt,
                    evidence.Description + " " + result.Reason, result.Provenance);
                var projected = projector.Build(decks, opponent.DeckBuildingObservations, opponent.HasStableFaction ? opponent.Faction : null);
                Check(projected.Slots.Count >= 25 && projected.Slots.Count(row => row.State == DeckSlotState.Observed) == projected.ObservedCopies, "Every event must preserve observed slot counts.");
                Check(projected.Slots.Where(row => row.State == DeckSlotState.Observed).All(row => opponent.DeckBuildingObservations.Any(item => item.Card.Id == row.Card!.Id)), "Predictions must never become recorded observations.");
            }
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "GwentCompanion", "tests", "opponent-inventory-" + session + ".json")));
            var expected = inventory.RootElement.GetProperty("Cards").EnumerateArray().Select(item => item.GetProperty("Id").GetString()!).ToHashSet();
            var actual = opponent.Observations.Select(item => item.Card.Id).ToHashSet();
            Check(actual.SetEquals(expected), $"{session}: visible identity inventory mismatch. Missing: {string.Join(',', expected.Except(actual))}; extra: {string.Join(',', actual.Except(expected))}");
            var final = projector.Build(decks, opponent.DeckBuildingObservations, opponent.Faction);
            Check(final.Devotion.State == ConstraintState.RuledOut, "Recorded neutral plays must rule out Devotion in both games.");
            if (session == "20260827-101339")
            {
                var excluded = new[] { "203127", "203169", "202915", "203097", "122102" };
                Check(opponent.DeckBuildingObservations.Select(item => item.Card.Id).ToHashSet().SetEquals(expected.Except(excluded)), "Latest recording must keep generated/uncertain/spy identities out of starting-deck composition.");
                Check(user.DeckBuildingObservations.Any(item => item.Card.Id == "122102") && opponent.DeckBuildingObservations.All(item => item.Card.Id != "122102"), "Vernon Roche belongs to the user's deck evidence, not the opponent's.");
                Check(events.Count(item => item.Sighting.Side == PlayerSide.Opponent && item.Sighting.Source == CardSightSource.PlayPreview) == 19, "Preserve all 19 verified opponent preview episodes.");
            }
            var pin = final.Meta.RankedDecks.FirstOrDefault()?.Deck;
            var pinned = projector.Build(decks, opponent.DeckBuildingObservations, opponent.Faction, pin);
            Check(pinned.ObservedCopies == final.ObservedCopies && pinned.Slots.Count(row => row.State == DeckSlotState.Observed) == final.ObservedCopies, "Pinning a real cached list cannot erase the recorded deck evidence.");
            var assessment = StartingDeckRules.EvaluateObservedDeck(opponent.DeckBuildingObservations);
            Check(assessment.ProvisionLowerBound == final.Slots.Where(row => row.State == DeckSlotState.Observed).Sum(row => row.Card!.Provision), "Displayed provisions must equal evidence-card costs only.");
            reports.Add(new { Session = session, Inputs = inputs, VisibleIdentities = actual.Count, ExpectedVisibleIdentities = expected.Count,
                StartingEvidenceIdentities = final.ObservedCopies, assessment.ProvisionLowerBound, Devotion = final.Devotion.State.ToString(),
                UnknownSlots = final.UnknownSlots, PinnedReference = pin?.Name, pinned.PinDeviations, pinned.PinInfluence,
                Cards = opponent.Observations.OrderByDescending(item => item.Card.Provision).Select(item => new { item.Card.Id, item.Card.Name, item.Card.Provision,
                    Provenance = item.Provenance.ToString(), item.ObservedCopies, InStartingSlots = opponent.DeckBuildingObservations.Any(known => known.Card.Id == item.Card.Id) }).ToArray(),
                Projection = final.Slots.Select(row => new { row.Position, Name = row.Card?.Name, Provision = row.Card?.Provision, State = row.State.ToString(), row.Copy, row.ModelShare }).ToArray(),
                Note = "Replays saved recognition output through current provenance/inference; not a new OCR/artwork run. Earlier game combines two saved recognition paths. Starting-deck membership and copy counts remain conservative estimates, not a full hidden-deck validation." });
            Console.WriteLine($"  {session}: {actual.Count}/{expected.Count} visible identities; {final.ObservedCopies} starting-evidence identities; {assessment.ProvisionLowerBound}p floor; Devotion ruled out; {final.UnknownSlots} unknown slots.");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.4-deck-replay.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        var jsonl = SavedVisionEvents.Read(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.3-verified-streaming.jsonl"), catalog);
        var json = SavedVisionEvents.Read(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.3-verified-streaming.json"), catalog);
        Check(jsonl.Count == json.Count, "Offline review must handle array and JSON-lines evidence identically.");
    }
}
