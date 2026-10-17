using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Radarr;

namespace Watchlist.Tests;

/// <summary>Films on the list go to Radarr, and Radarr's events move the card along.</summary>
public sealed class DownloadsTests : IDisposable
{
    private const int WeaponsId = 1078605;
    private readonly TestWorld _world = new(radarr: true);

    public DownloadsTests()
    {
        _world.Tmdb.On($"movie/{WeaponsId}", Samples.WeaponsMovie).On("tv/95396", Samples.SeveranceTv);
    }

    public void Dispose() => _world.Dispose();

    private Task<(Item Item, AddOutcome Outcome, Availability Availability)> AddWeapons() =>
        _world.Watchlist.AddAsync(WeaponsId, "movie", "weapons");

    private static RadarrEvent Event(string type, FakeRadarr.Film film, string? deleteReason = null) =>
        new(type, new RadarrEventMovie(film.Id, film.TmdbId, "Weapons"), deleteReason);

    [Fact]
    public async Task Adding_a_film_sends_it_to_radarr_with_a_search()
    {
        var (item, _, _) = await AddWeapons();

        var film = Assert.Single(_world.Radarr.Films.Values);
        Assert.Equal(WeaponsId, film.TmdbId);
        Assert.Equal(7, (int)film.Added["qualityProfileId"]!);   // the "Watchlist" profile
        Assert.Equal("/data/library/movies", (string?)film.Added["rootFolderPath"]);
        Assert.True((bool)film.Added["addOptions"]!["searchForMovie"]!);
        Assert.Equal(DownloadStates.Searching, item.DownloadState);
        Assert.Equal(film.Id, item.RadarrId);
    }

    [Fact]
    public async Task Tv_is_not_sent()
    {
        var (item, _, _) = await _world.Watchlist.AddAsync(95396, "tv", "severance");

        Assert.Empty(_world.Radarr.Requests);
        Assert.Null(item.DownloadState);
    }

    [Fact]
    public async Task Nothing_is_sent_when_radarr_is_not_set_up()
    {
        using var world = new TestWorld(radarr: false);
        world.Tmdb.On($"movie/{WeaponsId}", Samples.WeaponsMovie);

        var (item, _, _) = await world.Watchlist.AddAsync(WeaponsId, "movie", "weapons");

        Assert.Empty(world.Radarr.Requests);
        Assert.Null(item.DownloadState);
    }

    [Fact]
    public async Task Radarr_being_down_does_not_stop_the_add_and_it_is_retried()
    {
        _world.Radarr.Down = true;
        var (item, outcome, _) = await AddWeapons();
        Assert.Equal(AddOutcome.Added, outcome);
        Assert.Equal(DownloadStates.Pending, item.DownloadState);

        _world.Radarr.Down = false;
        await _world.Downloads.RetryPendingAsync(default);

        Assert.Equal(DownloadStates.Searching, (await _world.Items.GetAsync(item.Id))!.DownloadState);
        Assert.Single(_world.Radarr.Films);
    }

    [Fact]
    public async Task Asking_again_for_a_film_that_never_reached_radarr_sends_it()
    {
        await _world.Items.AddAsync(new Tmdb.TitleRef(WeaponsId, "movie", "Weapons", 2025, null, null), "added before Radarr");

        var (item, outcome, _) = await AddWeapons();

        Assert.Equal(AddOutcome.AlreadyThere, outcome);
        Assert.Equal(DownloadStates.Searching, item.DownloadState);
    }

