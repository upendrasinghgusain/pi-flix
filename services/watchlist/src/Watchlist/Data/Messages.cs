using Dapper;

namespace Watchlist.Data;

public sealed class Message
{
    public long Id { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The chat history: shown in the app, and the agent sees the last few.</summary>
public sealed class MessageStore(Database database, TimeProvider time)
{
    public const string User = "user";
    public const string Assistant = "assistant";

    public async Task AddAsync(string role, string text)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO messages (role, text, created_at) VALUES (@role, @text, @now)",
            new { role, text, now = time.GetUtcNow() });
    }

    /// <summary>The most recent messages, oldest first.</summary>
    public async Task<IReadOnlyList<Message>> RecentAsync(int count)
    {
        using var db = database.Open();
        var latest = await db.QueryAsync<Message>(
            "SELECT * FROM messages ORDER BY id DESC LIMIT @count", new { count });
        return latest.Reverse().ToList();
    }

    public async Task<int> CountUserMessagesSinceAsync(DateTimeOffset since)
    {
        // Timestamps are stored as UTC ISO strings, so they compare correctly as text.
        using var db = database.Open();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messages WHERE role = 'user' AND created_at >= @since",
            new { since = since.ToUniversalTime() });
    }
}
