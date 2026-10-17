using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Tmdb;

namespace Watchlist.Radarr;

/// <summary>The parts of a Radarr webhook call the watchlist reads.</summary>
public sealed record RadarrEvent(string EventType, RadarrEventMovie? Movie, string? DeleteReason = null);

public sealed record RadarrEventMovie(int Id, int TmdbId, string? Title = null);

/// <summary>
/// Every film on the list goes to Radarr, which searches, downloads and files it. Radarr's
/// webhook moves the card along: searching, downloading, in your library. The checker's ticks
/// re-send anything Radarr missed, and the daily check catches webhooks that never arrived.
/// </summary>
public sealed class Downloads(RadarrClient radarr, ItemStore items, AlertStore alerts, ILogger<Downloads> log)
{
    public bool IsOn => radarr.IsConfigured;

    /// <summary>
    /// Makes sure Radarr has the film and is looking for it. If Radarr can't be reached the film
    /// is marked pending and tried again later; adding to the list never fails because of Radarr.
    /// </summary>
    public async Task<Item> SendAsync(Item item, CancellationToken ct)
    {
        if (!IsOn || item.MediaType != MediaTypes.Movie || item.DownloadState is not (null or DownloadStates.Pending))
            return item;
        try
        {
            var movie = await radarr.FindByTmdbAsync(item.TmdbId, ct);
            if (movie is { HasFile: true })
            {
                await ImportedAsync(item, movie.Id, alert: false);
            }
            else if (movie is not null)
            {
                await radarr.SearchAsync(movie.Id, ct);
                await items.SetDownloadAsync(item.Id, DownloadStates.Searching, movie.Id);
            }
            else
            {
                movie = await radarr.AddAsync(item.TmdbId, ct);
                await items.SetDownloadAsync(item.Id, DownloadStates.Searching, movie.Id);
            }
            log.LogInformation("Sent {Title} to Radarr", item.Title);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Couldn't send {Title} to Radarr; will retry", item.Title);
            await items.SetDownloadAsync(item.Id, DownloadStates.Pending, item.RadarrId);
        }
        return (await items.GetAsync(item.Id))!;
    }

    /// <summary>Drops a film from Radarr, with any file, e.g. when the card was the wrong title.</summary>
    public async Task ForgetAsync(int radarrId, CancellationToken ct)
    {
        if (!IsOn)
            return;
        try
        {
            await radarr.DeleteAsync(radarrId, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Couldn't remove Radarr film {Id}", radarrId);
        }
    }

    /// <summary>One webhook call from Radarr.</summary>
    public async Task HandleAsync(RadarrEvent e)
    {
        if (e.Movie is null)
            return;   // Test, HealthIssue and the like
        var item = await items.FindAsync(e.Movie.TmdbId, MediaTypes.Movie);
        if (item is null || item.Status == Statuses.Removed)
            return;   // a film added in Radarr by hand

        switch (e.EventType)
        {
            case "Grab":
                if (item.DownloadState != DownloadStates.Imported)
                    await items.SetDownloadAsync(item.Id, DownloadStates.Downloading, e.Movie.Id);
                break;
            case "Download":
                await ImportedAsync(item, e.Movie.Id, alert: item.DownloadState != DownloadStates.Imported);
                break;
            case "MovieFileDelete" when e.DeleteReason != "upgrade":
                await items.SetDownloadAsync(item.Id, DownloadStates.Searching, e.Movie.Id);
                await LeaveLibraryAsync(item);
                break;
            case "MovieDelete":
                await items.SetDownloadAsync(item.Id, null, null);
                await LeaveLibraryAsync(item);
                break;
        }
    }

    /// <summary>Re-sends films whose first send failed. Cheap: usually there are none.</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        if (!IsOn)
            return;
        foreach (var item in await items.WithDownloadStateAsync(DownloadStates.Pending))
            await SendAsync(item, ct);
    }

    /// <summary>Asks Radarr about every film still in progress, in case a webhook was missed.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!IsOn)
            return;
        foreach (var item in await items.WithDownloadStateAsync(DownloadStates.Searching, DownloadStates.Downloading))
        {
            try
            {
                var movie = item.RadarrId is { } id ? await radarr.GetAsync(id, ct) : null;
                if (movie is null)
                    await items.SetDownloadAsync(item.Id, null, null);
                else if (movie.HasFile)
                    await ImportedAsync(item, movie.Id, alert: true);
                else
                {
                    var state = await radarr.IsDownloadingAsync(movie.Id, ct) ? DownloadStates.Downloading : DownloadStates.Searching;
                    if (state != item.DownloadState)
                        await items.SetDownloadAsync(item.Id, state, movie.Id);
                }
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Couldn't ask Radarr about {Title}", item.Title);
            }
        }
    }

    private async Task ImportedAsync(Item item, int radarrId, bool alert)
    {
        await items.SetDownloadAsync(item.Id, DownloadStates.Imported, radarrId);
        if (Statuses.IsTracked(item.Status))
            await items.SetStatusAsync(item.Id, Statuses.InLibrary);
        if (alert)
            await alerts.AddAsync(item.Id, AlertKinds.Downloaded, "Downloaded: it's in your library.");
        log.LogInformation("{Title} is in the library", item.Title);
    }

    /// <summary>The file has gone, so the card goes back to the release checker.</summary>
    private async Task LeaveLibraryAsync(Item item)
    {
        if (item.Status == Statuses.InLibrary)
            await items.SetStatusAsync(item.Id, Statuses.Available);
    }
}
