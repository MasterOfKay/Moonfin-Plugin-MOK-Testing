using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fetches filler and recap episode information for anime series from MyAnimeList via the Jikan API.
/// </summary>
public class AnimeFillerFetchService
{
    private const string ApiBase = "https://api.jikan.moe/v4";

    /// <summary>
    /// Jikan allows roughly 3/sec and 60/min.
    /// </summary>
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Guards against a malformed pagination response spinning forever. 100 eps/page.
    /// </summary>
    private const int MaxPages = 30;

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeFillerFetchService> _logger;

    public AnimeFillerFetchService(IHttpClientFactory httpClientFactory, ILogger<AnimeFillerFetchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static bool Verbose =>
        MoonfinPlugin.Instance?.Configuration?.AnimeFillerVerboseLogging == true;

    /// <summary>
    /// Anime Filler is super buggy and may have many problems. 
    /// Debugging is needed. This is a easy way.
    /// </summary>
    private void LogDetail(string message, params object?[] args)
    {
        if (Verbose)
        {
#pragma warning disable CA2254
            _logger.LogInformation(message, args);
        }
        else
        {
            _logger.LogDebug(message, args);
        }
#pragma warning restore CA2254
    }

    private void LogDetail(Exception ex, string message, params object?[] args)
    {
        if (Verbose)
        {
#pragma warning disable CA2254
            _logger.LogInformation(ex, message, args);
        }
        else
        {
            _logger.LogDebug(ex, message, args);
        }
#pragma warning restore CA2254
    }

    /// <summary>
    /// Fetches the filler and recap episode information for a given MyAnimeList ID.
    /// </summary>
    public async Task<FillerFetchResult> FetchAsync(int malId, CancellationToken cancellationToken)
    {
        var flagged = new List<AnimeFillerEpisode>();
        var episodeCount = 0;
        var page = 1;

        while (page <= MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (response, blocked) = await GetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                // The API refused to serve this entry, or we are being throttled. Either way, stop here.
                if (blocked)
                {
                    return FillerFetchResult.Blocked();
                }

                // Failing on page 1 leaves nothing worth keeping. Yeeeeet
                if (page == 1)
                {
                    return FillerFetchResult.Unavailable();
                }

                // Failing on a later page leaves the earlier pages, so keep them as a partial result.
                LogDetail("Jikan returned only {Count} episodes for MAL {MalId}, keeping them as partial", episodeCount, malId);

                return FillerFetchResult.Ok(new AnimeFillerCacheEntry
                {
                    Episodes = flagged,
                    EpisodeCount = episodeCount,
                    Partial = true
                });
            }

            foreach (var episode in response.Data)
            {
                episodeCount++;

                if (episode.Filler || episode.Recap)
                {
                    flagged.Add(new AnimeFillerEpisode
                    {
                        Number = episode.MalId,
                        Filler = episode.Filler,
                        Recap = episode.Recap
                    });
                }
            }

            if (response.Pagination?.HasNextPage != true)
            {
                break;
            }

            page++;
        }

        LogDetail(
            "Jikan returned MAL {MalId}: {Episodes} episodes, {Flagged} flagged",
            malId, episodeCount, flagged.Count);

        return FillerFetchResult.Ok(new AnimeFillerCacheEntry
        {
            Episodes = flagged,
            EpisodeCount = episodeCount
        });
    }

    /// <summary>
    /// The Jikan API is not reliable, so we retry a few times before giving up on a series.
    /// </summary>
    private const int MaxAttemptsPerPage = 3;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Used when we are throttled but told no Retry-After.
    /// </summary>
    private static readonly TimeSpan DefaultThrottleWait = TimeSpan.FromSeconds(10);

    private async Task<(JikanEpisodesResponse? Page, bool Blocked)> GetEpisodePageAsync(
        int malId, int page, CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/anime/{malId}/episodes?page={page}";
        var lastStatus = 0;
        var blocked = false;

        for (var attempt = 0; attempt < MaxAttemptsPerPage; attempt++)
        {
            await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // The mapping pointed at an id MAL does not serve. Not retryable.
                    LogDetail("Jikan has no entry for MAL {MalId}", malId);
                    return (null, false);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // The API is throttling us. Wait and retry, but don't count this as a failure for the series.
                    blocked = true;

                    var retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);

                    await Task.Delay(
                        retryAfter is { } wait && wait > TimeSpan.Zero
                            ? (wait > MaxRetryAfter ? MaxRetryAfter : wait)
                            : DefaultThrottleWait,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    // The API is having a server-side problem. Wait and retry, but don't count this as a failure for the series.
                    LogDetail(
                        "Jikan returned {Status} for MAL {MalId} page {Page}, attempt {Attempt}",
                        (int)response.StatusCode, malId, page, attempt + 1);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogDetail("Jikan returned {Status} for MAL {MalId} page {Page}", (int)response.StatusCode, malId, page);
                    return (null, false);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (JsonSerializer.Deserialize<JikanEpisodesResponse>(json, JsonOptions), false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No response at all: DNS, TLS, timeout, no route. That is the server's
                // connection, not this entry, so treat it the same as being throttled.
                blocked = true;
                LogDetail(ex, "Jikan request failed for MAL {MalId} page {Page}, attempt {Attempt}", malId, page, attempt + 1);
            }
        }

        LogDetail(
            "Jikan gave up on MAL {MalId} page {Page} after {Attempts} attempts, last status {Status}",
            malId, page, MaxAttemptsPerPage, lastStatus);

        return (null, blocked);
    }

    /// <summary>
    /// Spaces requests process-wide so the scan and any on-demand lookup share one budget.
    /// </summary>
    private static async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
            if (sinceLast < MinRequestSpacing)
            {
                await Task.Delay(MinRequestSpacing - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    public enum FillerFetchStatus
    {
        Success,
        Unavailable,
        Blocked
    }

    public readonly record struct FillerFetchResult(FillerFetchStatus Status, AnimeFillerCacheEntry? Entry)
    {
        public static FillerFetchResult Ok(AnimeFillerCacheEntry entry) => new(FillerFetchStatus.Success, entry);
        public static FillerFetchResult Unavailable() => new(FillerFetchStatus.Unavailable, null);
        public static FillerFetchResult Blocked() => new(FillerFetchStatus.Blocked, null);
    }

    private class JikanEpisodesResponse
    {
        [JsonPropertyName("data")]
        public List<JikanEpisode> Data { get; set; } = new();

        [JsonPropertyName("pagination")]
        public JikanPagination? Pagination { get; set; }
    }

    private class JikanPagination
    {
        [JsonPropertyName("has_next_page")]
        public bool HasNextPage { get; set; }
    }

    private class JikanEpisode
    {
        /// <summary>
        /// On the episodes endpoint this is the episode number within the MAL entry.
        /// </summary>
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("filler")]
        public bool Filler { get; set; }

        [JsonPropertyName("recap")]
        public bool Recap { get; set; }
    }
}
