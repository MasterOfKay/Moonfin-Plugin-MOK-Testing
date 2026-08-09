using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache of MyAnimeList filler/recap flags, keyed by "mal:{id}".
///
/// Episodes with no flag are not stored: for a 500-episode show only the few dozen filler
/// entries are kept. A present entry means "we fetched this show", which is what separates
/// "no filler" from "not looked up yet".
/// </summary>
public class AnimeFillerCacheService : FileBackedCacheService<AnimeFillerCacheEntry>
{
    public AnimeFillerCacheService(ILogger<AnimeFillerCacheService> logger)
        : base(logger, "anime_filler_cache.json", "Anime filler")
    {
    }

    public static string KeyFor(int malId) => $"mal:{malId}";

    public AnimeFillerCacheEntry? TryGet(int malId, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(KeyFor(malId), out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry;
        }

        return null;
    }

    public void Set(int malId, AnimeFillerCacheEntry entry)
    {
        var cache = EnsureLoaded();
        entry.CachedAt = DateTimeOffset.UtcNow;
        cache[KeyFor(malId)] = entry;
    }

    /// <summary>
    /// Partial entries are those that failed to fetch all episode pages. 
    /// They expire on faster than full entries, so a series that is partially cached will be retried sooner.
    /// </summary>
    private static readonly TimeSpan PartialMaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// MAL ids whose cached entry is still fresh and so can be skipped by the next scan.
    /// Partial entries expire on their own.
    /// </summary>
    public HashSet<int> GetFreshMalIds(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        var now = DateTimeOffset.UtcNow;
        var ids = new HashSet<int>();

        foreach (var (key, entry) in cache)
        {
            var age = now - entry.CachedAt;
            if (age >= (entry.Partial ? PartialMaxAge : maxAge))
            {
                continue;
            }

            if (key.StartsWith("mal:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(key.AsSpan(4), out var malId))
            {
                ids.Add(malId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Total flagged episodes across the cache, for the admin diagnostics panel.
    /// </summary>
    public int TotalFlaggedEpisodes()
    {
        var cache = EnsureLoaded();
        var total = 0;
        foreach (var entry in cache.Values)
        {
            total += entry.Episodes.Count;
        }

        return total;
    }

    public int EntryCount() => EnsureLoaded().Count;

    /// <summary>
    /// Partial entries are those that failed to fetch all episode pages. 
    /// They expire on faster than full entries, so a series that is partially cached will be goone sooner.
    /// </summary>
    public int PartialEntryCount() => EnsureLoaded().Values.Count(entry => entry.Partial);
}

/// <summary>
/// One cached MAL series: its flagged episodes and how many episodes it reported.
/// </summary>
public class AnimeFillerCacheEntry
{
    /// <summary>
    /// Flagged episodes only. Anything absent from this list is a normal episode (Hopefully).
    /// </summary>
    [JsonPropertyName("episodes")]
    public List<AnimeFillerEpisode> Episodes { get; set; } = new();

    /// <summary>
    /// How many episodes the MAL entry reported. 
    /// This is used to detect when a series has added new episodes since the last fetch.
    /// </summary>
    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }

    /// <summary>
    /// True if this entry is partial, meaning we failed to fetch all episode pages.
    /// </summary>
    [JsonPropertyName("partial")]
    public bool Partial { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}

/// <summary>
/// A single flagged episode, numbered as MyAnimeList numbers it.
/// </summary>
public class AnimeFillerEpisode
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("filler")]
    public bool Filler { get; set; }

    [JsonPropertyName("recap")]
    public bool Recap { get; set; }
}
