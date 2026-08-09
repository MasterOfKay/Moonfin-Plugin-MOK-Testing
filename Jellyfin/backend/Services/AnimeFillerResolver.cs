using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Resolves which MyAnimeList entry corresponds to each series and season in the library, and which episodes are filler or recaps.
/// </summary>
public class AnimeFillerResolver
{
    private static readonly string[] AnimeProviderKeys =
    {
        "AniList", "AniDB", "AniDb", "Anidb", "AniSearch", "Kitsu", "KitsuIo", "MyAnimeList", "Mal"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly AnimeIdMappingService _mappingService;
    private readonly AnimeFillerCacheService _cacheService;
    private readonly ILogger<AnimeFillerResolver> _logger;

    public AnimeFillerResolver(
        ILibraryManager libraryManager,
        AnimeIdMappingService mappingService,
        AnimeFillerCacheService cacheService,
        ILogger<AnimeFillerResolver> logger)
    {
        _libraryManager = libraryManager;
        _mappingService = mappingService;
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the series in the library that are candidates for filler lookup. If the admin has selected specific libraries, only series in those libraries are returned. Otherwise, all series that look like anime are returned.
    /// </summary>
    public List<Series> GetCandidateSeries()
    {
        var allowedLibraries = GetAllowedLibraryIds();
        var candidates = new List<Series>();

        foreach (var series in QueryAllSeries())
        {
            if (allowedLibraries != null)
            {
                if (!IsInAllowedLibrary(series, allowedLibraries))
                {
                    continue;
                }
            }
            else if (!IsLikelyAnime(series))
            {
                continue;
            }

            candidates.Add(series);
        }

        return candidates;
    }

    private IEnumerable<Series> QueryAllSeries()
    {
        return _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        }).Items.OfType<Series>();
    }

