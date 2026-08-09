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
    /// Jikan allows roughly 3/sec and 60/min. One per second stays well inside both.
    /// </summary>
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Fetches the filler and recap episode information for a given MyAnimeList ID.
    /// </summary>
    public async Task<AnimeFillerCacheEntry?> FetchAsync(int malId, CancellationToken cancellationToken)
    {
        var flagged = new List<AnimeFillerEpisode>();
        var episodeCount = 0;
        var page = 1;

        while (page <= MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await GetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                // Failing on page 1 leaves nothing worth keeping. Yeeeeet
                if (page == 1)
                {
                    return null;
                }

                // Failing on a later page leaves the earlier pages, so keep them as a partial result.
                _logger.LogDebug("Jikan returned only {Count} episodes for MAL {MalId}, keeping them as partial", episodeCount, malId);

                return new AnimeFillerCacheEntry
                {
                    Episodes = flagged,
                    EpisodeCount = episodeCount,
                    Partial = true
                };
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

        return new AnimeFillerCacheEntry
        {
            Episodes = flagged,
            EpisodeCount = episodeCount
        };
    }

    /// <summary>
    /// The Jikan API is not reliable, so we retry a few times before giving up on a series.
    /// </summary>
    private const int MaxAttemptsPerPage = 4;

    private async Task<JikanEpisodesResponse?> GetEpisodePageAsync(int malId, int page, CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/anime/{malId}/episodes?page={page}";

        for (var attempt = 0; attempt < MaxAttemptsPerPage; attempt++)
        {
            if (attempt > 0)
            {
                // Increasing backoff: 4s, 8s, 12s. Upstream hiccups usually clear inside that.
                await Task.Delay(TimeSpan.FromSeconds(4 * attempt), cancellationToken).ConfigureAwait(false);
            }

            await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // The mapping pointed at an id MAL does not serve. Not retryable.
                    _logger.LogDebug("Jikan has no entry for MAL {MalId}", malId);
                    return null;
                }

                // 429 is us beeing to fast; 5xx is Jikan failing to reach MyAnimeList.
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    _logger.LogDebug(
                        "Jikan returned {Status} for MAL {MalId} page {Page}, attempt {Attempt}",
                        (int)response.StatusCode, malId, page, attempt + 1);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Jikan returned {Status} for MAL {MalId} page {Page}", (int)response.StatusCode, malId, page);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JikanEpisodesResponse>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Jikan request failed for MAL {MalId} page {Page}, attempt {Attempt}", malId, page, attempt + 1);
            }
        }

        _logger.LogDebug("Jikan gave up on MAL {MalId} page {Page} after {Attempts} attempts", malId, page, MaxAttemptsPerPage);
        return null;
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
