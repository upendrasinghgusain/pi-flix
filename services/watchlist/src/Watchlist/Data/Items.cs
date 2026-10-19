using Dapper;
using Watchlist.Tmdb;

namespace Watchlist.Data;

public static class Statuses
{
    public const string Waiting = "waiting";
    public const string Available = "available";
    public const string InLibrary = "in_library";
    public const string Watched = "watched";
    public const string Removed = "removed";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Waiting, Available, InLibrary, Watched, Removed };

    /// <summary>Statuses the release checker keeps looking at.</summary>
    public static bool IsTracked(string status) => status is Waiting or Available;
}

/// <summary>
/// Where a film is in Radarr, or a show in Sonarr. Null: not sent (or the app is off). Imported
/// titles also move to the in_library status; for a show, that's once any episode is in.
/// </summary>
public static class DownloadStates
{
    /// <summary>Radarr or Sonarr couldn't be reached; sent again on the next tick.</summary>
    public const string Pending = "pending";
    public const string Searching = "searching";
    public const string Downloading = "downloading";
    public const string Imported = "imported";

    public static bool IsInProgress(string? state) => state is Searching or Downloading;
}

public sealed class Item
{
    public long Id { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    public string Status { get; set; } = Statuses.Waiting;
    public DateOnly? SnoozedUntil { get; set; }
    public string? Notes { get; set; }
    public string? AddedText { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? AvailabilityJson { get; set; }
    public string? DownloadState { get; set; }
    public int? RadarrId { get; set; }
    public int? SonarrId { get; set; }
}

public enum AddOutcome { Added, Revived, AlreadyThere }

/// <summary>
/// The watchlist table. Parameter names must match the SQL's case exactly:
/// Microsoft.Data.Sqlite doesn't match them case-insensitively.
/// </summary>
public sealed class ItemStore(Database database, TimeProvider time)
{
    public async Task<Item?> GetAsync(long id)
    {
        using var db = database.Open();
        return await db.QuerySingleOrDefaultAsync<Item>("SELECT * FROM items WHERE id = @id", new { id });
    }

    public async Task<Item?> FindAsync(int tmdbId, string mediaType)
    {
        using var db = database.Open();
        return await db.QuerySingleOrDefaultAsync<Item>(
            "SELECT * FROM items WHERE tmdb_id = @tmdbId AND media_type = @mediaType", new { tmdbId, mediaType });
    }

    /// <summary>Items with the given status, or every item except removed ones.</summary>
    public async Task<IReadOnlyList<Item>> ListAsync(string? status = null)
    {
        using var db = database.Open();
        var sql = status is null
            ? "SELECT * FROM items WHERE status <> 'removed' ORDER BY added_at DESC"
            : "SELECT * FROM items WHERE status = @status ORDER BY added_at DESC";
        return (await db.QueryAsync<Item>(sql, new { status })).AsList();
    }

    /// <summary>
    /// Saves a title. One already on the list is left alone; one that was removed or
    /// watched comes back as a fresh entry.
    /// </summary>
    public async Task<(Item Item, AddOutcome Outcome)> AddAsync(TitleRef title, string? addedText)
    {
        var existing = await FindAsync(title.TmdbId, title.MediaType);
        if (existing is not null && existing.Status is not (Statuses.Removed or Statuses.Watched))
            return (existing, AddOutcome.AlreadyThere);

        var now = time.GetUtcNow();
        using var db = database.Open();
        if (existing is not null)
        {
            await db.ExecuteAsync("""
                UPDATE items SET status = 'waiting', snoozed_until = NULL, added_text = @addedText,
                                 added_at = @now, updated_at = @now
                WHERE id = @id
                """, new { id = existing.Id, addedText, now });
            return ((await GetAsync(existing.Id))!, AddOutcome.Revived);
        }

        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO items (tmdb_id, media_type, title, year, poster_path, overview, status,
                               added_text, added_at, updated_at)
            VALUES (@TmdbId, @MediaType, @Title, @Year, @PosterPath, @Overview, 'waiting',
                    @addedText, @now, @now)
            RETURNING id
            """, new { title.TmdbId, title.MediaType, title.Title, title.Year, title.PosterPath, title.Overview, addedText, now });
        return ((await GetAsync(id))!, AddOutcome.Added);
    }

    /// <summary>Points an item at a different title (the "wrong title" fix). Clears what was found for the old one.</summary>
    public async Task<bool> ReplaceMatchAsync(long id, TitleRef title)
    {
        var clash = await FindAsync(title.TmdbId, title.MediaType);
        if (clash is not null && clash.Id != id)
            return false;

        using var db = database.Open();
        await db.ExecuteAsync("""
            UPDATE items SET tmdb_id = @TmdbId, media_type = @MediaType, title = @Title, year = @Year,
                             poster_path = @PosterPath, overview = @Overview, status = 'waiting',
                             availability_json = NULL, last_checked_at = NULL,
                             download_state = NULL, radarr_id = NULL, sonarr_id = NULL, updated_at = @now
            WHERE id = @id
            """, new { id, title.TmdbId, title.MediaType, title.Title, title.Year, title.PosterPath, title.Overview, now = time.GetUtcNow() });
        return true;
    }

    public async Task SetStatusAsync(long id, string status)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE items SET status = @status, updated_at = @now WHERE id = @id",
            new { id, status, now = time.GetUtcNow() });
    }

    public async Task SetNotesAsync(long id, string? notes)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE items SET notes = @notes, updated_at = @now WHERE id = @id",
            new { id, notes, now = time.GetUtcNow() });
    }

    public async Task SetSnoozeAsync(long id, DateOnly? until)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE items SET snoozed_until = @until, updated_at = @now WHERE id = @id",
            new { id, until, now = time.GetUtcNow() });
    }

    /// <summary>Records where the film is in Radarr. Null clears it.</summary>
    public async Task SetDownloadAsync(long id, string? state, int? radarrId)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE items SET download_state = @state, radarr_id = @radarrId, updated_at = @now WHERE id = @id",
            new { id, state, radarrId, now = time.GetUtcNow() });
    }

    /// <summary>Records where the show is in Sonarr. Null clears it.</summary>
    public async Task SetTvDownloadAsync(long id, string? state, int? sonarrId)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE items SET download_state = @state, sonarr_id = @sonarrId, updated_at = @now WHERE id = @id",
            new { id, state, sonarrId, now = time.GetUtcNow() });
    }

    public async Task<Item?> FindBySonarrIdAsync(int sonarrId)
    {
        using var db = database.Open();
        return await db.QueryFirstOrDefaultAsync<Item>(
            "SELECT * FROM items WHERE sonarr_id = @sonarrId AND media_type = 'tv'", new { sonarrId });
    }

    /// <summary>Items of one media type with any of these download states, oldest first.</summary>
    public async Task<IReadOnlyList<Item>> WithDownloadStateAsync(string mediaType, params string[] states)
    {
        using var db = database.Open();
        return (await db.QueryAsync<Item>(
            "SELECT * FROM items WHERE media_type = @mediaType AND download_state IN @states AND status <> 'removed' ORDER BY id",
            new { mediaType, states })).AsList();
    }

    /// <summary>Records a release check, and moves between waiting and available when the result says so.</summary>
    public async Task SaveCheckAsync(long id, string availabilityJson, bool available)
    {
        using var db = database.Open();
        await db.ExecuteAsync("""
            UPDATE items SET availability_json = @availabilityJson, last_checked_at = @now, updated_at = @now,
                             status = CASE WHEN status IN ('waiting', 'available')
                                           THEN @newStatus ELSE status END
            WHERE id = @id
            """, new { id, availabilityJson, now = time.GetUtcNow(), newStatus = available ? Statuses.Available : Statuses.Waiting });
    }
}
