using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class DeckBuilderAudit
{
    // Manually transcribed from the visible top/bottom pages, not derived from scanner output.
    // Confirmed user lists additionally cross-check Fruits and Invigorate membership/quantities.
    internal sealed record Reference(string Key, string Leader, string Stratagem, string[] Scans, string[] OrderedCards);
    internal static readonly Reference[] References =
    [
        new("fruits", "Fruits of Ysgith", "Urn of Shadows", ["20260827-140327-db4212"],
            ["Oneiromancy", "Aerondight", "Brewess: Ritual", "Korathi Heatwave", "Portal", "Hen Gaidth Sword", "Lord Riptide", "Toad Prince", "Golyat", "Lady of the Lake",
             "Griffin|2", "Barbegazi|2", "Rotfiend|2", "Siren|2", "Harpy Egg", "Ghoul|2", "Fiend|2", "Nekker Warrior|2"]),
        new("vampires", "Blood Scent", "Crystal Skull", ["20260827-144001-112220"],
            ["Oneiromancy", "Regis: Reborn", "Unseen Elder", "Korathi Heatwave", "Portal", "Dettlaff van der Eretein", "Eskel: Pathfinder", "Katakan", "Orianna", "Tesham Mutna Sword",
             "Fleder|2", "Nekurat|2", "Alp", "Incubus", "Vial of Forbidden Knowledge", "Epidemic", "Spores", "Feast of Blood|2", "Fiend|2", "Garkain|2"]),
        new("invigorate", "Invigorate", "Tactical Advantage", ["20260827-144112-759d80", "20260827-145544-3e3711"],
            ["Filavandrel aén Fidháil", "Korathi Heatwave", "Phoenix", "Royal Decree", "Roach", "Frog Mating Season", "Knickers", "Necromancy", "Tempest", "Harald Gord", "Fauve",
             "Whisperer of Dol Blathanna|2", "Ciaran aep Easnillen", "Orb of Insight|2", "Prism Pendant", "Bountiful Harvest", "Sorceress of Dol Blathanna|2", "Elven Seer|2", "Impenetrable Fog|2", "Squirrel"]),
        new("dwarves", "Precision Strike", "Magic Lamp", ["20260827-145706-4bca9f"],
            ["Zoltan: Warrior", "Filavandrel aén Fidháil", "Garrison", "Korathi Heatwave", "Call of the Forest", "Novigradian Justice", "Zoltan Chivay", "Zoltan: Scoundrel", "Saer Qu'an", "Eudora Breckenriggs",
             "Barclay Els", "Zoltan's Company", "Dwarven Chariot|2", "Dwarven Agitator", "Mahakam Volunteers|2", "Dwarf Berserker|2", "Mahakam Guard|2", "Brokilon Sentinel|2", "Dwarven Skirmisher|2"]),
    ];

    internal static DeckCard[] Cards(Reference reference, IReadOnlyList<CardDefinition> catalog) => reference.OrderedCards.Select(value =>
    {
        var parts = value.Split('|');
        return new DeckCard(catalog.Single(card => DeckSearchCatalog.Normalize(card.Name) == DeckSearchCatalog.Normalize(parts[0])),
            parts.Length == 2 ? int.Parse(parts[1]) : 1);
    }).ToArray();

    public static async Task<int> RunAsync(string root, string label)
    {
        if (!label.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Use an alphanumeric audit label.");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        using var scanner = new DeckBuilderScanner(catalog);
        var reports = new List<object>(); var failures = 0;
        foreach (var reference in References)
        foreach (var scan in reference.Scans)
        {
            var expected = Cards(reference, catalog); var truth = expected.ToDictionary(item => item.Card.Id, item => item.Count);
            var directory = Path.Combine(root, "GwentCompanion", "deck-scans", scan);
            var confirmedPath = Path.Combine(directory, "confirmed-deck.json");
            if (File.Exists(confirmedPath))
            {
                using var confirmed = JsonDocument.Parse(File.ReadAllText(confirmedPath));
                var cards = confirmed.RootElement.GetProperty("Cards").EnumerateArray().ToDictionary(item => item.GetProperty("Id").GetString()!, item => item.GetProperty("Count").GetInt32());
                if (cards.Count != truth.Count || cards.Any(item => truth.GetValueOrDefault(item.Key) != item.Value))
                    throw new InvalidDataException("Manual image audit disagrees with user-confirmed composition: " + scan);
            }
            var draft = new DeckScanDraft(); var pages = new List<object>(); var excessRows = new List<string>();
            foreach (var path in Directory.GetFiles(directory, "page-*.jpg").Order())
            {
                var page = await scanner.ReadAsync(DeckBuilderReplay.Restore(path), DeckBuilderScanner.LeftPanel);
                draft.Observe(page.Cards); draft.ObserveHeader(page.Leader, page.Stratagem);
                foreach (var group in page.Cards.GroupBy(item => item.Card.Id))
                    if (group.Sum(item => item.Count) > truth.GetValueOrDefault(group.Key)) excessRows.Add(Path.GetFileName(path) + ": " + group.First().Card.Name);
                pages.Add(new { Page = Path.GetFileName(path), Cards = page.Cards.Select(item => new { item.Card.Id, item.Card.Name, item.Count }),
                    page.Lines, page.Quantities, page.NameCorrections, page.UnresolvedRows, Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name });
            }
            var actual = draft.Cards.ToDictionary(item => item.Card.Id, item => item.Count);
            var missing = expected.Where(item => actual.GetValueOrDefault(item.Card.Id) < item.Count)
                .Select(item => item.Card.Name + ": " + actual.GetValueOrDefault(item.Card.Id) + "/" + item.Count).ToArray();
            var extra = draft.Cards.Where(item => item.Count > truth.GetValueOrDefault(item.Card.Id)).Select(item => item.Card.Name).ToArray();
            var orderMatches = draft.Cards.Select(item => item.Card.Id).SequenceEqual(expected.Select(item => item.Card.Id));
            // Only page 001 contains a full leader title in this four-keyframe scan. Page 002
            // visibly clips it, so unknown is the correct consensus result, not a guessed leader.
            var onlyOneHeaderFrame = scan == "20260827-145544-3e3711";
            var headerMatches = (onlyOneHeaderFrame ? draft.Leader is null : draft.Leader?.Name == reference.Leader) && draft.Stratagem?.Name == reference.Stratagem;
            var passed = missing.Length == 0 && extra.Length == 0 && excessRows.Count == 0 && orderMatches && headerMatches;
            if (!passed) failures++;
            reports.Add(new { Scan = scan, reference.Key, Reference = "Manually inspected visible rows in saved top/bottom images; compared with user confirmation where available",
                ExpectedCopies = expected.Sum(item => item.Count), Copies = draft.CardCount, Missing = missing, Extra = extra, ExcessRows = excessRows,
                OrderMatches = orderMatches, HeaderMatches = headerMatches, Leader = draft.Leader?.Name, Stratagem = draft.Stratagem?.Name,
                HeaderExpectation = onlyOneHeaderFrame ? "Leader unknown: only one saved image contains the full title; two observations required" : reference.Leader + " / " + reference.Stratagem,
                ExpectedOrder = expected.Select(item => item.Card.Name), ActualOrder = draft.Cards.Select(item => item.Card.Name), Pages = pages });
            Console.WriteLine($"AUDIT {scan}: {draft.CardCount}/{expected.Sum(item => item.Count)} copies; missing=[{string.Join(", ", missing)}]; extra={extra.Length}/{excessRows.Count}; order={orderMatches}; header={headerMatches}");
        }
        var output = Path.Combine(root, "GwentCompanion", "diagnostics", $"v0.1.8-builder-audit-{label}.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { Scope = "All five saved scans, 49 images; one observation per keyframe, not full live capture timing", Failures = failures, Scans = reports },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Audit report: " + output);
        return failures == 0 ? 0 : 1;
    }

    public static void SafetyAndOrder(string root)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        foreach (var reference in References)
        {
            var expected = Cards(reference, catalog);
            var draft = new DeckScanDraft(); draft.Load(expected.Reverse());
            Check(draft.Cards.Select(item => item.Card.Id).SequenceEqual(expected.Select(item => item.Card.Id)), "Builder sort differs from manually verified rows: " + reference.Key);
            var deck = new DeckDefinition("order", "Order check", "Neutral", reference.Leader, 0, expected.Reverse().ToArray());
            var slots = OpponentDeckProjector.Reference(deck);
            Check(slots.Select(item => item.Card!.Id).SequenceEqual(expected.SelectMany(item => Enumerable.Repeat(item.Card.Id, item.Count))), "Cached/reference list must use the same sort as the draft.");
        }
        var region = new NormalizedRegion(.062, .4, .18, .417);
        Check(DeckBuilderScanner.MatchLines([new("Saer Quian", region)], catalog).Count == 0, "Baseline apostrophe miss must be reproducible.");
        Check(DeckBuilderScanner.MatchLines([new("Saer Quian", region)], catalog, true).Single().Card.Name == "Saer Qu'an", "Only an apostrophe-position stroke should be corrected.");
        foreach (var bad in new[] { "Saer Quixan", "Saer Quian Extra", "Saer Quanx", "Saer Quan Card", "Sear Quian" })
            Check(DeckBuilderScanner.MatchLines([new(bad, region)], catalog, true).Count == 0, "Do not turn arbitrary insertion/prose into a card: " + bad);
        var card = catalog.Single(item => item.Name == "Dwarven Skirmisher");
        var leader = catalog.Single(item => item.Name == "Precision Strike");
        var otherLeader = catalog.Single(item => item.Name == "Invigorate");
        var scrolling = new DeckScanDraft();
        scrolling.Observe([new(card, 2)]); scrolling.Observe([]); scrolling.Observe([new(card)]);
        Check(scrolling.CardCount == 1, "One x2 frame plus one x1 frame confirms only one copy.");
        scrolling.Observe([]); scrolling.Observe([new(card, 2)]);
        Check(scrolling.CardCount == 2, "Two explicit x2 readings across a scroll gap confirm two, never sum four.");
        scrolling.SetCount(card, 0); scrolling.Observe([new(card, 2)]); scrolling.Observe([new(card, 2)]);
        Check(scrolling.CardCount == 0, "Manual removal must beat rolling evidence.");
        scrolling.Reset(); scrolling.Observe([new(card, 2)]);
        for (var i = 0; i < DeckScanDraft.ConsensusWindow; i++) scrolling.Observe([]);
        scrolling.Observe([new(card, 2)]);
        Check(scrolling.CardCount == 0, "Expired single sightings must not accumulate forever.");
        scrolling.BreakSequence(); scrolling.Observe([new(card, 2)]);
        Check(scrolling.CardCount == 0, "Foreground changes clear pending card evidence.");
        scrolling.ObserveHeader(leader, null); scrolling.ObserveHeader(null, null); scrolling.ObserveHeader(leader, null);
        Check(scrolling.Leader == leader, "An absent scrolled header must not erase a recent agreeing observation.");
        scrolling.Reset(); scrolling.ObserveHeader(leader, null); scrolling.ObserveHeader(otherLeader, null); scrolling.ObserveHeader(leader, null);
        Check(scrolling.Leader is null, "Conflicting recent headers must abstain.");
        scrolling.Reset(); scrolling.ObserveHeader(leader, null); scrolling.BreakSequence(); scrolling.ObserveHeader(leader, null);
        Check(scrolling.Leader is null, "Foreground changes clear pending header evidence.");
    }
}
