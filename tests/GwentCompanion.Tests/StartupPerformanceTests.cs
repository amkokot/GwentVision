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
        var workbookEntries = WorkbookIndexCache.LoadOrRead(root, Path.Combine(project, "cache", "workbook-index.json"));
        var workbookMs = clock.Elapsed.TotalMilliseconds;

        var definitions = library.Decks.SelectMany(deck => deck.Cards.Select(card => card.Card)
                .Concat(deck.Stratagem is null ? [] : [deck.Stratagem]))
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var conflicting = definitions.Count(group => group.Select(Signature).Distinct(StringComparer.Ordinal).Skip(1).Any());
        Console.WriteLine($"STARTUP library={libraryMs:F0}ms ({new FileInfo(libraryPath).Length / 1048576d:F1} MB), " +
            $"workbook={workbookMs:F0}ms ({workbookEntries.Length} links), decks={library.Decks.Length}, cards={definitions.Length}, conflicting-card-ids={conflicting}.");

        if (!includeVision) return;
        clock.Restart();
        var catalog = BuiltInCardCatalog.Merge(library.Decks.SelectMany(deck => deck.Cards).Select(card => card.Card)
            .Concat(GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"))));
        var references = VisionReferenceLibrary.Load(catalog, Path.Combine(project, "cache"));
        var referenceListMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var scope = Environment.GetCommandLineArgs().Contains("--scoped") ? VisionReferenceScope.CandidateDecks : VisionReferenceScope.FullCatalog;
        using var pipeline = new CardVisionPipeline(references, catalog, Path.Combine(project, "cache", "recognition-features"), scope);
        if (scope == VisionReferenceScope.CandidateDecks)
        {
            var own = library.Decks.First(deck => deck.CardCount >= 25).Cards.Select(card => card.Card.Id);
            var likely = library.Decks.Skip(1).Take(4).SelectMany(deck => deck.Cards).Select(card => card.Card.Id).Distinct().Take(70);
            pipeline.SetKnownPlayerDeck(own); pipeline.SetLikelyOpponentCards(likely);
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
