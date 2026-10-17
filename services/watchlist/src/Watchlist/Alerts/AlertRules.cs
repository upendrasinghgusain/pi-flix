using System.Globalization;
using Watchlist.Tmdb;

namespace Watchlist.Alerts;

/// <summary>Who to alert about what. From the "Alerts" config section, or data/settings.json on the Pi.</summary>
public sealed class AlertOptions
{
    /// <summary>
    /// UK services you pay for, as TMDb names them, e.g. "Netflix", "Amazon Prime Video", "Disney Plus".
    /// A service matches any provider whose name starts with it, ignoring case, spaces and punctuation.
    /// </summary>
    public List<string> MyServices { get; set; } = [];

    /// <summary>Whether free UK services (iPlayer, ITVX, Channel 4...) count as yours.</summary>
    public bool FreeServicesCount { get; set; } = true;

    /// <summary>When the daily check runs, UK time.</summary>
    public TimeOnly CheckAt { get; set; } = new(6, 0);

    /// <summary>Alerts found between these times wait until QuietUntil.</summary>
    public TimeOnly QuietFrom { get; set; } = new(22, 0);
    public TimeOnly QuietUntil { get; set; } = new(8, 0);

    public bool IsQuiet(TimeOnly now) =>
        QuietFrom <= QuietUntil
            ? now >= QuietFrom && now < QuietUntil
            : now >= QuietFrom || now < QuietUntil;
}

public static class AlertKinds
{
    public const string FirstHome = "first_home";
    public const string DateAnnounced = "date_announced";
    public const string DateMoved = "date_moved";
    public const string UkDate = "uk_date";
    public const string InCinemas = "in_cinemas";
    public const string UkRentBuy = "uk_rent_buy";
    public const string OnService = "on_service";
    public const string LeftService = "left_service";
    public const string NewSeason = "new_season";
    public const string Downloaded = "downloaded";
}

public sealed record Alert(string Kind, string Text);

/// <summary>
/// Compares two checks of the same title and says what's worth telling you. Only changes
/// count: the same result two days running says nothing.
/// </summary>
public static class AlertRules
{
    /// <param name="previous">The last check's result.</param>
    /// <param name="previousDate">The day of the last check, so dates that passed since then can be spotted.</param>
    /// <param name="alreadyToldOutAtHome">The "out at home" alert is sent only once per title.</param>
    public static IReadOnlyList<Alert> Compare(
        string mediaType, Availability previous, DateOnly previousDate, Availability current, DateOnly today,
        AlertOptions options, bool alreadyToldOutAtHome)
    {
        var alerts = new List<Alert>();

        // The one that matters most: watchable at home somewhere for the first time.
        if (current.IsAvailable && !previous.IsAvailable && !alreadyToldOutAtHome)
            alerts.Add(new Alert(AlertKinds.FirstHome, OutAtHome(mediaType, current, today)));

        if (!current.IsAvailable)
        {
            // Home release dates appearing or moving.
            if (current.FirstHome is { } home && home.Date > today)
            {
                if (previous.FirstHome is null)
                    alerts.Add(new Alert(AlertKinds.DateAnnounced, $"📅 {Capital(Kind(home))} in {Country(home.Country)}: {Day(home.Date, today)}"));
                else if (previous.FirstHome.Date != home.Date)
                    alerts.Add(new Alert(AlertKinds.DateMoved, $"📅 {Capital(Kind(home))} in {Country(home.Country)} moved to {Day(home.Date, today)} (was {Day(previous.FirstHome.Date, today)})"));
            }
            if (current.UkHome is { } uk && uk.Date > today && uk != current.FirstHome && previous.UkHome?.Date != uk.Date)
                alerts.Add(new Alert(AlertKinds.UkDate, $"📅 {Capital(Kind(uk))} in the UK: {Day(uk.Date, today)}"));

            // A cinema release that came round since the last check.
            var cinema = current.UkCinema ?? current.FirstCinema;
            if (cinema is not null && cinema.Date <= today && cinema.Date > previousDate)
                alerts.Add(new Alert(AlertKinds.InCinemas, $"🎬 In cinemas in {Country(cinema.Country)} from {Day(cinema.Date, today)}"));
        }

        // UK rent or buy appearing, if there was none before.
        var rentBuy = current.Uk.Where(o => o.Category is "rent" or "buy").Select(o => o.Provider).Distinct().ToList();
        if (rentBuy.Count > 0 && !previous.Uk.Any(o => o.Category is "rent" or "buy"))
            alerts.Add(new Alert(AlertKinds.UkRentBuy, $"💷 To rent or buy in the UK: {string.Join(", ", rentBuy)}"));

        // Your services, arriving and leaving.
        var mineNow = Mine(current, options);
        var mineBefore = Mine(previous, options);
        foreach (var provider in mineNow.Except(mineBefore, StringComparer.OrdinalIgnoreCase))
            alerts.Add(new Alert(AlertKinds.OnService, $"📺 Now on {provider}"));
        foreach (var provider in mineBefore.Except(mineNow, StringComparer.OrdinalIgnoreCase))
            alerts.Add(new Alert(AlertKinds.LeftService, $"👋 No longer on {provider}"));

        // A new season being scheduled.
        if (mediaType == MediaTypes.Tv && current.NextEpisode is { Episode: 1, AirDate: { } premiere } next
            && previous.NextEpisode?.Season != next.Season)
            alerts.Add(new Alert(AlertKinds.NewSeason, $"🆕 Season {next.Season} starts {Day(premiere, today)}"));

        // When it first becomes available, "out at home" already says where; don't repeat the services.
        if (alerts.Any(a => a.Kind == AlertKinds.FirstHome))
            alerts.RemoveAll(a => a.Kind is AlertKinds.UkRentBuy);

        return alerts;
    }

