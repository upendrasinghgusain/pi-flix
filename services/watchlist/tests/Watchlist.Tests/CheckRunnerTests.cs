using Microsoft.Extensions.Logging.Abstractions;
using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Push;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

public sealed class FakePushSender : IPushSender
{
    public List<(string Endpoint, object Payload)> Sent { get; } = [];
    public Dictionary<string, PushResult> Results { get; } = [];

    public Task<PushResult> SendAsync(PushSubscriptionRow subscription, object payload, CancellationToken ct)
    {
        var result = Results.GetValueOrDefault(subscription.Endpoint, PushResult.Sent);
        if (result == PushResult.Sent)
            Sent.Add((subscription.Endpoint, payload));
        return Task.FromResult(result);
    }
}

public sealed class CheckRunnerTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly FakePushSender _push = new();
    private readonly CheckRunner _runner;

    // A film announced for a US digital release on 1 Nov 2026, with nothing yet.
    private const string Before = """
        {"id": 1, "title": "Future Film", "release_date": "2026-10-01",
         "release_dates": {"results": [{"iso_3166_1": "US", "release_dates": [
           {"type": 3, "release_date": "2026-10-01T00:00:00.000Z"},
           {"type": 4, "release_date": "2026-11-01T00:00:00.000Z"}]}]},
         "watch/providers": {"results": {}}}
        """;

    // The same film once it's out: digital in the US and streaming on Netflix in the UK.
    private const string After = """
        {"id": 1, "title": "Future Film", "release_date": "2026-10-01",
         "release_dates": {"results": [{"iso_3166_1": "US", "release_dates": [
           {"type": 3, "release_date": "2026-10-01T00:00:00.000Z"},
           {"type": 4, "release_date": "2026-11-01T00:00:00.000Z"}]}]},
         "watch/providers": {"results": {
           "US": {"rent": [{"provider_id": 2, "provider_name": "Apple TV"}]},
           "GB": {"flatrate": [{"provider_id": 8, "provider_name": "Netflix"}]}}}}
        """;

    public CheckRunnerTests()
    {
        _runner = new CheckRunner(_world.Items, _world.Watchlist, _world.Alerts, _world.Pushes, _push, _world.Meta,
            new FixedOptions<AlertOptions>(_world.AlertOptions), NullLogger<CheckRunner>.Instance) { Pause = TimeSpan.Zero };
    }

    public void Dispose() => _world.Dispose();

    private async Task<Item> AddFutureFilm()
    {
        _world.Tmdb.On("movie/1", Before);
        var (item, _, _) = await _world.Watchlist.AddAsync(1, MediaTypes.Movie, "future film");
        return item;
    }

    /// <summary>Moves the clock to a UK time on a given day.</summary>
    private void At(int month, int day, int hour, int minute = 0) =>
        _world.Time.SetUtcNow(_world.Watchlist.LocalInstant(new DateOnly(2026, month, day), new TimeOnly(hour, minute)));

    [Fact]
    public async Task Adding_sends_no_alerts()
    {
        await AddFutureFilm();
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task The_day_it_comes_out_the_check_marks_it_available_and_alerts()
    {
        var item = await AddFutureFilm();
        await _world.Pushes.SaveAsync("https://push.example/phone", "k", "a");

        At(11, 2, 6);
        _world.Tmdb.On("movie/1", After);
        await _runner.RunChecksAsync(default);
        At(11, 2, 8, 5);   // the 06:00 check is in quiet hours, so delivery waits until 08:00
        await _runner.DeliverAsync(default);

        Assert.Equal(Statuses.Available, (await _world.Items.GetAsync(item.Id))!.Status);
        var (endpoint, payload) = Assert.Single(_push.Sent);
        Assert.Equal("https://push.example/phone", endpoint);
        var body = (string)payload.GetType().GetProperty("body")!.GetValue(payload)!;
        Assert.Equal("🌍 Out at home: digital release in the US since 1 Nov · UK: Netflix\n📺 Now on Netflix", body);
        Assert.Empty(await _world.Alerts.PendingAsync());

        // The next day, nothing new.
        At(11, 3, 6);
        await _runner.RunChecksAsync(default);
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task Alerts_found_in_quiet_hours_wait_until_morning()
    {
        await AddFutureFilm();
        await _world.Pushes.SaveAsync("https://push.example/phone", "k", "a");

        At(11, 2, 23);
        _world.Tmdb.On("movie/1", After);
        await _runner.RunChecksAsync(default);
        Assert.Equal(0, await _runner.DeliverAsync(default));
        Assert.Empty(_push.Sent);

        At(11, 3, 8, 5);
        Assert.Equal(1, await _runner.DeliverAsync(default));
        Assert.Single(_push.Sent);
    }

    [Fact]
    public async Task Snoozed_titles_are_checked_but_stay_quiet()
    {
        var item = await AddFutureFilm();
        await _world.Items.SetSnoozeAsync(item.Id, new DateOnly(2026, 12, 1));

        At(11, 2, 6);
        _world.Tmdb.On("movie/1", After);
        await _runner.RunChecksAsync(default);

        Assert.Equal(Statuses.Available, (await _world.Items.GetAsync(item.Id))!.Status);
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task Watched_and_removed_titles_are_not_checked()
    {
        var item = await AddFutureFilm();
        await _world.Items.SetStatusAsync(item.Id, Statuses.Watched);
        var requestsBefore = _world.Tmdb.Requests.Count;

        await _runner.RunChecksAsync(default);

        Assert.Equal(requestsBefore, _world.Tmdb.Requests.Count);
    }

    [Fact]
    public async Task One_failing_title_doesnt_stop_the_rest()
    {
        await AddFutureFilm();
        var other = Before.Replace("\"id\": 1", "\"id\": 2").Replace("Future Film", "Other Film");
        _world.Tmdb.On("movie/2", other);
        var (second, _, _) = await _world.Watchlist.AddAsync(2, MediaTypes.Movie, null);

        At(11, 2, 6);
        _world.Tmdb.Fail("movie/1");
        _world.Tmdb.On("movie/2", After.Replace("\"id\": 1", "\"id\": 2").Replace("Future Film", "Other Film"));
        await _runner.RunChecksAsync(default);

        Assert.Equal(Statuses.Available, (await _world.Items.GetAsync(second.Id))!.Status);
        Assert.False(await _runner.IsCheckDueAsync());
    }

    [Fact]
    public async Task Devices_that_have_gone_away_are_forgotten()
    {
        await AddFutureFilm();
        await _world.Pushes.SaveAsync("https://push.example/old-phone", "k", "a");
        await _world.Pushes.SaveAsync("https://push.example/phone", "k", "a");
        _push.Results["https://push.example/old-phone"] = PushResult.Gone;

        At(11, 2, 9);
        _world.Tmdb.On("movie/1", After);
        await _runner.RunChecksAsync(default);
        await _runner.DeliverAsync(default);

        Assert.Equal(["https://push.example/phone"], (await _world.Pushes.AllAsync()).Select(p => p.Endpoint));
        Assert.Single(_push.Sent);
    }

    [Fact]
    public async Task Alerts_are_kept_while_the_push_service_is_down()
    {
        await AddFutureFilm();
        await _world.Pushes.SaveAsync("https://push.example/phone", "k", "a");
        _push.Results["https://push.example/phone"] = PushResult.Failed;

        At(11, 2, 9);
        _world.Tmdb.On("movie/1", After);
        await _runner.RunChecksAsync(default);
        await _runner.DeliverAsync(default);
        Assert.NotEmpty(await _world.Alerts.PendingAsync());

        _push.Results.Clear();
        await _runner.DeliverAsync(default);
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task The_daily_check_is_due_once_a_day_after_the_check_time()
    {
        At(10, 18, 5, 55);
        Assert.False(await _runner.IsCheckDueAsync());

        At(10, 18, 6, 0);
        Assert.True(await _runner.IsCheckDueAsync());
        await _runner.RunChecksAsync(default);
        Assert.False(await _runner.IsCheckDueAsync());

        At(10, 18, 23, 0);
        Assert.False(await _runner.IsCheckDueAsync());

        At(10, 19, 7, 30);   // e.g. the Pi was off at 06:00
        Assert.True(await _runner.IsCheckDueAsync());
    }
}
