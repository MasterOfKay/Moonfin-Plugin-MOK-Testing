using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves filler, canon and recap markers for anime episodes.
/// </summary>
[ApiController]
[Route("Moonfin/AnimeMarkers")]
public class AnimeMarkersController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly AnimeFillerListClient _client;
    private readonly AnimeMarkerResolver _resolver;
    private readonly AnimeMarkerCacheService _cache;
    private readonly ILogger<AnimeMarkersController> _logger;

    public AnimeMarkersController(
        ILibraryManager libraryManager,
        AnimeFillerListClient client,
        AnimeMarkerResolver resolver,
        AnimeMarkerCacheService cache,
        ILogger<AnimeMarkersController> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _resolver = resolver;
        _cache = cache;
        _logger = logger;
    }

    private static TimeSpan CacheMaxAge =>
        TimeSpan.FromDays(Math.Max(1, MoonfinPlugin.Instance?.Configuration?.AnimeMarkerMaxAgeDays ?? 30));

    /// <summary>
    /// Markers for the episodes of one series.
    ///
    /// Reads the cache only and never touches the network, so a client can call it on every
    /// episode-list render without inheriting an upstream site's latency. A series whose
    /// data has not been fetched yet answers with <c>pending: true</c>.
    ///
    /// Answers with <c>enabled: false</c> rather than an error when the feature is off, so
    /// a client can call it all the time and simply render nothing.
    /// </summary>
    [HttpGet("Series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetSeriesMarkers(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        if (MoonfinPlugin.Instance?.Configuration?.AnimeMarkersEnabled != true)
        {
            return Ok(new { enabled = false, matched = false, episodes = new Dictionary<string, object>() });
        }

        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        // Loads from disk on the first call after a restart.
        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        var result = _resolver.GetMarkersForSeries(series, CacheMaxAge);

        return Ok(new
        {
            enabled = true,
            matched = result.Slug != null,
            slug = result.Slug,
            title = result.Title,
            pending = result.Pending,
            recapKnown = result.RecapKnown,
            episodes = result.Episodes.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    kind = pair.Value.Kind,

                    filler = pair.Value.Kind == AnimeEpisodeKind.Filler,
                    recap = pair.Value.Recap
                })
        });
    }

    /// <summary>
    /// What the feature currently knows: how many series matched a show, how many are
    /// waiting on a fetch, and which matched nothing. Intended for the admin page and for
    /// pasting into a bug report.
    /// </summary>
    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        if (!_client.CatalogLoaded)
        {
            return Ok(new
            {
                enabled = configuration?.AnimeMarkersEnabled ?? false,
                catalogLoaded = false,
                error = "The AnimeFillerList show catalogue could not be downloaded. Check that the server can reach animefillerlist.com."
            });
        }

        var candidates = _resolver.GetCandidateSeries();
        var fresh = _cache.GetFreshSlugs(CacheMaxAge);

        var matched = new List<object>();
        var unmatched = new List<string>();
        var pendingShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var series in candidates)
        {
            var show = _resolver.MatchSeries(series);
            if (show == null)
            {
                unmatched.Add(series.Name ?? series.Id.ToString("N"));
                continue;
            }

            var cached = fresh.Contains(show.Slug);
            if (!cached)
            {
                pendingShows.Add(show.Slug);
            }

            matched.Add(new { series = series.Name, slug = show.Slug, title = show.Title, cached });
        }

        var configuredLibraries = configuration?.AnimeMarkerLibraryIds ?? new List<string>();
        var resolvedLibraries = _resolver.GetSelectedLibraryFolderIds();

        return Ok(new
        {
            enabled = configuration?.AnimeMarkersEnabled ?? false,
            catalogLoaded = true,
            catalogShows = _client.CatalogCount,
            catalogDownloadedAt = _client.CatalogDownloadedAt,
            libraryFilterActive = resolvedLibraries != null,
            configuredLibraryCount = configuredLibraries.Count,
            resolvedLibraryCount = resolvedLibraries?.Count ?? 0,
            seriesConsidered = candidates.Count,
            seriesMatched = matched.Count,
            showsPending = pendingShows.Count,
            showsCached = _cache.EntryCount(),
            episodesClassified = _cache.TotalEpisodeCount(),
            episodesFlagged = _cache.FlaggedEpisodeCount(),

            // Capped so a large library cannot turn the admin page into a wall of text.
            matches = matched.Take(200).ToList(),
            unmatchedCount = unmatched.Count,
            unmatched = unmatched.Take(50).ToList()
        });
    }

    /// <summary>
    /// What the feature currently knows about one series, including the episode list and
    /// which episodes are marked as filler, recap or canon.
    /// </summary>
    [HttpGet("Preview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetPreview(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        var show = _resolver.MatchSeries(series);
        if (show == null)
        {
            return Ok(new
            {
                series = series.Name,
                matched = false,
                hint = "No AnimeFillerList show has this title. Check the site's spelling for it."
            });
        }

        var entry = _resolver.GetCachedEntry(series, CacheMaxAge);
        if (entry == null)
        {
            return Ok(new
            {
                series = series.Name,
                matched = true,
                slug = show.Slug,
                title = show.Title,
                pending = true,
                hint = "Matched, but not fetched yet. Use Fetch now, or run the Anime Markers Sync task."
            });
        }

        var markersByNumber = entry.Episodes.ToDictionary(episode => episode.Number);
        var numbering = _resolver.BuildNumbering(series);

        var rows = numbering.Episodes.Select(numbered =>
        {
            markersByNumber.TryGetValue(numbered.AbsoluteNumber, out var marker);

            return new
            {
                season = numbered.Episode.ParentIndexNumber,
                index = numbered.Episode.IndexNumber,
                absolute = numbered.AbsoluteNumber,
                name = numbered.Episode.Name,
                kind = marker?.Kind.ToString(),
                recap = marker?.Recap ?? false,
                marked = marker != null
            };
        }).ToList();

        return Ok(new
        {
            series = series.Name,
            matched = true,
            slug = show.Slug,
            title = show.Title,
            pending = false,
            recapKnown = entry.RecapMalId != null,
            numbering = numbering.Mode,
            showEpisodes = entry.Episodes.Count,
            libraryEpisodes = rows.Count,
            markedEpisodes = rows.Count(row => row.marked),
            fillerEpisodes = rows.Count(row => row.kind == nameof(AnimeEpisodeKind.Filler)),
            episodes = rows
        });
    }

    /// <summary>
    /// Fetches one series' show page immediately instead of waiting for the nightly task.
    /// </summary>
    [HttpPost("Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> RefreshSeries(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        var show = _resolver.MatchSeries(series);
        if (show == null)
        {
            return Ok(new { matched = false, series = series.Name });
        }

        var episodes = await _client.FetchEpisodesAsync(show.Slug, cancellationToken).ConfigureAwait(false);
        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogWarning("Anime markers: refresh of {Slug} returned nothing usable", show.Slug);
            return Ok(new { matched = true, slug = show.Slug, fetched = false });
        }

        _cache.Set(show.Slug, new AnimeMarkerCacheEntry
        {
            Slug = show.Slug,
            Title = show.Title,
            Episodes = episodes
        });

        await _cache.FlushAsync().ConfigureAwait(false);

        return Ok(new { matched = true, slug = show.Slug, fetched = true, episodes = episodes.Count });
    }

    /// <summary>
    /// Drops every cached show table so the next sync refetches from scratch. The show
    /// catalogue is kept, since re-downloading it is a separate concern.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ClearCache()
    {
        var removed = await _cache.ClearAsync().ConfigureAwait(false);
        return Ok(new { cleared = removed });
    }
}
