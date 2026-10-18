using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Watchlist.Alerts;
using Watchlist.Pipeline;
using Watchlist.Push;

namespace Watchlist.Tests;

/// <summary>
/// Prowlarr, Radarr and Sonarr in one handler, answering from canned JSON keyed by
/// "host/path" (no query). Unknown paths are 404s; a host in Down refuses connections.
/// </summary>
public sealed class FakeArrStack : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, string Json)> Responses { get; } = [];
    public HashSet<string> Down { get; } = [];
    public List<string> Requests { get; } = [];

    public FakeArrStack On(string hostAndPath, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        Responses[hostAndPath] = (status, json);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var key = uri.Host + uri.AbsolutePath.Replace("/api/v1/", "/").Replace("/api/v3/", "/");
        Requests.Add($"{request.Method} {key}");
        if (Down.Contains(uri.Host))
            throw new HttpRequestException("Connection refused");
        return Task.FromResult(Responses.TryGetValue(key, out var r)
            ? new HttpResponseMessage(r.Status) { Content = new StringContent(r.Json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>A healthy stack: the Internet Archive in Prowlarr and in both apps, qBittorrent reachable.</summary>
    public static FakeArrStack Healthy()
    {
        var stack = new FakeArrStack()
            .On("prowlarr/indexer", """[{"id": 1, "name": "Internet Archive", "enable": true}]""")
            .On("prowlarr/indexerstatus", "[]")
            .On("prowlarr/history", """
                {"records": [
                  {"date": "2026-10-17T08:00:00Z", "eventType": "indexerQuery", "successful": true, "indexerId": 1, "data": {"elapsedTime": "70000"}},
                  {"date": "2026-10-15T08:00:00Z", "eventType": "indexerQuery", "successful": false, "indexerId": 1, "data": {}}]}
                """)
            .On("prowlarr/log", """{"records": []}""")
            .On("prowlarr/health", "[]")
            .On("prowlarr/indexer/1", """{"id": 1, "name": "Internet Archive"}""")
            .On("prowlarr/indexer/test", "{}");
        foreach (var app in new[] { "radarr", "sonarr" })
            stack.On($"{app}/health", """[{"source": "IndexerRssCheck", "type": "error", "message": "No indexers available with RSS sync enabled"}]""")
                .On($"{app}/downloadclient", """[{"id": 1, "name": "qBittorrent", "enable": true}]""")
                .On($"{app}/downloadclient/test", "{}")
                .On($"{app}/queue", """{"records": []}""")
                .On($"{app}/history", """{"records": [{"date": "2026-10-16T23:16:00Z"}]}""")
                .On($"{app}/indexer", """[{"name": "Internet Archive (Prowlarr)"}]""");
        return stack;
    }

    public PipelineMonitor Monitor(TimeProvider time, bool sonarr = true) => new(
        new HttpClient(this),
        Options.Create(new ProwlarrOptions { Url = "http://prowlarr:9696", ApiKey = "p" }),
        Options.Create(new RadarrOptions { Url = "http://radarr:7878", ApiKey = "r" }),
        Options.Create(sonarr ? new SonarrOptions { Url = "http://sonarr:8989", ApiKey = "s" } : new SonarrOptions()),
        Options.Create(new WatchlistOptions()),
        time);
}

public sealed class FailureReasonsTests
{
    [Theory]
    [InlineData("Internet Archive server is currently unavailable. System.Net.WebException: Http request timed out", "timeouts", false)]
    [InlineData("Unable to access example.org, blocked by CloudFlare Protection.", "blocked by Cloudflare", true)]
    [InlineData("HTTP request failed: [401:Unauthorized]", "refused: check its login or API key", true)]
    [InlineData("Invalid API Key", "refused: check its login or API key", true)]
    [InlineData("HTTP request failed: [429:TooManyRequests]", "rate limiting", false)]
    [InlineData("Name or service not known (archive.org:443)", "network errors", false)]
    [InlineData("HTTP request failed: [503:ServiceUnavailable]", "server errors", false)]
    [InlineData("Something odd", "errors", false)]
    public void Errors_are_sorted_into_kinds(string error, string label, bool persistent)
    {
        var reason = FailureReasons.Classify(error);
        Assert.Equal(label, reason.Label);
        Assert.Equal(persistent, reason.Persistent);
    }

    [Fact]
    public void A_number_inside_a_word_is_not_a_status_code()
    {
        Assert.Equal("errors", FailureReasons.Classify("item id 14031 not found").Label);
    }
}

public sealed class PipelineMonitorTests
{
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));

    private static PipelineCheck Find(PipelineReport report, string key) => Assert.Single(report.Checks, c => c.Key == key);

    [Fact]
    public async Task A_healthy_stack_is_all_ok()
    {
        var report = await FakeArrStack.Healthy().Monitor(_time).RunAsync(default);

        Assert.Equal(CheckStates.Ok, report.State);
        // Only the last 24 hours count, and slow answers are mentioned.
        Assert.Equal("1 searches in the last 24 hours, all answered, slowly (about 70 s each).", Find(report, "indexer-1").Detail);
        Assert.Equal("Reachable.", Find(report, "sonarr-client-1").Detail);
        Assert.Equal("Last import 00:16.", Find(report, "radarr-imports").Detail);   // UK time, today
        Assert.Equal("Has Prowlarr's one indexer.", Find(report, "sonarr-indexers").Detail);
        // RSS being off is on purpose, so it's not reported.
        Assert.DoesNotContain(report.Checks, c => c.Detail.Contains("RSS"));
    }

    [Fact]
    public async Task A_recently_paused_indexer_is_a_warning_with_its_reason()
    {
        var stack = FakeArrStack.Healthy()
            .On("prowlarr/indexerstatus", """[{"indexerId": 1, "disabledTill": "2026-10-17T09:30:00Z", "initialFailure": "2026-10-17T06:00:00Z"}]""")
            // As Prowlarr logs it: the stack trace says "credentials", but this is a timeout.
            .On("prowlarr/log", """{"records": [{"time": "2026-10-17T08:59:00Z", "message": "Internet Archive server is currently unavailable.", "exception": "System.Net.WebException: Http request timed out\n   at NzbDrone.Common.Http.HttpClient.ExecuteAsync(HttpRequest request, NetworkCredential credentials)"}]}""");

        var check = Find(await stack.Monitor(_time).RunAsync(default), "indexer-1");

        Assert.Equal(CheckStates.Warn, check.State);
        Assert.Equal("Paused after timeouts; failing on and off since 07:00. Prowlarr tries it again at 10:30.", check.Detail);
        Assert.Equal(1, check.IndexerId);
    }

    [Fact]
    public async Task An_indexer_failing_for_over_a_day_is_broken()
    {
        var stack = FakeArrStack.Healthy()
            .On("prowlarr/indexerstatus", """[{"indexerId": 1, "disabledTill": "2026-10-17T12:00:00Z", "initialFailure": "2026-10-16T06:00:00Z"}]""");

        var check = Find(await stack.Monitor(_time).RunAsync(default), "indexer-1");

        Assert.Equal(CheckStates.Fail, check.State);
        Assert.EndsWith("That's over a day: look at it.", check.Detail);
    }

    [Fact]
    public async Task A_refused_login_is_broken_straight_away()
    {
        var stack = FakeArrStack.Healthy()
            .On("prowlarr/log", """{"records": [{"time": "2026-10-17T08:00:00Z", "message": "Internet Archive: HTTP request failed: [403:Forbidden]"}]}""");

        var check = Find(await stack.Monitor(_time).RunAsync(default), "indexer-1");

        Assert.Equal(CheckStates.Fail, check.State);
        Assert.Contains("refused", check.Detail);
    }

    [Fact]
    public async Task An_unreachable_download_client_is_broken()
    {
        var stack = FakeArrStack.Healthy()
            .On("radarr/downloadclient/test", """[{"errorMessage": "Unable to connect to qBittorrent"}]""", HttpStatusCode.BadRequest);

        var check = Find(await stack.Monitor(_time).RunAsync(default), "radarr-client-1");

        Assert.Equal(CheckStates.Fail, check.State);
        Assert.Equal("Can't reach it: Unable to connect to qBittorrent", check.Detail);
    }

    [Fact]
    public async Task Stuck_imports_are_listed()
    {
        var stack = FakeArrStack.Healthy().On("sonarr/queue", """
            {"records": [
              {"title": "Dragnet S01E01", "trackedDownloadStatus": "warning", "trackedDownloadState": "importBlocked",
               "statusMessages": [{"title": "x", "messages": ["Unable to parse episode"]}]}]}
            """);

        var check = Find(await stack.Monitor(_time).RunAsync(default), "sonarr-imports");

        Assert.Equal(CheckStates.Warn, check.State);
        Assert.Equal("1 stuck. Dragnet S01E01: Unable to parse episode", check.Detail);
    }

    [Fact]
    public async Task Indexers_out_of_sync_are_reported()
    {
        var stack = FakeArrStack.Healthy()
            .On("sonarr/indexer", "[]")
            .On("radarr/indexer", """[{"name": "Internet Archive (Prowlarr)"}, {"name": "Old one (Prowlarr)"}, {"name": "Added by hand"}]""");

        var report = await stack.Monitor(_time).RunAsync(default);

        Assert.Equal(CheckStates.Fail, Find(report, "sonarr-indexers").State);
        Assert.StartsWith("Missing: Internet Archive.", Find(report, "sonarr-indexers").Detail);
        Assert.Equal(CheckStates.Warn, Find(report, "radarr-indexers").State);
        Assert.Equal("Added in Radarr directly, not through Prowlarr: Added by hand. No longer in Prowlarr: Old one (Prowlarr).",
            Find(report, "radarr-indexers").Detail);
    }

    [Fact]
    public async Task One_app_down_does_not_hide_the_others()
    {
        var stack = FakeArrStack.Healthy();
        stack.Down.Add("sonarr");

        var report = await stack.Monitor(_time).RunAsync(default);

        Assert.Equal(CheckStates.Fail, Find(report, "sonarr").State);
        Assert.Equal(CheckStates.Ok, Find(report, "radarr-client-1").State);
        Assert.Equal(CheckStates.Ok, Find(report, "indexer-1").State);
    }

    [Fact]
    public async Task An_app_not_set_up_is_a_warning()
    {
        var stack = FakeArrStack.Healthy();
        var report = await stack.Monitor(_time, sonarr: false).RunAsync(default);

        Assert.Equal(CheckStates.Warn, Find(report, "sonarr").State);
        Assert.DoesNotContain(stack.Requests, r => r.Contains("sonarr"));
        Assert.Contains(stack.Requests, r => r.Contains("radarr"));
    }

    [Fact]
    public async Task Testing_an_indexer_reports_the_reason_it_failed()
    {
        var stack = FakeArrStack.Healthy()
            .On("prowlarr/indexer/test", """[{"errorMessage": "Http request timed out"}]""", HttpStatusCode.BadRequest);

        var (ok, message) = await stack.Monitor(_time).TestIndexerAsync(1, default);

        Assert.False(ok);
        Assert.Equal("Internet Archive failed: Http request timed out", message);
    }
}

public sealed class PipelineAlertsTests : IDisposable
{
    private readonly TestWorld _world = new();
    private readonly RecordingSender _sender = new();
    private readonly PipelineAlerts _alerts;

    public PipelineAlertsTests()
    {
        _alerts = new PipelineAlerts(_world.Meta, _world.Pushes, _sender, _world.Watchlist, new FixedOptions<AlertOptions>(_world.AlertOptions));
        _world.Pushes.SaveAsync("https://push.example/1", "p", "a").GetAwaiter().GetResult();
    }

    public void Dispose() => _world.Dispose();

    private PipelineReport Report(params PipelineCheck[] checks) => new(_world.Time.GetUtcNow(), checks);

    private static PipelineCheck Broken(string key = "radarr-client-1") =>
        new(key, "Radarr", "qBittorrent", CheckStates.Fail, "Can't reach it");

    [Fact]
    public async Task A_failure_alerts_once_it_has_lasted_half_an_hour_then_once_when_fixed()
    {
        Assert.Equal(0, await _alerts.NotifyAsync(Report(Broken()), default));
        _world.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await _alerts.NotifyAsync(Report(Broken()), default));
        _world.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(0, await _alerts.NotifyAsync(Report(Broken()), default));
        _world.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(1, await _alerts.NotifyAsync(Report(), default));
        Assert.Equal(0, await _alerts.NotifyAsync(Report(), default));

        Assert.Equal(["Radarr: qBittorrent: Can't reach it", "Fixed: Radarr: qBittorrent"], _sender.Bodies);
    }

    [Fact]
    public async Task A_blip_never_alerts()
    {
        await _alerts.NotifyAsync(Report(Broken()), default);
        _world.Time.Advance(TimeSpan.FromMinutes(15));
        await _alerts.NotifyAsync(Report(), default);
        _world.Time.Advance(TimeSpan.FromHours(1));
        await _alerts.NotifyAsync(Report(), default);

        Assert.Empty(_sender.Bodies);
    }

    [Fact]
    public async Task Warnings_never_alert()
    {
        var paused = new PipelineCheck("indexer-1", "Indexers", "Internet Archive", CheckStates.Warn, "Paused");
        await _alerts.NotifyAsync(Report(paused), default);
        _world.Time.Advance(TimeSpan.FromHours(2));
        await _alerts.NotifyAsync(Report(paused), default);

        Assert.Empty(_sender.Bodies);
    }

    [Fact]
    public async Task Quiet_hours_hold_the_alert_until_morning()
    {
        _world.Time.SetUtcNow(new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero));   // 23:00 UK
        await _alerts.NotifyAsync(Report(Broken()), default);
        _world.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await _alerts.NotifyAsync(Report(Broken()), default));
        _world.Time.SetUtcNow(new DateTimeOffset(2026, 10, 18, 7, 15, 0, TimeSpan.Zero));   // 08:15 UK
        Assert.Equal(1, await _alerts.NotifyAsync(Report(Broken()), default));
    }

    private sealed class RecordingSender : IPushSender
    {
        public List<string> Bodies { get; } = [];

        public Task<PushResult> SendAsync(Data.PushSubscriptionRow subscription, object payload, CancellationToken ct)
        {
            Bodies.Add((string)payload.GetType().GetProperty("body")!.GetValue(payload)!);
            return Task.FromResult(PushResult.Sent);
        }
    }
}
