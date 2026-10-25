using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Serves filler, canon and recap markers for anime episodes, plus the subbed/dubbed
    /// verdicts read from each file's audio tracks.
    /// </summary>
    public class AnimeMarkersService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly ILogger _logger;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public AnimeMarkersService(IApplicationHost appHost)
        {
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinAnimeMarkers");
        }

        private static AnimeFillerListClient Client => Plugin.Instance?.AnimeFillerList
            ?? throw new InvalidOperationException("AnimeFillerListClient not initialized");

        private static AnimeMarkerResolver Resolver => Plugin.Instance?.AnimeMarkerResolver
            ?? throw new InvalidOperationException("AnimeMarkerResolver not initialized");

        private static AnimeMarkerCacheService Cache => Plugin.Instance?.AnimeMarkerCache
            ?? throw new InvalidOperationException("AnimeMarkerCacheService not initialized");

        private static AnimeMarkerDiagnosticLog Diagnostics => Plugin.Instance?.AnimeMarkerDiagnostics
            ?? throw new InvalidOperationException("AnimeMarkerDiagnosticLog not initialized");

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        private static bool IsKindEnabled(PluginConfiguration? configuration, AnimeEpisodeKind kind) => kind switch
        {
            AnimeEpisodeKind.Filler => configuration?.AnimeMarkerShowFiller != false,
            AnimeEpisodeKind.Mixed => configuration?.AnimeMarkerShowMixed != false,
            AnimeEpisodeKind.MangaCanon => configuration?.AnimeMarkerShowMangaCanon == true,
            AnimeEpisodeKind.AnimeCanon => configuration?.AnimeMarkerShowAnimeCanon == true,
            _ => false
        };

        // Passed through rather than interpreted, so a client that doesnt know a placement falls
        // back to its own default.
        private static string PlacementOf(PluginConfiguration? configuration) =>
            string.IsNullOrEmpty(configuration?.AnimeMarkerPlacement) ? "below" : configuration!.AnimeMarkerPlacement;

        // A client that doesnt know a style keeps its full labels.
        private static string LabelStyleOf(PluginConfiguration? configuration) =>
            string.IsNullOrEmpty(configuration?.AnimeAudioLabelStyle) ? "full" : configuration!.AnimeAudioLabelStyle;

        private static TimeSpan CacheMaxAge =>
            TimeSpan.FromDays(Math.Max(1, Plugin.Instance?.Configuration?.AnimeMarkerMaxAgeDays ?? 30));

        /// <summary>
        /// Markers for the episodes of one series. Reads the cache only, so a client can call it
        /// on every episode list render without waiting on an upstream site.
        /// </summary>
        public async Task<object> Get(GetAnimeSeriesMarkersRequest request)
        {
            Diagnostics.Write($"request  seriesId={request.SeriesId} from={Request.Headers["User-Agent"]}");

            var configuration = Plugin.Instance?.Configuration;
            var fillerEnabled = configuration?.AnimeMarkersEnabled == true;
            var audioEnabled = configuration?.AnimeAudioMarkersEnabled == true;

            // The feature being off isnt an error, the client just draws nothing.
            if (!fillerEnabled && !audioEnabled)
            {
                Diagnostics.Write("  -> answered enabled=false (both marker features are off in settings)");
                return Json(new
                {
                    enabled = false,
                    matched = false,
                    episodes = new Dictionary<string, object>(),
                    seasons = new Dictionary<string, object>()
                });
            }

            if (string.IsNullOrWhiteSpace(request.SeriesId))
            {
                Diagnostics.Write("  -> 400, the seriesId was missing or malformed");
                return Json(400, new { error = "Missing or malformed seriesId" });
            }

            if (!(Resolver.FindItem(request.SeriesId) is Series series))
            {
                Diagnostics.Write("  -> 404, no series in the library has that id");
                return Json(404, new { error = "Series not found" });
            }

            // Loads from disk on the first call after a restart.
            await Client.EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false);

            Diagnostics.Write($"  series=\"{series.Name}\" filler={fillerEnabled} audio={audioEnabled}");

            var result = new SeriesMarkerResult();
            if (fillerEnabled)
            {
                try
                {
                    result = Resolver.GetMarkersForSeries(series, CacheMaxAge);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Anime markers: filler lookup failed for " + series.Name, ex);
                    Diagnostics.Write($"  !! filler lookup threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var audio = new AudioMarkerResult();
            if (audioEnabled)
            {
                try
                {
                    if (Resolver.IsAudioMarkerCandidateItem(series))
                    {
                        var started = Stopwatch.StartNew();
                        audio = Resolver.BuildAudioMarkers(series);
                        Diagnostics.Write($"  audio pass took {started.ElapsedMilliseconds} ms");
                    }
                    else
                    {
                        Diagnostics.Write("  audio skipped: this series is not in a selected library and does not look like anime");
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Anime markers: audio lookup failed for " + series.Name, ex);
                    Diagnostics.Write($"  !! audio lookup threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (AnimeMarkerDiagnosticLog.Enabled)
            {
                Diagnostics.Write(
                    $"  -> matched={result.Slug ?? "(nothing)"} pending={result.Pending} " +
                    $"markers={result.Episodes.Count} recapKnown={result.RecapKnown} " +
                    $"audioEpisodes={audio.Episodes.Count} audioSeasons={audio.Seasons.Count}");

                // The episode ids are what the client matches on, so a couple go out verbatim. A
                // client formatting ids differently looks exactly like one with no data.
                foreach (var pair in result.Episodes.Take(3))
                {
                    Diagnostics.Write($"    sample episodeId={pair.Key} kind={pair.Value.Kind} recap={pair.Value.Recap}");
                }
            }

            var recapEnabled = configuration?.AnimeMarkerShowRecap != false;
            var separateDualAudio = configuration?.AnimeAudioSeparateDualAudio == true;

            return Json(new
            {
                enabled = true,
                matched = result.Slug != null,
                slug = result.Slug,
                title = result.Title,
                pending = result.Pending,
                recapKnown = result.RecapKnown,
                episodes = result.Episodes.Keys
                    .Concat(audio.Episodes.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        episodeId => episodeId,
                        episodeId =>
                        {
                            result.Episodes.TryGetValue(episodeId, out var marker);
                            var hasAudio = audio.Episodes.TryGetValue(episodeId, out var audioKind);

                            var kind = marker != null && IsKindEnabled(configuration, marker.Kind)
                                ? marker.Kind
                                : (AnimeEpisodeKind?)null;

                            return new
                            {
                                kind,
                                recap = marker?.Recap == true && recapEnabled,
                                audio = hasAudio
                                    ? AnimeAudioClassifier.Collapse(audioKind, separateDualAudio)
                                    : (AnimeAudioKind?)null
                            };
                        },
                        StringComparer.OrdinalIgnoreCase),

                seasons = audio.Seasons.ToDictionary(
                    pair => pair.Key,
                    pair => new { audio = AnimeAudioClassifier.Collapse(pair.Value, separateDualAudio) }),

                placement = PlacementOf(configuration),
                labelStyle = LabelStyleOf(configuration)
            });
        }

        /// <summary>The subbed/dubbed verdict for one or more items.</summary>
        public object Get(GetAnimeItemMarkersRequest request)
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration?.AnimeAudioMarkersEnabled != true)
            {
                return Json(new { enabled = false, items = new Dictionary<string, object>() });
            }

            if (string.IsNullOrWhiteSpace(request.Ids))
            {
                return Json(400, new { error = "Missing ids" });
            }

            // Capped so one request cant become an unbounded amount of work.
            var requested = request.Ids
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .Take(200)
                .ToList();

            var moviesEnabled = configuration.AnimeAudioMarkersMovies;
            var separateDualAudio = configuration.AnimeAudioSeparateDualAudio;
            var answer = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            var libraryIds = Resolver.GetSelectedLibraryFolderIds();

            var notFound = 0;
            var moviesSkipped = 0;
            var notCandidate = 0;
            var noAudioTags = 0;
            var seriesOrFolder = 0;

            foreach (var raw in requested)
            {
                var item = Resolver.FindItem(raw);
                if (item == null)
                {
                    notFound++;
                    continue;
                }

                // A folder has no file of its own to read audio from. Series and seasons are
                // answered through the Series endpoint instead.
                if (item is Folder)
                {
                    seriesOrFolder++;
                    continue;
                }

                // Movies are opted in separately, since a mixed library is most likely to put a
                // pill on a movie that isnt anime.
                if (item is Movie && !moviesEnabled)
                {
                    moviesSkipped++;
                    continue;
                }

                if (!Resolver.IsAudioMarkerCandidateItem(item, libraryIds))
                {
                    notCandidate++;
                    continue;
                }

                var audio = Resolver.GetAudioForItem(item);
                if (audio == null)
                {
                    noAudioTags++;
                    continue;
                }

                // Keyed by the id the client sent, which is what it looks the answer up by.
                answer[raw] = new { audio = AnimeAudioClassifier.Collapse(audio.Value, separateDualAudio) };
            }

            Diagnostics.Write(
                $"items  asked about {requested.Count}, answered {answer.Count} " +
                $"(skipped: {seriesOrFolder} series/folders, {moviesSkipped} movies with the movie option off, " +
                $"{notCandidate} outside the chosen libraries or not anime, {noAudioTags} with no audio language tags, " +
                $"{notFound} unknown ids)");

            return Json(new { enabled = true, items = answer, placement = PlacementOf(configuration), labelStyle = LabelStyleOf(configuration) });
        }

        /// <summary>
        /// How many series matched a show, how many are waiting on a fetch, and which matched
        /// nothing, for the admin page.
        /// </summary>
        public async Task<object> Get(GetAnimeMarkerDiagnosticsRequest request)
        {
            var configuration = Plugin.Instance?.Configuration;

            await Client.EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false);

            if (!Client.CatalogLoaded)
            {
                return Json(new
                {
                    enabled = configuration?.AnimeMarkersEnabled ?? false,
                    catalogLoaded = false,
                    error = "The AnimeFillerList show catalog could not be downloaded. Check that the server can reach animefillerlist.com."
                });
            }

            var resolvedLibraries = Resolver.GetSelectedLibraryFolderIds();
            var candidates = Resolver.GetCandidateSeries(resolvedLibraries);
            var fresh = Cache.GetFreshSlugs(CacheMaxAge);

            var matched = new List<object>();
            var unmatched = new List<string>();
            var pendingShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var series in candidates)
            {
                var show = Resolver.MatchSeries(series);
                if (show == null)
                {
                    unmatched.Add(series.Name ?? AnimeMarkerResolver.IdOf(series));
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

            return Json(new
            {
                enabled = configuration?.AnimeMarkersEnabled ?? false,
                catalogLoaded = true,
                catalogShows = Client.CatalogCount,
                catalogDownloadedAt = Client.CatalogDownloadedAt,
                libraryFilterActive = resolvedLibraries != null,
                configuredLibraryCount = configuredLibraries.Count,
                resolvedLibraryCount = resolvedLibraries?.Count ?? 0,
                seriesConsidered = candidates.Count,
                seriesMatched = matched.Count,
                showsPending = pendingShows.Count,
                showsCached = Cache.EntryCount(),
                episodesClassified = Cache.TotalEpisodeCount(),
                episodesFlagged = Cache.FlaggedEpisodeCount(),

                // Capped so a large library doesnt turn the admin page into a wall of text.
                matches = matched.Take(200).ToList(),
                unmatchedCount = unmatched.Count,
                unmatched = unmatched.Take(50).ToList()
            });
        }

        /// <summary>
        /// One series' episode list and the marker each episode landed on, for the admin page.
        /// </summary>
        public async Task<object> Get(GetAnimeMarkerPreviewRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SeriesId))
            {
                return Json(400, new { error = "Missing or malformed seriesId" });
            }

            if (!(Resolver.FindItem(request.SeriesId) is Series series))
            {
                return Json(404, new { error = "Series not found" });
            }

            await Client.EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false);

            Diagnostics.Write($"preview  series=\"{series.Name}\" (from the admin page)");

            await Resolver.RefreshAliasesAsync(new[] { series }, CancellationToken.None).ConfigureAwait(false);

            var match = Resolver.MatchSeriesDetailed(series);
            var show = match.Show;
            if (show == null)
            {
                Diagnostics.Write("  -> matched nothing on AnimeFillerList, by title or by provider id");
                return Json(new
                {
                    series = series.Name,
                    matched = false,
                    hint = "No AnimeFillerList show has this title, and none of its provider ids lead to one. Check its AniList, AniDB or MyAnimeList id."
                });
            }

            if (match.AliasKey != null)
            {
                Diagnostics.Write($"  -> matched {show.Slug} through {match.AliasKey}");
            }

            var entry = Cache.TryGet(show.Slug, CacheMaxAge);
            if (entry == null)
            {
                return Json(new
                {
                    series = series.Name,
                    matched = true,
                    slug = show.Slug,
                    title = show.Title,
                    pending = true,
                    hint = "Matched, but not fetched yet. Use Fetch now, or run the Anime Markers Sync task."
                });
            }

            var markersByNumber = new Dictionary<int, AnimeMarkerEpisode>();
            foreach (var episode in entry.Episodes)
            {
                markersByNumber[episode.Number] = episode;
            }

            var numbering = Resolver.BuildNumbering(series);

            // One row per episode slot, with how many library files sit behind it.
            var rows = numbering.Episodes
                .GroupBy(numbered => (numbered.Episode.ParentIndexNumber, numbered.Episode.IndexNumber))
                .Select(group =>
                {
                    var numbered = group.First();
                    markersByNumber.TryGetValue(numbered.AbsoluteNumber, out var marker);

                    return new
                    {
                        season = numbered.Episode.ParentIndexNumber,
                        index = numbered.Episode.IndexNumber,
                        absolute = numbered.AbsoluteNumber,
                        name = numbered.Episode.Name,
                        kind = marker?.Kind.ToString(),
                        recap = marker?.Recap ?? false,
                        marked = marker != null,
                        copies = group.Count()
                    };
                })
                .OrderBy(row => row.season)
                .ThenBy(row => row.index)
                .ToList();

            return Json(new
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
                libraryFiles = numbering.Episodes.Count,
                markedEpisodes = rows.Count(row => row.marked),
                fillerEpisodes = rows.Count(row => row.kind == nameof(AnimeEpisodeKind.Filler)),
                episodes = rows
            });
        }

        /// <summary>Fetches one series' show page now instead of waiting for the nightly task.</summary>
        public async Task<object> Post(RefreshAnimeMarkersRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SeriesId))
            {
                return Json(400, new { error = "Missing or malformed seriesId" });
            }

            if (!(Resolver.FindItem(request.SeriesId) is Series series))
            {
                return Json(404, new { error = "Series not found" });
            }

            await Client.EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            await Resolver.RefreshAliasesAsync(new[] { series }, CancellationToken.None).ConfigureAwait(false);

            var show = Resolver.MatchSeries(series);
            if (show == null)
            {
                return Json(new { matched = false, series = series.Name });
            }

            var episodes = await Client.FetchEpisodesAsync(show.Slug, CancellationToken.None).ConfigureAwait(false);
            if (episodes == null || episodes.Count == 0)
            {
                return Json(new { matched = true, slug = show.Slug, fetched = false });
            }

            Cache.Set(show.Slug, new AnimeMarkerCacheEntry
            {
                Slug = show.Slug,
                Title = show.Title,
                Episodes = episodes
            });

            await Cache.FlushAsync().ConfigureAwait(false);

            return Json(new { matched = true, slug = show.Slug, fetched = true, episodes = episodes.Count });
        }

        public object Get(GetAnimeMarkerLogRequest request)
        {
            var clamped = Math.Max(1, Math.Min(2000, request.Lines ?? 200));

            return Json(new
            {
                enabled = AnimeMarkerDiagnosticLog.Enabled,
                path = Diagnostics.FilePath,
                sizeBytes = Diagnostics.SizeBytes,
                lines = Diagnostics.Tail(clamped)
            });
        }

        public async Task<object> Post(ClearAnimeMarkerLogRequest request)
        {
            var cleared = await Diagnostics.ClearAsync().ConfigureAwait(false);
            return Json(new { cleared });
        }

        // The show catalog is kept, only the episode tables are dropped.
        public async Task<object> Post(ClearAnimeMarkerCacheRequest request)
        {
            var removed = await Cache.ClearAsync().ConfigureAwait(false);
            return Json(new { cleared = removed });
        }
    }
}
