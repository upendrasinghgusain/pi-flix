using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchlist.Data;
using Watchlist.Tmdb;

namespace Watchlist;

/// <summary>Adding titles and checking them, shared by the agent, the API and (later) the daily checker.</summary>
public sealed class WatchlistService(ItemStore items, TmdbClient tmdb, TimeProvider time, IOptions<WatchlistOptions> options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);

    public DateTimeOffset StartOfToday
    {
        get
        {
            var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone);
            var midnight = local.Date;
            return new DateTimeOffset(midnight, _zone.GetUtcOffset(midnight));
        }
    }

    /// <summary>Saves the title, then checks it straight away so the first answer already says where it's showing.</summary>
    public async Task<(Item Item, AddOutcome Outcome, Availability Availability)> AddAsync(
        int tmdbId, string mediaType, string? addedText, CancellationToken ct = default)
    {
        var details = await tmdb.GetTitleAsync(tmdbId, mediaType, ct);
        var (item, outcome) = await items.AddAsync(details.Ref, addedText);
        var availability = await SaveCheckAsync(item.Id, details);
        return ((await items.GetAsync(item.Id))!, outcome, availability);
    }

    /// <summary>The "wrong title" fix: points the item at another title and checks it.</summary>
    public async Task<Item?> ReplaceMatchAsync(long id, int tmdbId, string mediaType, CancellationToken ct = default)
    {
        var details = await tmdb.GetTitleAsync(tmdbId, mediaType, ct);
        if (!await items.ReplaceMatchAsync(id, details.Ref))
            return null;
        await SaveCheckAsync(id, details);
        return await items.GetAsync(id);
    }

    public async Task<Item?> CheckAsync(long id, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id);
        if (item is null)
            return null;
        var details = await tmdb.GetTitleAsync(item.TmdbId, item.MediaType, ct);
        await SaveCheckAsync(id, details);
        return await items.GetAsync(id);
    }

    public async Task<Availability> WhereToWatchAsync(int tmdbId, string mediaType, CancellationToken ct = default) =>
        Availability.From(await tmdb.GetTitleAsync(tmdbId, mediaType, ct), Today);

    private async Task<Availability> SaveCheckAsync(long id, TitleDetails details)
    {
        var availability = Availability.From(details, Today);
        await items.SaveCheckAsync(id, JsonSerializer.Serialize(availability, Json), availability.IsAvailable);
        return availability;
    }
}
