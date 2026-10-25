using Moonfin.Anime;
using Xunit;

namespace Moonfin.Anime.Tests;

/// <summary>
/// Tests reading the show index and the episode tables out of the site's HTML.
/// </summary>
public class AnimeFillerListParserTests
{
    private const string IndexHtml = """
        <div class="Group"><h2>A</h2><ul>
        <li><a href="/shows/certain-magical-index">A Certain Magical Index (Toaru Majutsu No Index)</a></li>
        <li><a href="/shows/sh%C5%8Dnan-pure-love-gang">A Certain Scientific Accelerator (Toaru Kagaku no Accelerator)</a></li>
        <li><a href="/shows/attack-titan">Attack on Titan (Shingeki no Kyojin)</a></li>
        <li><a href="/shows/akame-ga-kill">Akame ga Kill!</a></li>
        </ul></div>
        """;

    private const string EpisodeHtml = """
        <table class="EpisodeList"><tbody>
        <tr class="manga_canon odd" id="eps-1"><td class="Number">1</td><td class="Type"><span>Manga Canon</span></td></tr>
        <tr class="filler even" id="eps-2"><td class="Number">2</td><td class="Type"><span>Filler</span></td></tr>
        <tr class="mixed_canon/filler odd" id="eps-3"><td class="Number">3</td><td class="Type"><span>Mixed Canon/Filler</span></td></tr>
        <tr class="anime_canon even" id="eps-4"><td class="Number">4</td><td class="Type"><span>Anime Canon</span></td></tr>
        </tbody></table>
        """;

    [Fact]
    public void ParseShowIndex_ReadsSlugAndTitle()
    {
        var shows = AnimeFillerListParser.ParseShowIndex(IndexHtml);

        Assert.Equal(4, shows.Count);
        Assert.Equal("attack-titan", shows[2].Slug);
        Assert.Equal("Attack on Titan (Shingeki no Kyojin)", shows[2].Title);
    }

    [Fact]
    public void ParseShowIndex_KeepsPercentEncodedSlugVerbatim()
    {
        var shows = AnimeFillerListParser.ParseShowIndex(IndexHtml);

        // The slug is the URL path segment, so re-encoding or decoding it would 404.
        Assert.Contains(shows, show => show.Slug == "sh%C5%8Dnan-pure-love-gang");
    }

    [Fact]
    public void ParseEpisodes_ReadsEveryCategory()
    {
        var episodes = AnimeFillerListParser.ParseEpisodes(EpisodeHtml);

        Assert.Equal(
            new[]
            {
                AnimeEpisodeKind.MangaCanon,
                AnimeEpisodeKind.Filler,
                AnimeEpisodeKind.Mixed,
                AnimeEpisodeKind.AnimeCanon
            },
            episodes.Select(episode => episode.Kind));
    }

    [Fact]
    public void ParseEpisodes_SortsByNumber()
    {
        const string outOfOrder = """
            <tr class="filler odd" id="eps-12"><td class="Number">12</td></tr>
            <tr class="manga_canon even" id="eps-3"><td class="Number">3</td></tr>
            """;

        var episodes = AnimeFillerListParser.ParseEpisodes(outOfOrder);

        Assert.Equal(new[] { 3, 12 }, episodes.Select(episode => episode.Number));
    }

    [Fact]
    public void ParseEpisodes_SkipsUnknownCategories()
    {
        // A category the site adds later must not be silently recorded as canon.
        const string unknown = """
            <tr class="brand_new_category odd" id="eps-1"><td class="Number">1</td></tr>
            <tr class="filler even" id="eps-2"><td class="Number">2</td></tr>
            """;

        var episodes = AnimeFillerListParser.ParseEpisodes(unknown);

        Assert.Equal(2, Assert.Single(episodes).Number);
    }

    [Fact]
    public void ParseEpisodes_ReadsTitleAndAirDate()
    {
        // Copied from the site's Pokémon table, which is the show air dates were added for.
        const string row = """
            <tr class="manga_canon odd" id="eps-11"><td class="Number">11</td><td class="Title"><a href="/shows/pokemon/charmander-%E2%80%93-stray-pok%C3%A9mon" rel="nofollow">Charmander-The Stray Pok&eacute;mon</a></td><td class="Type"><span>Manga Canon</span></td><td class="Date">1997-06-10</td> </tr>
            """;

        var episode = Assert.Single(AnimeFillerListParser.ParseEpisodes(row));

        Assert.Equal("Charmander-The Stray Pokémon", episode.Title);
        Assert.Equal(new DateTime(1997, 6, 10), episode.AirDate);
    }

    [Fact]
    public void ParseEpisodes_DoesNotBorrowTheNextRowsCells()
    {
        // The first row has no title or date of its own, so reading past its end would hand
        // it the second row's air date and pair it with the wrong episode.
        const string rows = """
            <tr class="filler odd" id="eps-1"><td class="Number">1</td></tr>
            <tr class="filler even" id="eps-2"><td class="Number">2</td><td class="Title">Two</td><td class="Date">2001-01-08</td></tr>
            """;

        var episodes = AnimeFillerListParser.ParseEpisodes(rows);

        Assert.Null(episodes[0].Title);
        Assert.Null(episodes[0].AirDate);
        Assert.Equal("Two", episodes[1].Title);
        Assert.Equal(new DateTime(2001, 1, 8), episodes[1].AirDate);
    }

    [Fact]
    public void ParseEpisodes_ReturnsEmptyForUnrelatedMarkup()
    {
        Assert.Empty(AnimeFillerListParser.ParseEpisodes("<html><body>Not found</body></html>"));
    }
}
