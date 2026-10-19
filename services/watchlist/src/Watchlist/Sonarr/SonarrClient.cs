using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Watchlist.Sonarr;

public sealed record SonarrSeries(int Id, int TvdbId, string Title, bool Added);
public sealed record SonarrEpisode(int Id, int SeasonNumber, int EpisodeNumber, bool HasFile, bool Monitored);

/// <summary>
/// What Sonarr has for one show. Wanted counts the monitored episodes that have aired (or have a
/// file); Sonarr unmonitors an episode when you delete its file, so a deleted episode isn't wanted.
/// </summary>
public sealed record SonarrShow(int Id, int TvdbId, int Files, int Wanted)
{
    public bool HasFiles => Files > 0;

    /// <summary>Wanted episodes still to find.</summary>
    public bool Missing => Wanted > Files;
}

/// <summary>The Sonarr API calls needed to add a show, search it, see how it's doing, and drop it.</summary>
public sealed class SonarrClient
{
    private readonly HttpClient _http;
    private readonly SonarrOptions _options;
    private int? _profileId;

    public SonarrClient(HttpClient http, IOptions<SonarrOptions> options)
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

    public async Task<SonarrShow?> FindAsync(int tvdbId, CancellationToken ct)
    {
        var series = await _http.GetFromJsonAsync<JsonArray>($"series?tvdbId={tvdbId}", ct) ?? [];
        return series.OfType<JsonObject>().Where(s => (int?)s["tvdbId"] == tvdbId).Select(Show).FirstOrDefault();
    }

    public async Task<SonarrShow?> GetAsync(int id, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"series/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return Show((await response.Content.ReadFromJsonAsync<JsonObject>(ct))!);
    }

    /// <summary>True while the show has a download in Sonarr's queue.</summary>
    public async Task<bool> IsDownloadingAsync(int id, CancellationToken ct)
    {
        var queue = await _http.GetFromJsonAsync<JsonArray>($"queue/details?seriesId={id}", ct) ?? [];
        return queue.Count > 0;
    }

    /// <summary>Stops the show's downloads and removes them, with their data, from qBittorrent.</summary>
    public async Task CancelDownloadsAsync(int id, CancellationToken ct)
    {
        var queue = await _http.GetFromJsonAsync<JsonArray>($"queue/details?seriesId={id}", ct) ?? [];
        foreach (var entry in queue.OfType<JsonObject>())
        {
            using var response = await _http.DeleteAsync($"queue/{(int)entry["id"]!}?removeFromClient=true&blocklist=false", ct);
            if (response.StatusCode != HttpStatusCode.NotFound)
                await EnsureSuccessAsync(response, ct);
        }
    }

