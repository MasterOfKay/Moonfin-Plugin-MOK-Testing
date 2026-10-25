using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Moonfin.Anime;

/// <summary>
/// Parses the HTML of animefillerlist.com into a form that can be used to mark episodes as filler, recap or canon.
/// </summary>
public static class AnimeFillerListParser
{
    private static readonly Regex ShowLink = new(
        @"<a\s+href=""/shows/(?<slug>[^""/]+)""\s*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The row carries its classification as the first token of its own class attribute,
    /// with the site's striping token appended: <c>class="mixed_canon/filler even"</c>.
    /// </summary>
    private static readonly Regex EpisodeRow = new(
        @"<tr\s+class=""(?<kind>[^""]+?)(?:\s+(?:odd|even))?""\s+id=""eps-(?<number>\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The Title cell, usually wrapping the title in a link to the episode page.</summary>
    private static readonly Regex TitleCell = new(
        @"<td\s+class=""Title""[^>]*>(?<title>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex DateCell = new(
        @"<td\s+class=""Date""[^>]*>\s*(?<date>\d{4}-\d{2}-\d{2})\s*</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Reads the show list out of /shows. Duplicate slugs are collapsed, keeping the first title seen.
    /// </summary>
    public static List<AnimeFillerShow> ParseShowIndex(string html)
    {
        var shows = new List<AnimeFillerShow>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ShowLink.Matches(html))
        {
            var slug = match.Groups["slug"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();

            if (slug.Length == 0 || title.Length == 0 || !seenSlugs.Add(slug))
            {
                continue;
            }

            shows.Add(new AnimeFillerShow { Slug = slug, Title = title });
        }

        return shows;
    }

    /// <summary>
    /// Reads one show's episode table, sorted by number with duplicates dropped.
    /// </summary>
    public static List<AnimeMarkerEpisode> ParseEpisodes(string html)
    {
        var byNumber = new Dictionary<int, AnimeMarkerEpisode>();

        foreach (Match match in EpisodeRow.Matches(html))
        {
            if (!int.TryParse(match.Groups["number"].Value, out var number) || number <= 0)
            {
                continue;
            }

            var kind = ParseKind(match.Groups["kind"].Value);
            if (kind == null)
            {
                continue;
            }

            var row = RowAt(html, match.Index);

            byNumber[number] = new AnimeMarkerEpisode
            {
                Number = number,
                Kind = kind.Value,
                Title = ParseTitle(row),
                AirDate = ParseAirDate(row)
            };
        }

        return byNumber.Values.OrderBy(episode => episode.Number).ToList();
    }

    /// <summary>
    /// The markup of the row starting at <paramref name="start"/>, so its cells cant be read
    /// from the next row when one is missing.
    /// </summary>
    private static string RowAt(string html, int start)
    {
        var end = html.IndexOf("</tr>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? html.Substring(start) : html.Substring(start, end - start);
    }

    private static string? ParseTitle(string row)
    {
        var match = TitleCell.Match(row);
        if (!match.Success)
        {
            return null;
        }

        var title = WebUtility.HtmlDecode(Tag.Replace(match.Groups["title"].Value, string.Empty)).Trim();
        return title.Length == 0 ? null : title;
    }

    private static DateTime? ParseAirDate(string row)
    {
        var match = DateCell.Match(row);
        return match.Success &&
               DateTime.TryParseExact(
                   match.Groups["date"].Value,
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date)
            ? date
            : null;
    }

    private static AnimeEpisodeKind? ParseKind(string cssClass) => cssClass.Trim().ToLowerInvariant() switch
    {
        "manga_canon" => AnimeEpisodeKind.MangaCanon,
        "anime_canon" => AnimeEpisodeKind.AnimeCanon,
        "mixed_canon/filler" => AnimeEpisodeKind.Mixed,
        "filler" => AnimeEpisodeKind.Filler,
        _ => null
    };
}
