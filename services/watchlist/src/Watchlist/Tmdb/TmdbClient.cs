using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Watchlist.Tmdb;

public static class MediaTypes
{
    public const string Movie = "movie";
    public const string Tv = "tv";

    public static bool IsValid(string? type) => type is Movie or Tv;
}

/// <summary>Just enough to show and save a title.</summary>
public sealed record TitleRef(
    int TmdbId, string MediaType, string Title, int? Year, string? PosterPath, string? Overview, double Popularity = 0);

public sealed record PersonHit(int Id, string Name, string? Department, IReadOnlyList<string> KnownFor);

public sealed record Credit(TitleRef Title, string Role);

/// <summary>A release in one country. TMDb types: 1 premiere, 2 limited cinema, 3 cinema, 4 digital, 5 disc, 6 TV.</summary>
public sealed record CountryRelease(string Country, int Type, DateOnly Date);

/// <summary>One way to watch in one country. Category is flatrate, free, ads, rent or buy.</summary>
public sealed record ProviderOffer(string Country, string Category, int ProviderId, string Provider);

public sealed record NextEpisode(DateOnly? AirDate, int Season, int Episode, string? Name);

public sealed record TitleDetails(
    TitleRef Ref,
    string? Status,
    IReadOnlyList<string> Genres,
    int? Runtime,
    IReadOnlyList<string> OriginCountries,
    DateOnly? FirstAirDate,
    int? NumberOfSeasons,
    NextEpisode? NextEpisode,
    IReadOnlyList<CountryRelease> Releases,
    IReadOnlyList<ProviderOffer> Offers);

/// <summary>The few TMDb endpoints the app needs. Responses are parsed by hand to stay small.</summary>
public sealed class TmdbClient
{
    public const string ImageBase = "https://image.tmdb.org/t/p/";
    private static readonly string[] OfferCategories = ["flatrate", "free", "ads", "rent", "buy"];

    private readonly HttpClient _http;

