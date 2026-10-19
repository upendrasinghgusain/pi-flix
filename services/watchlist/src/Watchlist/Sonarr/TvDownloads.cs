using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Tmdb;

namespace Watchlist.Sonarr;

/// <summary>The parts of a Sonarr webhook call the watchlist reads.</summary>
public sealed record SonarrEvent(
    string EventType, SonarrEventSeries? Series, IReadOnlyList<SonarrEventEpisode>? Episodes = null,
    bool IsUpgrade = false, string? DeleteReason = null);

public sealed record SonarrEventSeries(int Id, string? Title = null);

public sealed record SonarrEventEpisode(int SeasonNumber, int EpisodeNumber);

/// <summary>What an explicit season or episode request did.</summary>
public sealed record TvRequest(string Message, int SeriesId);

/// <summary>
/// Every show on the list goes to Sonarr, the whole series (specials aside), just as films go to
/// Radarr. Sonarr's webhook moves the card along: searching, downloading, in your library (from
/// the first episode in). The checker's ticks re-send anything Sonarr missed, and the daily check
/// catches webhooks that never arrived and searches again for episodes still missing.
/// An explicit season or episode request narrows what Sonarr looks for.
/// </summary>
public sealed class TvDownloads(SonarrClient sonarr, TmdbClient tmdb, ItemStore items, AlertStore alerts, ILogger<TvDownloads> log)
{
    public bool IsOn => sonarr.IsConfigured;

    /// <summary>
    /// Makes sure Sonarr has the whole show and is looking for it. If Sonarr can't be reached the
    /// show is marked pending and tried again later; adding to the list never fails because of Sonarr.
    /// </summary>
    public async Task<Item> SendAsync(Item item, CancellationToken ct)
    {
        // Searching too: asking for a show again sends a fresh search.
        if (!IsOn || item.MediaType != MediaTypes.Tv || item.DownloadState is not (null or DownloadStates.Pending or DownloadStates.Searching))
            return item;
        try
        {
            var tvdbId = await tmdb.GetTvdbIdAsync(item.TmdbId, ct);
            var show = await sonarr.FindAsync(tvdbId, ct);
            if (show is { HasFiles: true, Missing: false })
            {
                await ImportedAsync(item, show.Id, alert: false);
            }
            else if (show is not null)
            {
                // Asked for again: want every season, and the episodes you deleted (Sonarr unmonitored those).
                await sonarr.FindOrAddAsync(tvdbId, item.Title, "series", null, ct);
                var unwanted = (await sonarr.EpisodesAsync(show.Id, null, ct))
                    .Where(e => e.SeasonNumber > 0 && !e.HasFile && !e.Monitored).Select(e => e.Id).ToList();
                if (unwanted.Count > 0)
                    await sonarr.MonitorEpisodesAsync(unwanted, ct);
                await sonarr.SearchSeriesAsync(show.Id, ct);
                if (show.HasFiles)
                    await ImportedAsync(item, show.Id, alert: false);
                else
                    await items.SetTvDownloadAsync(item.Id, DownloadStates.Searching, show.Id);
            }
            else
            {
                var added = await sonarr.FindOrAddAsync(tvdbId, item.Title, "series", null, ct);
                await items.SetTvDownloadAsync(item.Id, DownloadStates.Searching, added.Id);
            }
            log.LogInformation("Sent {Title} to Sonarr", item.Title);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Couldn't send {Title} to Sonarr; will retry", item.Title);
            await items.SetTvDownloadAsync(item.Id, DownloadStates.Pending, item.SonarrId);
        }
        return (await items.GetAsync(item.Id))!;
    }

