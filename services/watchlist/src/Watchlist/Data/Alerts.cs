using Dapper;

namespace Watchlist.Data;

public sealed class AlertRow
{
    public long Id { get; set; }
    public long ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Alerts found by checks. They wait here (unsent) through quiet hours.</summary>
public sealed class AlertStore(Database database, TimeProvider time)
{
    public async Task AddAsync(long itemId, string kind, string text)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO alerts (item_id, kind, text, created_at) VALUES (@itemId, @kind, @text, @now)",
            new { itemId, kind, text, now = time.GetUtcNow() });
    }

    public async Task<bool> HasKindAsync(long itemId, string kind)
    {
        using var db = database.Open();
        return await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM alerts WHERE item_id = @itemId AND kind = @kind)", new { itemId, kind });
    }

    public async Task<IReadOnlyList<AlertRow>> PendingAsync()
    {
        using var db = database.Open();
        return (await db.QueryAsync<AlertRow>("SELECT * FROM alerts WHERE sent_at IS NULL ORDER BY id")).AsList();
    }

    public async Task MarkSentAsync(IEnumerable<long> ids)
    {
        using var db = database.Open();
        await db.ExecuteAsync("UPDATE alerts SET sent_at = @now WHERE id IN @ids",
            new { ids = ids.ToArray(), now = time.GetUtcNow() });
    }

    public async Task<IReadOnlyList<AlertRow>> RecentAsync(int count)
    {
        using var db = database.Open();
        return (await db.QueryAsync<AlertRow>("SELECT * FROM alerts ORDER BY id DESC LIMIT @count", new { count })).AsList();
    }
}

/// <summary>Small values the app keeps for itself: the push signing keys and when the daily check last ran.</summary>
public sealed class MetaStore(Database database)
{
    public async Task<string?> GetAsync(string key)
    {
        using var db = database.Open();
        return await db.ExecuteScalarAsync<string?>("SELECT value FROM meta WHERE key = @key", new { key });
    }

    public async Task SetAsync(string key, string value)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key, value });
    }
}

public sealed class PushSubscriptionRow
{
    public long Id { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The devices that receive alerts: one row per browser that said yes.</summary>
public sealed class PushStore(Database database, TimeProvider time)
{
    public async Task SaveAsync(string endpoint, string p256dh, string auth)
    {
        using var db = database.Open();
        await db.ExecuteAsync("""
            INSERT INTO push_subscriptions (endpoint, p256dh, auth, created_at) VALUES (@endpoint, @p256dh, @auth, @now)
            ON CONFLICT (endpoint) DO UPDATE SET p256dh = excluded.p256dh, auth = excluded.auth
            """, new { endpoint, p256dh, auth, now = time.GetUtcNow() });
    }

    public async Task DeleteAsync(string endpoint)
    {
        using var db = database.Open();
        await db.ExecuteAsync("DELETE FROM push_subscriptions WHERE endpoint = @endpoint", new { endpoint });
    }

    public async Task<IReadOnlyList<PushSubscriptionRow>> AllAsync()
    {
        using var db = database.Open();
        return (await db.QueryAsync<PushSubscriptionRow>("SELECT * FROM push_subscriptions")).AsList();
    }
}
