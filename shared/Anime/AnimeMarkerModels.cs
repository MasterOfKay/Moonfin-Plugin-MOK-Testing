using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Moonfin.Anime;

/// <summary>
/// One of the four categories AnimeFillerList uses to classify episodes. The site doesnt
/// have a recap category, so recaps are stored in a field of their own.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnimeEpisodeKind
{
    /// <summary>Adapted straight from the manga.</summary>
    MangaCanon,

    /// <summary>Not in the manga, but part of the anime's own continuity.</summary>
    AnimeCanon,

    /// <summary>Part manga, part padding. Worth watching, worth warning about.</summary>
    Mixed,

    Filler
}

/// <summary>
/// One AnimeFillerList show as it appears in the site's A-Z index.
/// </summary>
public class AnimeFillerShow
{
    /// <summary>The path segment under /shows/, used verbatim. The site's own slugs are
    /// not always derivable from the title, so this is stored rather than recomputed.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>The display title exactly as the index lists it, parentheses included.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// One episode's classification, numbered the way AnimeFillerList numbers it: absolute
/// across the whole show, ignoring season boundaries.
/// </summary>
public class AnimeMarkerEpisode
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("kind")]
    public AnimeEpisodeKind Kind { get; set; }

    /// <summary>
    /// MyAnimeList's recap flag, filled in separately and only when the recap lookup is
    /// enabled. AnimeFillerList has no recap category of its own.
    /// </summary>
    [JsonPropertyName("recap")]
    public bool Recap { get; set; }

    /// <summary>
    /// The episode's title as the site lists it, which is usually the English dub title. Only
    /// used to tell apart two episodes that aired on the same day.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// The original air date the site lists. Null in tables cached before dates were read.
    /// </summary>
    [JsonPropertyName("airDate")]
    public DateTime? AirDate { get; set; }
}

/// <summary>
/// One cached show and every episode AnimeFillerList lists for it.
/// </summary>
public class AnimeMarkerCacheEntry
{
    /// <summary>
    /// The format tables are parsed in today. Format 2 added each episode's title and air
    /// date, so an older entry still serves its markers but is fetched again on the next sync.
    /// </summary>
    public const int CurrentFormat = 2;

    [JsonPropertyName("format")]
    public int Format { get; set; }

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("episodes")]
    public List<AnimeMarkerEpisode> Episodes { get; set; } = new();

    /// <summary>
    /// The MyAnimeList id the recap flags came from, when one was resolved. Null means the
    /// recap pass hasnt run for this show, so <see cref="AnimeMarkerEpisode.Recap"/> is
    /// "not known" rather than "known false".
    /// </summary>
    [JsonPropertyName("recapMalId")]
    public int? RecapMalId { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}

/// <summary>
/// The titles AniList knows one provider id by. Lets a series whose library name is
/// localized still be found under the English or romaji title AnimeFillerList uses.
/// </summary>
public class AnimeTitleAliasEntry
{
    /// <summary>
    /// The MyAnimeList id behind the titles, when AniList has one.
    /// </summary>
    [JsonPropertyName("malId")]
    public int? MalId { get; set; }

    /// <summary>
    /// English title, then romaji, then synonyms. Empty when AniList had no entry.
    /// </summary>
    [JsonPropertyName("titles")]
    public List<string> Titles { get; set; } = new();

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }
}
