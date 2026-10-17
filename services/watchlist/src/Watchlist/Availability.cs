using Watchlist.Tmdb;

namespace Watchlist;

/// <summary>A dated release. Kind is cinema, digital, disc, tv or aired (a show's first episode).</summary>
public sealed record Release(string Country, DateOnly Date, string Kind);

public sealed record Offer(string Category, string Provider, int ProviderId);

public sealed record CountryOffers(string Country, IReadOnlyList<Offer> Offers);

/// <summary>
/// What one release check found, worldwide. Stored on the item as JSON and sent to the app as is.
/// </summary>
public sealed record Availability(
    bool IsAvailable,
    Release? FirstHome,
    Release? FirstCinema,
    Release? UkHome,
    Release? UkCinema,
    IReadOnlyList<Offer> Uk,
    IReadOnlyList<CountryOffers> Elsewhere,
    NextEpisode? NextEpisode,
    string? ShowStatus,
    int? NumberOfSeasons)
{
    public const string UkCode = "GB";

    /// <summary>
    /// Available means watchable at home somewhere in the world: offered by any service in any
    /// country, or a digital, disc or TV release date that has passed anywhere. Being in cinemas
    /// doesn't count. A show counts once its first episode has aired.
    /// </summary>
    public static Availability From(TitleDetails title, DateOnly today)
    {
        var homeReleases = new List<Release>();
        var cinemaReleases = new List<Release>();
        foreach (var r in title.Releases)
        {
            switch (r.Type)
            {
                case 2 or 3: cinemaReleases.Add(new Release(r.Country, r.Date, "cinema")); break;
                case 4: homeReleases.Add(new Release(r.Country, r.Date, "digital")); break;
                case 5: homeReleases.Add(new Release(r.Country, r.Date, "disc")); break;
                case 6: homeReleases.Add(new Release(r.Country, r.Date, "tv")); break;
            }
        }
        if (title.Ref.MediaType == MediaTypes.Tv && title.FirstAirDate is { } aired)
            homeReleases.Add(new Release(title.OriginCountries.FirstOrDefault() ?? "", aired, "aired"));

        var offersByCountry = title.Offers
            .GroupBy(o => o.Country)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Offer>)g
                    .Select(o => new Offer(o.Category, o.Provider, o.ProviderId))
                    .Distinct()
                    .OrderBy(o => CategoryOrder(o.Category))
                    .ToList());

        var isAvailable = offersByCountry.Count > 0 || homeReleases.Any(r => r.Date <= today);

        return new Availability(
            isAvailable,
            Earliest(homeReleases),
            Earliest(cinemaReleases),
            Earliest(homeReleases.Where(r => r.Country == UkCode)),
            Earliest(cinemaReleases.Where(r => r.Country == UkCode)),
            offersByCountry.GetValueOrDefault(UkCode) ?? [],
            offersByCountry
                .Where(kv => kv.Key != UkCode)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CountryOffers(kv.Key, kv.Value))
                .ToList(),
            title.NextEpisode,
            title.Ref.MediaType == MediaTypes.Tv ? title.Status : null,
            title.NumberOfSeasons);
    }

    private static Release? Earliest(IEnumerable<Release> releases) =>
        releases.OrderBy(r => r.Date).ThenBy(r => r.Country, StringComparer.Ordinal).FirstOrDefault();

    private static int CategoryOrder(string category) => category switch
    {
        "flatrate" => 0,
        "free" => 1,
        "ads" => 2,
        "rent" => 3,
        _ => 4,
    };
}
