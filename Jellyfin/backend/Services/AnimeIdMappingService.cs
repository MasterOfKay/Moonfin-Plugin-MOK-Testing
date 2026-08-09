using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fribb maps if no AniList id is avlaible, the other ID for example ImDb to a MAL id.
/// </summary>
public class AnimeIdMappingService
{
    private const string MappingUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";
    private const string MappingFileName = "anime_id_mapping.json";

    /// <summary>New Season appear first</summary>
    private static readonly TimeSpan MappingMaxAge = TimeSpan.FromDays(14);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeIdMappingService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly string _mappingPath;

    /// <summary>
    /// If the download failes, retry after a interval.
    /// This avoids a overload of the api or if bad internet is blocking many requests.
    /// </summary>
    private static readonly TimeSpan DownloadRetryInterval = TimeSpan.FromHours(6);

    private MappingIndex? _index;
    private DateTimeOffset _lastDownloadAttempt = DateTimeOffset.MinValue;

    public AnimeIdMappingService(IHttpClientFactory httpClientFactory, ILogger<AnimeIdMappingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _mappingPath = Path.Combine(dataPath, MappingFileName);
    }

    /// <summary>
    /// True once the table is in memory and lookups can succeed.
    /// </summary>
    public bool IsLoaded => _index != null;

    /// <summary>
    /// Gets the number of entries in the loaded mapping.
    /// </summary>
    public int MappingCount => _index?.Count ?? 0;

    /// <summary>
    /// Gets the timestamp of the last successful download of the mapping file.
    /// </summary>
    public DateTimeOffset? MappingDownloadedAt =>
        File.Exists(_mappingPath) ? File.GetLastWriteTimeUtc(_mappingPath) : null;