    /// <summary>Removes the show from Sonarr, with its files.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"series/{id}?deleteFiles=true", ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, ct);
    }

    public async Task<SonarrSeries> FindOrAddAsync(int tvdbId, string title, string scope, int? season, CancellationToken ct)
    {
        var lookup = await _http.GetFromJsonAsync<JsonArray>($"series/lookup?term=tvdb:{tvdbId}", ct)
                     ?? throw new InvalidOperationException("Sonarr returned no series lookup results.");
        var match = lookup.OfType<JsonObject>().FirstOrDefault(s => (int?)s["tvdbId"] == tvdbId)
                    ?? throw new InvalidOperationException($"Sonarr couldn't find {title} by its TVDB id.");

        var existing = await _http.GetFromJsonAsync<JsonArray>($"series?tvdbId={tvdbId}", ct) ?? [];
        var series = existing.OfType<JsonObject>().FirstOrDefault(s => (int?)s["tvdbId"] == tvdbId);
        var added = series is null;
        if (series is null)
        {
            match["qualityProfileId"] = await ProfileIdAsync(ct);
            match["rootFolderPath"] = _options.RootFolder;
            match["monitored"] = true;
            ConfigureSeasons(match, scope, season);
            match["addOptions"] = new JsonObject
            {
                ["searchForMissingEpisodes"] = scope is "series" or "season",
            };
            using var response = await _http.PostAsJsonAsync("series", match, ct);
            await EnsureSuccessAsync(response, ct);
            series = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                     ?? throw new InvalidOperationException("Sonarr added the series but returned no details.");
        }
        else
        {
            SetSeriesMonitoring(series, scope, season);
            using var response = await _http.PutAsJsonAsync($"series/{(int)series["id"]!}", series, ct);
            await EnsureSuccessAsync(response, ct);
            series = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? series;
        }

        return new SonarrSeries((int)series["id"]!, tvdbId, (string?)series["title"] ?? title, added);
    }

    public async Task<IReadOnlyList<SonarrEpisode>> EpisodesAsync(int seriesId, int? season, CancellationToken ct)
    {
        var query = $"episode?seriesId={seriesId}" + (season is null ? "" : $"&seasonNumber={season}");
        var episodes = await _http.GetFromJsonAsync<JsonArray>(query, ct) ?? [];
        return episodes.OfType<JsonObject>().Select(e => new SonarrEpisode(
            (int)e["id"]!, (int)e["seasonNumber"]!, (int)e["episodeNumber"]!,
            (bool?)e["hasFile"] ?? false, (bool?)e["monitored"] ?? false)).ToList();
    }

    public async Task<SonarrEpisode?> WaitForEpisodeAsync(int seriesId, int season, int episode, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var match = (await EpisodesAsync(seriesId, season, ct)).FirstOrDefault(e => e.EpisodeNumber == episode);
            if (match is not null)
                return match;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return null;
    }

    public async Task MonitorEpisodesAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("episode/monitor", new { episodeIds = ids, monitored = true }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task SearchSeriesAsync(int id, CancellationToken ct) =>
        await CommandAsync(new { name = "SeriesSearch", seriesId = id }, ct);

    public async Task SearchSeasonAsync(int id, int season, CancellationToken ct) =>
        await CommandAsync(new { name = "SeasonSearch", seriesId = id, seasonNumber = season }, ct);

    public async Task SearchEpisodesAsync(IReadOnlyList<int> ids, CancellationToken ct) =>
        await CommandAsync(new { name = "EpisodeSearch", episodeIds = ids }, ct);

    private async Task CommandAsync(object command, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("command", command, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static void ConfigureSeasons(JsonObject series, string scope, int? selectedSeason)
    {
        if (series["seasons"] is not JsonArray seasons)
            return;
        foreach (var entry in seasons.OfType<JsonObject>())
        {
            var number = (int?)entry["seasonNumber"] ?? -1;
            entry["monitored"] = (scope == "series" && number > 0) || (scope == "season" && number == selectedSeason);
        }
    }

    private static void SetSeriesMonitoring(JsonObject series, string scope, int? selectedSeason)
    {
        series["monitored"] = true;
        if (scope == "episode" || series["seasons"] is not JsonArray seasons)
            return;
        foreach (var entry in seasons.OfType<JsonObject>())
        {
            var number = (int?)entry["seasonNumber"] ?? -1;
            if ((scope == "series" && number > 0) || (scope == "season" && number == selectedSeason))
                entry["monitored"] = true;
        }
    }

    private async Task<int> ProfileIdAsync(CancellationToken ct)
    {
        if (_profileId is { } id)
            return id;
        var profiles = await _http.GetFromJsonAsync<JsonArray>("qualityprofile", ct) ?? [];
        var match = profiles.OfType<JsonObject>()
            .FirstOrDefault(p => string.Equals((string?)p["name"], _options.QualityProfile, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Sonarr has no quality profile called \"{_options.QualityProfile}\".");
        return (_profileId = (int)match["id"]!).Value;
    }

    private static SonarrShow Show(JsonObject s) => new(
        (int)s["id"]!, (int?)s["tvdbId"] ?? 0,
        (int?)s["statistics"]?["episodeFileCount"] ?? 0,
        (int?)s["statistics"]?["episodeCount"] ?? 0);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Sonarr said {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);
        }
    }
}
