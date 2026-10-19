namespace Watchlist.Sonarr;

/// <summary>Turns an explicit series, season or episode request into Sonarr searches.</summary>
public sealed class TvDownloads(SonarrClient sonarr)
{
    public bool IsOn => sonarr.IsConfigured;

    public async Task<string> RequestAsync(int tvdbId, string title, string scope, int? season, int? episode, CancellationToken ct)
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
                return $"Sonarr is searching for monitored episodes of {series.Title}.";
            case "season":
                if (series.Added)
                    return $"Sonarr added {series.Title} and is searching for missing episodes in season {season}.";
                var seasonEpisodes = await sonarr.EpisodesAsync(series.Id, season, ct);
                if (seasonEpisodes.Count == 0)
                    throw new InvalidOperationException($"Sonarr doesn't have episode data for season {season} of {series.Title} yet.");
                var missing = seasonEpisodes.Where(e => !e.HasFile).Select(e => e.Id).ToList();
                if (missing.Count == 0)
                    return $"Season {season} of {series.Title} is already in the library.";
                await sonarr.MonitorEpisodesAsync(missing, ct);
                await sonarr.SearchSeasonAsync(series.Id, season!.Value, ct);
                return $"Sonarr is searching for missing episodes in season {season} of {series.Title}.";
            default:
                var selected = await sonarr.WaitForEpisodeAsync(series.Id, season!.Value, episode!.Value, ct);
                if (selected is null)
                    throw new InvalidOperationException($"Sonarr doesn't have S{season:00}E{episode:00} for {series.Title} yet.");
                if (selected.HasFile)
                    return $"S{season:00}E{episode:00} of {series.Title} is already in the library.";
                await sonarr.MonitorEpisodesAsync([selected.Id], ct);
                await sonarr.SearchEpisodesAsync([selected.Id], ct);
                return $"Sonarr is searching for S{season:00}E{episode:00} of {series.Title}.";
        }
    }
}