    /// <summary>
    /// Mapping table needs to be loaded in memory befor calling <see cref="Resolve"/> or <see cref="ResolveByTvdbSeason"/>.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_index != null && !IsFileStale())
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index != null && !IsFileStale())
            {
                return;
            }

            // Download is stale or missing. We don't block the caller or fail the download.
            // We retry on the next time frame and keep what we have.
            var downloaded = false;
            if (IsFileStale() && DateTimeOffset.UtcNow - _lastDownloadAttempt > DownloadRetryInterval)
            {
                _lastDownloadAttempt = DateTimeOffset.UtcNow;
                downloaded = await DownloadMappingAsync(cancellationToken).ConfigureAwait(false);
            }

            // Load the table from disk and parse to see what we have in the index.
            if ((_index == null || downloaded) && File.Exists(_mappingPath))
            {
                _index = ParseMapping(_mappingPath);
                _logger.LogInformation("Anime id mapping loaded ({Count} entries with a MAL id)", _index.Count);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool IsFileStale()
    {
        if (!File.Exists(_mappingPath))
        {
            return true;
        }

        return DateTime.UtcNow - File.GetLastWriteTimeUtc(_mappingPath) > MappingMaxAge;
    }

    /// <summary>
    /// Downloads the anime id mapping file.
    /// </summary>
    private async Task<bool> DownloadMappingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client.GetAsync(MappingUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anime id mapping download returned status {Status}, keeping any existing copy", (int)response.StatusCode);
                return false;
            }

            // Before overwriting the file, we write a temp file, and only if it is done and complete we replace the one we have.
            var tempPath = _mappingPath + ".tmp";
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _mappingPath, overwrite: true);
            _logger.LogInformation("Anime id mapping downloaded from Fribb/anime-lists");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anime id mapping download failed, keeping any existing copy");
            return false;
        }
    }

    /// <summary>
    /// Parsing the mapped file to a index for fast lookups.
    /// </summary>
    private MappingIndex ParseMapping(string path)
    {
        var index = new MappingIndex();

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var malId = ReadInt(entry, "mal_id");
            if (malId == null)
            {
                continue;
            }

            index.Count++;

            Add(index.ByAniList, ReadInt(entry, "anilist_id"), malId.Value);
            Add(index.ByAniDb, ReadInt(entry, "anidb_id"), malId.Value);
            Add(index.ByKitsu, ReadInt(entry, "kitsu_id"), malId.Value);
            Add(index.ByAniSearch, ReadInt(entry, "anisearch_id"), malId.Value);

            // TVDB is a special case. Only the first course is mapped, as the others have other Mal ids.
            var tvdbId = ReadInt(entry, "tvdb_id");
            if (tvdbId != null)
            {
                var tvdbSeason = ReadSeason(entry, "tvdb");
                if (tvdbSeason != null)
                {
                    index.ByTvdbSeason.TryAdd((tvdbId.Value, tvdbSeason.Value), malId.Value);
                }

                Add(index.ByTvdb, tvdbId, malId.Value);
            }
        }

        return index;
    }

    private static void Add(Dictionary<int, int> map, int? key, int malId)
    {
        if (key != null)
        {
            // The mapping table is not guaranteed to be unique, so we only add the first one we see. The others are ignored.
            map.TryAdd(key.Value, malId);
        }
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static int? ReadSeason(JsonElement element, string source)
    {
        if (!element.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadInt(season, source);
    }

    /// <summary>
    /// Resolves a MAL id from the given provider ids. Returns null if no mapping is found.
    /// </summary>
    public MalLookupResult? Resolve(IReadOnlyDictionary<string, string> providerIds, int? seasonNumber)
    {
        // A real MAL id needs no mapping at all.
        if (TryGetProviderInt(providerIds, out var directMal, "MyAnimeList", "Mal", "MAL"))
        {
            return new MalLookupResult(directMal, "MyAnimeList id");
        }

        var index = _index;
        if (index == null)
        {
            return null;
        }

        if (TryGetProviderInt(providerIds, out var anilistId, "AniList", "Anilist") &&
            index.ByAniList.TryGetValue(anilistId, out var fromAniList))
        {
            return new MalLookupResult(fromAniList, "AniList id");
        }

        if (TryGetProviderInt(providerIds, out var anidbId, "AniDB", "AniDb", "Anidb") &&
            index.ByAniDb.TryGetValue(anidbId, out var fromAniDb))
        {
            return new MalLookupResult(fromAniDb, "AniDB id");
        }

        if (TryGetProviderInt(providerIds, out var kitsuId, "Kitsu", "KitsuIo") &&
            index.ByKitsu.TryGetValue(kitsuId, out var fromKitsu))
        {
            return new MalLookupResult(fromKitsu, "Kitsu id");
        }

        if (TryGetProviderInt(providerIds, out var anisearchId, "AniSearch", "Anisearch") &&
            index.ByAniSearch.TryGetValue(anisearchId, out var fromAniSearch))
        {
            return new MalLookupResult(fromAniSearch, "AniSearch id");
        }

        if (TryGetProviderInt(providerIds, out var tvdbId, "Tvdb", "TheTVDB"))
        {
            if (seasonNumber != null &&
                index.ByTvdbSeason.TryGetValue((tvdbId, seasonNumber.Value), out var fromTvdbSeason))
            {
                return new MalLookupResult(fromTvdbSeason, $"TVDB id season {seasonNumber}");
            }

            // For TVDB we only have the first season mapped. If others are needed we still only give the first seasons mal id, with a nothe that we need SXX.
            if (index.ByTvdb.TryGetValue(tvdbId, out var fromTvdb))
            {
                return new MalLookupResult(fromTvdb, "TVDB id (no season match)");
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a MAL id from the given provider ids and season number. Returns null if no mapping is found.
    /// </summary>
    public MalLookupResult? ResolveByTvdbSeason(IReadOnlyDictionary<string, string> providerIds, int seasonNumber)
    {
        var index = _index;
        if (index == null)
        {
            return null;
        }

        if (TryGetProviderInt(providerIds, out var tvdbId, "Tvdb", "TheTVDB") &&
            index.ByTvdbSeason.TryGetValue((tvdbId, seasonNumber), out var malId))
        {
            return new MalLookupResult(malId, $"TVDB id season {seasonNumber}");
        }

        return null;
    }

    /// <summary>
    /// Resolves a MAL id from the given AniList id by querying the AniList GraphQL API. Returns null if no mapping is found or if the request fails.
    /// </summary>
    public async Task<int?> ResolveViaAniListAsync(int anilistId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var body = JsonSerializer.Serialize(new
            {
                query = "query($id:Int){Media(id:$id,type:ANIME){idMal}}",
                variables = new { id = anilistId }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("https://graphql.anilist.co", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("Media", out var media) &&
                media.ValueKind == JsonValueKind.Object &&
                media.TryGetProperty("idMal", out var idMal) &&
                idMal.ValueKind == JsonValueKind.Number &&
                idMal.TryGetInt32(out var malId))
            {
                return malId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AniList idMal lookup failed for {AniListId}", anilistId);
        }

        return null;
    }

    /// <summary>
    /// Tries to get an integer value from the provider IDs dictionary for any of the specified keys.
    /// </summary>
    private static bool TryGetProviderInt(IReadOnlyDictionary<string, string> providerIds, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (providerKey, providerValue) in providerIds)
            {
                if (!string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(providerValue?.Trim(), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    private class MappingIndex
    {
        public int Count { get; set; }
        public Dictionary<int, int> ByAniList { get; } = new();
        public Dictionary<int, int> ByAniDb { get; } = new();
        public Dictionary<int, int> ByKitsu { get; } = new();
        public Dictionary<int, int> ByAniSearch { get; } = new();
        public Dictionary<int, int> ByTvdb { get; } = new();
        public Dictionary<(int TvdbId, int Season), int> ByTvdbSeason { get; } = new();
    }
}

/// <summary>
/// A resolved MAL id plus which provider id it came from, shown in diagnostics.
/// </summary>
public record MalLookupResult(int MalId, string Source);
