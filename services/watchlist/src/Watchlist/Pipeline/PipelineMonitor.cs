using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Watchlist.Pipeline;

public static class CheckStates
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

/// <summary>One line of the Status tab. Key stays the same from run to run, for alerts.</summary>
public sealed record PipelineCheck(string Key, string Area, string Name, string State, string Detail, int? IndexerId = null);

public sealed record PipelineReport(DateTimeOffset CheckedAt, IReadOnlyList<PipelineCheck> Checks)
{
    public string State =>
        Checks.Any(c => c.State == CheckStates.Fail) ? CheckStates.Fail
        : Checks.Any(c => c.State == CheckStates.Warn) ? CheckStates.Warn
        : CheckStates.Ok;
}

/// <summary>The latest report, shared by the background check and the Status tab.</summary>
public sealed class PipelineState
{
    public PipelineReport? Latest { get; set; }
}

/// <summary>
/// Looks over the whole download pipeline, read-only: Prowlarr's indexers (failures over the
/// last day, classified), whether Radarr and Sonarr have every indexer Prowlarr syncs, whether
/// each can reach qBittorrent, and whether imports are going through. Changes nothing, except
/// that <see cref="TestIndexerAsync"/> asks Prowlarr to test an indexer, which lifts a pause.
/// </summary>
public sealed class PipelineMonitor
{
    /// <summary>An indexer failing on and off for this long is a problem, not a blip.</summary>
    public static readonly TimeSpan PersistentAfter = TimeSpan.FromHours(24);

    private const string ViaProwlarr = " (Prowlarr)";
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly App? _prowlarr;
    private readonly (string Name, App? App)[] _apps;

    private sealed record App(string Name, string BaseUrl, string ApiKey);

    public PipelineMonitor(HttpClient http, IOptions<ProwlarrOptions> prowlarr, IOptions<RadarrOptions> radarr,
        IOptions<SonarrOptions> sonarr, IOptions<WatchlistOptions> watchlist, TimeProvider time)
    {
        _http = http;
        _time = time;
        _zone = TimeZoneInfo.FindSystemTimeZoneById(watchlist.Value.TimeZone);
        _prowlarr = Make("Prowlarr", prowlarr.Value.Url, prowlarr.Value.ApiKey, "v1");
        _apps =
        [
            ("Radarr", Make("Radarr", radarr.Value.Url, radarr.Value.ApiKey, "v3")),
            ("Sonarr", Make("Sonarr", sonarr.Value.Url, sonarr.Value.ApiKey, "v3")),
        ];
    }

    private static App? Make(string name, string url, string key, string version) =>
        url == "" || key == "" ? null : new(name, $"{url.TrimEnd('/')}/api/{version}/", key);

    public async Task<PipelineReport> RunAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var checks = new List<PipelineCheck>();
        List<string>? synced = null;

        if (_prowlarr is null)
            checks.Add(new("prowlarr", "Indexers", "Prowlarr", CheckStates.Warn, "Not connected: set Prowlarr__Url and Prowlarr__ApiKey."));
        else
        {
            try
            {
                synced = await IndexerChecksAsync(_prowlarr, checks, now, ct);
            }
            catch (Exception ex) when (IsDown(ex, ct))
            {
                checks.Add(new("prowlarr", "Indexers", "Prowlarr", CheckStates.Fail, $"Can't reach Prowlarr: {ex.Message}"));
            }
        }

