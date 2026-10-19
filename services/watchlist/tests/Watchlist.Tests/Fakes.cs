using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Radarr;
using Watchlist.Sonarr;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

/// <summary>Answers TMDb requests from canned JSON, matched by path prefix. Records what was asked.</summary>
public sealed class FakeTmdb : HttpMessageHandler
{
    private readonly Dictionary<string, string?> _responses = [];
    public List<string> Requests { get; } = [];

    public FakeTmdb On(string pathPrefix, string json)
    {
        _responses[pathPrefix] = json;
        return this;
    }

    /// <summary>Makes requests under this path fail with a server error.</summary>
    public FakeTmdb Fail(string pathPrefix)
    {
        _responses[pathPrefix] = null;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery.Replace("/3/", "");
        Requests.Add(path);
        var match = _responses.Where(r => path.StartsWith(r.Key, StringComparison.Ordinal))
            .OrderByDescending(r => r.Key.Length)
            .Select(r => (Found: true, r.Value))
            .FirstOrDefault();
        return Task.FromResult(match switch
        {
            { Found: false } => new HttpResponseMessage(HttpStatusCode.NotFound),
            { Value: null } => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Value, Encoding.UTF8, "application/json") },
        });
    }

    public TmdbClient Client() =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://api.themoviedb.org/3/") }, Options.Create(new TmdbOptions()));
}

/// <summary>
/// A small in-memory Radarr: films added through the API, a queue, and files that tests can
/// "finish". Records every request. Down = every call fails, like a stopped container.
/// </summary>
public sealed class FakeRadarr : HttpMessageHandler
{
    public sealed class Film
    {
        public int Id { get; init; }
        public int TmdbId { get; init; }
        public bool HasFile { get; set; }
        public bool InQueue { get; set; }
        public bool Monitored { get; set; } = true;
        public JsonObject Added { get; init; } = [];
    }

    public Dictionary<int, Film> Films { get; } = [];
    public List<string> Requests { get; } = [];
    public bool Down { get; set; }
    private int _nextId = 1;

