using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Resolves a series to its AnimeFillerList show and the cached episode table for it, if any.
    /// Episode and season ids are Emby's numeric item ids, which is what clients see.
    /// </summary>
    public class AnimeMarkerResolver
    {
        private static readonly string[] AnimeProviderKeys =
        {
            "AniList", "AniDB", "AniSearch", "Kitsu", "KitsuIo", "MyAnimeList", "Mal"
        };

        private static readonly TimeSpan AudioCacheTtl = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<long, (DateTimeOffset At, AudioMarkerResult Result)> AudioCache =
            new ConcurrentDictionary<long, (DateTimeOffset At, AudioMarkerResult Result)>();

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly AnimeFillerListClient _client;
        private readonly AnimeMarkerCacheService _cache;
        private readonly AnimeTitleAliasService _aliases;
        private readonly ILogger _logger;

        public AnimeMarkerResolver(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            AnimeFillerListClient client,
            AnimeMarkerCacheService cache,
            AnimeTitleAliasService aliases,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _client = client;
            _cache = cache;
            _aliases = aliases;
            _logger = logger;
        }

        public static string IdOf(BaseItem item) => IdOf(item.InternalId);

        public static string IdOf(long internalId) => internalId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Looks an item up by the id a client or the admin page sent, which on Emby is the
        /// numeric item id. A Guid works too.
        /// </summary>
        public BaseItem? FindItem(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var trimmed = id.Trim();
            try
            {
                if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
                {
                    return internalId > 0 ? _libraryManager.GetItemById(internalId) : null;
                }

                return Guid.TryParse(trimmed, out var guid) ? _libraryManager.GetItemById(guid) : null;
            }
            catch (Exception ex)
            {
                _logger.Debug("Anime markers: item " + trimmed + " could not be looked up: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The series that could match an AnimeFillerList show. With no libraries configured the
        /// whole server is scanned. With libraries configured but none of them resolvable,
        /// nothing is scanned.
        /// </summary>
        public List<Series> GetCandidateSeries() => GetCandidateSeries(GetSelectedLibraryFolderIds());

        public List<Series> GetCandidateSeries(HashSet<long>? libraryIds)
        {
            if (libraryIds == null)
            {
                return QuerySeries(null).ToList();
            }

            // Scanning the whole server instead would be the opposite of what was asked for.
            if (libraryIds.Count == 0)
            {
                _logger.Warn("Anime markers: libraries are selected but none could be resolved, so nothing was scanned");
                return new List<Series>();
            }

            var candidates = new List<Series>();
            var seen = new HashSet<long>();

            foreach (var libraryId in libraryIds)
            {
                foreach (var series in QuerySeries(libraryId))
                {
                    if (seen.Add(series.InternalId))
                    {
                        candidates.Add(series);
                    }
                }
            }

            return candidates;
        }

        private IEnumerable<Series> QuerySeries(long? libraryId)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Series" },
                IsVirtualItem = false,
                Recursive = true
            };

            if (libraryId != null)
            {
                query.AncestorIds = new[] { libraryId.Value };
            }

            return _libraryManager.GetItemsResult(query).Items.OfType<Series>();
        }

        /// <summary>
        /// The configured libraries that still exist, or null when none are configured.
        /// </summary>
        public HashSet<long>? GetSelectedLibraryFolderIds()
        {
            var configuredIds = Plugin.Instance?.Configuration?.AnimeMarkerLibraryIds ?? new List<string>();
            if (configuredIds.Count == 0)
            {
                return null;
            }

            var resolved = new HashSet<long>();
            foreach (var rawId in configuredIds)
            {
                var item = FindItem(rawId);
                if (item != null)
                {
                    resolved.Add(item.InternalId);
                }
            }

            return resolved;
        }

        public AnimeFillerShow? MatchSeries(Series series) => MatchSeriesDetailed(series).Show;

        /// <summary>
        /// Tries the library name and original title first, then the English and romaji titles
        /// behind the series' provider ids, which only exist once <see cref="RefreshAliasesAsync"/>
        /// has fetched them.
        /// </summary>
        public SeriesMatchResult MatchSeriesDetailed(Series series)
        {
            var byTitle = MatchByLibraryTitle(series);
            if (byTitle != null)
            {
                return new SeriesMatchResult(byTitle, null, null);
            }

            foreach (var (key, alias) in _aliases.GetAliases(series))
            {
                var show = AnimeTitleMatcher.MatchPrimary(_client.Index, alias.Titles);
                if (show != null)
                {
                    return new SeriesMatchResult(show, key, alias.MalId);
                }
            }

            return new SeriesMatchResult(null, null, null);
        }

        private AnimeFillerShow? MatchByLibraryTitle(Series series) =>
            AnimeTitleMatcher.Match(_client.Index, WithProductionYear(series), series.Name, series.OriginalTitle);

        /// <summary>
        /// Fetches the provider id titles for every series whose library title matches nothing,
        /// so a series that matches today wont change.
        /// </summary>
        public Task<int> RefreshAliasesAsync(IEnumerable<Series> series, CancellationToken cancellationToken) =>
            _aliases.RefreshAsync(series.Where(item => MatchByLibraryTitle(item) == null), cancellationToken);

        private static string? WithProductionYear(Series series) =>
            series.ProductionYear is int year && !string.IsNullOrWhiteSpace(series.Name)
                ? $"{series.Name} ({year.ToString(CultureInfo.InvariantCulture)})"
                : null;

        public List<SeriesMatch> BuildMatches(IEnumerable<Series> candidates)
        {
            var matches = new List<SeriesMatch>();

            foreach (var series in candidates)
            {
                var match = MatchSeriesDetailed(series);
                if (match.Show != null)
                {
                    matches.Add(new SeriesMatch(series, match.Show, match.MalId));
                }
            }

            return matches;
        }

        /// <summary>
        /// Markers for one series' episodes. Empty when the series doesnt match a show or its
        /// table hasnt been fetched yet.
        /// </summary>
        public SeriesMarkerResult GetMarkersForSeries(Series series, TimeSpan maxAge)
        {
            var result = new SeriesMarkerResult();

            var show = MatchSeries(series);
            if (show == null)
            {
                return result;
            }

            result.Slug = show.Slug;
            result.Title = show.Title;

            var entry = _cache.TryGet(show.Slug, maxAge);
            if (entry == null)
            {
                result.Pending = true;
                return result;
            }

            result.RecapKnown = entry.RecapMalId != null;

            var matched = MatchEpisodes(series, entry);
            result.MatchMode = matched.Match.Mode;

            foreach (var numbered in matched.Numbering.Episodes)
            {
                if (matched.BySlot.TryGetValue(SlotOf(numbered.Episode), out var marker))
                {
                    result.Episodes[IdOf(numbered.Episode)] = marker;
                }
            }

            return result;
        }

        /// <summary>
        /// Pairs a series' episodes with a show's cached table, by absolute number unless the
        /// air dates show the numbers are off. Keyed by season and episode number, so every
        /// copy of an episode gets the same marker.
        /// </summary>
        public EpisodeMarkerMatch MatchEpisodes(Series series, AnimeMarkerCacheEntry entry)
        {
            var numbering = BuildNumbering(series);

            var slots = numbering.Episodes
                .GroupBy(numbered => SlotOf(numbered.Episode))
                .OrderBy(group => group.First().AbsoluteNumber)
                .ToList();

            var match = AnimeEpisodeMatcher.Match(
                slots.Select(group => new AnimeEpisodeSlot(
                    group.First().AbsoluteNumber,
                    group.Select(numbered => numbered.Episode.PremiereDate?.Date).FirstOrDefault(date => date != null),
                    group.First().Episode.Name)).ToList(),
                entry.Episodes);

            var bySlot = new Dictionary<(int Season, int Index), AnimeMarkerEpisode>();
            for (var i = 0; i < slots.Count; i++)
            {
                var marker = match.Markers[i];
                if (marker != null)
                {
                    bySlot[slots[i].Key] = marker;
                }
            }

            return new EpisodeMarkerMatch(numbering, match, bySlot);
        }

        private static (int Season, int Index) SlotOf(Episode episode) =>
            (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value);

        public AnimeAudioKind? GetAudioForItem(BaseItem item)
        {
            try
            {
                var streams = _mediaSourceManager.GetMediaStreams(item) ?? new List<MediaStream>();
                return AnimeAudioClassifier.Classify(streams
                    .Where(stream => stream?.Type == MediaStreamType.Audio)
                    .Select(stream => stream.Language));
            }
            catch (Exception ex)
            {
                _logger.Debug("Anime markers: audio streams unreadable for " + item.InternalId + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// True when the item is a candidate for audio markers. With no libraries configured,
        /// anything that looks like anime counts.
        /// </summary>
        public bool IsAudioMarkerCandidateItem(BaseItem item) =>
            IsAudioMarkerCandidateItem(item, GetSelectedLibraryFolderIds());

        public bool IsAudioMarkerCandidateItem(BaseItem item, HashSet<long>? libraryIds)
        {
            if (libraryIds == null)
            {
                return LooksLikeAnime(item);
            }

            if (!IsInSelectedLibrary(item, libraryIds))
            {
                return false;
            }

            // Trusting the chosen libraries counts everything in them as anime, which is what an
            // anime-only library wants.
            return TrustsSelectedLibraries || LooksLikeAnime(item);
        }

        private static bool TrustsSelectedLibraries =>
            Plugin.Instance?.Configuration?.AnimeAudioTrustSelectedLibraries == true;

        private bool IsInSelectedLibrary(BaseItem item, HashSet<long> libraryIds)
        {
            try
            {
                foreach (var folder in _libraryManager.GetCollectionFolders(item))
                {
                    if (libraryIds.Contains(folder.InternalId))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Anime markers: collection folders unreadable for " + item.InternalId + ": " + ex.Message);
            }

            for (var parent = item.GetParent(); parent != null; parent = parent.GetParent())
            {
                if (libraryIds.Contains(parent.InternalId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksLikeAnime(BaseItem item)
        {
            // An episode carries almost no provider ids of its own. The anime ids live on the
            // series, so checking the episode alone would call every anime episode not anime.
            if (item is Episode episode)
            {
                var parentSeries = episode.Series;
                if (parentSeries != null && LooksLikeAnimeCore(parentSeries))
                {
                    return true;
                }
            }

            return LooksLikeAnimeCore(item);
        }

        private static bool LooksLikeAnimeCore(BaseItem series)
        {
            foreach (var providerKey in AnimeProviderIds.Of(series).Keys)
            {
                if (AnimeProviderKeys.Any(key => string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            foreach (var label in (series.Genres ?? Array.Empty<string>()).Concat(series.Tags ?? Array.Empty<string>()))
            {
                if (label != null && label.IndexOf("anime", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Every real episode under a series, once each.
        private List<Episode> QueryEpisodes(Series series) =>
            _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                AncestorIds = new[] { series.InternalId },
                IsVirtualItem = false,
                Recursive = true
            }).Items.OfType<Episode>()
                .GroupBy(episode => episode.InternalId)
                .Select(group => group.First())
                .ToList();

        /// <summary>
        /// Subbed/dubbed verdicts for a series' episodes and seasons, read from each file's audio
        /// track languages.
        /// </summary>
        public AudioMarkerResult BuildAudioMarkers(Series series)
        {
            if (AudioCache.TryGetValue(series.InternalId, out var cached) &&
                DateTimeOffset.UtcNow - cached.At < AudioCacheTtl)
            {
                return cached.Result;
            }

            var result = new AudioMarkerResult();
            var bySeason = new Dictionary<long, List<AnimeAudioKind?>>();

            foreach (var episode in QueryEpisodes(series))
            {
                var kind = GetAudioForItem(episode);

                if (kind != null)
                {
                    result.Episodes[IdOf(episode)] = kind.Value;
                }

                // Emby links an episode to its season through the album id.
                var seasonId = episode.AlbumId;
                if (seasonId != 0)
                {
                    if (!bySeason.TryGetValue(seasonId, out var kinds))
                    {
                        kinds = new List<AnimeAudioKind?>();
                        bySeason[seasonId] = kinds;
                    }

                    kinds.Add(kind);
                }
            }

            foreach (var pair in bySeason)
            {
                var seasonKind = AnimeAudioClassifier.ClassifySeason(pair.Value);
                if (seasonKind != null)
                {
                    result.Seasons[IdOf(pair.Key)] = seasonKind.Value;
                }
            }

            AudioCache[series.InternalId] = (DateTimeOffset.UtcNow, result);

            // Trimmed past the cap by dropping entries already past their TTL, so a burst of
            // browsing inside one TTL window can still run over it.
            if (AudioCache.Count > 200)
            {
                foreach (var stale in AudioCache
                             .Where(pair => DateTimeOffset.UtcNow - pair.Value.At >= AudioCacheTtl)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    AudioCache.TryRemove(stale, out _);
                }
            }

            return result;
        }

        /// <summary>
        /// Numbers a series' episodes absolutely and reports which numbering the library uses,
        /// the first thing to check when markers land on the wrong episodes.
        /// </summary>
        public SeriesNumbering BuildNumbering(Series series)
        {
            var episodes = QueryEpisodes(series)
                // Season 0 is specials, which the site doesnt list alongside the main run.
                .Where(episode => episode.ParentIndexNumber > 0 && episode.IndexNumber > 0)
                .OrderBy(episode => episode.ParentIndexNumber!.Value)
                .ThenBy(episode => episode.IndexNumber!.Value)
                .ToList();

            // Two items can share one season/episode slot, such as a second cut of the same
            // episode. The site sees one episode, so numbering runs over the distinct slots and
            // every item in a slot takes that slot's number.
            var slots = episodes
                .Select(episode => (Season: episode.ParentIndexNumber!.Value, Index: episode.IndexNumber!.Value))
                .Distinct()
                .OrderBy(slot => slot.Season)
                .ThenBy(slot => slot.Index)
                .ToList();

            var numbers = AnimeEpisodeNumbering.AssignAbsoluteNumbers(slots);
            var absoluteBySlot = new Dictionary<(int Season, int Index), int>();
            for (var i = 0; i < slots.Count; i++)
            {
                absoluteBySlot[slots[i]] = numbers[i];
            }

            var numbered = episodes
                .Select(episode => new NumberedEpisode(
                    episode,
                    absoluteBySlot[(episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)]))
                .ToList();

            return new SeriesNumbering(AnimeEpisodeNumbering.IsAlreadyAbsolute(slots) ? "absolute" : "per-season", numbered);
        }
    }

    /// <summary>
    /// Subbed/dubbed verdicts for one series, keyed by Emby item id. A season appears only
    /// when every episode in it agreed.
    /// </summary>
    public class AudioMarkerResult
    {
        public Dictionary<string, AnimeAudioKind> Episodes { get; } = new Dictionary<string, AnimeAudioKind>();

        public Dictionary<string, AnimeAudioKind> Seasons { get; } = new Dictionary<string, AnimeAudioKind>();
    }

    /// <summary>
    /// One library series and the show it matched. The MyAnimeList id is set when the match
    /// came from a provider id's titles, and is the entry the recap pass should use.
    /// </summary>
    public sealed class SeriesMatch
    {
        public SeriesMatch(Series series, AnimeFillerShow show, int? malId)
        {
            Series = series;
            Show = show;
            MalId = malId;
        }

        public Series Series { get; }

        public AnimeFillerShow Show { get; }

        public int? MalId { get; }
    }

    /// <summary>
    /// The show a series matched, if any, and the provider id key ("anidb:266") whose titles
    /// matched it. The key is null for a match on the library title.
    /// </summary>
    public sealed class SeriesMatchResult
    {
        public SeriesMatchResult(AnimeFillerShow? show, string? aliasKey, int? malId)
        {
            Show = show;
            AliasKey = aliasKey;
            MalId = malId;
        }

        public AnimeFillerShow? Show { get; }

        public string? AliasKey { get; }

        public int? MalId { get; }
    }

    public sealed class NumberedEpisode
    {
        public NumberedEpisode(Episode episode, int absoluteNumber)
        {
            Episode = episode;
            AbsoluteNumber = absoluteNumber;
        }

        public Episode Episode { get; }

        public int AbsoluteNumber { get; }
    }

    public sealed class SeriesNumbering
    {
        public SeriesNumbering(string mode, IReadOnlyList<NumberedEpisode> episodes)
        {
            Mode = mode;
            Episodes = episodes;
        }

        public string Mode { get; }

        public IReadOnlyList<NumberedEpisode> Episodes { get; }
    }

    /// <summary>
    /// A series' episodes paired with a show's table: the numbering, how the pairing was
    /// decided, and the marker for each season/episode slot that matched.
    /// </summary>
    public sealed class EpisodeMarkerMatch
    {
        public EpisodeMarkerMatch(
            SeriesNumbering numbering,
            AnimeEpisodeMatchResult match,
            IReadOnlyDictionary<(int Season, int Index), AnimeMarkerEpisode> bySlot)
        {
            Numbering = numbering;
            Match = match;
            BySlot = bySlot;
        }

        public SeriesNumbering Numbering { get; }

        public AnimeEpisodeMatchResult Match { get; }

        public IReadOnlyDictionary<(int Season, int Index), AnimeMarkerEpisode> BySlot { get; }
    }

    public class SeriesMarkerResult
    {
        /// <summary>Emby episode id to its marker. Unmatched episodes are absent.</summary>
        public Dictionary<string, AnimeMarkerEpisode> Episodes { get; } = new Dictionary<string, AnimeMarkerEpisode>();

        public string? Slug { get; set; }

        public string? Title { get; set; }

        /// <summary>True when the series matched a show whose table hasnt been fetched yet.</summary>
        public bool Pending { get; set; }

        /// <summary>How the episodes were paired with the table: "number" or "airdate".</summary>
        public string? MatchMode { get; set; }

        /// <summary>
        /// False until the recap pass has run for this show, so an episode's recap flag means
        /// "not looked up" rather than "not a recap".
        /// </summary>
        public bool RecapKnown { get; set; }
    }
}