    public TmdbClient(HttpClient http, IOptions<TmdbOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.themoviedb.org/3/");
        if (!string.IsNullOrEmpty(options.Value.ReadToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ReadToken);
    }

    /// <summary>Films and shows matching the query, most popular first. People are left out.</summary>
    public async Task<IReadOnlyList<TitleRef>> SearchTitlesAsync(string query, string? mediaType = null, int? year = null, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query);
        var path = mediaType switch
        {
            MediaTypes.Movie => $"search/movie?query={q}&include_adult=false&language=en-GB{(year is null ? "" : $"&year={year}")}",
            MediaTypes.Tv => $"search/tv?query={q}&include_adult=false&language=en-GB{(year is null ? "" : $"&first_air_date_year={year}")}",
            _ => $"search/multi?query={q}&include_adult=false&language=en-GB",
        };
        using var doc = await GetAsync(path, ct);
        var hits = new List<TitleRef>();
        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var type = mediaType ?? Str(result, "media_type");
            if (!MediaTypes.IsValid(type))
                continue;
            var hit = ParseRef(result, type!);
            if (year is not null && mediaType is null && hit.Year != year)
                continue;
            hits.Add(hit);
        }
        return hits.OrderByDescending(h => h.Popularity).ToList();
    }

    public async Task<IReadOnlyList<PersonHit>> SearchPeopleAsync(string name, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"search/person?query={Uri.EscapeDataString(name)}&include_adult=false&language=en-GB", ct);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(p => new PersonHit(
                p.GetProperty("id").GetInt32(),
                Str(p, "name") ?? "",
                Str(p, "known_for_department"),
                p.TryGetProperty("known_for", out var known)
                    ? known.EnumerateArray().Select(k => Str(k, "title") ?? Str(k, "name") ?? "").Where(t => t != "").ToList()
                    : []))
            .ToList();
    }

    /// <summary>A person's films and shows, newest first. Role is "cast" or their crew job, e.g. "Director".</summary>
    public async Task<IReadOnlyList<Credit>> PersonCreditsAsync(int personId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"person/{personId}/combined_credits?language=en-GB", ct);
        var credits = new List<Credit>();
        foreach (var (list, isCast) in new[] { ("cast", true), ("crew", false) })
        {
            if (!doc.RootElement.TryGetProperty(list, out var entries))
                continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var type = Str(entry, "media_type");
                if (!MediaTypes.IsValid(type))
                    continue;
                credits.Add(new Credit(ParseRef(entry, type!), isCast ? "cast" : Str(entry, "job") ?? "crew"));
            }
        }
        return credits
            .OrderByDescending(c => c.Title.Year ?? 9999)
            .ThenByDescending(c => c.Title.Popularity)
            .ToList();
    }

    /// <summary>Details plus release dates and watch providers for every country, in one request.</summary>
    public async Task<TitleDetails> GetTitleAsync(int tmdbId, string mediaType, CancellationToken ct = default)
    {
        var append = mediaType == MediaTypes.Movie ? "release_dates,watch/providers" : "watch/providers";
        using var doc = await GetAsync($"{mediaType}/{tmdbId}?append_to_response={append}&language=en-GB", ct);
        return ParseDetails(doc.RootElement, mediaType);
    }

    /// <summary>Returns the TVDB id Sonarr uses for a TMDb TV series.</summary>
    public async Task<int> GetTvdbIdAsync(int tmdbId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"tv/{tmdbId}/external_ids", ct);
        return Int(doc.RootElement, "tvdb_id")
               ?? throw new InvalidOperationException($"TMDb has no TVDB id for series {tmdbId}.");
    }

    internal static TitleDetails ParseDetails(JsonElement root, string mediaType)
    {
        var releases = new List<CountryRelease>();
        if (root.TryGetProperty("release_dates", out var rd) && rd.TryGetProperty("results", out var rdResults))
        {
            foreach (var country in rdResults.EnumerateArray())
            {
                var code = Str(country, "iso_3166_1") ?? "";
                foreach (var release in country.GetProperty("release_dates").EnumerateArray())
                {
                    if (Date(release, "release_date") is { } date)
                        releases.Add(new CountryRelease(code, release.GetProperty("type").GetInt32(), date));
                }
            }
        }

        var offers = new List<ProviderOffer>();
        if (root.TryGetProperty("watch/providers", out var wp) && wp.TryGetProperty("results", out var wpResults))
        {
            foreach (var country in wpResults.EnumerateObject())
            {
                foreach (var category in OfferCategories)
                {
                    if (!country.Value.TryGetProperty(category, out var providers))
                        continue;
                    foreach (var p in providers.EnumerateArray())
                        offers.Add(new ProviderOffer(country.Name, category, p.GetProperty("provider_id").GetInt32(), Str(p, "provider_name") ?? ""));
                }
            }
        }

        NextEpisode? next = null;
        if (root.TryGetProperty("next_episode_to_air", out var ne) && ne.ValueKind == JsonValueKind.Object)
            next = new NextEpisode(Date(ne, "air_date"), Int(ne, "season_number") ?? 0, Int(ne, "episode_number") ?? 0, Str(ne, "name"));

        return new TitleDetails(
            ParseRef(root, mediaType),
            Str(root, "status"),
            root.TryGetProperty("genres", out var genres) ? genres.EnumerateArray().Select(g => Str(g, "name") ?? "").ToList() : [],
            Int(root, "runtime"),
            root.TryGetProperty("origin_country", out var oc) ? oc.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [],
            mediaType == MediaTypes.Tv ? Date(root, "first_air_date") : null,
            Int(root, "number_of_seasons"),
            next,
            releases,
            offers);
    }

    private static TitleRef ParseRef(JsonElement e, string mediaType)
    {
        var isMovie = mediaType == MediaTypes.Movie;
        var date = Date(e, isMovie ? "release_date" : "first_air_date");
        return new TitleRef(
            e.GetProperty("id").GetInt32(),
            mediaType,
            Str(e, isMovie ? "title" : "name") ?? "",
            date?.Year,
            Str(e, "poster_path"),
            Str(e, "overview"),
            e.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>Reads "2025-08-08" or "2025-08-08T00:00:00.000Z". Empty strings are common and mean no date.</summary>
    private static DateOnly? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null || s.Length < 10)
            return null;
        return DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
