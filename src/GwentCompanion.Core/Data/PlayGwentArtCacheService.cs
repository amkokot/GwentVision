using System.Collections.Concurrent;
using System.Net.Http.Headers;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record ArtCacheProgress(int Completed, int Requested, int Downloaded, int Cached, int Failed);

public sealed record ArtCacheSyncResult(
    int Requested,
    int Downloaded,
    int LoadedFromCache,
    IReadOnlyList<string> Errors);

public sealed class PlayGwentArtCacheService
{
    private readonly HttpClient _httpClient;

    public PlayGwentArtCacheService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GwentCompanion", "0.1"));
        }
    }

    public async Task<ArtCacheSyncResult> SyncAsync(
        IEnumerable<DeckDefinition> decks,
        string cacheDirectory,
        IProgress<ArtCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var cards = decks
            .SelectMany(deck => deck.Cards)
            .Select(item => item.Card)
            .Where(card => card.ArtUri is not null)
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var errors = new ConcurrentBag<string>();
        var completed = 0;
        var downloaded = 0;
        var cached = 0;
        using var concurrency = new SemaphoreSlim(6, 6);

        var tasks = cards.Select(async card =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var path = Path.Combine(cacheDirectory, $"{card.Id}.jpg");
                if (File.Exists(path) && new FileInfo(path).Length > 256)
                {
                    Interlocked.Increment(ref cached);
                    return;
                }

                var bytes = await _httpClient.GetByteArrayAsync(card.ArtUri!, cancellationToken).ConfigureAwait(false);
                if (bytes.Length <= 256)
                {
                    throw new InvalidDataException("The downloaded art file was unexpectedly small.");
                }

                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref downloaded);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{card.Id} {card.Name}: {exception.Message}");
            }
            finally
            {
                concurrency.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ArtCacheProgress(
                    done,
                    cards.Length,
                    Volatile.Read(ref downloaded),
                    Volatile.Read(ref cached),
                    errors.Count));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ArtCacheSyncResult(cards.Length, downloaded, cached, errors.ToArray());
    }
}
