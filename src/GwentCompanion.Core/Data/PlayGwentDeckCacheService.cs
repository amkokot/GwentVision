using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record DeckCacheProgress(int Completed, int Requested, int Loaded, int Expired, int Failed);

public sealed record DeckCacheSyncResult(
    IReadOnlyList<DeckDefinition> Decks,
    int Requested,
    int Downloaded,
    int LoadedFromCache,
    int Expired,
    IReadOnlyList<string> Errors);

public sealed class PlayGwentDeckCacheService
{
    private readonly HttpClient _httpClient;
    private readonly PlayGwentDeckPageParser _parser = new();

    public PlayGwentDeckCacheService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GwentCompanion", "0.1"));
        }
    }

    public async Task<DeckCacheSyncResult> SyncAsync(
        IEnumerable<DeckIndexEntry> sourceEntries,
        string cacheDirectory,
        int maximumDecks,
        IProgress<DeckCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (maximumDecks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecks));
        }

        Directory.CreateDirectory(cacheDirectory);
        var entries = SelectEntries(sourceEntries, maximumDecks);
        var decks = new ConcurrentBag<DeckDefinition>();
        var errors = new ConcurrentBag<string>();
        var completed = 0;
        var downloaded = 0;
        var cacheHits = 0;
        var expired = 0;
        using var concurrency = new SemaphoreSlim(4, 4);

        var tasks = entries.Select(async entry =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var hash = DeckHash(entry.DeckUri);
                var cachePath = Path.Combine(cacheDirectory, $"{hash}.json");
                var expiredPath = Path.Combine(cacheDirectory, $"{hash}.expired");
                if (File.Exists(expiredPath) &&
                    DateTimeOffset.Now - File.GetLastWriteTimeUtc(expiredPath) < TimeSpan.FromDays(7))
                {
                    Interlocked.Increment(ref expired);
                    return;
                }

                string stateJson;
                DeckDefinition? parsed = null;
                if (File.Exists(cachePath))
                {
                    stateJson = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    try { parsed = ParseValidated(stateJson, entry); Interlocked.Increment(ref cacheHits); }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
                    { /* Re-fetch corrupt payloads; never delete the last file before a replacement is valid. */ }
                }
                if (parsed is null)
                {
                    var html = await _httpClient.GetStringAsync(DeckLinkFileReader.CanonicalUrl(entry.DeckUri)!, cancellationToken).ConfigureAwait(false);
                    stateJson = _parser.ExtractStateJson(html);
                    parsed = ParseValidated(stateJson, entry);
                    var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await File.WriteAllTextAsync(temporary, stateJson, cancellationToken).ConfigureAwait(false);
                        File.Move(temporary, cachePath, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    if (File.Exists(expiredPath))
                    {
                        File.Delete(expiredPath);
                    }

                    Interlocked.Increment(ref downloaded);
                }

                decks.Add(parsed with { LastEdited = entry.LastEdited ?? parsed.LastEdited, CachedAt = File.GetLastWriteTimeUtc(cachePath) });
            }
            catch (InvalidDataException)
            {
                var expiredPath = Path.Combine(cacheDirectory, $"{DeckHash(entry.DeckUri)}.expired");
                await File.WriteAllTextAsync(
                    expiredPath,
                    DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref expired);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // Dead public links are unavailable, not transient import failures.
                await File.WriteAllTextAsync(Path.Combine(cacheDirectory, $"{DeckHash(entry.DeckUri)}.expired"),
                    $"{DateTimeOffset.UtcNow:O} HTTP {(int)exception.StatusCode.Value}", cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref expired);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{entry.DeckUri}: request timed out ({exception.Message})");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{entry.DeckUri}: {exception.Message}");
            }
            finally
            {
                concurrency.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DeckCacheProgress(
                    done,
                    entries.Length,
                    decks.Count,
                    Volatile.Read(ref expired),
                    errors.Count));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new DeckCacheSyncResult(
            decks.OrderBy(deck => deck.RecencyRank).ToArray(),
            entries.Length,
            downloaded,
            cacheHits,
            expired,
            errors.ToArray());
    }

    public IReadOnlyList<DeckDefinition> LoadCached(
        IEnumerable<DeckIndexEntry> sourceEntries,
        string cacheDirectory,
        int maximumDecks)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (maximumDecks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecks));
        }

        if (!Directory.Exists(cacheDirectory))
        {
            return Array.Empty<DeckDefinition>();
        }

        var decks = new List<DeckDefinition>();
        foreach (var entry in SelectEntries(sourceEntries, maximumDecks))
        {
            var path = Path.Combine(cacheDirectory, $"{DeckHash(entry.DeckUri)}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var parsed = ParseValidated(File.ReadAllText(path), entry);
                decks.Add(parsed with { LastEdited = entry.LastEdited ?? parsed.LastEdited, CachedAt = File.GetLastWriteTimeUtc(path) });
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                // A corrupt or obsolete cache entry is skipped and can be refreshed later.
            }
        }

        return decks.OrderBy(deck => deck.RecencyRank).ToArray();
    }

    private static DeckIndexEntry[] SelectEntries(IEnumerable<DeckIndexEntry> entries, int maximumDecks) =>
        entries
            .Where(entry => DeckLinkFileReader.CanonicalUrl(entry.DeckUri) is not null)
            .Select(entry => entry with { Occurrences = entry.Occurrences is { Count: > 0 } ? entry.Occurrences : DeckOccurrences.FromIndex(entry) })
            .GroupBy(entry => DeckHash(entry.DeckUri), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.LastEdited).ThenBy(item => item.RecencyRank).First()
                with { Patches = DeckPatchMetadata.Merge(group.SelectMany(item => item.Patches ?? [])),
                    Occurrences = DeckOccurrences.Merge(group.SelectMany(item => item.Occurrences ?? [])) })
            .OrderByDescending(entry => entry.LastEdited)
            .ThenBy(entry => entry.RecencyRank)
            .Take(maximumDecks)
            .ToArray();

    public static string CacheKey(Uri uri)
    {
        var hash = uri.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(hash) || !hash.All(character => char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException($"The deck URL does not end in a valid hash: {uri}");
        }

        return uri.AbsolutePath.Contains("/decks/guides/", StringComparison.OrdinalIgnoreCase) ? "guide-" + hash : hash;
    }

    private static string DeckHash(Uri uri) => CacheKey(uri);

    private DeckDefinition ParseValidated(string json, DeckIndexEntry entry)
    {
        var deck = _parser.ParseStateJson(json, entry.DeckUri, entry.Name, entry.RecencyRank);
        var guide = entry.DeckUri.AbsolutePath.Contains("/decks/guides/", StringComparison.OrdinalIgnoreCase);
        if ((!guide && !string.Equals(deck.Id, DeckHash(entry.DeckUri), StringComparison.OrdinalIgnoreCase)) ||
            deck.Id.Length is < 16 or > 64 || !deck.Id.All(char.IsAsciiLetterOrDigit) ||
            string.IsNullOrWhiteSpace(deck.Leader) || deck.CardCount < 25 || deck.CardCount > 100 ||
            deck.Cards.Any(item => item.Count < 1 || item.Count > (item.Card.IsGold ? 1 : 2) ||
                item.Card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact) || string.IsNullOrWhiteSpace(item.Card.Name)))
            throw new InvalidDataException("The public payload is not a complete deck with valid card quantities.");
        // Historical lists may exceed today's provisions after balance changes; retain them.
        return deck with { Patches = entry.Patches, Occurrences = entry.Occurrences,
            LastEdited = entry.LastEdited ?? DeckPatchMetadata.HistoricalDate(entry.Patches, deck.LastEdited) ?? deck.LastEdited };
    }
}
