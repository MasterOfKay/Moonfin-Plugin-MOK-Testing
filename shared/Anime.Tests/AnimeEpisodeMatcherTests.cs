using Moonfin.Anime;
using Xunit;

namespace Moonfin.Anime.Tests;

/// <summary>
/// Tests pairing library episodes with the site's table: by number for every library that
/// already works, by air date only when the dates prove the numbers wrong.
/// </summary>
public class AnimeEpisodeMatcherTests
{
    private static readonly DateTime FirstAirDate = new(1997, 4, 1);

    /// <summary>
    /// A Pokémon-like show: the library has the original 43 episodes, and the site leaves out
    /// 18, 35 and 38 the way the dub did, so its numbers drift from the library's.
    /// </summary>
    private static readonly int[] SkippedBySite = { 18, 35, 38 };

    private static DateTime Weekly(int originalNumber) => FirstAirDate.AddDays(7 * (originalNumber - 1));

    private static List<AnimeMarkerEpisode> SiteTable(int count, Func<int, DateTime?> date, Func<int, string?>? title = null) =>
        Enumerable.Range(1, count)
            .Select(number => new AnimeMarkerEpisode
            {
                Number = number,
                Kind = number % 5 == 0 ? AnimeEpisodeKind.Filler : AnimeEpisodeKind.MangaCanon,
                AirDate = date(number),
                Title = title?.Invoke(number)
            })
            .ToList();

    private static List<AnimeEpisodeSlot> Library(int count, Func<int, DateTime?> date, Func<int, string?>? title = null) =>
        Enumerable.Range(1, count)
            .Select(number => new AnimeEpisodeSlot(number, date(number), title?.Invoke(number)))
            .ToList();

    /// <summary>The site's table for the dub-ordered show, numbered 1..40 over the original 43.</summary>
    private static List<AnimeMarkerEpisode> DubOrderedSite(Func<int, DateTime> originalDate)
    {
        var originals = Enumerable.Range(1, 43).Where(number => !SkippedBySite.Contains(number)).ToList();
        return SiteTable(originals.Count, siteNumber => originalDate(originals[siteNumber - 1]));
    }

    [Fact]
    public void Match_KeepsNumbersWhenTheyAgreeWithTheAirDates()
    {
        var site = SiteTable(30, number => Weekly(number));
        var library = Library(30, number => Weekly(number));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.NumberMode, result.Mode);
        Assert.Equal(site, result.Markers);
        Assert.Equal(30, result.NumberAgreements);
    }

    [Fact]
    public void Match_KeepsNumbersWhenTheTableHasNoDates()
    {
        // A table cached before air dates were read behaves exactly as it always has.
        var site = SiteTable(30, _ => null);
        var library = Library(30, number => Weekly(number));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.NumberMode, result.Mode);
        Assert.Equal(site, result.Markers);
    }

    [Fact]
    public void Match_KeepsNumbersWhenTheLibraryHasNoDates()
    {
        var site = DubOrderedSite(Weekly);
        var library = Library(43, _ => null);

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.NumberMode, result.Mode);
        Assert.Same(site[17], result.Markers[17]);
    }

    [Fact]
    public void Match_KeepsNumbersWhenTooFewEpisodesAreDated()
    {
        var site = DubOrderedSite(Weekly);
        var library = Library(43, number => number <= 5 ? Weekly(number) : null);

        Assert.Equal(AnimeEpisodeMatcher.NumberMode, AnimeEpisodeMatcher.Match(library, site).Mode);
    }

    [Fact]
    public void Match_KeepsNumbersWhenTheLibraryDatesComeFromAnotherBroadcast()
    {
        // Dates from a dub that aired a year later agree with neither the numbers nor the
        // site's dates. The dates explain nothing, so the library keeps what it had.
        var site = SiteTable(30, number => Weekly(number));
        var library = Library(30, number => Weekly(number).AddDays(365));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.NumberMode, result.Mode);
        Assert.Equal(site, result.Markers);
    }

    [Fact]
    public void Match_PairsByAirDateWhenTheSiteSkipsEpisodes()
    {
        var site = DubOrderedSite(Weekly);
        var library = Library(43, number => Weekly(number));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.AirDateMode, result.Mode);

        // Before the first skipped episode the two numberings agree.
        Assert.Equal(17, result.Markers[16]!.Number);

        // The episodes the site leaves out get no marker rather than a neighbour's.
        Assert.Null(result.Markers[17]);
        Assert.Null(result.Markers[34]);
        Assert.Null(result.Markers[37]);

        // Original 19 is the site's 18, and original 43 is its 40.
        Assert.Equal(18, result.Markers[18]!.Number);
        Assert.Equal(40, result.Markers[42]!.Number);
    }

    [Fact]
    public void Match_PairsDatesThatLandADayApart()
    {
        // The library's dates sit a day later, as when one source stores the Japanese date
        // and the other converts it across midnight.
        var site = DubOrderedSite(Weekly);
        var library = Library(43, number => Weekly(number).AddDays(1));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.AirDateMode, result.Mode);
        Assert.Equal(18, result.Markers[18]!.Number);
        Assert.Null(result.Markers[17]);
    }

    [Fact]
    public void Match_PairsADoubleBroadcastInOrder()
    {
        // Originals 39 and 40 aired the same day. Without titles the two are paired in order.
        static DateTime Original(int number) => number >= 40 ? Weekly(number - 1) : Weekly(number);

        var site = DubOrderedSite(Original);
        var library = Library(43, number => Original(number));

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.AirDateMode, result.Mode);
        Assert.Equal(36, result.Markers[38]!.Number);
        Assert.Equal(37, result.Markers[39]!.Number);
    }

    [Fact]
    public void Match_UsesTitlesToSplitADoubleBroadcast()
    {
        // The library lists the two episodes of one day the other way round. Their titles
        // match the site's, which beats air order.
        static DateTime Original(int number) => number >= 40 ? Weekly(number - 1) : Weekly(number);

        var originals = Enumerable.Range(1, 43).Where(number => !SkippedBySite.Contains(number)).ToList();
        var site = SiteTable(
            originals.Count,
            siteNumber => Original(originals[siteNumber - 1]),
            siteNumber => "Episode " + originals[siteNumber - 1]);

        var library = Library(
            43,
            number => Original(number),
            number => number switch
            {
                39 => "Episode 40",
                40 => "Episode 39",
                _ => "Folge " + number
            });

        var result = AnimeEpisodeMatcher.Match(library, site);

        Assert.Equal(AnimeEpisodeMatcher.AirDateMode, result.Mode);
        Assert.Equal("Episode 40", result.Markers[38]!.Title);
        Assert.Equal("Episode 39", result.Markers[39]!.Title);
    }
}