    /// <summary>
    /// Gets the library ids the admin has selected for filler lookup. If none are selected, all libraries are allowed.
    /// </summary>
    private HashSet<Guid>? GetAllowedLibraryIds()
    {
        var configuredIds = MoonfinPlugin.Instance?.Configuration?.AnimeFillerLibraryIds ?? new List<string>();
        if (configuredIds.Count == 0)
        {
            return null;
        }

        var allowed = new HashSet<Guid>();

        foreach (var rawId in configuredIds)
        {
            if (!Guid.TryParse(rawId, out var libraryId))
            {
                continue;
            }

            allowed.Add(libraryId);

            var item = _libraryManager.GetItemById(libraryId);
            if (item == null)
            {
                continue;
            }

            // Collection folders are the only library items that can contain series, so if the admin has selected a collection folder, also allow any series filed under it.
            if (!item.DisplayParentId.Equals(default))
            {
                allowed.Add(item.DisplayParentId);
            }

            // If a virtual folder has ethe same name as a ibary we allow it too, for convenience.
            if (!string.IsNullOrEmpty(item.Name))
            {
                foreach (var folder in _libraryManager.GetVirtualFolders())
                {
                    if (string.Equals(folder.Name, item.Name, StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(folder.ItemId, out var folderId))
                    {
                        allowed.Add(folderId);
                    }
                }
            }
        }

        return allowed.Count > 0 ? allowed : null;
    }

    private bool IsInAllowedLibrary(BaseItem series, HashSet<Guid> allowedLibraries)
    {
        foreach (var folder in _libraryManager.GetCollectionFolders(series))
        {
            if (allowedLibraries.Contains(folder.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the series has a provider id or genre/tag that suggests it is anime.
    /// This is not perfect, as Jellyfin does not have a dedictaed anime flag.
    /// </summary>
    public static bool IsLikelyAnime(BaseItem series)
    {
        foreach (var key in AnimeProviderKeys)
        {
            foreach (var providerKey in series.ProviderIds.Keys)
            {
                if (string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var genre in series.Genres)
        {
            if (genre.Contains("anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var tag in series.Tags)
        {
            if (tag.Contains("anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the seasons of a series to their MyAnimeList entries, if any. 
    /// The result is used to look up filler flags and map them onto the episodes the user actually has.
    /// </summary>
    public List<SeasonMapping> ResolveSeasons(Series series)
    {
        var seasons = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Season],
            ParentId = series.Id,
            IsVirtualItem = false,
            Recursive = true
        }).Items.OfType<Season>().ToList();

        var realSeasons = seasons.Where(s => s.IndexNumber is > 0).ToList();
        var mappings = new List<SeasonMapping>();

        foreach (var season in realSeasons)
        {
            var seasonNumber = season.IndexNumber!.Value;

            // 1. IF AniList/AniDB/MAL is on the season itself, use that.
            var result = _mappingService.Resolve(season.ProviderIds, seasonNumber);

            // 2. Otherwise fall back to the providers of other metadata.
            if (result == null)
            {
                result = realSeasons.Count == 1 || seasonNumber == 1
                    ? _mappingService.Resolve(series.ProviderIds, seasonNumber)
                    : _mappingService.ResolveByTvdbSeason(series.ProviderIds, seasonNumber);
            }

            mappings.Add(new SeasonMapping(season, seasonNumber, result));
        }

        return mappings;
    }

    /// <summary>
    /// Gets the filler and recap flags for a series, using the cache if possible. 
    /// If the cache is stale or missing, the caller should fetch the MAL entry and update the cache before calling again.
    /// </summary>
    public SeriesFillerResult GetFillerForSeries(Series series, TimeSpan maxAge)
    {
        var result = new SeriesFillerResult();

        foreach (var mapping in ResolveSeasons(series))
        {
            if (mapping.Lookup == null)
            {
                result.UnresolvedSeasons.Add(mapping.SeasonNumber);
                continue;
            }

            var cached = _cacheService.TryGet(mapping.Lookup.MalId, maxAge);
            if (cached == null)
            {
                result.UncachedSeasons.Add(mapping.SeasonNumber);
                continue;
            }

            result.ResolvedMalIds.Add(mapping.Lookup.MalId);

            if (cached.Episodes.Count == 0)
            {
                continue;
            }

            var flagsByNumber = new Dictionary<int, AnimeFillerEpisode>();
            foreach (var episode in cached.Episodes)
            {
                flagsByNumber[episode.Number] = episode;
            }

            var episodes = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                ParentId = mapping.Season.Id,
                IsVirtualItem = false,
                Recursive = true
            }).Items.OfType<Episode>();

            foreach (var episode in episodes)
            {
                if (episode.IndexNumber is not { } number)
                {
                    continue;
                }

                if (!flagsByNumber.TryGetValue(number, out var flags))
                {
                    continue;
                }

                result.Episodes[episode.Id.ToString("N")] = new EpisodeFlags
                {
                    Filler = flags.Filler,
                    Recap = flags.Recap
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a plan for what the sync task will do, without actually fetching anything. 
    /// The plan is used to report progress to the admin page and to decide which MAL entries need to be fetched.
    /// </summary>
    public ScanPlan BuildScanPlan()
    {
        var plan = new ScanPlan();

        foreach (var series in GetCandidateSeries())
        {
            plan.SeriesConsidered++;

            var mappings = ResolveSeasons(series);
            if (mappings.Count == 0)
            {
                continue;
            }

            var resolvedAny = false;
            foreach (var mapping in mappings)
            {
                if (mapping.Lookup == null)
                {
                    continue;
                }

                resolvedAny = true;
                plan.MalIds.Add(mapping.Lookup.MalId);
                plan.SourceCounts.TryGetValue(mapping.Lookup.Source, out var count);
                plan.SourceCounts[mapping.Lookup.Source] = count + 1;
            }

            if (resolvedAny)
            {
                plan.SeriesResolved++;
            }
            else
            {
                plan.UnresolvedSeries.Add(series.Name ?? series.Id.ToString("N"));
            }
        }

        return plan;
    }

    /// <summary>
    /// Attempts to recover missing MAL ids for series that the mapping table could not resolve, by asking AniList for the idMal field.
    /// </summary>
    public async Task<int> ResolveMissingViaAniListAsync(HashSet<int> malIds, int maxLookups, CancellationToken cancellationToken)
    {
        var added = 0;

        foreach (var series in GetCandidateSeries())
        {
            if (added >= maxLookups)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var mappings = ResolveSeasons(series);
            if (mappings.Count == 0 || mappings.Any(m => m.Lookup != null))
            {
                continue;
            }

            if (!TryGetAniListId(series, out var anilistId))
            {
                continue;
            }

            var malId = await _mappingService.ResolveViaAniListAsync(anilistId, cancellationToken).ConfigureAwait(false);
            if (malId != null && malIds.Add(malId.Value))
            {
                added++;
                _logger.LogDebug("Resolved {Series} via AniList idMal to MAL {MalId}", series.Name, malId.Value);
            }
        }

        return added;
    }

    private static bool TryGetAniListId(BaseItem series, out int anilistId)
    {
        foreach (var (key, value) in series.ProviderIds)
        {
            if (string.Equals(key, "AniList", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value?.Trim(), out anilistId))
            {
                return true;
            }
        }

        anilistId = 0;
        return false;
    }
}

/// <summary>
/// One library season and the MAL entry it was matched to, if any.
/// </summary>
public record SeasonMapping(Season Season, int SeasonNumber, MalLookupResult? Lookup);

/// <summary>
/// Flags for a single episode. Both can be true; MAL marks some recaps as filler too.
/// </summary>
public class EpisodeFlags
{
    public bool Filler { get; set; }
    public bool Recap { get; set; }
}

/// <summary>
/// What the client gets back for one series.
/// </summary>
public class SeriesFillerResult
{
    /// <summary>
    /// Jellyfin episode id (32-char "N" form) to its flags. Unflagged episodes are absent.
    /// </summary>
    public Dictionary<string, EpisodeFlags> Episodes { get; } = new();

    public List<int> ResolvedMalIds { get; } = new();

    /// <summary>
    /// Seasons no MAL entry could be found for.
    /// </summary>
    public List<int> UnresolvedSeasons { get; } = new();

    /// <summary>
    /// Seasons that mapped to a MAL entry the scan has not fetched yet.
    /// </summary>
    public List<int> UncachedSeasons { get; } = new();
}

/// <summary>
/// The work a scan has to do, plus the counts the admin page reports.
/// </summary>
public class ScanPlan
{
    public HashSet<int> MalIds { get; } = new();
    public int SeriesConsidered { get; set; }
    public int SeriesResolved { get; set; }
    public List<string> UnresolvedSeries { get; } = new();
    public Dictionary<string, int> SourceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
