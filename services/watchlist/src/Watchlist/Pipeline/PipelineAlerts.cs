using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Push;

namespace Watchlist.Pipeline;

/// <summary>
/// Pushes an alert when part of the pipeline has been broken for a while, and again when it's
/// fixed. Only "fail" checks count: warnings (a paused indexer that's likely to recover, a stuck
/// download) show on the Status tab but don't buzz your phone. Respects quiet hours.
/// </summary>
public sealed class PipelineAlerts(
    MetaStore meta,
    PushStore pushes,
    IPushSender sender,
    WatchlistService watchlist,
    IOptionsMonitor<AlertOptions> options)
{
    private const string Key = "pipeline_problems";

    /// <summary>A failure must last this long before it alerts, so restarts don't.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    public sealed record Problem(string Name, string Detail, DateTimeOffset Since, bool Notified);

    /// <summary>Returns the number of alerts sent (0 or 1: everything goes in one).</summary>
    public async Task<int> NotifyAsync(PipelineReport report, CancellationToken ct)
    {
        var json = await meta.GetAsync(Key);
        var known = json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, Problem>>(json) ?? [];

        var now = report.CheckedAt;
        var current = new Dictionary<string, Problem>();
        foreach (var check in report.Checks.Where(c => c.State == CheckStates.Fail))
            current[check.Key] = known.TryGetValue(check.Key, out var seen)
                ? seen with { Detail = check.Detail }
                : new($"{check.Area}: {check.Name}", check.Detail, now, false);

        var broken = current.Where(p => !p.Value.Notified && now - p.Value.Since >= Grace).ToList();
        var mended = known.Where(p => p.Value.Notified && !current.ContainsKey(p.Key)).ToList();

        var sent = 0;
        if (broken.Count + mended.Count > 0 && !options.CurrentValue.IsQuiet(TimeOnly.FromDateTime(watchlist.LocalNow.DateTime)))
        {
            var lines = broken.Select(p => $"{p.Value.Name}: {p.Value.Detail}")
                .Concat(mended.Select(p => $"Fixed: {p.Value.Name}"));
            if (await SendAsync(string.Join("\n", lines), ct))
            {
                foreach (var (key, problem) in broken)
                    current[key] = problem with { Notified = true };
                mended = [];
                sent = 1;
            }
        }
        // A fix not yet announced (quiet hours, push down) stays, so "Fixed" goes out later.
        foreach (var (key, problem) in mended)
            current[key] = problem;

        await meta.SetAsync(Key, JsonSerializer.Serialize(current));
        return sent;
    }

    private async Task<bool> SendAsync(string body, CancellationToken ct)
    {
        var devices = await pushes.AllAsync();
        var payload = new { title = "Download pipeline", body, tag = "pipeline", url = "./#status" };
        var delivered = devices.Count == 0;
        foreach (var device in devices)
        {
            switch (await sender.SendAsync(device, payload, ct))
            {
                case PushResult.Sent:
                    delivered = true;
                    break;
                case PushResult.Gone:
                    await pushes.DeleteAsync(device.Endpoint);
                    break;
            }
        }
        return delivered;
    }
}
