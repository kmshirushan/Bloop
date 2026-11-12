using Microsoft.Data.Sqlite;

namespace Bloop.Storage;

/// <summary>
/// Manages the local SQLite database for persisting companion settings and state.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;

    public DatabaseService(string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "Bloop");
            Directory.CreateDirectory(folder);
            dbPath = Path.Combine(folder, "bloop.db");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeSchema();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS user_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }
}

