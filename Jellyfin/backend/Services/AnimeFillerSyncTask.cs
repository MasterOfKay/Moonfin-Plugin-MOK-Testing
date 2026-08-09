using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// A scheduled task that looks up filler and recap episodes for anime series from MyAnimeList.
/// </summary>
public class AnimeFillerSyncTask : IScheduledTask
{
    public string Name => "Moonfin Anime Filler Sync";
    public string Key => "Moonfin.Anime.FillerSync";
    public string Description => "Looks up filler and recap episodes for anime series from MyAnimeList (via Jikan API). No account or API key is needed.";
    public string Category => "Moonfin";

    /// <summary>
    /// Cap on AniList fallback lookups per run, so a big unmapped library cannot stall the task.
    /// </summary>
    private const int MaxAniListFallbacks = 50;

    private const int FlushEveryNSeries = 25;

    private const int MaxConsecutiveFailures = 15;

    private readonly AnimeIdMappingService _mappingService;
    private readonly AnimeFillerResolver _resolver;
    private readonly AnimeFillerCacheService _cacheService;
    private readonly AnimeFillerFetchService _fetchService;
    private readonly ILogger<AnimeFillerSyncTask> _logger;

    public AnimeFillerSyncTask(
        AnimeIdMappingService mappingService,
        AnimeFillerResolver resolver,
        AnimeFillerCacheService cacheService,
        AnimeFillerFetchService fetchService,
        ILogger<AnimeFillerSyncTask> logger)
    {
        _mappingService = mappingService;
        _resolver = resolver;
        _cacheService = cacheService;
        _fetchService = fetchService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (MoonfinPlugin.Instance?.Configuration?.AnimeFillerEnabled != true)
        {
            _logger.LogInformation("Anime filler sync skipped: disabled in configuration");
            return;
        }

        progress.Report(0);

        await _mappingService.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_mappingService.IsLoaded)
        {
            _logger.LogWarning("Anime filler sync aborted: the anime id mapping could not be loaded");
            return;
        }

        progress.Report(5);

        var plan = _resolver.BuildScanPlan();
        _logger.LogInformation(
            "Anime filler sync: {Resolved}/{Considered} series mapped to a MyAnimeList entry ({MalCount} entries)",
            plan.SeriesResolved, plan.SeriesConsidered, plan.MalIds.Count);

        if (plan.UnresolvedSeries.Count > 0)
        {
            _logger.LogInformation(
                "Anime filler sync: {Count} series had no usable anime id, for example {Examples}",
                plan.UnresolvedSeries.Count,
                string.Join(", ", plan.UnresolvedSeries.Take(5)));
        }

        var maxAge = TimeSpan.FromDays(MoonfinPlugin.Instance?.Configuration?.AnimeFillerMaxAgeDays ?? 30);
        var fresh = _cacheService.GetFreshMalIds(maxAge);
        var pending = plan.MalIds.Where(id => !fresh.Contains(id)).ToHashSet();

        // If the mapping table is missing AniList ids, try to recover them via AniList's idMal field. This is a fallback for shows added upstream since the last table refresh.
        if (plan.UnresolvedSeries.Count > 0)
        {
            var recovered = await _resolver
                .ResolveMissingViaAniListAsync(pending, MaxAniListFallbacks, cancellationToken)
                .ConfigureAwait(false);

            if (recovered > 0)
            {
                _logger.LogInformation("Anime filler sync: recovered {Count} series through AniList's idMal field", recovered);
            }
        }

        progress.Report(10);

        if (pending.Count == 0)
        {
            _logger.LogInformation("Anime filler sync complete: every mapped series is already cached");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Anime filler sync: fetching {Count} MyAnimeList entries", pending.Count);

        var processed = 0;
        var fetched = 0;
        var failed = 0;
        var sinceFlush = 0;

        var consecutiveFailures = 0;
        var abortedEarly = false;

        try
        {
            foreach (var malId in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var entry = await _fetchService.FetchAsync(malId, cancellationToken).ConfigureAwait(false);
                    if (entry != null)
                    {
                        _cacheService.Set(malId, entry);
                        fetched++;
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        failed++;
                        consecutiveFailures++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Anime filler fetch failed for MAL {MalId}, continuing", malId);
                    failed++;
                    consecutiveFailures++;
                }

                processed++;
                sinceFlush++;

                // 10% went to mapping, so the fetch loop owns the remaining 90%.
                progress.Report(10 + ((double)processed / pending.Count * 90));

                if (sinceFlush >= FlushEveryNSeries)
                {
                    await _cacheService.FlushAsync().ConfigureAwait(false);
                    sinceFlush = 0;

                    _logger.LogInformation(
                        "Anime filler sync progress: {Processed}/{Total} processed, {Fetched} cached, {Failed} failed",
                        processed, pending.Count, fetched, failed);
                }
                
                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    abortedEarly = true;
                    _logger.LogWarning(
                        "Anime filler sync stopped early: {Count} MyAnimeList lookups failed in a row. " +
                        "The API is likely rate limiting or unavailable. {Fetched} entries were cached; " +
                        "the rest will be retried on the next run",
                        consecutiveFailures, fetched);
                    break;
                }
            }
        }
        finally
        {
            await _cacheService.FlushAsync().ConfigureAwait(false);

            _logger.LogInformation(
                "Anime filler sync finished{Aborted}: {Fetched} entries cached, {Failed} could not be read, {Flagged} flagged episodes stored",
                abortedEarly ? " early" : string.Empty,
                fetched, failed, _cacheService.TotalFlaggedEpisodes());
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}
