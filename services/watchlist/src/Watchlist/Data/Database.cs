using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Watchlist.Data;

/// <summary>The SQLite file. Each call opens a short-lived connection; SQLite pools them.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Migrate()
    {
        using var db = Open();
        db.Execute("PRAGMA journal_mode = WAL;");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS items (
                id                INTEGER PRIMARY KEY,
                tmdb_id           INTEGER NOT NULL,
                media_type        TEXT    NOT NULL,
                title             TEXT    NOT NULL,
                year              INTEGER,
                poster_path       TEXT,
                overview          TEXT,
                status            TEXT    NOT NULL,
                snoozed_until     TEXT,
                notes             TEXT,
                added_text        TEXT,
                added_at          TEXT    NOT NULL,
                updated_at        TEXT    NOT NULL,
                last_checked_at   TEXT,
                availability_json TEXT,
                UNIQUE (tmdb_id, media_type)
            );

            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY,
                role       TEXT NOT NULL,
                text       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS alerts (
                id         INTEGER PRIMARY KEY,
                item_id    INTEGER NOT NULL REFERENCES items (id) ON DELETE CASCADE,
                kind       TEXT NOT NULL,
                text       TEXT NOT NULL,
                created_at TEXT NOT NULL,
                sent_at    TEXT
            );
            CREATE INDEX IF NOT EXISTS alerts_pending ON alerts (sent_at);

            CREATE TABLE IF NOT EXISTS push_subscriptions (
                id         INTEGER PRIMARY KEY,
                endpoint   TEXT NOT NULL UNIQUE,
                p256dh     TEXT NOT NULL,
                auth       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        // Added after the first deploy.
        AddColumn(db, "items", "download_state", "TEXT");
        AddColumn(db, "items", "radarr_id", "INTEGER");
        AddColumn(db, "messages", "conversation", "TEXT");
        db.Execute("CREATE INDEX IF NOT EXISTS messages_conversation ON messages (conversation, id);");
    }

    private static void AddColumn(SqliteConnection db, string table, string column, string type)
    {
        var columns = db.Query<string>($"SELECT name FROM pragma_table_info('{table}')");
        if (!columns.Contains(column))
            db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture);
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value) =>
            parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public override DateOnly Parse(object value) =>
            DateOnly.ParseExact((string)value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
