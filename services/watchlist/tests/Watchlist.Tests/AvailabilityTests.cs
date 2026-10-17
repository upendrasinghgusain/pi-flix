using System.Text.Json;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

public class AvailabilityTests
{
    private static TitleDetails Parse(string json, string type) =>
        TmdbClient.ParseDetails(JsonDocument.Parse(json).RootElement, type);

    [Fact]
    public void Film_out_digitally_abroad_is_available_and_leads_with_the_earliest_home_release()
    {
        var a = Availability.From(Parse(Samples.WeaponsMovie, MediaTypes.Movie), new DateOnly(2025, 10, 1));

        Assert.True(a.IsAvailable);
        Assert.Equal(new Release("US", new DateOnly(2025, 9, 9), "digital"), a.FirstHome);
        Assert.Equal(new Release("GB", new DateOnly(2025, 10, 20), "digital"), a.UkHome);
        Assert.Equal(new DateOnly(2025, 8, 8), a.UkCinema!.Date);
        Assert.Empty(a.Uk);
        var us = Assert.Single(a.Elsewhere);
        Assert.Equal("US", us.Country);
        Assert.Equal(["rent", "buy"], us.Offers.Select(o => o.Category));
    }

    [Fact]
    public void Film_only_in_cinemas_is_waiting()
    {
        var json = """
            {"id": 1, "title": "New Film", "release_date": "2026-10-10",
             "release_dates": {"results": [
               {"iso_3166_1": "GB", "release_dates": [
                 {"type": 3, "release_date": "2026-10-10T00:00:00.000Z"},
                 {"type": 4, "release_date": "2026-12-01T00:00:00.000Z"}]}]},
             "watch/providers": {"results": {}}}
            """;
        var a = Availability.From(Parse(json, MediaTypes.Movie), new DateOnly(2026, 10, 17));

        Assert.False(a.IsAvailable);
        Assert.Equal(new DateOnly(2026, 12, 1), a.FirstHome!.Date);
        Assert.Equal("cinema", a.FirstCinema!.Kind);
    }

    [Fact]
    public void Film_with_any_offer_is_available_even_without_dates()
    {
        var json = """
            {"id": 1, "title": "Old Film", "release_date": "1980-01-01",
             "watch/providers": {"results": {"GB": {"flatrate": [{"provider_id": 8, "provider_name": "Netflix"}]}}}}
            """;
        var a = Availability.From(Parse(json, MediaTypes.Movie), new DateOnly(2026, 10, 17));

        Assert.True(a.IsAvailable);
        Assert.Equal(new Offer("flatrate", "Netflix", 8), Assert.Single(a.Uk));
    }

    [Fact]
    public void Film_with_no_information_is_waiting()
    {
        var a = Availability.From(Parse("""{"id": 1, "title": "Announced", "release_date": ""}""", MediaTypes.Movie), new DateOnly(2026, 10, 17));

        Assert.False(a.IsAvailable);
        Assert.Null(a.FirstHome);
    }

    [Fact]
    public void Show_that_has_aired_is_available_with_its_next_episode()
    {
        var a = Availability.From(Parse(Samples.SeveranceTv, MediaTypes.Tv), new DateOnly(2026, 10, 17));

        Assert.True(a.IsAvailable);
        Assert.Equal(new Release("US", new DateOnly(2022, 2, 17), "aired"), a.FirstHome);
        Assert.Equal(new NextEpisode(new DateOnly(2027, 1, 14), 3, 1, "Hello"), a.NextEpisode);
        Assert.Equal("Returning Series", a.ShowStatus);
        Assert.Equal("Apple TV+", Assert.Single(a.Uk).Provider);
    }

    [Fact]
    public void Show_not_yet_aired_is_waiting()
    {
        var json = """{"id": 2, "name": "Coming Soon", "first_air_date": "2027-03-01", "origin_country": ["GB"]}""";
        var a = Availability.From(Parse(json, MediaTypes.Tv), new DateOnly(2026, 10, 17));

        Assert.False(a.IsAvailable);
        Assert.Equal(new Release("GB", new DateOnly(2027, 3, 1), "aired"), a.FirstHome);
    }

    [Fact]
    public void Duplicate_offers_are_listed_once()
    {
        var json = """
            {"id": 1, "title": "X", "watch/providers": {"results": {"GB": {
              "rent": [{"provider_id": 2, "provider_name": "Apple TV"}, {"provider_id": 2, "provider_name": "Apple TV"}]}}}}
            """;
        var a = Availability.From(Parse(json, MediaTypes.Movie), new DateOnly(2026, 10, 17));

        Assert.Single(a.Uk);
    }
}
