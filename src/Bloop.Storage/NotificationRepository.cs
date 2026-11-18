using System.Globalization;
using Bloop.Core.Notifications;
using Microsoft.Data.Sqlite;

namespace Bloop.Storage;

/// <summary>
/// SQLite-backed repository for persistent notification history, transactional snapshot synchronization,
/// panel placement geometry, and notification settings.
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly DatabaseService _db;
    private readonly object _sync = new();
    private readonly Dictionary<string, NotificationItem> _memoryFallback = new(StringComparer.Ordinal);

    public NotificationRepository(DatabaseService db)
    {
        _db = db;
    }

    public NotificationSyncResult SynchronizeSnapshot(
        IReadOnlyList<NotificationItem> osSnapshot,
        NotificationSyncTrigger trigger,
        bool isInitialStartupImport,
        NotificationSettings settings,
        Func<NotificationItem, NotificationImportance> importanceClassifier,
        long durationMs = 0)
    {
        lock (_sync)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var deduplicatedSnapshot = new Dictionary<string, NotificationItem>(StringComparer.Ordinal);
            foreach (var raw in osSnapshot)
            {
                string detId = string.IsNullOrWhiteSpace(raw.Id)
                    ? NotificationItem.BuildDeterministicId(raw.Source.AppUserModelId, raw.OsNotificationId, raw.CreatedAtUtc)
                    : raw.Id;
                if (!deduplicatedSnapshot.ContainsKey(detId))
                {
                    deduplicatedSnapshot[detId] = raw with { Id = detId, IsPresentInOs = true };
                }
            }

            if (!settings.StoreLocalHistoryEnabled)
            {
                return SynchronizeInMemoryOnly(
                    deduplicatedSnapshot,
                    trigger,
                    isInitialStartupImport,
                    settings,
                    importanceClassifier,
                    durationMs,
                    nowUtc);
            }

            var addedItems = new List<NotificationItem>();
            var removedOsIds = new List<uint>();

            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            // 1. Load existing IDs and currently-present-in-OS map
            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            var presentInOsMap = new Dictionary<string, uint>(StringComparer.Ordinal);

            using (var existingCmd = conn.CreateCommand())
            {
                existingCmd.Transaction = tx;
                existingCmd.CommandText = "SELECT id, os_notification_id, is_present_in_os FROM notifications;";
                using var reader = existingCmd.ExecuteReader();
                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    uint osId = (uint)reader.GetInt64(1);
                    bool inOs = reader.GetInt32(2) != 0;
                    existingIds.Add(id);
                    if (inOs)
                    {
                        presentInOsMap[id] = osId;
                    }
                }
            }

            // 2. Insert new notifications or mark existing ones present in OS (preserving is_read and is_dismissed_locally)
            foreach (var (id, incoming) in deduplicatedSnapshot)
            {
                if (!existingIds.Contains(id))
                {
                    var importance = importanceClassifier(incoming);
                    var toInsert = incoming with
                    {
                        Id = id,
                        Importance = importance,
                        ReceivedAtUtc = incoming.ReceivedAtUtc == default ? nowUtc : incoming.ReceivedAtUtc,
                        IsPresentInOs = true
                    };

                    using var insCmd = conn.CreateCommand();
                    insCmd.Transaction = tx;
                    insCmd.CommandText = """
                        INSERT INTO notifications (
                            id, os_notification_id, app_user_model_id, app_display_name, app_logo_png,
                            title, body, created_at_utc, received_at_utc,
                            is_read, is_dismissed_locally, is_present_in_os, importance, has_explicit_urgency
                        ) VALUES (
                            $id, $osId, $aumid, $appName, $logo,
                            $title, $body, $created, $received,
                            $isRead, $isDismissed, 1, $importance, $urgency
                        );
                        """;
                    insCmd.Parameters.AddWithValue("$id", toInsert.Id);
                    insCmd.Parameters.AddWithValue("$osId", (long)toInsert.OsNotificationId);
                    insCmd.Parameters.AddWithValue("$aumid", toInsert.Source.AppUserModelId);
                    insCmd.Parameters.AddWithValue("$appName", toInsert.Source.DisplayName);
                    insCmd.Parameters.AddWithValue("$logo", (object?)toInsert.Source.LogoPngBytes ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$title", toInsert.Title);
                    insCmd.Parameters.AddWithValue("$body", toInsert.Body);
                    insCmd.Parameters.AddWithValue("$created", toInsert.CreatedAtUtc.ToString("O"));
                    insCmd.Parameters.AddWithValue("$received", toInsert.ReceivedAtUtc.ToString("O"));
                    insCmd.Parameters.AddWithValue("$isRead", toInsert.IsRead ? 1 : 0);
                    insCmd.Parameters.AddWithValue("$isDismissed", toInsert.IsDismissedLocally ? 1 : 0);
                    insCmd.Parameters.AddWithValue("$importance", (int)toInsert.Importance);
                    insCmd.Parameters.AddWithValue("$urgency", toInsert.HasExplicitUrgencyHint ? 1 : 0);
                    insCmd.ExecuteNonQuery();

                    addedItems.Add(toInsert);
                    existingIds.Add(id);
                }
                else if (!presentInOsMap.ContainsKey(id))
                {
                    using var updCmd = conn.CreateCommand();
                    updCmd.Transaction = tx;
                    updCmd.CommandText = "UPDATE notifications SET is_present_in_os = 1 WHERE id = $id;";
                    updCmd.Parameters.AddWithValue("$id", id);
                    updCmd.ExecuteNonQuery();
                }
            }

            // 3. Mark notifications that disappeared from Windows as is_present_in_os = 0 (without deleting local history)
            foreach (var (id, osId) in presentInOsMap)
            {
                if (!deduplicatedSnapshot.ContainsKey(id))
                {
                    using var remCmd = conn.CreateCommand();
                    remCmd.Transaction = tx;
                    remCmd.CommandText = "UPDATE notifications SET is_present_in_os = 0 WHERE id = $id;";
                    remCmd.Parameters.AddWithValue("$id", id);
                    remCmd.ExecuteNonQuery();
                    removedOsIds.Add(osId);
                }
            }

            // 4. Enforce retention policy on non-OS-present rows
            ApplyRetentionPolicy(conn, tx, settings, nowUtc);

            // 5. Compute inbox counts
            int visibleCount = 0;
            int unreadCount = 0;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.Transaction = tx;
                countCmd.CommandText = """
                    SELECT
                        COUNT(1),
                        COALESCE(SUM(CASE WHEN is_read = 0 THEN 1 ELSE 0 END), 0)
                    FROM notifications
                    WHERE is_dismissed_locally = 0;
                    """;
                using var reader = countCmd.ExecuteReader();
                if (reader.Read())
                {
                    visibleCount = reader.GetInt32(0);
                    unreadCount = reader.GetInt32(1);
                }
            }

            tx.Commit();

            return new NotificationSyncResult(
                trigger,
                addedItems,
                removedOsIds,
                deduplicatedSnapshot.Count,
                visibleCount,
                unreadCount,
                durationMs,
                nowUtc,
                isInitialStartupImport);
        }
    }

    private NotificationSyncResult SynchronizeInMemoryOnly(
        Dictionary<string, NotificationItem> deduplicatedSnapshot,
        NotificationSyncTrigger trigger,
        bool isInitialStartupImport,
        NotificationSettings settings,
        Func<NotificationItem, NotificationImportance> importanceClassifier,
        long durationMs,
        DateTimeOffset nowUtc)
    {
        var added = new List<NotificationItem>();
        var removedOsIds = new List<uint>();

        foreach (var (id, incoming) in deduplicatedSnapshot)
        {
            if (!_memoryFallback.TryGetValue(id, out var existing))
            {
                var item = incoming with
                {
                    Importance = importanceClassifier(incoming),
                    ReceivedAtUtc = incoming.ReceivedAtUtc == default ? nowUtc : incoming.ReceivedAtUtc,
                    IsPresentInOs = true
                };
                _memoryFallback[id] = item;
                added.Add(item);
            }
            else if (!existing.IsPresentInOs)
            {
                _memoryFallback[id] = existing with { IsPresentInOs = true };
            }
        }

        foreach (var (id, existing) in _memoryFallback.ToList())
        {
            if (existing.IsPresentInOs && !deduplicatedSnapshot.ContainsKey(id))
            {
                _memoryFallback[id] = existing with { IsPresentInOs = false };
                removedOsIds.Add(existing.OsNotificationId);
            }
        }

        var visible = _memoryFallback.Values.Where(x => !x.IsDismissedLocally).ToList();
        return new NotificationSyncResult(
            trigger,
            added,
            removedOsIds,
            deduplicatedSnapshot.Count,
            visible.Count,
            visible.Count(x => !x.IsRead),
            durationMs,
            nowUtc,
            isInitialStartupImport);
    }

    private static void ApplyRetentionPolicy(
        SqliteConnection conn,
        SqliteTransaction tx,
        NotificationSettings settings,
        DateTimeOffset nowUtc)
    {
        int retentionDays = Math.Clamp(settings.RetentionDays, 1, 90);
        var cutoffIso = nowUtc.AddDays(-retentionDays).ToString("O");

        using (var ageCmd = conn.CreateCommand())
        {
            ageCmd.Transaction = tx;
            ageCmd.CommandText = """
                DELETE FROM notifications
                WHERE is_present_in_os = 0
                  AND received_at_utc < $cutoff;
                """;
            ageCmd.Parameters.AddWithValue("$cutoff", cutoffIso);
            ageCmd.ExecuteNonQuery();
        }

        int maxStored = Math.Clamp(settings.MaxStoredNotifications, 20, 2000);
        using (var capCmd = conn.CreateCommand())
        {
            capCmd.Transaction = tx;
            capCmd.CommandText = """
                DELETE FROM notifications
                WHERE id IN (
                    SELECT id FROM notifications
                    WHERE is_present_in_os = 0
                    ORDER BY created_at_utc DESC
                    LIMIT -1 OFFSET $maxStored
                );
                """;
            capCmd.Parameters.AddWithValue("$maxStored", maxStored);
            capCmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<NotificationItem> GetInboxNotifications(bool includeLocallyDismissed = false)
    {
        lock (_sync)
        {
            var list = new List<NotificationItem>();
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeLocallyDismissed
                ? """
                  SELECT id, os_notification_id, app_user_model_id, app_display_name, app_logo_png,
                         title, body, created_at_utc, received_at_utc,
                         is_read, is_dismissed_locally, is_present_in_os, importance, has_explicit_urgency
                  FROM notifications
                  ORDER BY created_at_utc DESC;
                  """
                : """
                  SELECT id, os_notification_id, app_user_model_id, app_display_name, app_logo_png,
                         title, body, created_at_utc, received_at_utc,
                         is_read, is_dismissed_locally, is_present_in_os, importance, has_explicit_urgency
                  FROM notifications
                  WHERE is_dismissed_locally = 0
                  ORDER BY created_at_utc DESC;
                  """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                byte[]? logo = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
                var created = DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c)
                    ? c
                    : DateTimeOffset.UtcNow;
                var received = DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r)
                    ? r
                    : created;

                list.Add(new NotificationItem
                {
                    Id = reader.GetString(0),
                    OsNotificationId = (uint)reader.GetInt64(1),
                    Source = new NotificationSource(reader.GetString(2), reader.GetString(3), logo),
                    Title = reader.GetString(5),
                    Body = reader.GetString(6),
                    CreatedAtUtc = created,
                    ReceivedAtUtc = received,
                    IsRead = reader.GetInt32(9) != 0,
                    IsDismissedLocally = reader.GetInt32(10) != 0,
                    IsPresentInOs = reader.GetInt32(11) != 0,
                    Importance = (NotificationImportance)reader.GetInt32(12),
                    HasExplicitUrgencyHint = reader.GetInt32(13) != 0
                });
            }

            if (list.Count == 0 && _memoryFallback.Count > 0)
            {
                return _memoryFallback.Values
                    .Where(x => includeLocallyDismissed || !x.IsDismissedLocally)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ToList();
            }

            return list;
        }
    }

    public int GetUnreadCount()
    {
        lock (_sync)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM notifications WHERE is_dismissed_locally = 0 AND is_read = 0;";
            int dbCount = Convert.ToInt32(cmd.ExecuteScalar());
            if (dbCount == 0 && _memoryFallback.Count > 0)
            {
                return _memoryFallback.Values.Count(x => !x.IsDismissedLocally && !x.IsRead);
            }
            return dbCount;
        }
    }

    public void MarkRead(string id, bool isRead = true)
    {
        lock (_sync)
        {
            if (_memoryFallback.TryGetValue(id, out var memItem))
            {
                _memoryFallback[id] = memItem with { IsRead = isRead };
            }

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_read = $read WHERE id = $id;";
            cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkAllRead()
    {
        lock (_sync)
        {
            foreach (var key in _memoryFallback.Keys.ToList())
            {
                _memoryFallback[key] = _memoryFallback[key] with { IsRead = true };
            }

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_read = 1 WHERE is_dismissed_locally = 0;";
            cmd.ExecuteNonQuery();
        }
    }

    public void DismissLocally(string id)
    {
        lock (_sync)
        {
            if (_memoryFallback.TryGetValue(id, out var memItem))
            {
                _memoryFallback[id] = memItem with { IsDismissedLocally = true, IsRead = true };
            }

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_dismissed_locally = 1, is_read = 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DismissAppGroupLocally(string appUserModelId)
    {
        lock (_sync)
        {
            foreach (var (k, v) in _memoryFallback.ToList())
            {
                if (string.Equals(v.Source.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    _memoryFallback[k] = v with { IsDismissedLocally = true, IsRead = true };
                }
            }

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_dismissed_locally = 1, is_read = 1 WHERE app_user_model_id = $aumid;";
            cmd.Parameters.AddWithValue("$aumid", appUserModelId);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkRemovedFromOs(uint osNotificationId)
    {
        lock (_sync)
        {
            foreach (var (k, v) in _memoryFallback.ToList())
            {
                if (v.OsNotificationId == osNotificationId)
                {
                    _memoryFallback[k] = v with { IsPresentInOs = false };
                }
            }

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_present_in_os = 0 WHERE os_notification_id = $osId;";
            cmd.Parameters.AddWithValue("$osId", (long)osNotificationId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearLocalHistory()
    {
        lock (_sync)
        {
            _memoryFallback.Clear();
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            // Delete all historical rows no longer in Windows Action Center
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM notifications WHERE is_present_in_os = 0;";
                delCmd.ExecuteNonQuery();
            }

            // Redact and locally dismiss rows still present in Windows so the next OS snapshot sync
            // does not re-import them as unread or re-trigger mascot reactions
            using (var tombCmd = conn.CreateCommand())
            {
                tombCmd.Transaction = tx;
                tombCmd.CommandText = """
                    UPDATE notifications
                    SET is_dismissed_locally = 1,
                        is_read = 1,
                        title = '',
                        body = '',
                        app_logo_png = NULL
                    WHERE is_present_in_os = 1;
                    """;
                tombCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public NotificationSettings LoadNotificationSettings()
    {
        var d = NotificationSettings.Default;
        using var conn = _db.CreateConnection();

        var appRules = new Dictionary<string, NotificationImportance>(StringComparer.OrdinalIgnoreCase);
        using (var ruleCmd = conn.CreateCommand())
        {
            ruleCmd.CommandText = "SELECT app_key, importance FROM notification_app_rules;";
            using var reader = ruleCmd.ExecuteReader();
            while (reader.Read())
            {
                appRules[reader.GetString(0)] = (NotificationImportance)reader.GetInt32(1);
            }
        }

        return new NotificationSettings
        {
            IntegrationEnabled = ParseBool(GetSetting(conn, "notification.integration_enabled"), d.IntegrationEnabled),
            ConsentExplained = ParseBool(GetSetting(conn, "notification.consent_explained"), d.ConsentExplained),
            StoreLocalHistoryEnabled = ParseBool(GetSetting(conn, "notification.store_history_enabled"), d.StoreLocalHistoryEnabled),
            RetentionDays = (int)ParseDouble(GetSetting(conn, "notification.retention_days"), d.RetentionDays, 1, 90),
            MaxStoredNotifications = (int)ParseDouble(GetSetting(conn, "notification.max_stored"), d.MaxStoredNotifications, 20, 2000),
            ReactionsEnabled = ParseBool(GetSetting(conn, "notification.reactions_enabled"), d.ReactionsEnabled),
            HighImportanceCenterEntranceEnabled = ParseBool(GetSetting(conn, "notification.center_entrance_enabled"), d.HighImportanceCenterEntranceEnabled),
            BatchWindowSeconds = ParseDouble(GetSetting(conn, "notification.batch_window_seconds"), d.BatchWindowSeconds, 0.5, 15.0),
            ReactionCooldownSeconds = ParseDouble(GetSetting(conn, "notification.reaction_cooldown_seconds"), d.ReactionCooldownSeconds, 1.0, 120.0),
            FastPollIntervalSeconds = ParseDouble(GetSetting(conn, "notification.fast_poll_seconds"), d.FastPollIntervalSeconds, 1.0, 30.0),
            FastPollWindowSeconds = ParseDouble(GetSetting(conn, "notification.fast_poll_window_seconds"), d.FastPollWindowSeconds, 5.0, 120.0),
            IdlePollIntervalSeconds = ParseDouble(GetSetting(conn, "notification.idle_poll_seconds"), d.IdlePollIntervalSeconds, 3.0, 120.0),
            HighPriorityKeywordsCsv = GetSetting(conn, "notification.high_priority_keywords") ?? d.HighPriorityKeywordsCsv,
            AppPriorityRules = appRules
        };
    }

    public void SaveNotificationSettings(NotificationSettings settings)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        SetSetting(conn, tx, "notification.integration_enabled", settings.IntegrationEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.consent_explained", settings.ConsentExplained.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.store_history_enabled", settings.StoreLocalHistoryEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.retention_days", settings.RetentionDays.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.max_stored", settings.MaxStoredNotifications.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.reactions_enabled", settings.ReactionsEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.center_entrance_enabled", settings.HighImportanceCenterEntranceEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.batch_window_seconds", settings.BatchWindowSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.reaction_cooldown_seconds", settings.ReactionCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.fast_poll_seconds", settings.FastPollIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.fast_poll_window_seconds", settings.FastPollWindowSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.idle_poll_seconds", settings.IdlePollIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification.high_priority_keywords", settings.HighPriorityKeywordsCsv ?? "");

        using (var clearRulesCmd = conn.CreateCommand())
        {
            clearRulesCmd.Transaction = tx;
            clearRulesCmd.CommandText = "DELETE FROM notification_app_rules;";
            clearRulesCmd.ExecuteNonQuery();
        }

        foreach (var (appKey, importance) in settings.AppPriorityRules)
        {
            if (string.IsNullOrWhiteSpace(appKey)) continue;
            using var insRuleCmd = conn.CreateCommand();
            insRuleCmd.Transaction = tx;
            insRuleCmd.CommandText = "INSERT INTO notification_app_rules (app_key, importance) VALUES ($k, $v);";
            insRuleCmd.Parameters.AddWithValue("$k", appKey.Trim());
            insRuleCmd.Parameters.AddWithValue("$v", (int)importance);
            insRuleCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public NotificationPanelPlacement LoadPanelPlacement()
    {
        var d = NotificationPanelPlacement.Default;
        using var conn = _db.CreateConnection();
        return new NotificationPanelPlacement(
            IsDetached: ParseBool(GetSetting(conn, "notification_panel.is_detached"), d.IsDetached),
            DetachedLeftDip: ParseDouble(GetSetting(conn, "notification_panel.left_dip"), d.DetachedLeftDip, -10000.0, 20000.0),
            DetachedTopDip: ParseDouble(GetSetting(conn, "notification_panel.top_dip"), d.DetachedTopDip, -10000.0, 20000.0),
            DetachedWidthDip: ParseDouble(GetSetting(conn, "notification_panel.width_dip"), d.DetachedWidthDip, 300.0, 900.0),
            DetachedHeightDip: ParseDouble(GetSetting(conn, "notification_panel.height_dip"), d.DetachedHeightDip, 320.0, 1000.0),
            MonitorId: GetSetting(conn, "notification_panel.monitor_id") ?? d.MonitorId
        );
    }

    public void SavePanelPlacement(NotificationPanelPlacement placement)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        SetSetting(conn, tx, "notification_panel.is_detached", placement.IsDetached.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification_panel.left_dip", placement.DetachedLeftDip.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification_panel.top_dip", placement.DetachedTopDip.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification_panel.width_dip", placement.DetachedWidthDip.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification_panel.height_dip", placement.DetachedHeightDip.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "notification_panel.monitor_id", string.IsNullOrWhiteSpace(placement.MonitorId) ? "primary" : placement.MonitorId);
        tx.Commit();
    }

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var val) ? val : fallback;

    private static double ParseDouble(string? raw, double fallback, double min, double max) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? Math.Clamp(val, min, max)
            : fallback;

    private static string? GetSetting(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM user_settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetSetting(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO user_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}

