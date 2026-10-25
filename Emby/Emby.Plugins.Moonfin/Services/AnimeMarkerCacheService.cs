using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// AnimeFillerList episode tables, keyed by "afl:{slug}" and held in a single JSON file.
    /// </summary>
    public class AnimeMarkerCacheService : FileBackedCache<AnimeMarkerCacheEntry>
    {
        private const string KeyPrefix = "afl:";

        public AnimeMarkerCacheService(ILogger logger)
            : base(logger, "anime_markers_cache.json", "Anime markers")
        {
        }

        private static string KeyFor(string slug) => KeyPrefix + slug;

        /// <summary>The cached table for a show, or null when missing or older than maxAge.</summary>
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

        /// <summary>The cached table for a show regardless of age.</summary>
        public AnimeMarkerCacheEntry? TryGetAny(string slug)
        {
            EnsureLoaded().TryGetValue(KeyFor(slug), out var entry);
            return entry;
        }

        public void Set(string slug, AnimeMarkerCacheEntry entry)
        {
            entry.CachedAt = DateTimeOffset.UtcNow;
            entry.Format = AnimeMarkerCacheEntry.CurrentFormat;
            EnsureLoaded()[KeyFor(slug)] = entry;
        }

        public HashSet<string> GetFreshSlugs(TimeSpan maxAge)
        {
            var now = DateTimeOffset.UtcNow;
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in EnsureLoaded())
            {
                if (pair.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.Format >= AnimeMarkerCacheEntry.CurrentFormat &&
                    now - pair.Value.CachedAt < maxAge)
                {
                    slugs.Add(pair.Key.Substring(KeyPrefix.Length));
                }
            }

            return slugs;
        }

        public int EntryCount() => EnsureLoaded().Count;

        public int FlaggedEpisodeCount() =>
            EnsureLoaded().Values
                .SelectMany(entry => entry.Episodes)
                .Count(episode => episode.Kind == AnimeEpisodeKind.Filler || episode.Kind == AnimeEpisodeKind.Mixed);

        public int TotalEpisodeCount() => EnsureLoaded().Values.Sum(entry => entry.Episodes.Count);
    }
}
