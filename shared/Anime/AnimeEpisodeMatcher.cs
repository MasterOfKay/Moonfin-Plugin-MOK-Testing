using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Anime;

/// <summary>
/// A library episode, with its absolute number, air date, and title. The site may have
/// a different title, and the library may have no date or title at all.
/// </summary>
public sealed class AnimeEpisodeSlot
{
    public AnimeEpisodeSlot(int absoluteNumber, DateTime? airDate, string? title)
    {
        AbsoluteNumber = absoluteNumber;
        AirDate = airDate?.Date;
        Title = title;
    }

    public int AbsoluteNumber { get; }

    public DateTime? AirDate { get; }

    public string? Title { get; }
}

/// <summary>
/// The site's episode for each library slot, in the order the slots were given, plus how the
/// pairing was decided so the admin page can show it.
/// </summary>
public sealed class AnimeEpisodeMatchResult
{
    public AnimeEpisodeMatchResult(
        string mode,
        IReadOnlyList<AnimeMarkerEpisode?> markers,
        int datedEpisodes,
        int numberAgreements,
        int airDateMatches)
    {
        Mode = mode;
        Markers = markers;
        DatedEpisodes = datedEpisodes;
        NumberAgreements = numberAgreements;
        AirDateMatches = airDateMatches;
    }

    /// <summary><see cref="AnimeEpisodeMatcher.NumberMode"/> or <see cref="AnimeEpisodeMatcher.AirDateMode"/>.</summary>
    public string Mode { get; }

    /// <summary>The site's episode per slot, null where nothing matched.</summary>
    public IReadOnlyList<AnimeMarkerEpisode?> Markers { get; }

    /// <summary>Slots whose number lands on a site episode and where both sides have a date.</summary>
    public int DatedEpisodes { get; }

    /// <summary>How many of those dated slots aired within a day of the site episode their number picks.</summary>
    public int NumberAgreements { get; }

    /// <summary>How many slots the air dates paired, whichever mode won.</summary>
    public int AirDateMatches { get; }
}

/// <summary>
/// Pairs library episodes with the site's episode table.
///
/// The number is used whenever it can be trusted, which is every library that worked before
/// air dates were read. Air dates only take over when they prove the numbers wrong: the site
/// numbers some shows in a dub order that skips episodes (Pokémon leaves out the banned and
/// undubbed ones), so every episode after the first gap lands on the wrong marker, while
/// the original air dates still line up.
/// </summary>
public static class AnimeEpisodeMatcher
{
    public const string NumberMode = "number";

    public const string AirDateMode = "airdate";

    /// <summary>
    /// Fewer dated episodes than this cant tell a numbering problem apart from a few bad
    /// dates, so the number is kept.
    /// </summary>
    private const int MinimumEvidence = 10;

    /// <summary>
    /// When at least this share of dated episodes agree with their number, the numbering is
    /// right and the stragglers are metadata noise.
    /// </summary>
    private const double TrustedNumberAgreement = 0.9;

    /// <summary>
    /// Air dates only win when they pair at least this share of the dated library episodes,
    /// so a library whose dates come from another country's broadcast keeps its numbers.
    /// </summary>
    private const double RequiredAirDateCoverage = 0.5;

    /// <summary>Air dates from two sources can sit either side of midnight.</summary>
    private const int DayTolerance = 1;

    public static AnimeEpisodeMatchResult Match(
        IReadOnlyList<AnimeEpisodeSlot> slots,
        IReadOnlyList<AnimeMarkerEpisode> siteEpisodes)
    {
        var byNumber = new Dictionary<int, AnimeMarkerEpisode>();
        foreach (var episode in siteEpisodes)
        {
            byNumber[episode.Number] = episode;
        }

        var numbered = slots
            .Select(slot => byNumber.TryGetValue(slot.AbsoluteNumber, out var episode) ? episode : null)
            .ToList();

        var dated = 0;
        var agreements = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var slotDate = slots[i].AirDate;
            var siteDate = numbered[i]?.AirDate;
            if (slotDate == null || siteDate == null)
            {
                continue;
            }

            dated++;
            if (WithinTolerance(slotDate.Value, siteDate.Value))
            {
                agreements++;
            }
        }

        // The common case, and the one every working library is in: the numbers line up, or
        // there are too few dates to say otherwise. Nothing else is computed.
        if (dated < MinimumEvidence || agreements >= dated * TrustedNumberAgreement)
        {
            return new AnimeEpisodeMatchResult(NumberMode, numbered, dated, agreements, 0);
        }

        var byDate = MatchByAirDate(slots, siteEpisodes);
        var dateMatches = byDate.Count(episode => episode != null);
        var datedSlots = slots.Count(slot => slot.AirDate != null);

