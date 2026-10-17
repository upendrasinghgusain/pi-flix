using System.Text.Json;
using Watchlist.Data;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

public sealed class ItemStoreTests : IDisposable
{
    private readonly TestWorld _world = new();
    private static readonly TitleRef Weapons = new(1078605, MediaTypes.Movie, "Weapons", 2025, "/w.jpg", "Kids vanish");

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Adding_saves_the_title_and_what_was_typed()
    {
        var (item, outcome) = await _world.Items.AddAsync(Weapons, "that new zach cregger horror");

        Assert.Equal(AddOutcome.Added, outcome);
        Assert.Equal("Weapons", item.Title);
        Assert.Equal(Statuses.Waiting, item.Status);
        Assert.Equal("that new zach cregger horror", item.AddedText);
        Assert.Equal(_world.Time.GetUtcNow(), item.AddedAt);
    }

    [Fact]
    public async Task Adding_twice_keeps_one_item()
    {
        await _world.Items.AddAsync(Weapons, "weapons");
        var (_, outcome) = await _world.Items.AddAsync(Weapons, "weapons again");

        Assert.Equal(AddOutcome.AlreadyThere, outcome);
        Assert.Single(await _world.Items.ListAsync());
    }

    [Theory]
    [InlineData(Statuses.Removed)]
    [InlineData(Statuses.Watched)]
    public async Task Adding_a_removed_or_watched_title_brings_it_back(string status)
    {
        var (first, _) = await _world.Items.AddAsync(Weapons, "weapons");
        await _world.Items.SetStatusAsync(first.Id, status);

        var (again, outcome) = await _world.Items.AddAsync(Weapons, "weapons please");

        Assert.Equal(AddOutcome.Revived, outcome);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(Statuses.Waiting, again.Status);
        Assert.Equal("weapons please", again.AddedText);
    }

    [Fact]
    public async Task Listing_hides_removed_items_unless_asked()
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, null);
        await _world.Items.SetStatusAsync(item.Id, Statuses.Removed);

        Assert.Empty(await _world.Items.ListAsync());
        Assert.Single(await _world.Items.ListAsync(Statuses.Removed));
    }

    [Fact]
    public async Task A_check_moves_waiting_to_available_and_back()
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, null);

        await _world.Items.SaveCheckAsync(item.Id, "{}", available: true);
        Assert.Equal(Statuses.Available, (await _world.Items.GetAsync(item.Id))!.Status);

        await _world.Items.SaveCheckAsync(item.Id, "{}", available: false);
        Assert.Equal(Statuses.Waiting, (await _world.Items.GetAsync(item.Id))!.Status);
    }

    [Theory]
    [InlineData(Statuses.InLibrary)]
    [InlineData(Statuses.Watched)]
    [InlineData(Statuses.Removed)]
    public async Task A_check_never_changes_a_status_set_elsewhere(string status)
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, null);
        await _world.Items.SetStatusAsync(item.Id, status);

        await _world.Items.SaveCheckAsync(item.Id, "{}", available: true);

        var saved = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(status, saved.Status);
        Assert.Equal(_world.Time.GetUtcNow(), saved.LastCheckedAt);
    }

    [Fact]
    public async Task Replacing_the_match_resets_what_was_found()
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, "weapons");
        await _world.Items.SaveCheckAsync(item.Id, "{}", available: true);

        var other = new TitleRef(999, MediaTypes.Movie, "Weapons", 1999, null, null);
        Assert.True(await _world.Items.ReplaceMatchAsync(item.Id, other));

        var saved = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal(999, saved.TmdbId);
        Assert.Equal(1999, saved.Year);
        Assert.Equal(Statuses.Waiting, saved.Status);
        Assert.Null(saved.AvailabilityJson);
        Assert.Equal("weapons", saved.AddedText);
    }

    [Fact]
    public async Task Replacing_the_match_with_a_title_already_listed_is_refused()
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, null);
        var other = new TitleRef(999, MediaTypes.Movie, "Weapons", 1999, null, null);
        await _world.Items.AddAsync(other, null);

        Assert.False(await _world.Items.ReplaceMatchAsync(item.Id, other));
    }

    [Fact]
    public async Task Notes_and_snooze_round_trip()
    {
        var (item, _) = await _world.Items.AddAsync(Weapons, null);
        await _world.Items.SetNotesAsync(item.Id, "Sam recommended");
        await _world.Items.SetSnoozeAsync(item.Id, new DateOnly(2026, 10, 24));

        var saved = (await _world.Items.GetAsync(item.Id))!;
        Assert.Equal("Sam recommended", saved.Notes);
        Assert.Equal(new DateOnly(2026, 10, 24), saved.SnoozedUntil);

        await _world.Items.SetSnoozeAsync(item.Id, null);
        Assert.Null((await _world.Items.GetAsync(item.Id))!.SnoozedUntil);
    }

    [Fact]
    public async Task Adding_through_the_service_checks_straight_away()
    {
        _world.Tmdb.On("movie/1078605", Samples.WeaponsMovie);

        var (item, outcome, availability) = await _world.Watchlist.AddAsync(1078605, MediaTypes.Movie, "weapons");

        Assert.Equal(AddOutcome.Added, outcome);
        Assert.Equal(Statuses.Available, item.Status);   // digital in the US since 9 Sep 2025
        Assert.True(availability.IsAvailable);
        var stored = JsonSerializer.Deserialize<Availability>(item.AvailabilityJson!, WatchlistService.Json)!;
        Assert.Equal("US", stored.FirstHome!.Country);
    }

    [Fact]
    public async Task Daily_message_count_starts_at_midnight_uk_time()
    {
        // 22:30 UTC on 17 Oct is 23:30 in London (BST): still the 17th.
        // 23:30 UTC is 00:30 on the 18th, so only the second message counts as today.
        _world.Time.SetUtcNow(new DateTimeOffset(2026, 10, 17, 22, 30, 0, TimeSpan.Zero));
        await _world.Messages.AddAsync("c1", MessageStore.User, "yesterday");
        _world.Time.SetUtcNow(new DateTimeOffset(2026, 10, 17, 23, 30, 0, TimeSpan.Zero));
        await _world.Messages.AddAsync("c1", MessageStore.User, "today");
        await _world.Messages.AddAsync("c1", MessageStore.Assistant, "reply");

        Assert.Equal(1, await _world.Messages.CountUserMessagesSinceAsync(_world.Watchlist.StartOfToday));
    }
}
