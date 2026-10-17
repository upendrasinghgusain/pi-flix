using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Tmdb;

namespace Watchlist;

/// <summary>Adding titles and checking them, shared by the agent, the API and the daily checker.</summary>
public sealed class WatchlistService(
    ItemStore items,
    AlertStore alerts,
    TmdbClient tmdb,
    TimeProvider time,
    IOptions<WatchlistOptions> options,
    IOptionsMonitor<AlertOptions> alertOptions)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone);

    public DateOnly Today => DateOnly.FromDateTime(LocalNow.DateTime);

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _zone).DateTime);

    /// <summary>A UK wall-clock time on a given day, as an instant.</summary>
    public DateTimeOffset LocalInstant(DateOnly day, TimeOnly at)
    {
        var local = day.ToDateTime(at);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local));
    }

    public DateTimeOffset StartOfToday => LocalInstant(Today, TimeOnly.MinValue);

    /// <summary>
    /// Saves the title, then checks it straight away so the first answer already says where it's
    /// showing. That first check sends no alerts: the chat reply has just said it all.
    /// </summary>
    public async Task<(Item Item, AddOutcome Outcome, Availability Availability)> AddAsync(
        int tmdbId, string mediaType, string? addedText, CancellationToken ct = default)
    {
        var details = await tmdb.GetTitleAsync(tmdbId, mediaType, ct);
        var (item, outcome) = await items.AddAsync(details.Ref, addedText);
        var availability = Availability.From(details, Today);
        await Save(item.Id, availability);
        return ((await items.GetAsync(item.Id))!, outcome, availability);
    }

    /// <summary>The "wrong title" fix: points the item at another title and checks it, without alerts.</summary>
    public async Task<Item?> ReplaceMatchAsync(long id, int tmdbId, string mediaType, CancellationToken ct = default)
    {
        var details = await tmdb.GetTitleAsync(tmdbId, mediaType, ct);
        if (!await items.ReplaceMatchAsync(id, details.Ref))
            return null;
        await Save(id, Availability.From(details, Today));
        return await items.GetAsync(id);
    }

    /// <summary>
    /// Checks one title, compares the result with the last check and saves any alerts. Used by
    /// the daily checker and the "Check now" button alike. Snoozed titles are checked but stay quiet.
    /// </summary>
    public async Task<(Item Item, IReadOnlyList<Alert> Alerts)?> CheckAsync(long id, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id);
        if (item is null)
            return null;

        var details = await tmdb.GetTitleAsync(item.TmdbId, item.MediaType, ct);
        var current = Availability.From(details, Today);

        IReadOnlyList<Alert> found = [];
        if (item.AvailabilityJson is not null && item.LastCheckedAt is { } lastChecked
            && JsonSerializer.Deserialize<Availability>(item.AvailabilityJson, Json) is { } previous)
        {
            found = AlertRules.Compare(
                item.MediaType, previous, LocalDate(lastChecked), current, Today, alertOptions.CurrentValue,
                alreadyToldOutAtHome: await alerts.HasKindAsync(id, AlertKinds.FirstHome));
        }

        var snoozed = item.SnoozedUntil is { } until && until > Today;
        if (!snoozed && Statuses.IsTracked(item.Status))
        {
            foreach (var alert in found)
                await alerts.AddAsync(id, alert.Kind, alert.Text);
        }
        else
        {
            found = [];
        }

        await Save(id, current);
        return ((await items.GetAsync(id))!, found);
    }

    public async Task<Availability> WhereToWatchAsync(int tmdbId, string mediaType, CancellationToken ct = default) =>
        Availability.From(await tmdb.GetTitleAsync(tmdbId, mediaType, ct), Today);

    private Task Save(long id, Availability availability) =>
        items.SaveCheckAsync(id, JsonSerializer.Serialize(availability, Json), availability.IsAvailable);
}