    /// <summary>An explicit season or episode request (or the whole series, again).</summary>
    public async Task<TvRequest> RequestAsync(int tvdbId, string title, string scope, int? season, int? episode, CancellationToken ct)
    {
        if (!IsOn)
            throw new InvalidOperationException("Sonarr isn't configured for the chat app.");
        if (scope is not ("series" or "season" or "episode"))
            throw new InvalidOperationException("Choose a series, season or episode request.");
        if ((scope is "season" or "episode") && (season is null or < 0))
            throw new InvalidOperationException("Give a season number (zero is for specials).");
        if (scope == "episode" && (episode is null or < 1))
            throw new InvalidOperationException("Give an episode number greater than zero.");

        var series = await sonarr.FindOrAddAsync(tvdbId, title, scope, season, ct);
        switch (scope)
        {
            case "series":
                if (!series.Added)
                    await sonarr.SearchSeriesAsync(series.Id, ct);
                return new($"Sonarr is searching for monitored episodes of {series.Title}.", series.Id);
            case "season":
                if (series.Added)
                    return new($"Sonarr added {series.Title} and is searching for missing episodes in season {season}.", series.Id);
                var seasonEpisodes = await sonarr.EpisodesAsync(series.Id, season, ct);
                if (seasonEpisodes.Count == 0)
                    throw new InvalidOperationException($"Sonarr doesn't have episode data for season {season} of {series.Title} yet.");
                var missing = seasonEpisodes.Where(e => !e.HasFile).Select(e => e.Id).ToList();
                if (missing.Count == 0)
                    return new($"Season {season} of {series.Title} is already in the library.", series.Id);
                await sonarr.MonitorEpisodesAsync(missing, ct);
                await sonarr.SearchSeasonAsync(series.Id, season!.Value, ct);
                return new($"Sonarr is searching for missing episodes in season {season} of {series.Title}.", series.Id);
            default:
                var selected = await sonarr.WaitForEpisodeAsync(series.Id, season!.Value, episode!.Value, ct);
                if (selected is null)
                    throw new InvalidOperationException($"Sonarr doesn't have S{season:00}E{episode:00} for {series.Title} yet.");
                if (selected.HasFile)
                    return new($"S{season:00}E{episode:00} of {series.Title} is already in the library.", series.Id);
                await sonarr.MonitorEpisodesAsync([selected.Id], ct);
                await sonarr.SearchEpisodesAsync([selected.Id], ct);
                return new($"Sonarr is searching for S{season:00}E{episode:00} of {series.Title}.", series.Id);
        }
    }

    /// <summary>Links the card to a show an explicit request sent to Sonarr, so its webhook moves the card along.</summary>
    public async Task<Item> TrackAsync(Item item, int sonarrId)
    {
        if (item.DownloadState != DownloadStates.Imported)
            await items.SetTvDownloadAsync(item.Id, DownloadStates.Searching, sonarrId);
        return (await items.GetAsync(item.Id))!;
    }

    /// <summary>
    /// Drops a show from Sonarr: cancels any download (removing it from qBittorrent) and deletes the
    /// show with its files, so Jellyfin drops it too. Used for the wrong-title fix, for deleting a
    /// card that's still downloading, and for "Delete file".
    /// </summary>
    public async Task<bool> ForgetAsync(int sonarrId, CancellationToken ct)
    {
        if (!IsOn)
            return false;
        try
        {
            await sonarr.CancelDownloadsAsync(sonarrId, ct);
            await sonarr.DeleteAsync(sonarrId, ct);
            return true;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Couldn't remove Sonarr show {Id}", sonarrId);
            return false;
        }
    }

    /// <summary>
    /// A card was deleted. A show still being looked for or downloaded is cancelled in Sonarr; one
    /// with episodes in stays in the library (use DeleteFileAsync for that).
    /// </summary>
    public async Task CardDeletedAsync(Item item, CancellationToken ct)
    {
        if (item.MediaType != MediaTypes.Tv || item.DownloadState is DownloadStates.Imported or null)
            return;
        if (item.SonarrId is { } sonarrId)
            await ForgetAsync(sonarrId, ct);
        await items.SetTvDownloadAsync(item.Id, null, null);
    }

    /// <summary>
    /// Deletes the downloaded show and all its episodes. The card stays, back to release tracking.
    /// Null if Sonarr couldn't do it, so nothing claims the files are gone when they aren't.
    /// </summary>
    public async Task<Item?> DeleteFileAsync(Item item, CancellationToken ct)
    {
        if (item.SonarrId is { } sonarrId && !await ForgetAsync(sonarrId, ct))
            return null;
        await items.SetTvDownloadAsync(item.Id, null, null);
        await LeaveLibraryAsync(item);
        return (await items.GetAsync(item.Id))!;
    }