    /// <summary>The UK streaming services that count as yours, by provider name.</summary>
    public static IReadOnlyList<string> Mine(Availability a, AlertOptions options) =>
        a.Uk.Where(o => o.Category == "flatrate" && IsMine(o.Provider, options)
                        || o.Category is "free" or "ads" && (options.FreeServicesCount || IsMine(o.Provider, options)))
            .Select(o => o.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsMine(string provider, AlertOptions options)
    {
        var name = Normalise(provider);
        return options.MyServices.Any(s => Normalise(s) is { Length: > 0 } mine && name.StartsWith(mine, StringComparison.Ordinal));
    }

    private static string OutAtHome(string mediaType, Availability a, DateOnly today)
    {
        var parts = new List<string>();
        if (a.FirstHome is { } home && home.Date <= today)
            parts.Add(mediaType == MediaTypes.Tv
                ? $"🌍 Out: first aired in {Country(home.Country)} {Day(home.Date, today)}"
                : $"🌍 Out at home: {Kind(home)} in {Country(home.Country)} since {Day(home.Date, today)}");
        else if (a.Elsewhere.FirstOrDefault() is { } first)
            parts.Add($"🌍 Out at home in {Country(first.Country)}: {string.Join(", ", first.Offers.Take(3).Select(Describe))}");
        else
            parts.Add("🌍 Out at home");

        if (a.Uk.Count > 0)
            parts.Add($"UK: {string.Join(", ", a.Uk.Take(4).Select(Describe))}");
        return string.Join(" · ", parts);
    }

    private static string Describe(Offer o) => o.Category switch
    {
        "flatrate" => o.Provider,
        "free" or "ads" => $"{o.Provider} (free)",
        _ => $"{o.Provider} ({o.Category})",
    };

    private static string Kind(Release r) => r.Kind switch
    {
        "digital" => "digital release",
        "disc" => "disc release",
        "tv" => "TV premiere",
        "aired" => "first episode",
        _ => r.Kind,
    };

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Day(DateOnly d, DateOnly today) =>
        d.ToString(d.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

    internal static string Country(string code)
    {
        switch (code)
        {
            case "": return "one country";
            case "GB": return "the UK";
            case "US": return "the US";
        }
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }

    private static string Normalise(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
