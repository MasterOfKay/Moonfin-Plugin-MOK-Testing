using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves filler and recap markers for anime episodes.
/// </summary>
[ApiController]
[Route("Moonfin/AnimeFiller")]
public class AnimeFillerController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly AnimeFillerResolver _resolver;
    private readonly AnimeFillerCacheService _cacheService;
    private readonly AnimeFillerFetchService _fetchService;
    private readonly AnimeIdMappingService _mappingService;
    private readonly ILogger<AnimeFillerController> _logger;

    public AnimeFillerController(
        ILibraryManager libraryManager,
        AnimeFillerResolver resolver,
        AnimeFillerCacheService cacheService,
        AnimeFillerFetchService fetchService,
        AnimeIdMappingService mappingService,
        ILogger<AnimeFillerController> logger)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
        _cacheService = cacheService;
        _fetchService = fetchService;
        _mappingService = mappingService;
        _logger = logger;
    }

    private static TimeSpan CacheMaxAge =>
        TimeSpan.FromDays(MoonfinPlugin.Instance?.Configuration?.AnimeFillerMaxAgeDays ?? 30);

    /// <summary>
    /// Filler and recap flags for the episodes of one series that exist in the library.
    ///
    /// Answers with <c>enabled: false</c> rather than an error when the feature is off, so a
    /// client can call this unconditionally and just render nothing.
    /// </summary>
    [HttpGet("Series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetSeriesFiller(
        [FromQuery] string seriesId,
        [FromQuery] bool allowFetch = true,
        CancellationToken cancellationToken = default)
    {
        if (MoonfinPlugin.Instance?.Configuration?.AnimeFillerEnabled != true)
        {
            return Ok(new { enabled = false, episodes = new Dictionary<string, object>() });
        }

        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        await _mappingService.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var result = _resolver.GetFillerForSeries(series, CacheMaxAge);

        // If the series has any seasons that are not cached, try to fetch them on demand. 
        // This is only done if the caller explicitly allows it, because it can be slow and can fail if the server cannot reach the upstream API.
        if (allowFetch && result.UncachedSeasons.Count > 0)
        {
            await FetchMissingAsync(series, cancellationToken).ConfigureAwait(false);
            result = _resolver.GetFillerForSeries(series, CacheMaxAge);
        }

        return Ok(new
        {
            enabled = true,
            episodes = result.Episodes.ToDictionary(
                pair => pair.Key,
                pair => new { filler = pair.Value.Filler, recap = pair.Value.Recap }),
            resolvedMalIds = result.ResolvedMalIds,
            unresolvedSeasons = result.UnresolvedSeasons,
            pendingSeasons = result.UncachedSeasons
        });
    }

    private const int MaxOnDemandFetches = 2;

    private async Task FetchMissingAsync(Series series, CancellationToken cancellationToken)
    {
        var fetchedAny = false;
        var fetches = 0;

        foreach (var mapping in _resolver.ResolveSeasons(series))
        {
            if (mapping.Lookup == null || _cacheService.TryGet(mapping.Lookup.MalId, CacheMaxAge) != null)
            {
                continue;
            }

            if (fetches++ >= MaxOnDemandFetches)
            {
                break;
            }

            try
            {
                var entry = await _fetchService.FetchAsync(mapping.Lookup.MalId, cancellationToken).ConfigureAwait(false);
                if (entry != null)
                {
                    _cacheService.Set(mapping.Lookup.MalId, entry);
                    fetchedAny = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "On-demand filler fetch failed for MAL {MalId}", mapping.Lookup.MalId);
            }
        }

        if (fetchedAny)
        {
            await _cacheService.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns diagnostic information about the anime filler feature, including how many series are mapped, how many are cached, and how many episodes are flagged.
    /// This is intended for the admin panel to display, and for users to report when filing issues.
    /// </summary>
    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        await _mappingService.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!_mappingService.IsLoaded)
        {
            return Ok(new
            {
                enabled = config?.AnimeFillerEnabled ?? false,
                mappingLoaded = false,
                error = "The anime id mapping could not be downloaded. Check that the server can reach raw.githubusercontent.com."
            });
        }

        var plan = _resolver.BuildScanPlan();
        var fresh = _cacheService.GetFreshMalIds(TimeSpan.FromDays(config?.AnimeFillerMaxAgeDays ?? 30));

        // Library selection is the most common thing to get wrong, and when it
        // silently fails the scan looks like it is ignoring the picker. Report
        // what was configured and what it actually resolved to.
        var configuredLibraries = config?.AnimeFillerLibraryIds ?? new List<string>();
        var resolvedLibraries = _resolver.GetSelectedLibraryFolderIds();

        return Ok(new
        {
            enabled = config?.AnimeFillerEnabled ?? false,
            mappingLoaded = true,
            libraryFilterActive = resolvedLibraries != null,
            configuredLibraryCount = configuredLibraries.Count,
            resolvedLibraryCount = resolvedLibraries?.Count ?? 0,
            mappingEntries = _mappingService.MappingCount,
            mappingDownloadedAt = _mappingService.MappingDownloadedAt,
            seriesConsidered = plan.SeriesConsidered,
            seriesResolved = plan.SeriesResolved,
            malEntries = plan.MalIds.Count,
            malEntriesCached = plan.MalIds.Count(fresh.Contains),
            flaggedEpisodes = _cacheService.TotalFlaggedEpisodes(),
            partialEntries = _cacheService.PartialEntryCount(),
            matchSources = plan.SourceCounts,
            unresolvedSeries = plan.UnresolvedSeries.Take(50).ToList(),
            unresolvedCount = plan.UnresolvedSeries.Count
        });
    }

    /// <summary>
    /// Drops every cached lookup so the next scan refetches from scratch.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ClearCache()
    {
        var removed = await _cacheService.ClearAsync().ConfigureAwait(false);
        return Ok(new { cleared = removed });
    }
}