    /// <summary>One webhook call from Sonarr.</summary>
    public async Task HandleAsync(SonarrEvent e, CancellationToken ct)
    {
        if (e.Series is null)
            return;   // Test, HealthIssue and the like
        var item = await items.FindBySonarrIdAsync(e.Series.Id);
        if (item is null || item.Status == Statuses.Removed)
            return;   // a show added in Sonarr by hand

        switch (e.EventType)
        {
            case "Grab":
                if (item.DownloadState != DownloadStates.Imported)
                    await items.SetTvDownloadAsync(item.Id, DownloadStates.Downloading, e.Series.Id);
                break;
            case "Download" when !e.IsUpgrade:
                await ImportedAsync(item, e.Series.Id, alert: true, e.Episodes);
                break;
            // You deleted episodes (in Jellyfin, say). Sonarr unmonitors them; once none are
            // left, the card leaves the download flow. Asking for it again in the chat brings it back.
            case "EpisodeFileDelete" when e.DeleteReason != "upgrade":
                try
                {
                    if (await sonarr.GetAsync(e.Series.Id, ct) is not { HasFiles: true })
                    {
                        await items.SetTvDownloadAsync(item.Id, null, e.Series.Id);
                        await LeaveLibraryAsync(item);
                    }
                }
                catch (HttpRequestException ex)
                {
                    log.LogWarning(ex, "Couldn't ask Sonarr about {Title}; the daily check will", item.Title);
                }
                break;
            case "SeriesDelete":
                await items.SetTvDownloadAsync(item.Id, null, null);
                await LeaveLibraryAsync(item);
                break;
        }
    }

    /// <summary>Re-sends shows whose first send failed. Cheap: usually there are none.</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        if (!IsOn)
            return;
        foreach (var item in await items.WithDownloadStateAsync(MediaTypes.Tv, DownloadStates.Pending))
            await SendAsync(item, ct);
    }

    /// <summary>
    /// Asks Sonarr about every show in progress or in the library, in case a webhook was missed.
    /// Wanted episodes still missing get a fresh search: with RSS off (the Internet Archive's
    /// listing is too slow), nothing else retries a search that failed or found nothing.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!IsOn)
            return;
        foreach (var item in await items.WithDownloadStateAsync(
                     MediaTypes.Tv, DownloadStates.Searching, DownloadStates.Downloading, DownloadStates.Imported))
        {
            try
            {
                var show = item.SonarrId is { } id ? await sonarr.GetAsync(id, ct) : null;
                if (show is null)
                {
                    await items.SetTvDownloadAsync(item.Id, null, null);
                    await LeaveLibraryAsync(item);
                    continue;
                }
                if (show.HasFiles)
                {
                    if (item.DownloadState != DownloadStates.Imported)
                        await ImportedAsync(item, show.Id, alert: true);
                }
                else if (item.DownloadState == DownloadStates.Imported)
                {
                    // Every episode deleted; see HandleAsync.
                    await items.SetTvDownloadAsync(item.Id, null, show.Id);
                    await LeaveLibraryAsync(item);
                    continue;
                }
                if (!show.Missing)
                    continue;   // all in, or nothing has aired yet

                if (await sonarr.IsDownloadingAsync(show.Id, ct))
                {
                    if (!show.HasFiles && item.DownloadState != DownloadStates.Downloading)
                        await items.SetTvDownloadAsync(item.Id, DownloadStates.Downloading, show.Id);
                }
                else
                {
                    await sonarr.SearchSeriesAsync(show.Id, ct);
                    if (!show.HasFiles && item.DownloadState != DownloadStates.Searching)
                        await items.SetTvDownloadAsync(item.Id, DownloadStates.Searching, show.Id);
                }
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Couldn't ask Sonarr about {Title}", item.Title);
            }
        }
    }

    private async Task ImportedAsync(Item item, int sonarrId, bool alert, IReadOnlyList<SonarrEventEpisode>? episodes = null)
    {
        await items.SetTvDownloadAsync(item.Id, DownloadStates.Imported, sonarrId);
        if (Statuses.IsTracked(item.Status))
            await items.SetStatusAsync(item.Id, Statuses.InLibrary);
        if (alert)
            await alerts.AddAsync(item.Id, AlertKinds.Downloaded, $"Downloaded: {Episodes(episodes)} in your library.");
        log.LogInformation("{Title} is in the library", item.Title);
    }

    /// <summary>"S01E02 is", "S01E01, S01E02 and 3 more are", or "it's" when Sonarr didn't say.</summary>
    internal static string Episodes(IReadOnlyList<SonarrEventEpisode>? episodes)
    {
        if (episodes is not { Count: > 0 })
            return "it's";
        var codes = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .Select(e => $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}").ToList();
        return codes.Count switch
        {
            1 => $"{codes[0]} is",
            2 => $"{codes[0]} and {codes[1]} are",
            _ => $"{codes[0]}, {codes[1]} and {codes.Count - 2} more are",
        };
    }

    /// <summary>The files have gone, so the card goes back to the release checker.</summary>
    private async Task LeaveLibraryAsync(Item item)
    {
        if (item.Status == Statuses.InLibrary)
            await items.SetStatusAsync(item.Id, Statuses.Available);
    }
}