    public Film Has(int tmdbId, bool hasFile = false, bool monitored = true)
    {
        var film = new Film { Id = _nextId++, TmdbId = tmdbId, HasFile = hasFile, Monitored = monitored };
        Films[film.Id] = film;
        return film;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery.Replace("/api/v3/", "");
        Requests.Add($"{request.Method} {path}");
        if (Down)
            throw new HttpRequestException("Connection refused");

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var segments = request.RequestUri.AbsolutePath.Replace("/api/v3/", "").Split('/');
        return (request.Method.Method, segments) switch
        {
            ("GET", ["movie"]) => Json(new JsonArray(Films.Values.Where(f => f.TmdbId == int.Parse(query["tmdbId"]!)).Select(Node).ToArray())),
            ("GET", ["movie", "lookup", "tmdb"]) => Json(new JsonObject { ["tmdbId"] = int.Parse(query["tmdbId"]!), ["title"] = "Looked up" }),
            ("GET", ["movie", var id]) => Films.TryGetValue(int.Parse(id), out var f) ? Json(Node(f)) : new HttpResponseMessage(HttpStatusCode.NotFound),
            ("GET", ["qualityprofile"]) => Json(JsonNode.Parse("""[{"id": 1, "name": "Any"}, {"id": 7, "name": "Watchlist"}]""")!),
            // A film's queue entry has the film's id, to keep the fake simple.
            ("GET", ["queue", "details"]) => Json(new JsonArray(Films.Values.Where(f => f.Id == int.Parse(query["movieId"]!) && f.InQueue).Select(f => (JsonNode)new JsonObject { ["id"] = f.Id }).ToArray())),
            ("DELETE", ["queue", var id]) when Films.TryGetValue(int.Parse(id), out var queued) && queued.InQueue => Dequeue(queued),
            ("POST", ["movie"]) => Json(Node(Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!))),
            ("POST", ["command"]) => Json(new JsonObject { ["id"] = 1 }),
            ("PUT", ["movie", "editor"]) => Monitor((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!),
            ("DELETE", ["movie", var id]) => Films.Remove(int.Parse(id)) ? new HttpResponseMessage(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private Film Add(JsonObject body)
    {
        var film = new Film { Id = _nextId++, TmdbId = (int)body["tmdbId"]!, Added = body };
        Films[film.Id] = film;
        return film;
    }

    private HttpResponseMessage Monitor(JsonObject body)
    {
        foreach (var id in body["movieIds"]!.AsArray())
            Films[(int)id!].Monitored = (bool)body["monitored"]!;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage Dequeue(Film film)
    {
        film.InQueue = false;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static JsonNode Node(Film f) => new JsonObject { ["id"] = f.Id, ["tmdbId"] = f.TmdbId, ["hasFile"] = f.HasFile, ["monitored"] = f.Monitored };

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    public RadarrClient Client(bool configured = true) =>
        new(new HttpClient(this), Options.Create(configured ? new RadarrOptions { Url = "http://radarr:7878", ApiKey = "test-key" } : new RadarrOptions()));
}

/// <summary>
/// A small in-memory Sonarr: shows added through the API with seasons 0-2 of two episodes each,
/// a queue, and episode files that tests can "finish". Records every request. Down = every call fails.
/// </summary>
public sealed class FakeSonarr : HttpMessageHandler
{
    public sealed class Episode
    {
        public int Id { get; init; }
        public int Season { get; init; }
        public int Number { get; init; }
        public bool HasFile { get; set; }
        public bool Monitored { get; set; }
    }

    public sealed class Show
    {
        public int Id { get; init; }
        public int TvdbId { get; init; }
        public bool InQueue { get; set; }
        public List<Episode> Episodes { get; } = [];
        public JsonObject Added { get; init; } = [];

        public int Files => Episodes.Count(e => e.HasFile);
        public int Wanted => Episodes.Count(e => e.Monitored || e.HasFile);
    }

    public Dictionary<int, Show> Shows { get; } = [];
    public List<string> Requests { get; } = [];
    public bool Down { get; set; }
    private int _nextId = 1;

    /// <summary>A show Sonarr already has, every episode monitored, the first <paramref name="files"/> on disk.</summary>
    public Show Has(int tvdbId, int files = 0)
    {
        var show = NewShow(tvdbId, [], monitored: _ => true);
        foreach (var e in show.Episodes.Take(files))
            e.HasFile = true;
        return show;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery.Replace("/api/v3/", "");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add($"{request.Method} {path}" + (body is null ? "" : $" {body}"));
        if (Down)
            throw new HttpRequestException("Connection refused");

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var segments = request.RequestUri.AbsolutePath.Replace("/api/v3/", "").Split('/');
        return (request.Method.Method, segments) switch
        {
            ("GET", ["series", "lookup"]) => Json(new JsonArray(new JsonObject
            {
                ["tvdbId"] = int.Parse(query["term"]!.Replace("tvdb:", "")), ["title"] = "Looked up",
                ["seasons"] = new JsonArray([.. new[] { 0, 1, 2 }.Select(n => (JsonNode)new JsonObject { ["seasonNumber"] = n, ["monitored"] = false })]),
            })),
            ("GET", ["series"]) => Json(new JsonArray([.. Shows.Values.Where(s => s.TvdbId == int.Parse(query["tvdbId"]!)).Select(Node)])),
            ("GET", ["series", var id]) => Shows.TryGetValue(int.Parse(id), out var s) ? Json(Node(s)) : new HttpResponseMessage(HttpStatusCode.NotFound),
            ("POST", ["series"]) => Json(Node(Add((JsonObject)JsonNode.Parse(body!)!))),
            ("PUT", ["series", var id]) => Json(JsonNode.Parse(body!)!),
            ("DELETE", ["series", var id]) => Shows.Remove(int.Parse(id)) ? new HttpResponseMessage(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.NotFound),
            ("GET", ["episode"]) => Json(new JsonArray([.. Shows[int.Parse(query["seriesId"]!)].Episodes
                .Where(e => query["seasonNumber"] is not { } n || e.Season == int.Parse(n))
                .Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["seasonNumber"] = e.Season, ["episodeNumber"] = e.Number, ["hasFile"] = e.HasFile, ["monitored"] = e.Monitored })])),
            ("PUT", ["episode", "monitor"]) => Monitor((JsonObject)JsonNode.Parse(body!)!),
            ("GET", ["qualityprofile"]) => Json(JsonNode.Parse("""[{"id": 1, "name": "Any"}, {"id": 4, "name": "Watchlist"}]""")!),
            // A show's queue entry has the show's id, to keep the fake simple.
            ("GET", ["queue", "details"]) => Json(new JsonArray([.. Shows.Values.Where(s => s.Id == int.Parse(query["seriesId"]!) && s.InQueue).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id })])),
            ("DELETE", ["queue", var id]) when Shows.TryGetValue(int.Parse(id), out var queued) && queued.InQueue => Dequeue(queued),
            ("POST", ["command"]) => Json(new JsonObject { ["id"] = 1 }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private Show Add(JsonObject body)
    {
        var monitoredSeasons = body["seasons"]!.AsArray().OfType<JsonObject>()
            .Where(s => (bool)s["monitored"]!).Select(s => (int)s["seasonNumber"]!).ToHashSet();
        return NewShow((int)body["tvdbId"]!, body, monitored: season => monitoredSeasons.Contains(season));
    }

    private Show NewShow(int tvdbId, JsonObject added, Func<int, bool> monitored)
    {
        var show = new Show { Id = _nextId++, TvdbId = tvdbId, Added = added };
        foreach (var season in new[] { 0, 1, 2 })
            for (var number = 1; number <= 2; number++)
                show.Episodes.Add(new Episode { Id = show.Id * 100 + season * 10 + number, Season = season, Number = number, Monitored = monitored(season) });
        Shows[show.Id] = show;
        return show;
    }

    private HttpResponseMessage Monitor(JsonObject body)
    {
        var ids = body["episodeIds"]!.AsArray().Select(i => (int)i!).ToHashSet();
        foreach (var e in Shows.Values.SelectMany(s => s.Episodes).Where(e => ids.Contains(e.Id)))
            e.Monitored = (bool)body["monitored"]!;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage Dequeue(Show show)
    {
        show.InQueue = false;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static JsonNode Node(Show s) => new JsonObject
    {
        ["id"] = s.Id, ["tvdbId"] = s.TvdbId, ["title"] = "Looked up", ["monitored"] = true,
        ["seasons"] = new JsonArray([.. new[] { 0, 1, 2 }.Select(n => (JsonNode)new JsonObject { ["seasonNumber"] = n, ["monitored"] = n > 0 })]),
        ["statistics"] = new JsonObject { ["episodeFileCount"] = s.Files, ["episodeCount"] = s.Wanted },
    };

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    public SonarrClient Client(bool configured = true) =>
        new(new HttpClient(this), Options.Create(configured ? new SonarrOptions { Url = "http://sonarr:8989", ApiKey = "test-key" } : new SonarrOptions()));
}

/// <summary>A fresh database file per test, plus the services built on it.</summary>
public sealed class TestWorld : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchlist-tests", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
    public FakeTmdb Tmdb { get; } = new();
    public Database Database { get; }
    public ItemStore Items { get; }
    public MessageStore Messages { get; }
    public AlertStore Alerts { get; }
    public PushStore Pushes { get; }
    public MetaStore Meta { get; }
    public AlertOptions AlertOptions { get; } = new() { MyServices = ["Netflix", "Apple TV Plus"] };
    public FakeRadarr Radarr { get; } = new();
    public Downloads Downloads { get; }
    public FakeSonarr Sonarr { get; } = new();
    public TvDownloads TvDownloads { get; }
    public WatchlistService Watchlist { get; }

    /// <param name="radarr">Whether Radarr is set up; off by default, as when only the release checker is wanted.</param>
    /// <param name="sonarr">Whether Sonarr is set up; off by default too.</param>
    public TestWorld(bool radarr = false, bool sonarr = false)
    {
        Directory.CreateDirectory(_dir);
        Database = new Database(Path.Combine(_dir, "watchlist.db"));
        Database.Migrate();
        Items = new ItemStore(Database, Time);
        Messages = new MessageStore(Database, Time);
        Alerts = new AlertStore(Database, Time);
        Pushes = new PushStore(Database, Time);
        Meta = new MetaStore(Database);
        Downloads = new Downloads(Radarr.Client(configured: radarr), Items, Alerts, NullLogger<Downloads>.Instance);
        TvDownloads = new TvDownloads(Sonarr.Client(configured: sonarr), Tmdb.Client(), Items, Alerts, NullLogger<TvDownloads>.Instance);
        Watchlist = new WatchlistService(Items, Alerts, Tmdb.Client(), Downloads, TvDownloads, Time, Options.Create(new WatchlistOptions()), new FixedOptions<AlertOptions>(AlertOptions));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Options that never change, for code that reads IOptionsMonitor.</summary>
public sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public static class Samples
{
    /// <summary>A film in cinemas in the UK and US, out digitally in the US only, and to rent in the US.</summary>
    public const string WeaponsMovie = """
        {
          "id": 1078605, "title": "Weapons", "release_date": "2025-08-06", "poster_path": "/w.jpg",
          "overview": "When all but one child from the same class mysteriously vanish...", "status": "Released",
          "runtime": 128, "genres": [{"id": 27, "name": "Horror"}], "popularity": 120.5,
          "release_dates": {"results": [
            {"iso_3166_1": "GB", "release_dates": [
              {"type": 3, "release_date": "2025-08-08T00:00:00.000Z"},
              {"type": 4, "release_date": "2025-10-20T00:00:00.000Z"}]},
            {"iso_3166_1": "US", "release_dates": [
              {"type": 3, "release_date": "2025-08-08T00:00:00.000Z"},
              {"type": 4, "release_date": "2025-09-09T00:00:00.000Z"}]}
          ]},
          "watch/providers": {"results": {
            "US": {"link": "x", "rent": [{"provider_id": 2, "provider_name": "Apple TV"}],
                   "buy": [{"provider_id": 2, "provider_name": "Apple TV"}]}
          }}
        }
        """;

    public const string SeveranceTv = """
        {
          "id": 95396, "name": "Severance", "first_air_date": "2022-02-17", "poster_path": "/s.jpg",
          "overview": "Mark leads a team of office workers...", "status": "Returning Series",
          "number_of_seasons": 2, "origin_country": ["US"], "genres": [{"name": "Drama"}],
          "next_episode_to_air": {"air_date": "2027-01-14", "season_number": 3, "episode_number": 1, "name": "Hello"},
          "watch/providers": {"results": {
            "GB": {"flatrate": [{"provider_id": 350, "provider_name": "Apple TV+"}]}
          }}
        }
        """;

    public const string WeaponsSearch = """
        {"results": [
          {"id": 1078605, "media_type": "movie", "title": "Weapons", "release_date": "2025-08-06", "popularity": 120.5},
          {"id": 999, "media_type": "movie", "title": "Weapons", "release_date": "1999-01-01", "popularity": 1.2},
          {"id": 5, "media_type": "person", "name": "Someone"}
        ]}
        """;
}
