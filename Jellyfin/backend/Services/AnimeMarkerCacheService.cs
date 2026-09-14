using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent cache of AnimeFillerList episode tables, keyed by "afl:{slug}" and held in a
/// single JSON file.
/// </summary>
public class AnimeMarkerCacheService : FileBackedCacheService<AnimeMarkerCacheEntry>
{
    public AnimeMarkerCacheService(ILogger<AnimeMarkerCacheService> logger)
        : base(logger, "anime_markers_cache.json", "Anime markers")
    {
    }

    private const string KeyPrefix = "afl:";

    private static string KeyFor(string slug) => KeyPrefix + slug;

    /// <summary>
    /// The cached table for a show, or null when it is missing or older than
    /// <paramref name="maxAge"/>.
    /// </summary>
    public AnimeMarkerCacheEntry? TryGet(string slug, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(KeyFor(slug), out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry;
        }

        return null;
    }

    /// <summary>
    /// The cached table for a show regardless of age. Used by the recap pass, which only
    /// adds a field to an entry the filler pass already stored.
    /// </summary>
    public AnimeMarkerCacheEntry? TryGetAny(string slug)
    {
        EnsureLoaded().TryGetValue(KeyFor(slug), out var entry);
        return entry;
    }

    public void Set(string slug, AnimeMarkerCacheEntry entry)
    {
        entry.CachedAt = DateTimeOffset.UtcNow;
        EnsureLoaded()[KeyFor(slug)] = entry;
    }

    /// <summary>
    /// Stores an entry without moving its fetch time. The recap pass only adds a field to a
    /// table the filler pass fetched, so marking it fresh would buy an airing show another
    /// full max-age before its episode list is read again.
    /// </summary>
    public void Update(string slug, AnimeMarkerCacheEntry entry) =>
        EnsureLoaded()[KeyFor(slug)] = entry;

    /// <summary>Slugs whose cached table is still fresh, so the next sync can skip them.</summary>
    public HashSet<string> GetFreshSlugs(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        var now = DateTimeOffset.UtcNow;
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, entry) in cache)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                now - entry.CachedAt < maxAge)
            {
                slugs.Add(key[KeyPrefix.Length..]);
            }
        }

        return slugs;
    }

    public int EntryCount() => EnsureLoaded().Count;

    /// <summary>Episodes classified as filler or mixed, for the diagnostics readout.</summary>
    public int FlaggedEpisodeCount()
    {
        var total = 0;
        foreach (var entry in EnsureLoaded().Values)
        {
            foreach (var episode in entry.Episodes)
            {
                if (episode.Kind is AnimeEpisodeKind.Filler or AnimeEpisodeKind.Mixed)
                {
                    total++;
                }
            }
        }

        return total;
    }

    /// <summary>Total episodes across every cached show, for the diagnostics readout.</summary>
    public int TotalEpisodeCount()
    {
        var total = 0;
        foreach (var entry in EnsureLoaded().Values)
        {
            total += entry.Episodes.Count;
        }

        return total;
    }
}
