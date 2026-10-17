using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Watchlist.Alerts;
using Watchlist.Data;
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
    public WatchlistService Watchlist { get; }

    public TestWorld()
    {
        Directory.CreateDirectory(_dir);
        Database = new Database(Path.Combine(_dir, "watchlist.db"));
        Database.Migrate();
        Items = new ItemStore(Database, Time);
        Messages = new MessageStore(Database, Time);
        Alerts = new AlertStore(Database, Time);
        Pushes = new PushStore(Database, Time);
        Meta = new MetaStore(Database);
        Watchlist = new WatchlistService(Items, Alerts, Tmdb.Client(), Time, Options.Create(new WatchlistOptions()), new FixedOptions<AlertOptions>(AlertOptions));
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
