using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Sonarr;

namespace Watchlist.Tests;

/// <summary>Shows on the list go to Sonarr, the whole series, and Sonarr's events move the card along: just like films and Radarr.</summary>
public sealed class ShowDownloadsTests : IDisposable
{
    private const int SeveranceId = 95396;
    private const int SeveranceTvdb = 371980;
    private readonly TestWorld _world = new(radarr: true, sonarr: true);

    public ShowDownloadsTests()
    {
        _world.Tmdb
            .On($"tv/{SeveranceId}/external_ids", $$"""{"tvdb_id":{{SeveranceTvdb}}}""")
            .On($"tv/{SeveranceId}", Samples.SeveranceTv);
    }

    public void Dispose() => _world.Dispose();

    private Task<(Item Item, AddOutcome Outcome, Availability Availability)> AddSeverance() =>
        _world.Watchlist.AddAsync(SeveranceId, "tv", "severance");

    private static SonarrEvent Event(string type, FakeSonarr.Show show, bool isUpgrade = false, string? deleteReason = null, (int Season, int Episode)[]? episodes = null) =>
        new(type, new SonarrEventSeries(show.Id, "Severance"), [.. (episodes ?? []).Select(e => new SonarrEventEpisode(e.Season, e.Episode))], isUpgrade, deleteReason);

    private void Import(FakeSonarr.Show show, int season, int episode) =>
        show.Episodes.Single(e => e.Season == season && e.Number == episode).HasFile = true;

    [Fact]
    public async Task Adding_a_show_sends_the_whole_series_to_sonarr_with_a_search()
    {
        var (item, _, _) = await AddSeverance();

        var show = Assert.Single(_world.Sonarr.Shows.Values);
        Assert.Equal(SeveranceTvdb, show.TvdbId);
        Assert.Equal(4, (int)show.Added["qualityProfileId"]!);   // the "Watchlist" profile
        Assert.Equal("/data/library/shows", (string?)show.Added["rootFolderPath"]);
        Assert.True((bool)show.Added["addOptions"]!["searchForMissingEpisodes"]!);
        Assert.All(show.Episodes, e => Assert.Equal(e.Season > 0, e.Monitored));   // every season, specials aside
        Assert.Equal(DownloadStates.Searching, item.DownloadState);
        Assert.Equal(show.Id, item.SonarrId);
        Assert.Empty(_world.Radarr.Requests);
    }

    [Fact]
    public async Task Sonarr_being_down_does_not_stop_the_add_and_it_is_retried()
    {
        _world.Sonarr.Down = true;
        var (item, outcome, _) = await AddSeverance();
        Assert.Equal(AddOutcome.Added, outcome);
        Assert.Equal(DownloadStates.Pending, item.DownloadState);

        _world.Sonarr.Down = false;
        await _world.TvDownloads.RetryPendingAsync(default);

        Assert.Equal(DownloadStates.Searching, (await _world.Items.GetAsync(item.Id))!.DownloadState);
        Assert.Single(_world.Sonarr.Shows);
        // Radarr's retry leaves shows alone.
        await _world.Downloads.RetryPendingAsync(default);
        Assert.Empty(_world.Radarr.Requests);
    }

    [Fact]
    public async Task A_show_sonarr_already_has_in_full_goes_straight_to_the_library_without_an_alert()
    {
        _world.Sonarr.Has(SeveranceTvdb, files: 6);

        var (item, _, _) = await AddSeverance();

        Assert.Equal(DownloadStates.Imported, item.DownloadState);
        Assert.Equal(Statuses.InLibrary, item.Status);
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task Asking_again_for_a_show_with_deleted_episodes_wants_them_again_and_searches()
    {
        await _world.Items.AddAsync(new Tmdb.TitleRef(SeveranceId, "tv", "Severance", 2022, null, null), "watched it once");
        var show = _world.Sonarr.Has(SeveranceTvdb);
        foreach (var e in show.Episodes)
            e.Monitored = false;   // what Sonarr does when you delete episode files

        var (item, outcome, _) = await AddSeverance();

        Assert.Equal(AddOutcome.AlreadyThere, outcome);
        Assert.All(show.Episodes, e => Assert.Equal(e.Season > 0, e.Monitored));
        Assert.Contains(_world.Sonarr.Requests, r => r.StartsWith("POST command", StringComparison.Ordinal) && r.Contains("SeriesSearch", StringComparison.Ordinal));
        Assert.Equal(DownloadStates.Searching, item.DownloadState);
    }

    [Fact]
    public async Task Sonarr_events_move_the_card_along_and_each_import_alerts()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();

        await _world.TvDownloads.HandleAsync(Event("Grab", show), default);
        Assert.Equal(DownloadStates.Downloading, (await _world.Items.GetAsync(item.Id))!.DownloadState);

        Import(show, 1, 1);
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);
        var done = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(DownloadStates.Imported, done.DownloadState);
        Assert.Equal(Statuses.InLibrary, done.Status);

