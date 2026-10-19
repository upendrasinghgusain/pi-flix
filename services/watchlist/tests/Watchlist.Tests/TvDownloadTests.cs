using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchlist.Sonarr;
using Watchlist.Tmdb;
using Watchlist.Agent;
using Watchlist.Data;

namespace Watchlist.Tests;

public sealed class TvDownloadTests : IDisposable
{
    private readonly TestWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Adding_a_season_monitors_only_that_season_and_searches_for_missing_episodes()
    {
        var api = new SonarrApi(existing: false);
        var downloads = Downloads(api);

        var result = await downloads.RequestAsync(1234, "Example Show", "season", 2, null, default);

        Assert.Contains("season 2", result.Message);
        Assert.Equal(10, result.SeriesId);
        Assert.StartsWith("POST series ", Assert.Single(api.Requests, r => r.StartsWith("POST series", StringComparison.Ordinal)));
        Assert.Equal(4, (int)api.AddedSeries!["qualityProfileId"]!);
        Assert.True((bool)api.AddedSeries["addOptions"]!["searchForMissingEpisodes"]!);
        var seasons = api.AddedSeries["seasons"]!.AsArray();
        Assert.False((bool)seasons[1]!["monitored"]!);
        Assert.True((bool)seasons[2]!["monitored"]!);
        Assert.False((bool)seasons[3]!["monitored"]!);
    }

    [Fact]
    public async Task Episode_request_monitors_and_searches_only_the_selected_episode()
    {
        var api = new SonarrApi(existing: true);
        var downloads = Downloads(api);

        var result = await downloads.RequestAsync(1234, "Example Show", "episode", 2, 3, default);

        Assert.Contains("S02E03", result.Message);
        var monitor = Assert.Single(api.Requests, r => r.StartsWith("PUT episode/monitor ", StringComparison.Ordinal));
        Assert.Contains("\"episodeIds\":[203]", monitor);
        var command = Assert.Single(api.Requests, r => r.StartsWith("POST command ", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"EpisodeSearch\"", command);
        Assert.Contains("\"episodeIds\":[203]", command);
        Assert.DoesNotContain(api.Requests, r => r.Contains("SeriesSearch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_series_request_runs_a_series_search()
    {
        var api = new SonarrApi(existing: true);

        await Downloads(api).RequestAsync(1234, "Example Show", "series", null, null, default);

        var command = Assert.Single(api.Requests, r => r.StartsWith("POST command ", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"SeriesSearch\"", command);
        Assert.Contains("\"seriesId\":10", command);
    }

    [Theory]
    [InlineData("season", null, null)]
    [InlineData("episode", 1, null)]
    [InlineData("episode", 1, 0)]
    public async Task Invalid_scope_details_are_rejected_before_sonarr_is_called(string scope, int? season, int? episode)
    {
        var api = new SonarrApi(existing: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Downloads(api).RequestAsync(1234, "Example Show", scope, season, episode, default));

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Chat_tool_exposes_explicit_series_season_and_episode_download_scopes()
    {
        var tool = Assert.Single(WatchlistTools.Definitions, t => t.FunctionName == "download_tv");
        var schema = tool.FunctionParameters!.ToString();

        Assert.Contains("series", schema);
        Assert.Contains("season", schema);
        Assert.Contains("episode", schema);
    }

    [Fact]
    public async Task Chat_download_tool_resolves_tvdb_id_adds_the_show_and_routes_episode_to_sonarr()
    {
        _world.Tmdb
            .On("tv/95396/external_ids", """{"tvdb_id":1234}""")
            .On("tv/95396", """{"id":95396,"name":"Severance","first_air_date":"2022-02-17","status":"Returning Series","number_of_seasons":2,"watch/providers":{"results":{}}}""");
        var api = new SonarrApi(existing: true);
        var effects = new TurnEffects();
        var tools = new WatchlistTools(_world.Watchlist, _world.Items, _world.Tmdb.Client(), Downloads(api));

        var result = await tools.ExecuteAsync("download_tv", """{"tmdb_id":95396,"scope":"episode","season":2,"episode":3}""", "download Severance S2E3", effects, default);

        Assert.Contains("S02E03", result);
        var item = Assert.Single(effects.Added);
        Assert.Equal("Severance", item.Title);
        Assert.Contains(_world.Tmdb.Requests, r => r.StartsWith("tv/95396/external_ids", StringComparison.Ordinal));
        Assert.Contains(api.Requests, r => r.StartsWith("POST command ", StringComparison.Ordinal) && r.Contains("EpisodeSearch", StringComparison.Ordinal));
        // Only the episode was asked for: the rest of the show isn't searched.
        Assert.DoesNotContain(api.Requests, r => r.Contains("SeriesSearch", StringComparison.Ordinal));
        // The card follows Sonarr, like a film's follows Radarr.
        var tracked = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(DownloadStates.Searching, tracked.DownloadState);
        Assert.Equal(10, tracked.SonarrId);
    }

    private TvDownloads Downloads(SonarrApi api) => new(
        new SonarrClient(
            new HttpClient(api),
            Options.Create(new SonarrOptions { Url = "http://sonarr", ApiKey = "test", QualityProfile = "Watchlist" })),
        _world.Tmdb.Client(), _world.Items, _world.Alerts, NullLogger<TvDownloads>.Instance);

    private sealed class SonarrApi(bool existing) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public JsonObject? AddedSeries { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.Replace("/api/v3/", "");
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {path}" + (body is null ? "" : $" {body}"));
            if (path.StartsWith("series/lookup?", StringComparison.Ordinal))
                return Json("""[{"id":0,"tvdbId":1234,"title":"Example Show","seasons":[{"seasonNumber":0,"monitored":false},{"seasonNumber":1,"monitored":false},{"seasonNumber":2,"monitored":false},{"seasonNumber":3,"monitored":false}]}]""");
            if (path.StartsWith("series?", StringComparison.Ordinal))
                return Json(existing ? """[{"id":10,"tvdbId":1234,"title":"Example Show","monitored":true,"seasons":[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true}]}]""" : "[]");
            if (path == "qualityprofile")
                return Json("""[{"id":4,"name":"Watchlist"}]""");
            if (request.Method == HttpMethod.Post && path == "series")
            {
                AddedSeries = (JsonObject)JsonNode.Parse(body!)!;
                var added = (JsonObject)AddedSeries.DeepClone();
                added["id"] = 10;
                return Json(added.ToJsonString());
            }
            if (request.Method == HttpMethod.Put && path == "series/10")
                return Json(body!);
            if (path.StartsWith("episode?", StringComparison.Ordinal))
                return Json("""[{"id":203,"seasonNumber":2,"episodeNumber":3,"hasFile":false,"monitored":false}]""");
            if (request.Method == HttpMethod.Put && path == "episode/monitor")
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Post && path == "command")
                return Json("""{"id":99}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
