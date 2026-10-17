using Watchlist.Alerts;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

public class AlertRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly AlertOptions Options = new() { MyServices = ["Netflix", "Disney Plus"], FreeServicesCount = true };

    private static Availability Nothing() => new(false, null, null, null, null, [], [], null, null, null);

    private static IReadOnlyList<Alert> Compare(Availability before, Availability after, string type = MediaTypes.Movie, bool told = false, AlertOptions? options = null) =>
        AlertRules.Compare(type, before, Yesterday, after, Today, options ?? Options, told);

    [Fact]
    public void Same_result_twice_says_nothing()
    {
        var a = Nothing() with { FirstHome = new Release("US", new DateOnly(2026, 12, 1), "digital") };
        Assert.Empty(Compare(a, a));
    }

    [Fact]
    public void Out_at_home_abroad_leads_with_the_country_and_includes_the_UK()
    {
        var before = Nothing() with { FirstHome = new Release("US", Today, "digital") };
        var after = before with
        {
            IsAvailable = true,
            Uk = [new Offer("rent", "Prime Video", 10)],
            Elsewhere = [new CountryOffers("US", [new Offer("rent", "Apple TV", 2)])],
        };

        var alert = Assert.Single(Compare(before, after));

        Assert.Equal(AlertKinds.FirstHome, alert.Kind);
        Assert.Equal("🌍 Out at home: digital release in the US since 17 Oct · UK: Prime Video (rent)", alert.Text);
    }

    [Fact]
    public void Out_at_home_is_only_said_once_per_title()
    {
        var after = Nothing() with { IsAvailable = true, FirstHome = new Release("US", Today, "digital") };
        Assert.Empty(Compare(Nothing(), after, told: true));
    }

    [Fact]
    public void Out_at_home_by_offer_alone_names_where()
    {
        var after = Nothing() with
        {
            IsAvailable = true,
            Elsewhere = [new CountryOffers("DE", [new Offer("flatrate", "Netflix", 8)])],
        };

        Assert.Equal("🌍 Out at home in Germany: Netflix", Assert.Single(Compare(Nothing(), after)).Text);
    }

    [Fact]
    public void A_home_date_appearing_then_moving()
    {
        var dated = Nothing() with { FirstHome = new Release("US", new DateOnly(2026, 12, 1), "digital") };
        var moved = dated with { FirstHome = new Release("US", new DateOnly(2026, 12, 8), "digital") };

        Assert.Equal("📅 Digital release in the US: 1 Dec", Assert.Single(Compare(Nothing(), dated)).Text);
        Assert.Equal("📅 Digital release in the US moved to 8 Dec (was 1 Dec)", Assert.Single(Compare(dated, moved)).Text);
    }

    [Fact]
    public void A_later_UK_date_is_mentioned_separately()
    {
        var after = Nothing() with
        {
            FirstHome = new Release("US", new DateOnly(2026, 12, 1), "digital"),
            UkHome = new Release("GB", new DateOnly(2027, 1, 5), "disc"),
        };

        var texts = Compare(Nothing(), after).Select(a => a.Text).ToList();

        Assert.Equal(["📅 Digital release in the US: 1 Dec", "📅 Disc release in the UK: 5 Jan 2027"], texts);
    }

    [Fact]
    public void In_cinemas_once_the_date_passes_and_not_again()
    {
        var before = Nothing() with { UkCinema = new Release("GB", Today, "cinema") };

        Assert.Equal("🎬 In cinemas in the UK from 17 Oct", Assert.Single(Compare(before, before)).Text);
        Assert.Empty(AlertRules.Compare(MediaTypes.Movie, before, Today, before, Today.AddDays(1), Options, false));
    }

    [Fact]
    public void Your_service_arriving_and_leaving()
    {
        var on = Nothing() with { IsAvailable = true, Uk = [new Offer("flatrate", "Netflix Standard with Ads", 1796), new Offer("flatrate", "Paramount Plus", 531)] };
        var off = on with { Uk = [new Offer("flatrate", "Paramount Plus", 531)] };

        Assert.Equal("📺 Now on Netflix Standard with Ads", Assert.Single(Compare(off, on)).Text);
        Assert.Equal("👋 No longer on Netflix Standard with Ads", Assert.Single(Compare(on, off)).Text);
    }

    [Fact]
    public void Services_you_dont_pay_for_are_ignored()
    {
        var before = Nothing() with { IsAvailable = true };
        var after = before with { Uk = [new Offer("flatrate", "Paramount Plus", 531)] };
        Assert.Empty(Compare(before, after));
    }

    [Fact]
    public void Free_services_count_only_when_switched_on()
    {
        var before = Nothing() with { IsAvailable = true };
        var after = before with { Uk = [new Offer("free", "BBC iPlayer", 38)] };

        Assert.Equal("📺 Now on BBC iPlayer", Assert.Single(Compare(before, after)).Text);
        Assert.Empty(Compare(before, after, options: new AlertOptions { FreeServicesCount = false }));
    }

    [Fact]
    public void UK_rent_or_buy_appearing()
    {
        var before = Nothing() with { IsAvailable = true };
        var after = before with { Uk = [new Offer("rent", "Prime Video", 10), new Offer("buy", "Prime Video", 10), new Offer("buy", "Apple TV", 2)] };

        Assert.Equal("💷 To rent or buy in the UK: Prime Video, Apple TV", Assert.Single(Compare(before, after)).Text);
    }

    [Fact]
    public void New_season_scheduled()
    {
        var before = Nothing() with { IsAvailable = true };
        var after = before with { NextEpisode = new NextEpisode(new DateOnly(2027, 1, 14), 3, 1, "Hello") };

        Assert.Equal("🆕 Season 3 starts 14 Jan 2027", Assert.Single(Compare(before, after, MediaTypes.Tv)).Text);
        Assert.Empty(Compare(after, after, MediaTypes.Tv));
    }

    [Theory]
    [InlineData("21:59", false)]
    [InlineData("22:00", true)]
    [InlineData("03:00", true)]
    [InlineData("07:59", true)]
    [InlineData("08:00", false)]
    public void Quiet_hours_wrap_past_midnight(string time, bool quiet)
    {
        Assert.Equal(quiet, new AlertOptions().IsQuiet(TimeOnly.Parse(time)));
    }
}
