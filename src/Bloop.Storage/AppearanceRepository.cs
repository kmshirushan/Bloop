using System.Text.Json;
using Bloop.Core.Appearance;
using Microsoft.Data.Sqlite;

namespace Bloop.Storage;

public sealed class AppearanceRepository : IAppearanceRepository
{
    private readonly DatabaseService _database;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AppearanceRepository(DatabaseService database)
    {
        _database = database;
        EnsureDefaultProfileSeeded();
    }

    private void EnsureDefaultProfileSeeded()
    {
        lock (_gate)
        {
            using var conn = _database.CreateConnection();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(1) FROM appearance_profiles;";
            long count = Convert.ToInt64(countCmd.ExecuteScalar());
            if (count > 0)
            {
                return;
            }

            var defaultProfile = AppearanceDefaults.CreateDefaultProfile();
            UpsertProfileInternal(conn, defaultProfile, setActive: true);
        }
    }

    public IReadOnlyList<AppearanceProfile> GetProfiles()
    {
        lock (_gate)
        {
            using var conn = _database.CreateConnection();
            var list = ReadAllProfilesInternal(conn);
            if (list.Count == 0)
            {
                var seeded = AppearanceDefaults.CreateDefaultProfile();
                UpsertProfileInternal(conn, seeded, setActive: true);
                return [seeded];
            }

            if (!list.Any(p => p.IsActive))
            {
                var first = list[0] with { IsActive = true };
                UpsertProfileInternal(conn, first, setActive: true);
                list[0] = first;
            }

            return list;
        }
    }

    public AppearanceProfile GetActiveProfile()
    {
        var profiles = GetProfiles();
        return profiles.FirstOrDefault(p => p.IsActive) ?? profiles[0];
    }

    public AppearanceProfile? GetProfileById(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var profiles = GetProfiles();
        return profiles.FirstOrDefault(p => string.Equals(p.ProfileId, profileId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public AppearanceProfile SaveProfile(AppearanceProfile profile, bool setActive = false)
    {
        lock (_gate)
        {
            using var conn = _database.CreateConnection();
            var normalized = AppearanceValidator.Normalize(profile) with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return UpsertProfileInternal(conn, normalized, setActive || normalized.IsActive);
        }
    }

    public AppearanceProfile CreateProfile(
        string name,
        CharacterAppearance? character = null,
        PanelAppearance? panel = null,
        bool setActive = true)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AppearanceProfile(
            ProfileId: $"profile-{Guid.NewGuid():N}",
            Name: string.IsNullOrWhiteSpace(name) ? "Custom Profile" : name.Trim(),
            SchemaVersion: AppearanceDefaults.CurrentSchemaVersion,
            Character: character ?? AppearanceDefaults.DefaultCharacter,
            Panel: panel ?? AppearanceDefaults.DefaultPanel,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            IsActive: setActive
        );
        return SaveProfile(profile, setActive);
    }

    public AppearanceProfile DuplicateProfile(string sourceProfileId, string newName, bool setActive = true)
    {
        var source = GetProfileById(sourceProfileId) ?? GetActiveProfile();
        var duplicateName = string.IsNullOrWhiteSpace(newName)
            ? $"{source.Name} (Copy)"
            : newName.Trim();

        return CreateProfile(duplicateName, source.Character, source.Panel, setActive);
    }

    public AppearanceProfile RenameProfile(string profileId, string newName)
    {
        var existing = GetProfileById(profileId) ?? GetActiveProfile();
        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(newName) ? existing.Name : newName.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return SaveProfile(updated, setActive: existing.IsActive);
    }

    public bool DeleteProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        lock (_gate)
        {
            using var conn = _database.CreateConnection();
            var profiles = ReadAllProfilesInternal(conn);
            if (profiles.Count <= 1)
            {
                // Always keep at least one appearance profile
                return false;
            }

            var target = profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileId, profileId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM appearance_profiles WHERE profile_id = $id;";
                delCmd.Parameters.AddWithValue("$id", target.ProfileId);
                delCmd.ExecuteNonQuery();
            }

            if (target.IsActive)
            {
                var fallback = profiles.First(p => !string.Equals(p.ProfileId, target.ProfileId, StringComparison.OrdinalIgnoreCase));
                UpsertProfileInternal(conn, fallback with { IsActive = true }, setActive: true);
            }

            return true;
        }
    }