        foreach (var (name, app) in _apps)
        {
            if (app is null)
            {
                checks.Add(new(name.ToLowerInvariant(), name, name, CheckStates.Warn, $"Not connected: set {name}__Url and {name}__ApiKey."));
                continue;
            }
            try
            {
                await AppChecksAsync(app, synced, checks, ct);
            }
            catch (Exception ex) when (IsDown(ex, ct))
            {
                checks.Add(new(name.ToLowerInvariant(), name, name, CheckStates.Fail, $"Can't reach {name}: {ex.Message}"));
            }
        }
        return new(now, checks);
    }

    /// <summary>Checks each indexer, and returns the names of those Prowlarr should be syncing.</summary>
    private async Task<List<string>> IndexerChecksAsync(App prowlarr, List<PipelineCheck> checks, DateTimeOffset now, CancellationToken ct)
    {
        var indexers = Objects(await GetAsync(prowlarr, "indexer", ct));
        var statuses = Objects(await GetAsync(prowlarr, "indexerstatus", ct)).ToDictionary(s => (int)s["indexerId"]!);
        var dayAgo = now - TimeSpan.FromDays(1);
        var history = Objects((await GetAsync(prowlarr, "history?pageSize=500&sortKey=date&sortDirection=descending", ct))["records"])
            .Where(h => Date(h["date"]) >= dayAgo && (string?)h["eventType"] is "indexerQuery" or "indexerRss")
            .ToList();
        var errors = Objects((await GetAsync(prowlarr, "log?pageSize=200&sortKey=time&sortDirection=descending&level=warn", ct))["records"])
            .Where(l => Date(l["time"]) >= dayAgo)
            .ToList();

        foreach (var health in Objects(await GetAsync(prowlarr, "health", ct)))
        {
            // Per-indexer failures are shown below, with more detail.
            if ((string?)health["source"] is "IndexerStatusCheck" or "IndexerLongTermStatusCheck")
                continue;
            checks.Add(new($"prowlarr-health-{health["source"]}", "Indexers", "Prowlarr",
                (string?)health["type"] == "error" ? CheckStates.Fail : CheckStates.Warn, (string?)health["message"] ?? ""));
        }

        if (indexers.Count == 0)
            checks.Add(new("prowlarr-indexers", "Indexers", "Prowlarr", CheckStates.Fail, "No indexers, so nothing can be found."));

        foreach (var indexer in indexers)
        {
            var id = (int)indexer["id"]!;
            var name = (string)indexer["name"]!;
            var key = $"indexer-{id}";
            var searches = history.Where(h => (int?)h["indexerId"] == id).ToList();
            var mine = errors.Where(l => ((string?)l["message"])?.Contains(name, StringComparison.OrdinalIgnoreCase) == true).ToList();
            var failed = Math.Max(searches.Count(h => (bool?)h["successful"] == false), mine.Count);
            // The message and the exception's first line: the stack trace below it mentions
            // things like "credentials" whatever went wrong.
            var reason = mine.Count == 0 ? null
                : FailureReasons.Classify($"{mine[0]["message"]} {((string?)mine[0]["exception"])?.Split('\n')[0]}");

            if ((bool?)indexer["enable"] != true)
            {
                checks.Add(new(key, "Indexers", name, CheckStates.Warn, "Switched off in Prowlarr.", id));
                continue;
            }
            if (statuses.TryGetValue(id, out var status) && Date(status["disabledTill"]) is { } till && till > now)
            {
                var since = Date(status["initialFailure"]) ?? now;
                var persistent = reason?.Persistent == true || now - since >= PersistentAfter;
                var detail = $"Paused after {reason?.Label ?? "errors"}; failing on and off since {Local(since, now)}. "
                             + $"Prowlarr tries it again at {Local(till, now)}."
                             + (persistent && reason?.Persistent != true ? " That's over a day: look at it." : "");
                checks.Add(new(key, "Indexers", name, persistent ? CheckStates.Fail : CheckStates.Warn, detail, id));
            }
            else if (failed > 0)
            {
                var why = reason is null ? "" : $" ({reason.Label})";
                checks.Add(new(key, "Indexers", name, reason?.Persistent == true ? CheckStates.Fail : CheckStates.Warn,
                    $"{failed} failed in the last 24 hours{why}; {searches.Count} searches answered.", id));
            }
            else
            {
                var seconds = searches.Select(h => double.TryParse((string?)h["data"]?["elapsedTime"], CultureInfo.InvariantCulture, out var ms) ? ms / 1000 : 0).DefaultIfEmpty().Average();
                var detail = searches.Count == 0
                    ? "No searches in the last 24 hours."
                    : $"{searches.Count} searches in the last 24 hours, all answered" + (seconds > 30 ? $", slowly (about {seconds:0} s each)." : ".");
                checks.Add(new(key, "Indexers", name, CheckStates.Ok, detail, id));
            }
        }
        return indexers.Where(i => (bool?)i["enable"] == true).Select(i => (string)i["name"]!).ToList();
    }

    private async Task AppChecksAsync(App app, List<string>? synced, List<PipelineCheck> checks, CancellationToken ct)
    {
        var area = app.Name;
        var prefix = app.Name.ToLowerInvariant();

        // Health, minus the warning we cause on purpose: RSS is off, as the Internet Archive can't do it.
        var health = Objects(await GetAsync(app, "health", ct)).Where(h => (string?)h["source"] != "IndexerRssCheck").ToList();
        if (health.Count == 0)
            checks.Add(new($"{prefix}-health", area, "Health", CheckStates.Ok, "No warnings."));
        foreach (var h in health)
            checks.Add(new($"{prefix}-health-{h["source"]}", area, "Health",
                (string?)h["type"] == "error" ? CheckStates.Fail : CheckStates.Warn, (string?)h["message"] ?? ""));

        var clients = Objects(await GetAsync(app, "downloadclient", ct)).Where(c => (bool?)c["enable"] == true).ToList();
        if (clients.Count == 0)
            checks.Add(new($"{prefix}-client", area, "Download client", CheckStates.Fail, "None set up, so nothing can download."));
        foreach (var client in clients)
        {
            var name = (string?)client["name"] ?? "Download client";
            var error = await TestAsync(app, "downloadclient/test", client, ct);
            checks.Add(error is null
                ? new($"{prefix}-client-{client["id"]}", area, name, CheckStates.Ok, "Reachable.")
                : new($"{prefix}-client-{client["id"]}", area, name, CheckStates.Fail, $"Can't reach it: {error}"));
        }

        var queue = Objects((await GetAsync(app, "queue?pageSize=100", ct))["records"]);
        var stuck = queue.Where(q => (string?)q["trackedDownloadStatus"] is "warning" or "error"
                                     || (string?)q["trackedDownloadState"] is "importBlocked" or "failedPending" or "failed").ToList();
        if (stuck.Count > 0)
        {
            var examples = string.Join("; ", stuck.Take(3).Select(q => $"{q["title"]}: {FirstMessage(q)}"));
            checks.Add(new($"{prefix}-imports", area, "Imports", CheckStates.Warn, $"{stuck.Count} stuck. {examples}"));
        }
        else
        {
            // eventType 3 is "downloadFolderImported" in both apps.
            var last = Objects((await GetAsync(app, "history?eventType=3&pageSize=1&sortKey=date&sortDirection=descending", ct))["records"]).FirstOrDefault();
            var downloading = queue.Count == 0 ? "" : $"{queue.Count} downloading. ";
            var imported = Date(last?["date"]) is { } when ? $"Last import {Local(when, _time.GetUtcNow())}." : "Nothing imported yet.";
            checks.Add(new($"{prefix}-imports", area, "Imports", CheckStates.Ok, downloading + imported));
        }

        if (synced is null)
            return;
        var names = Objects(await GetAsync(app, "indexer", ct)).Select(i => (string?)i["name"] ?? "").ToList();
        var missing = synced.Where(s => !names.Contains(s + ViaProwlarr)).ToList();
        var direct = names.Where(n => !n.EndsWith(ViaProwlarr, StringComparison.Ordinal)).ToList();
        var stale = names.Where(n => n.EndsWith(ViaProwlarr, StringComparison.Ordinal) && !synced.Contains(n[..^ViaProwlarr.Length])).ToList();
        checks.Add(missing.Count > 0
            ? new($"{prefix}-indexers", area, "Indexers from Prowlarr", CheckStates.Fail,
                $"Missing: {string.Join(", ", missing)}. Check Prowlarr > Settings > Apps, then sync.")
            : direct.Count + stale.Count > 0
                ? new($"{prefix}-indexers", area, "Indexers from Prowlarr", CheckStates.Warn,
                    string.Join(" ", new[]
                    {
                        direct.Count > 0 ? $"Added in {app.Name} directly, not through Prowlarr: {string.Join(", ", direct)}." : "",
                        stale.Count > 0 ? $"No longer in Prowlarr: {string.Join(", ", stale)}." : "",
                    }.Where(s => s != "")))
                : new($"{prefix}-indexers", area, "Indexers from Prowlarr", CheckStates.Ok,
                    synced.Count == 1 ? "Has Prowlarr's one indexer." : $"Has all {synced.Count} of Prowlarr's indexers."));
    }

    /// <summary>
    /// Asks Prowlarr to test one indexer now; a pass lifts its pause. Never throws: the answer is
    /// in the message.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestIndexerAsync(int id, CancellationToken ct)
    {
        if (_prowlarr is null)
            return (false, "Prowlarr isn't connected.");
        try
        {
            var indexer = await GetAsync(_prowlarr, $"indexer/{id}", ct);
            var name = (string?)indexer["name"] ?? "The indexer";
            var error = await TestAsync(_prowlarr, "indexer/test", indexer, ct);
            return error is null ? (true, $"{name} works.") : (false, $"{name} failed: {error}");
        }
        catch (Exception ex) when (IsDown(ex, ct))
        {
            return (false, $"Couldn't test it: {ex.Message}");
        }
    }

    private async Task<JsonNode> GetAsync(App app, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, app.BaseUrl + path);
        request.Headers.Add("X-Api-Key", app.ApiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{app.Name} said {(int)response.StatusCode} to {path.Split('?')[0]}", null, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>(ct))!;
    }

    /// <summary>Runs one of the apps' "test" endpoints. Null when it passed, else the reasons.</summary>
    private async Task<string?> TestAsync(App app, string path, JsonNode body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, app.BaseUrl + path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Api-Key", app.ApiKey);
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return null;
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var reasons = Objects(JsonNode.Parse(text)).Select(f => (string?)f["errorMessage"]).Where(m => !string.IsNullOrWhiteSpace(m));
            var joined = string.Join("; ", reasons);
            return joined == "" ? $"HTTP {(int)response.StatusCode}" : joined;
        }
        catch (JsonException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    private static List<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>().ToList() : [];

    private static DateTimeOffset? Date(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static string FirstMessage(JsonObject q) =>
        Objects(q["statusMessages"]).SelectMany(m => (m["messages"] as JsonArray ?? []).Select(x => (string?)x)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
        ?? (string?)q["errorMessage"]
        ?? (string?)q["trackedDownloadState"]
        ?? "no reason given";

    /// <summary>UK time: "14:24" today, "17 Oct 23:38" otherwise.</summary>
    private string Local(DateTimeOffset when, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(when, _zone);
        var today = TimeZoneInfo.ConvertTime(now, _zone).Date;
        return local.ToString(local.Date == today ? "HH:mm" : "d MMM HH:mm", CultureInfo.GetCultureInfo("en-GB"));
    }

    private static bool IsDown(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested;
}
