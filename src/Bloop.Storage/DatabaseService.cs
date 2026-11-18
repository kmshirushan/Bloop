using Microsoft.Data.Sqlite;

namespace Bloop.Storage;

/// <summary>
/// Manages the single local SQLite database (bloop.db) and applies idempotent schema migrations.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;

    public int SchemaVersion { get; private set; }

    public DatabaseService(string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = !string.IsNullOrWhiteSpace(appData)
                ? Path.Combine(appData, "Bloop")
                : Path.Combine(AppContext.BaseDirectory, "data");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
                folder = AppContext.BaseDirectory;
            }
            dbPath = Path.Combine(folder, "bloop.db");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        InitializeAndMigrateSchema();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeAndMigrateSchema()
    {
        using var conn = CreateConnection();
        using (var initCmd = conn.CreateCommand())
        {
            initCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS user_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            initCmd.ExecuteNonQuery();
        }

        ApplyMigrationIfNeeded(conn, 1, """
            CREATE TABLE IF NOT EXISTS user_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        ApplyMigrationIfNeeded(conn, 2, """
            CREATE TABLE IF NOT EXISTS notifications (
                id TEXT PRIMARY KEY,
                os_notification_id INTEGER NOT NULL,
                app_user_model_id TEXT NOT NULL,
                app_display_name TEXT NOT NULL,
                app_logo_png BLOB NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                is_read INTEGER NOT NULL DEFAULT 0,
                is_dismissed_locally INTEGER NOT NULL DEFAULT 0,
                is_present_in_os INTEGER NOT NULL DEFAULT 1,
                importance INTEGER NOT NULL DEFAULT 1,
                has_explicit_urgency INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_notifications_inbox
                ON notifications (is_dismissed_locally, created_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_notifications_os_present
                ON notifications (is_present_in_os, os_notification_id);

            CREATE TABLE IF NOT EXISTS notification_app_rules (
                app_key TEXT PRIMARY KEY,
                importance INTEGER NOT NULL
            );
            """);

        ApplyMigrationIfNeeded(conn, 3, """
            CREATE TABLE IF NOT EXISTS appearance_profiles (
                profile_id TEXT PRIMARY KEY,
                profile_name TEXT NOT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1,
                character_json TEXT NOT NULL,
                panel_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_appearance_profiles_active
                ON appearance_profiles (is_active);
            """);

        using var verCmd = conn.CreateCommand();
        verCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        SchemaVersion = Convert.ToInt32(verCmd.ExecuteScalar());
    }

    private static void ApplyMigrationIfNeeded(SqliteConnection conn, int version, string sql)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM schema_migrations WHERE version = $v;";
        checkCmd.Parameters.AddWithValue("$v", version);
        long exists = Convert.ToInt64(checkCmd.ExecuteScalar());
        if (exists > 0)
        {
            return;
        }

        using var tx = conn.BeginTransaction();
        using (var migCmd = conn.CreateCommand())
        {
            migCmd.Transaction = tx;
            migCmd.CommandText = sql;
            migCmd.ExecuteNonQuery();
        }

        using (var recCmd = conn.CreateCommand())
        {
            recCmd.Transaction = tx;
            recCmd.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES ($v, $ts);";
            recCmd.Parameters.AddWithValue("$v", version);
            recCmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            recCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Dispose()
    {
    }
}
