using System.Globalization;
using Microsoft.Extensions.Options;
using Watchlist.Data;
using Watchlist.Push;

namespace Watchlist.Alerts;

/// <summary>
/// The daily check and the delivery of alerts. Kept apart from the timer so tests can drive it.
/// </summary>
public sealed class CheckRunner(
    ItemStore items,
    WatchlistService watchlist,
    AlertStore alerts,
    PushStore pushes,
    IPushSender sender,
    MetaStore meta,
    IOptionsMonitor<AlertOptions> options,
    ILogger<CheckRunner> log)
{
    private const string LastRunKey = "last_check_run";

    /// <summary>Pause between titles, to be gentle with TMDb.</summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Due once a day, at the check time or the first chance after it (e.g. after a restart).</summary>
    public async Task<bool> IsCheckDueAsync()
    {
        var todaysRun = watchlist.LocalInstant(watchlist.Today, options.CurrentValue.CheckAt);
        if (watchlist.LocalNow < todaysRun)
            return false;
        var last = await meta.GetAsync(LastRunKey);
        return last is null || DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) < todaysRun;
    }

    /// <summary>Checks every title still being tracked. One failure doesn't stop the rest.</summary>
    public async Task<int> RunChecksAsync(CancellationToken ct)
    {
        var tracked = (await items.ListAsync()).Where(i => Statuses.IsTracked(i.Status)).ToList();
        var alertCount = 0;
        foreach (var item in tracked)
        {
            try
            {
                if (await watchlist.CheckAsync(item.Id, ct) is { } result)
                    alertCount += result.Alerts.Count;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Check failed for {Title}", item.Title);
            }
            if (Pause > TimeSpan.Zero)
                await Task.Delay(Pause, ct);
        }
        await meta.SetAsync(LastRunKey, watchlist.LocalNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        log.LogInformation("Checked {Count} titles, {Alerts} alerts", tracked.Count, alertCount);
        return alertCount;
    }

    /// <summary>
    /// Sends waiting alerts, one notification per title, unless it's quiet hours. Devices that have
    /// gone away are forgotten. With no devices at all, alerts are marked sent so they don't pile up.
    /// </summary>
    public async Task<int> DeliverAsync(CancellationToken ct)
    {
        if (options.CurrentValue.IsQuiet(TimeOnly.FromDateTime(watchlist.LocalNow.DateTime)))
            return 0;

        var pending = await alerts.PendingAsync();
        if (pending.Count == 0)
            return 0;

        var devices = await pushes.AllAsync();
        var sent = 0;
        foreach (var group in pending.GroupBy(a => a.ItemId))
        {
            var item = await items.GetAsync(group.Key);
            if (item is null)
            {
                await alerts.MarkSentAsync(group.Select(a => a.Id));
                continue;
            }

            var payload = new
            {
                title = item.Year is null ? item.Title : $"{item.Title} ({item.Year})",
                body = string.Join("\n", group.Select(a => a.Text)),
                tag = $"item-{item.Id}",
                url = $"./#item-{item.Id}",
            };
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
            // A push service that's down: keep the alert and try again on the next tick.
            if (delivered)
            {
                await alerts.MarkSentAsync(group.Select(a => a.Id));
                sent++;
            }
        }
        return sent;
    }
}

/// <summary>Wakes every few minutes: runs the daily check when it's due, then delivers alerts.</summary>
public sealed class ReleaseChecker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ReleaseChecker> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<CheckRunner>();
                if (await runner.IsCheckDueAsync())
                    await runner.RunChecksAsync(stoppingToken);
                await runner.DeliverAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Release checker tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