    public AppearanceProfile SetActiveProfile(string profileId)
    {
        var target = GetProfileById(profileId) ?? GetActiveProfile();
        return SaveProfile(target with { IsActive = true }, setActive: true);
    }

    public AppearanceProfile ResetProfileToDefaults(string profileId)
    {
        var existing = GetProfileById(profileId) ?? GetActiveProfile();
        var reset = existing with
        {
            Character = AppearanceDefaults.DefaultCharacter,
            Panel = AppearanceDefaults.DefaultPanel,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return SaveProfile(reset, setActive: existing.IsActive);
    }

    private static List<AppearanceProfile> ReadAllProfilesInternal(SqliteConnection conn)
    {
        var list = new List<AppearanceProfile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT profile_id, profile_name, schema_version, character_json, panel_json, created_at_utc, updated_at_utc, is_active
            FROM appearance_profiles
            ORDER BY CASE WHEN profile_id = 'classic-blue' THEN 0 ELSE 1 END, created_at_utc ASC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var schemaVer = reader.GetInt32(2);
            var charJson = reader.GetString(3);
            var panelJson = reader.GetString(4);
            var createdStr = reader.GetString(5);
            var updatedStr = reader.GetString(6);
            var isActive = reader.GetInt32(7) != 0;

            CharacterAppearance character;
            try
            {
                character = AppearanceValidator.Normalize(
                    JsonSerializer.Deserialize<CharacterAppearance>(charJson, JsonOpts));
            }
            catch
            {
                character = AppearanceDefaults.DefaultCharacter;
            }

            PanelAppearance panel;
            try
            {
                panel = AppearanceValidator.Normalize(
                    JsonSerializer.Deserialize<PanelAppearance>(panelJson, JsonOpts),
                    character.AccentColor);
            }
            catch
            {
                panel = AppearanceDefaults.DefaultPanel;
            }

            var createdAt = DateTimeOffset.TryParse(createdStr, out var c) ? c : DateTimeOffset.UtcNow;
            var updatedAt = DateTimeOffset.TryParse(updatedStr, out var u) ? u : createdAt;

            list.Add(AppearanceValidator.Normalize(new AppearanceProfile(
                ProfileId: id,
                Name: name,
                SchemaVersion: schemaVer,
                Character: character,
                Panel: panel,
                CreatedAtUtc: createdAt,
                UpdatedAtUtc: updatedAt,
                IsActive: isActive
            )));
        }

        return list;
    }

    private static AppearanceProfile UpsertProfileInternal(SqliteConnection conn, AppearanceProfile profile, bool setActive)
    {
        var normalized = AppearanceValidator.Normalize(profile) with
        {
            IsActive = setActive
        };

        using var tx = conn.BeginTransaction();
        if (setActive)
        {
            using var clearActive = conn.CreateCommand();
            clearActive.Transaction = tx;
            clearActive.CommandText = "UPDATE appearance_profiles SET is_active = 0 WHERE is_active != 0;";
            clearActive.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO appearance_profiles (
                    profile_id, profile_name, schema_version, character_json, panel_json, created_at_utc, updated_at_utc, is_active
                ) VALUES (
                    $id, $name, $ver, $charJson, $panelJson, $created, $updated, $active
                )
                ON CONFLICT(profile_id) DO UPDATE SET
                    profile_name = excluded.profile_name,
                    schema_version = excluded.schema_version,
                    character_json = excluded.character_json,
                    panel_json = excluded.panel_json,
                    updated_at_utc = excluded.updated_at_utc,
                    is_active = excluded.is_active;
                """;
            cmd.Parameters.AddWithValue("$id", normalized.ProfileId);
            cmd.Parameters.AddWithValue("$name", normalized.Name);
            cmd.Parameters.AddWithValue("$ver", normalized.SchemaVersion);
            cmd.Parameters.AddWithValue("$charJson", JsonSerializer.Serialize(normalized.Character, JsonOpts));
            cmd.Parameters.AddWithValue("$panelJson", JsonSerializer.Serialize(normalized.Panel, JsonOpts));
            cmd.Parameters.AddWithValue("$created", normalized.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", normalized.UpdatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$active", normalized.IsActive ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return normalized;
    }
}