        // More episodes arrive: still in the library, and each import says which.
        await _world.TvDownloads.HandleAsync(Event("Grab", show), default);
        Assert.Equal(DownloadStates.Imported, (await _world.Items.GetAsync(item.Id))!.DownloadState);
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 2), (2, 1), (2, 2)]), default);
        // An upgrade says nothing.
        await _world.TvDownloads.HandleAsync(Event("Download", show, isUpgrade: true, episodes: [(1, 1)]), default);

        var alerts = await _world.Alerts.PendingAsync();
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(AlertKinds.Downloaded, a.Kind));
        Assert.Contains(alerts, a => a.Text == "Downloaded: S01E01 is in your library.");
        Assert.Contains(alerts, a => a.Text == "Downloaded: S01E02, S02E01 and 1 more are in your library.");
    }

    [Fact]
    public async Task Deleting_every_episode_in_jellyfin_takes_the_card_out_and_nothing_is_searched_again()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        Import(show, 1, 1);
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);

        // Sonarr notices the file is gone, unmonitors the episode and calls the webhook.
        foreach (var e in show.Episodes)
            (e.HasFile, e.Monitored) = (false, false);
        await _world.TvDownloads.HandleAsync(Event("EpisodeFileDelete", show, deleteReason: "missingFromDisk", episodes: [(1, 1)]), default);
        _world.Sonarr.Requests.Clear();
        await _world.TvDownloads.ReconcileAsync(default);

        var after = (await _world.Items.GetAsync(item.Id))!;
        Assert.Null(after.DownloadState);
        Assert.Equal(Statuses.Available, after.Status);
        Assert.DoesNotContain(_world.Sonarr.Requests, r => r.StartsWith("POST command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_some_episodes_keeps_the_show_in_the_library()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        Import(show, 1, 1);
        Import(show, 1, 2);
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1), (1, 2)]), default);

        var deleted = show.Episodes.Single(e => e.Season == 1 && e.Number == 1);
        (deleted.HasFile, deleted.Monitored) = (false, false);
        await _world.TvDownloads.HandleAsync(Event("EpisodeFileDelete", show, deleteReason: "manual", episodes: [(1, 1)]), default);

        var after = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(DownloadStates.Imported, after.DownloadState);
        Assert.Equal(Statuses.InLibrary, after.Status);
    }

    [Fact]
    public async Task Deleting_the_show_in_sonarr_puts_the_card_back()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);

        await _world.TvDownloads.HandleAsync(Event("SeriesDelete", show), default);

        var after = (await _world.Items.GetAsync(item.Id))!;
        Assert.Null(after.DownloadState);
        Assert.Null(after.SonarrId);
        Assert.Equal(Statuses.Available, after.Status);
    }

    [Fact]
    public async Task Events_for_shows_not_on_the_list_are_ignored()
    {
        var stranger = _world.Sonarr.Has(42);

        await _world.TvDownloads.HandleAsync(Event("Download", stranger, episodes: [(1, 1)]), default);
        await _world.TvDownloads.HandleAsync(new SonarrEvent("Test", null), default);

        Assert.Empty(await _world.Items.ListAsync());
    }

    [Fact]
    public async Task The_daily_catch_up_finds_imports_whose_webhook_was_missed()
    {
        var (item, _, _) = await AddSeverance();
        Import(_world.Sonarr.Shows.Values.Single(), 1, 1);

        await _world.TvDownloads.ReconcileAsync(default);

        Assert.Equal(Statuses.InLibrary, (await _world.Items.GetAsync(item.Id))!.Status);
        Assert.Equal("Downloaded: it's in your library.", Assert.Single(await _world.Alerts.PendingAsync()).Text);
    }

    [Fact]
    public async Task The_daily_catch_up_searches_again_for_missing_episodes_unless_downloading()
    {
        await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        Import(show, 1, 1);
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);
        _world.Sonarr.Requests.Clear();

        await _world.TvDownloads.ReconcileAsync(default);
        Assert.Contains(_world.Sonarr.Requests, r => r.Contains("SeriesSearch", StringComparison.Ordinal));

        show.InQueue = true;
        _world.Sonarr.Requests.Clear();
        await _world.TvDownloads.ReconcileAsync(default);
        Assert.DoesNotContain(_world.Sonarr.Requests, r => r.StartsWith("POST command", StringComparison.Ordinal));

        // Everything in: nothing to search for.
        show.InQueue = false;
        foreach (var e in show.Episodes.Where(e => e.Monitored))
            e.HasFile = true;
        _world.Sonarr.Requests.Clear();
        await _world.TvDownloads.ReconcileAsync(default);
        Assert.DoesNotContain(_world.Sonarr.Requests, r => r.StartsWith("POST command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_daily_catch_up_notices_downloads_and_shows_deleted_in_sonarr()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();

        show.InQueue = true;
        await _world.TvDownloads.ReconcileAsync(default);
        Assert.Equal(DownloadStates.Downloading, (await _world.Items.GetAsync(item.Id))!.DownloadState);

        _world.Sonarr.Shows.Clear();
        await _world.TvDownloads.ReconcileAsync(default);
        Assert.Null((await _world.Items.GetAsync(item.Id))!.DownloadState);
    }

    [Fact]
    public async Task Deleting_a_card_that_is_downloading_cancels_it_in_sonarr_and_qbittorrent()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        show.InQueue = true;

        await _world.Watchlist.SetStatusAsync(item.Id, Statuses.Removed);

        Assert.Contains($"DELETE queue/{show.Id}?removeFromClient=true&blocklist=false", _world.Sonarr.Requests);
        Assert.Empty(_world.Sonarr.Shows);
        Assert.Null((await _world.Items.GetAsync(item.Id))!.DownloadState);
        Assert.Empty(_world.Radarr.Requests);
    }

    [Fact]
    public async Task Undo_after_deleting_sends_the_show_again()
    {
        var (item, _, _) = await AddSeverance();
        await _world.Watchlist.SetStatusAsync(item.Id, Statuses.Removed);

        var restored = await _world.Watchlist.SetStatusAsync(item.Id, Statuses.Available);

        Assert.Equal(DownloadStates.Searching, restored!.DownloadState);
        Assert.Single(_world.Sonarr.Shows);
    }

    [Fact]
    public async Task Deleting_a_card_keeps_downloaded_episodes()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);

        await _world.Watchlist.SetStatusAsync(item.Id, Statuses.Removed);

        Assert.Single(_world.Sonarr.Shows);
        Assert.DoesNotContain(_world.Sonarr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_file_removes_the_show_from_sonarr_and_the_card_goes_back_to_tracking()
    {
        var (item, _, _) = await AddSeverance();
        var show = _world.Sonarr.Shows.Values.Single();
        await _world.TvDownloads.HandleAsync(Event("Download", show, episodes: [(1, 1)]), default);

        var after = await _world.Watchlist.DeleteFileAsync((await _world.Items.GetAsync(item.Id))!);

        Assert.Contains($"DELETE series/{show.Id}?deleteFiles=true", _world.Sonarr.Requests);
        Assert.Null(after!.DownloadState);
        Assert.Equal(Statuses.Available, after.Status);
        Assert.Empty(_world.Radarr.Requests);
    }

    [Fact]
    public async Task Delete_file_says_so_when_sonarr_is_down_and_changes_nothing()
    {
        var (item, _, _) = await AddSeverance();
        await _world.TvDownloads.HandleAsync(Event("Download", _world.Sonarr.Shows.Values.Single(), episodes: [(1, 1)]), default);
        _world.Sonarr.Down = true;

        var after = await _world.Watchlist.DeleteFileAsync((await _world.Items.GetAsync(item.Id))!);

        Assert.Null(after);
        Assert.Equal(DownloadStates.Imported, (await _world.Items.GetAsync(item.Id))!.DownloadState);
    }

    [Fact]
    public async Task Fixing_a_wrong_title_drops_the_old_show_from_sonarr_and_sends_the_new_one()
    {
        _world.Tmdb
            .On("tv/999/external_ids", """{"tvdb_id":555}""")
            .On("tv/999", Samples.SeveranceTv.Replace("95396", "999"));
        var (item, _, _) = await AddSeverance();
        var old = _world.Sonarr.Shows.Values.Single();

        var fixedItem = await _world.Watchlist.ReplaceMatchAsync(item.Id, 999, "tv");

        Assert.Contains($"DELETE series/{old.Id}?deleteFiles=true", _world.Sonarr.Requests);
        var show = Assert.Single(_world.Sonarr.Shows.Values);
        Assert.Equal(555, show.TvdbId);
        Assert.Equal(show.Id, fixedItem!.SonarrId);
    }

    [Fact]
    public async Task Fixing_a_film_into_a_show_drops_it_from_radarr_and_sends_it_to_sonarr()
    {
        _world.Tmdb.On("movie/1078605", Samples.WeaponsMovie);
        var (item, _, _) = await _world.Watchlist.AddAsync(1078605, "movie", "weapons");
        var film = _world.Radarr.Films.Values.Single();

        var fixedItem = await _world.Watchlist.ReplaceMatchAsync(item.Id, SeveranceId, "tv");

        Assert.Contains($"DELETE movie/{film.Id}?deleteFiles=true", _world.Radarr.Requests);
        Assert.Equal(_world.Sonarr.Shows.Values.Single().Id, fixedItem!.SonarrId);
        Assert.Null(fixedItem.RadarrId);
    }
}