    [Fact]
    public async Task A_film_radarr_already_has_on_disk_goes_straight_to_the_library_without_an_alert()
    {
        _world.Radarr.Has(WeaponsId, hasFile: true);

        var (item, _, _) = await AddWeapons();

        Assert.Equal(DownloadStates.Imported, item.DownloadState);
        Assert.Equal(Statuses.InLibrary, item.Status);
        Assert.Empty(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task Radarr_events_move_the_card_along_and_the_import_alerts_once()
    {
        var (item, _, _) = await AddWeapons();
        var film = _world.Radarr.Films.Values.Single();

        await _world.Downloads.HandleAsync(Event("Grab", film));
        Assert.Equal(DownloadStates.Downloading, (await _world.Items.GetAsync(item.Id))!.DownloadState);

        await _world.Downloads.HandleAsync(Event("Download", film));
        var done = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(DownloadStates.Imported, done.DownloadState);
        Assert.Equal(Statuses.InLibrary, done.Status);

        // An upgrade: the old file is deleted and a better one imported. No second alert.
        await _world.Downloads.HandleAsync(Event("MovieFileDelete", film, deleteReason: "upgrade"));
        await _world.Downloads.HandleAsync(Event("Download", film));
        Assert.Equal(Statuses.InLibrary, (await _world.Items.GetAsync(item.Id))!.Status);
        var alert = Assert.Single(await _world.Alerts.PendingAsync());
        Assert.Equal(AlertKinds.Downloaded, alert.Kind);
    }

    [Fact]
    public async Task Deleting_the_film_in_radarr_puts_the_card_back()
    {
        var (item, _, _) = await AddWeapons();
        var film = _world.Radarr.Films.Values.Single();
        await _world.Downloads.HandleAsync(Event("Download", film));

        await _world.Downloads.HandleAsync(Event("MovieDelete", film));

        var after = (await _world.Items.GetAsync(item.Id))!;
        Assert.Null(after.DownloadState);
        Assert.Null(after.RadarrId);
        Assert.Equal(Statuses.Available, after.Status);
    }

    [Fact]
    public async Task Events_for_films_not_on_the_list_are_ignored()
    {
        var stranger = _world.Radarr.Has(42);

        await _world.Downloads.HandleAsync(Event("Download", stranger));
        await _world.Downloads.HandleAsync(new RadarrEvent("Test", null));

        Assert.Empty(await _world.Items.ListAsync());
    }

    [Fact]
    public async Task The_daily_catch_up_finds_imports_whose_webhook_was_missed()
    {
        var (item, _, _) = await AddWeapons();
        _world.Radarr.Films.Values.Single().HasFile = true;

        await _world.Downloads.ReconcileAsync(default);

        Assert.Equal(Statuses.InLibrary, (await _world.Items.GetAsync(item.Id))!.Status);
        Assert.Single(await _world.Alerts.PendingAsync());
    }

    [Fact]
    public async Task The_daily_catch_up_notices_downloads_and_films_deleted_in_radarr()
    {
        var (item, _, _) = await AddWeapons();
        var film = _world.Radarr.Films.Values.Single();

        film.InQueue = true;
        await _world.Downloads.ReconcileAsync(default);
        Assert.Equal(DownloadStates.Downloading, (await _world.Items.GetAsync(item.Id))!.DownloadState);

        _world.Radarr.Films.Clear();
        await _world.Downloads.ReconcileAsync(default);
        Assert.Null((await _world.Items.GetAsync(item.Id))!.DownloadState);
    }

    [Fact]
    public async Task Fixing_a_wrong_title_drops_the_old_film_from_radarr_and_sends_the_new_one()
    {
        _world.Tmdb.On("movie/999", Samples.WeaponsMovie.Replace("1078605", "999"));
        var (item, _, _) = await AddWeapons();
        var old = _world.Radarr.Films.Values.Single();

        var fixedItem = await _world.Watchlist.ReplaceMatchAsync(item.Id, 999, "movie");

        Assert.Contains($"DELETE movie/{old.Id}?deleteFiles=true", _world.Radarr.Requests);
        var film = Assert.Single(_world.Radarr.Films.Values);
        Assert.Equal(999, film.TmdbId);
        Assert.Equal(film.Id, fixedItem!.RadarrId);
    }
}
