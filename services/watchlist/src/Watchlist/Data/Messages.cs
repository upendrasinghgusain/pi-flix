using Dapper;

namespace Watchlist.Data;

public sealed class Message
{
    public long Id { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Conversation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The chat history. Each opening of the app is a new conversation, and the agent only sees the
/// last few messages of the current one. Older messages stay for the daily limit's count.
/// </summary>
public sealed class MessageStore(Database database, TimeProvider time)
{
    public const string User = "user";
    public const string Assistant = "assistant";

    public async Task AddAsync(string conversation, string role, string text)
    {
        using var db = database.Open();
        await db.ExecuteAsync("INSERT INTO messages (conversation, role, text, created_at) VALUES (@conversation, @role, @text, @now)",
            new { conversation, role, text, now = time.GetUtcNow() });
    }

    /// <summary>The conversation's most recent messages, oldest first.</summary>
    public async Task<IReadOnlyList<Message>> RecentAsync(string conversation, int count)
    {
        using var db = database.Open();
        var latest = await db.QueryAsync<Message>(
            "SELECT * FROM messages WHERE conversation = @conversation ORDER BY id DESC LIMIT @count", new { conversation, count });
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
