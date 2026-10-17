using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Watchlist.Radarr;

/// <summary>What Radarr has for one film.</summary>
public sealed record RadarrMovie(int Id, int TmdbId, bool HasFile);

/// <summary>
/// The few Radarr API calls the watchlist needs: add a film (and search for it), see how it's
/// doing, and drop it. Radarr does the searching, choosing, downloading and filing.
/// </summary>
public sealed class RadarrClient
{
    private readonly HttpClient _http;
    private readonly RadarrOptions _options;
    private int? _profileId;

    public RadarrClient(HttpClient http, IOptions<RadarrOptions> options)
    {
        _http = http;
        _options = options.Value;
        if (_options.IsConfigured)
        {
            _http.BaseAddress = new Uri(_options.Url.TrimEnd('/') + "/api/v3/");
            _http.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<RadarrMovie?> FindByTmdbAsync(int tmdbId, CancellationToken ct)
    {
        var movies = await _http.GetFromJsonAsync<JsonArray>($"movie?tmdbId={tmdbId}", ct) ?? [];
        return movies.OfType<JsonObject>().Select(Movie).FirstOrDefault();
    }

    public async Task<RadarrMovie?> GetAsync(int id, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"movie/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return Movie((await response.Content.ReadFromJsonAsync<JsonObject>(ct))!);
    }

    /// <summary>Adds the film, monitored, and starts a search for it straight away.</summary>
    public async Task<RadarrMovie> AddAsync(int tmdbId, CancellationToken ct)
    {
        var movie = await _http.GetFromJsonAsync<JsonObject>($"movie/lookup/tmdb?tmdbId={tmdbId}", ct)
                    ?? throw new InvalidOperationException($"Radarr doesn't know TMDb {tmdbId}");
        movie["qualityProfileId"] = await ProfileIdAsync(ct);
        movie["rootFolderPath"] = _options.RootFolder;
        movie["monitored"] = true;
        movie["minimumAvailability"] = "released";
        movie["addOptions"] = new JsonObject { ["searchForMovie"] = true };

        using var response = await _http.PostAsJsonAsync("movie", movie, ct);
        await EnsureSuccessAsync(response, ct);
        return Movie((await response.Content.ReadFromJsonAsync<JsonObject>(ct))!);
    }

    public async Task SearchAsync(int id, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("command", new { name = "MoviesSearch", movieIds = new[] { id } }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>True while the film has a download in Radarr's queue.</summary>
    public async Task<bool> IsDownloadingAsync(int id, CancellationToken ct)
    {
        var queue = await _http.GetFromJsonAsync<JsonArray>($"queue/details?movieId={id}", ct) ?? [];
        return queue.Count > 0;
    }

    /// <summary>Removes the film from Radarr, and its file if it has one.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"movie/{id}?deleteFiles=true", ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, ct);
    }

    private async Task<int> ProfileIdAsync(CancellationToken ct)
    {
        if (_profileId is { } id)
            return id;
        var profiles = await _http.GetFromJsonAsync<JsonArray>("qualityprofile", ct) ?? [];
        var match = profiles.OfType<JsonObject>()
            .FirstOrDefault(p => string.Equals((string?)p["name"], _options.QualityProfile, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Radarr has no quality profile called \"{_options.QualityProfile}\"");
        return (_profileId = (int)match["id"]!).Value;
    }

    private static RadarrMovie Movie(JsonObject m) =>
        new((int)m["id"]!, (int)m["tmdbId"]!, (bool?)m["hasFile"] ?? false);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Radarr said {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);
        }
    }
}
