using System.Diagnostics;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Platform.Windows.Vision;

internal static class StartupPerformanceTests
{
    public static void Run(string root, bool includeVision)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var libraryPath = Path.Combine(project, "cache", "deck-library.json");
        var clock = Stopwatch.StartNew();
        var library = DeckLibrary.Load(libraryPath);
        var libraryMs = clock.Elapsed.TotalMilliseconds;
        if (Environment.GetCommandLineArgs().Contains("--migrate")) library.Save(libraryPath);

        clock.Restart();
        library.EnsureVariationGroups();
        var groupingMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var workbookEntries = WorkbookIndexCache.LoadOrRead(root, Path.Combine(project, "cache", "workbook-index.json"));
        var workbookMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var publicCatalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var catalogMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var currentDecks = library.Decks.Select(new CurrentCardValues(publicCatalog).Deck).ToArray();
        var currentValuesMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var searchItems = DeckSearchCatalog.Build(workbookEntries.Concat(library.ImportedLinks), currentDecks, library);
        var searchMs = clock.Elapsed.TotalMilliseconds;

        var saveMs = -1d;
        if (Environment.GetCommandLineArgs().Contains("--save"))
        {
            var temporary = Path.Combine(Path.GetTempPath(), "gwent-vision-startup-profile-" + Guid.NewGuid().ToString("N") + ".json");
            try { clock.Restart(); library.Save(temporary); saveMs = clock.Elapsed.TotalMilliseconds; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(temporary + ".bak")) File.Delete(temporary + ".bak"); }
        }
        var mergeMs = -1d;
        if (Environment.GetCommandLineArgs().Contains("--merge"))
        {
            clock.Restart(); library.Merge(currentDecks); mergeMs = clock.Elapsed.TotalMilliseconds;
        }

        var definitions = library.Decks.SelectMany(deck => deck.Cards.Select(card => card.Card)
                .Concat(deck.Stratagem is null ? [] : [deck.Stratagem]))
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var conflicting = definitions.Count(group => group.Select(Signature).Distinct(StringComparer.Ordinal).Skip(1).Any());
        Console.WriteLine($"STARTUP library={libraryMs:F0}ms ({new FileInfo(libraryPath).Length / 1048576d:F1} MB), grouping={groupingMs:F0}ms, " +
            $"workbook={workbookMs:F0}ms ({workbookEntries.Length} links), catalog={catalogMs:F0}ms, values={currentValuesMs:F0}ms, " +
            $"search={searchMs:F0}ms ({searchItems.Count} items), save={(saveMs < 0 ? "skipped" : $"{saveMs:F0}ms")}, merge={(mergeMs < 0 ? "skipped" : $"{mergeMs:F0}ms")}, " +
            $"decks={library.Decks.Length}, cards={definitions.Length}, conflicting-card-ids={conflicting}.");

        if (!includeVision) return;
        clock.Restart();
        var catalog = BuiltInCardCatalog.Merge(library.Decks.SelectMany(deck => deck.Cards).Select(card => card.Card)
            .Concat(publicCatalog));
        var references = VisionReferenceLibrary.Load(catalog, Path.Combine(project, "cache"));
        var referenceListMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var scope = Environment.GetCommandLineArgs().Contains("--scoped") ? VisionReferenceScope.CandidateDecks : VisionReferenceScope.FullCatalog;
        using var pipeline = new CardVisionPipeline(references, catalog, Path.Combine(project, "cache", "recognition-features"), scope);
        if (scope == VisionReferenceScope.CandidateDecks)
        {
            var own = library.Decks.First(deck => deck.CardCount >= 25).Cards.Select(card => card.Card.Id);
            pipeline.SetKnownPlayerDeck(own);
            if (!Environment.GetCommandLineArgs().Contains("--player-only"))
            {
                var likely = library.Decks.Skip(1).Take(4).SelectMany(deck => deck.Cards).Select(card => card.Card.Id).Distinct().Take(70);
                pipeline.SetLikelyOpponentCards(likely);
            }
        }
        Console.WriteLine($"STARTUP references={referenceListMs:F0}ms ({references.Count} images), vision={clock.Elapsed.TotalMilliseconds:F0}ms " +
            $"(load={pipeline.ReferenceLoadMilliseconds:F0}ms, train={pipeline.MatcherTrainMilliseconds:F0}ms; " +
            $"{pipeline.CachedReferenceImages} cached, {pipeline.ComputedReferenceImages} computed).");
    }

    private static string Signature(CardDefinition card) => string.Join('|', card.Name, card.Faction, card.Kind, card.Provision,
        card.Power, card.IsGold, card.AbilityText, card.CanBeInStartingDeck, card.PrintedArmor,
        string.Join(',', card.Categories.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(',', card.SecondaryFactions.Order(StringComparer.OrdinalIgnoreCase)));
}
