using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Capture;
using System.Windows.Media.Imaging;

internal static class StreamArchiveEvaluation
{
    public static async Task<int> ProbeCardsAsync(string gameRoot, string[] args)
    {
        var directory = Path.GetFullPath(ValueAfter(args, "--stream-card-window-probe") ??
            throw new ArgumentException("Supply --stream-card-window-probe <frame-directory>."));
        var ids = (ValueAfter(args, "--player-card-ids") ?? "").Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cache = Path.Combine(gameRoot, "GwentCompanion", "cache");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        if (ValueAfter(args, "--thinning-card-id") is { } thinningId)
        {
            var card = cards.Single(item => item.Id == thinningId);
            using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
                scope: VisionReferenceScope.CandidateDecks);
            features.SetKnownPlayerDeck([thinningId]);
            var pair = new CardFrameRecognizer(new CardArtMatcher(features.ArtReferences));
            pair.Trace = line => Console.WriteLine("  PAIR " + line);
            var paths = Directory.GetFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
            var firstStamp = long.Parse(Path.GetFileNameWithoutExtension(paths[0]).Split('-').Last());
            pair.ObserveEvents([new(DateTimeOffset.UnixEpoch.AddMilliseconds(firstStamp - 100),
                new(card, PlayerSide.User, CardSightSource.PlayPreview, new(.815, .413, .918, .665), 0, 1), "Probe")]);
            using var screenReader = new ScreenStateRecognizer { UseTextCache = false };
            foreach (var path in paths)
            {
                using var input = File.OpenRead(path);
                var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
                var screen = await screenReader.AnalyzeAsync(frame).ConfigureAwait(false);
                var hits = pair.Recognize(frame, screen, includeBoard: true);
                Console.WriteLine($"FRAME {Path.GetFileName(path)} pair-hits={hits.Count}: " +
                    string.Join(" | ", hits.Select(hit => $"{hit.Region.Left:F3}/{hit.Region.Top:F3}/{hit.Distance:F3}")));
            }
            return 0;
        }
        using var pipeline = new CardVisionPipeline(references, cards, Path.Combine(cache, "recognition-features"),
            VisionReferenceScope.CandidateDecks, allowStreamResolution: true) { UseTextCache = false };
        pipeline.SetKnownPlayerDeck(ids);
        foreach (var path in Directory.GetFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(path);
            var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            var stamp = Path.GetFileNameWithoutExtension(path).Split('-').Last();
            var at = DateTimeOffset.UnixEpoch.AddMilliseconds(long.Parse(stamp));
            var result = await pipeline.AnalyzeAsync(BitmapFrameAdapter.ToPixelFrame(bitmap), at,
                includeBoard: true).ConfigureAwait(false);
            if (result.HoveredCard is null && result.Sightings.Count == 0 && result.Events.Count == 0) continue;
            Console.WriteLine($"FRAME {Path.GetFileName(path)} hover={result.HoveredCard?.Name ?? "-"} " +
                $"hand={result.HoverInPlayerHand}/{result.PointerInPlayerHand}");
            foreach (var sight in result.Sightings)
                Console.WriteLine($"  SIGHT {sight.Side} {sight.Source} {sight.Card.Name} d={sight.Distance:F3} " +
                    $"confirm={sight.NeedsTemporalConfirmation} region={sight.Region} evidence={sight.Evidence}");
            foreach (var evidence in result.Events)
                Console.WriteLine($"  EVENT {evidence.Sighting.Side} {evidence.Sighting.Source} " +
                    $"{evidence.Sighting.Card.Name}: {evidence.Description}");
        }
        return 0;
    }

    public static int ProbeDeckGate(string[] args)
    {
        var directory = Path.GetFullPath(ValueAfter(args, "--stream-deck-gate-probe") ??
            throw new ArgumentException("Supply --stream-deck-gate-probe <frame-directory>."));
        foreach (var path in Directory.GetFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(path);
            var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
            var counts = StreamDeckBuilderGate.RowBandCounts(frame);
            Console.WriteLine($"{Path.GetFileName(path)} left={counts.Left} right={counts.Right} " +
                $"loose={StreamDeckBuilderGate.LooksLikeDeckList(frame)} " +
                $"strong={StreamDeckBuilderGate.LooksLikeDeckListDespiteApparentHud(frame)}");
        }
        return 0;
    }

    public static async Task<int> ProbeScoresAsync(string[] args)
    {
        var directory = Path.GetFullPath(ValueAfter(args, "--stream-score-window-probe") ??
            throw new ArgumentException("Supply --stream-score-window-probe <frame-directory>."));
        using var reader = new ScreenStateRecognizer { UseTextCache = false };
        var gate = new StreamTerminalScoreGate();
        (int UserScore, int OpponentScore)? confirmed = null;
        foreach (var path in Directory.GetFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(path);
            var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
            var user = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame,
                new(.90, .625, .995, .69), reader).ConfigureAwait(false);
            var opponent = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame,
                new(.90, .315, .995, .385), reader).ConfigureAwait(false);
            var screen = await reader.AnalyzeAsync(frame).ConfigureAwait(false);
            var center = string.Join(' ', (await reader.ReadLinesAsync(frame,
                new(.30, .35, .70, .61), scale: 4, enhance: true, whiteLetterMask: true).ConfigureAwait(false))
                .Select(line => line.Text));
            var milliseconds = Path.GetFileNameWithoutExtension(path).Split('-').Last();
            var seconds = long.TryParse(milliseconds, out var parsed) ? parsed / 1000d : 0;
            confirmed = gate.Observe(seconds, user, opponent);
            Console.WriteLine($"{Path.GetFileName(path)} raw={user?.ToString() ?? "?"}-" +
                $"{opponent?.ToString() ?? "?"} confirmed=" +
                (confirmed is { } pair ? $"{pair.UserScore}-{pair.OpponentScore}" : "?") +
                $" header={screen.ScreenHeader ?? "?"} center={center}");
        }
        Console.WriteLine("DONE terminal-score=" +
            (confirmed is { } final ? $"{final.UserScore}-{final.OpponentScore}" : "unconfirmed"));
        return 0;
    }

    public static async Task<int> RunAsync(string gameRoot, string[] args)
    {
        var media = ValueAfter(args, "--stream-archive-evaluate") ??
            throw new ArgumentException("Supply --stream-archive-evaluate <direct-media-source>.");
        var sourceUrl = ValueAfter(args, "--source-url") ??
            throw new ArgumentException("Supply --source-url <canonical-public-page>.");
        var outputRoot = Path.GetFullPath(ValueAfter(args, "--output-root") ??
            throw new ArgumentException("Supply --output-root <directory>.") );
        if (!StreamSourceIdentity.TryCreate(sourceUrl, out var source) || source is null)
            throw new ArgumentException("The canonical stream source URL is unsupported.");

        var cache = Path.Combine(gameRoot, "GwentCompanion", "cache");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        var games = Path.Combine(outputRoot, "games");
        var validation = Path.Combine(outputRoot, "anonymized-validation-working");
        Directory.CreateDirectory(games);
        Directory.CreateDirectory(validation);
        var scanner = new StreamArchiveScanner(cards, references, Path.Combine(cache, "recognition-features"), "0.2.102-full-audit");
        if (args.Contains("--trace-events")) scanner.EventTrace = Console.WriteLine;
        var progress = new Progress<StreamScanProgress>(item =>
            Console.WriteLine($"{item.Stage}|{item.Fraction:F3}|games={item.GamesFound}|{item.Message}"));
        if (args.Contains("--index-only"))
        {
            var index = await scanner.IndexAsync(media, progress).ConfigureAwait(false);
            var indexPath = Path.Combine(outputRoot, "index-summary.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"DONE stream-index games={index.Games.Length} decks={index.Decks.Length} " +
                $"duration={index.DurationSeconds:F1}s summary={indexPath}");
            return 0;
        }
        StreamDeckEvidence? sourceDeck = null;
        if (ValueAfter(args, "--deck-url") is { } deckUrl)
        {
            var entries = DeckLinkFileReader.FromText(deckUrl, "Full stream audit");
            var cached = await new PlayGwentDeckCacheService().SyncAsync(entries,
                Path.Combine(gameRoot, "GwentCompanion", "cache", "stream-decks"), maximumDecks: 3).ConfigureAwait(false);
            var deck = cached.Decks.OrderByDescending(item => item.CardCount).FirstOrDefault();
            if (deck is not null) sourceDeck = StreamDeckEvidence.FromDeck(deck);
        }
        StreamScanResult result;
        if (ValueAfter(args, "--index-summary") is { } savedIndexPath)
        {
            var index = JsonSerializer.Deserialize<StreamScanIndex>(File.ReadAllText(Path.GetFullPath(savedIndexPath))) ??
                throw new InvalidDataException("The supplied stream index was empty.");
            if (ValueAfter(args, "--game-index") is { } requestedText)
            {
                if (!int.TryParse(requestedText, out var requested) || requested <= 0)
                    throw new ArgumentException("--game-index must be a positive integer.");
                var selected = index.Games.Where(game => game.Index == requested).ToArray();
                if (selected.Length != 1) throw new ArgumentException($"Game {requested} is not present in the supplied index.");
                index = index with { Games = selected };
            }
            result = await scanner.ScanIndexedAsync(media, source, ValueAfter(args, "--title"),
                ValueAfter(args, "--channel"), games, validation, index, sourceDeck, progress).ConfigureAwait(false);
        }
        else
        {
            if (ValueAfter(args, "--game-index") is not null)
                throw new ArgumentException("--game-index requires --index-summary.");
            result = await scanner.ScanAsync(media, source, ValueAfter(args, "--title"),
                ValueAfter(args, "--channel"), games, validation, sourceDeck, progress).ConfigureAwait(false);
        }
        var sourceFolder = Path.Combine(games, source.SafeKey);
        var storedPaths = Directory.Exists(sourceFolder)
            ? Directory.GetFiles(sourceFolder, "*.gvs.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : result.RecordPaths;
        var records = storedPaths.Select(path => JsonSerializer.Deserialize<StreamGameRecord>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Empty stream game record: " + path)).ToArray();
        var summaryPath = Path.Combine(outputRoot, "audit-summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            result.Source,
            GamesFound = records.Length,
            GamesSaved = records.Length,
            ValidationFramesSaved = Directory.GetFiles(validation, "*.jpg", SearchOption.AllDirectories).Length,
            result.DurationSeconds,
            result.Warnings,
            Records = records.Select(record => new
            {
                record.StreamTag, record.GameIndex, record.StartSeconds, record.EndSeconds, record.Reliability,
                record.AnalyzedFrames, record.ReducedFrames, record.SkippedObscuredFrames,
                PlayerDeck = record.PlayerDeck is null ? null : new { record.PlayerDeck.LeaderName,
                    record.PlayerDeck.StratagemName, CardCount = record.PlayerDeck.Cards.Sum(card => card.Copies),
                    record.PlayerDeck.CompleteEnoughToUse },
                Cards = record.Cards.Select(card => new { card.Name, Side = card.Side.ToString(), card.ObservedCopies, card.EvidenceEvents }),
                record.FinalUserScore, record.FinalOpponentScore, record.Result, record.Rank, record.Mmr
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE full-stream games-found={records.Length} games-saved={records.Length} " +
            $"validation={Directory.GetFiles(validation, "*.jpg", SearchOption.AllDirectories).Length} " +
            $"duration={result.DurationSeconds:F1}s summary={summaryPath}");
        return 0;
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