        var datesExplainLibrary =
            dateMatches >= MinimumEvidence &&
            dateMatches > agreements &&
            dateMatches >= datedSlots * RequiredAirDateCoverage;

        return datesExplainLibrary
            ? new AnimeEpisodeMatchResult(AirDateMode, byDate, dated, agreements, dateMatches)
            : new AnimeEpisodeMatchResult(NumberMode, numbered, dated, agreements, dateMatches);
    }

    /// <summary>
    /// Pairs each slot with the site episode that aired the same day, then with one a day
    /// either side. A slot with no date, or no unambiguous partner, stays unmatched rather
    /// than borrowing a neighbour's marker.
    /// </summary>
    private static AnimeMarkerEpisode?[] MatchByAirDate(
        IReadOnlyList<AnimeEpisodeSlot> slots,
        IReadOnlyList<AnimeMarkerEpisode> siteEpisodes)
    {
        var result = new AnimeMarkerEpisode?[slots.Count];
        var used = new HashSet<int>();

        var siteByDate = siteEpisodes
            .Where(episode => episode.AirDate != null)
            .GroupBy(episode => episode.AirDate!.Value.Date)
            .ToDictionary(group => group.Key, group => group.OrderBy(episode => episode.Number).ToList());

        // Same day first. A day can carry two episodes (a double broadcast), so slots and
        // site episodes are paired per day rather than one by one.
        var slotsByDate = Enumerable.Range(0, slots.Count)
            .Where(i => slots[i].AirDate != null)
            .GroupBy(i => slots[i].AirDate!.Value)
            .OrderBy(group => group.Key);

        foreach (var day in slotsByDate)
        {
            if (!siteByDate.TryGetValue(day.Key, out var candidates))
            {
                continue;
            }

            PairDay(slots, day.OrderBy(i => slots[i].AbsoluteNumber).ToList(), candidates, result, used);
        }

        // Then a day either side, for dates that crossed midnight between the two sources.
        // Only an unambiguous candidate is taken.
        for (var i = 0; i < slots.Count; i++)
        {
            var date = slots[i].AirDate;
            if (result[i] != null || date == null)
            {
                continue;
            }

            var nearby = new List<AnimeMarkerEpisode>();
            for (var offset = -DayTolerance; offset <= DayTolerance; offset++)
            {
                if (offset != 0 && siteByDate.TryGetValue(date.Value.AddDays(offset), out var onDay))
                {
                    nearby.AddRange(onDay.Where(episode => !used.Contains(episode.Number)));
                }
            }

            var pick = nearby.Count == 1 ? nearby[0] : UniqueTitleMatch(slots[i], nearby);
            if (pick != null)
            {
                result[i] = pick;
                used.Add(pick.Number);
            }
        }

        return result;
    }

    /// <summary>
    /// Pairs the slots and site episodes of one day. Matching titles pair first; whatever is
    /// left pairs in order, but only when both sides have the same number of episodes left.
    /// </summary>
    private static void PairDay(
        IReadOnlyList<AnimeEpisodeSlot> slots,
        List<int> slotIndexes,
        List<AnimeMarkerEpisode> candidates,
        AnimeMarkerEpisode?[] result,
        HashSet<int> used)
    {
        var open = candidates.Where(episode => !used.Contains(episode.Number)).ToList();
        var unpaired = new List<int>();

        foreach (var index in slotIndexes)
        {
            var pick = open.Count == 1 && slotIndexes.Count == 1
                ? open[0]
                : UniqueTitleMatch(slots[index], open);

            if (pick == null)
            {
                unpaired.Add(index);
                continue;
            }

            result[index] = pick;
            used.Add(pick.Number);
            open.Remove(pick);
        }

        if (unpaired.Count == 0 || unpaired.Count != open.Count)
        {
            return;
        }

        for (var i = 0; i < unpaired.Count; i++)
        {
            result[unpaired[i]] = open[i];
            used.Add(open[i].Number);
        }
    }

    /// <summary>
    /// The one candidate whose title matches the slot's once both are normalized, or null.
    /// The site lists English dub titles, so this only helps English libraries; anything else
    /// falls through to air order.
    /// </summary>
    private static AnimeMarkerEpisode? UniqueTitleMatch(AnimeEpisodeSlot slot, List<AnimeMarkerEpisode> candidates)
    {
        var title = AnimeTitleMatcher.Normalize(slot.Title);
        if (title.Length == 0)
        {
            return null;
        }

        var matches = candidates
            .Where(episode => AnimeTitleMatcher.Normalize(episode.Title) == title)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool WithinTolerance(DateTime a, DateTime b) =>
        Math.Abs((a.Date - b.Date).TotalDays) <= DayTolerance;
}
