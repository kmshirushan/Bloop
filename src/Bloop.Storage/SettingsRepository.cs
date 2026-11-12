using System.Globalization;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;

namespace Bloop.Storage;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly DatabaseService _db;

    public SettingsRepository(DatabaseService db)
    {
        _db = db;
    }

    public (double Left, double Top, bool HasSavedPosition) LoadWindowPosition()
    {
        var leftStr = GetSetting("window.left");
        var topStr = GetSetting("window.top");

        if (double.TryParse(leftStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
            double.TryParse(topStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var top))
        {
            return (left, top, true);
        }

        return (0, 0, false);
    }

    public void SaveWindowPosition(double left, double top)
    {
        SetSetting("window.left", left.ToString(CultureInfo.InvariantCulture));
        SetSetting("window.top", top.ToString(CultureInfo.InvariantCulture));
    }

    public PersistedMascotPlacement LoadMascotPlacement()
    {
        var (left, top, hasPos) = LoadWindowPosition();
        var edgeStr = GetSetting("window.docked_edge");
        var monitorId = GetSetting("window.monitor_id") ?? "primary";
        var alwaysOnTopStr = GetSetting("window.always_on_top");

        var edge = Enum.TryParse<DockingEdge>(edgeStr, ignoreCase: true, out var parsedEdge)
            ? parsedEdge
            : DockingEdge.None;
        bool alwaysOnTop = !bool.TryParse(alwaysOnTopStr, out var parsedTop) || parsedTop;

        return new PersistedMascotPlacement(left, top, hasPos, edge, monitorId, alwaysOnTop);
    }

    public void SaveMascotPlacement(double left, double top, DockingEdge edge, string monitorId, bool alwaysOnTop = true)
    {
        SaveWindowPosition(left, top);
        SetSetting("window.docked_edge", edge.ToString());
        SetSetting("window.monitor_id", string.IsNullOrWhiteSpace(monitorId) ? "primary" : monitorId);
        SetSetting("window.always_on_top", alwaysOnTop.ToString(CultureInfo.InvariantCulture));
    }

    public MovementSettings LoadMovementSettings()
    {
        var defaults = MovementSettings.Default;

        bool autoEnabled = ParseBool(GetSetting("movement.autonomous_enabled"), defaults.AutonomousMovementEnabled);
        bool dockEnabled = ParseBool(GetSetting("movement.edge_docking_enabled"), defaults.EdgeDockingEnabled);
        double threshold = ParseDouble(GetSetting("movement.edge_threshold_dip"), defaults.EdgeDetectionThresholdDip, 10.0, 120.0);
        double visRatio = ParseDouble(GetSetting("movement.docked_visible_ratio"), defaults.DockedVisibleRatio, 0.35, 0.90);
        double speed = ParseDouble(GetSetting("movement.speed_multiplier"), defaults.MovementSpeedMultiplier, 0.5, 2.0);
        double minInterval = ParseDouble(GetSetting("movement.min_interval_minutes"), defaults.MinAutonomyIntervalMinutes, 0.5, 60.0);
        double maxInterval = ParseDouble(GetSetting("movement.max_interval_minutes"), defaults.MaxAutonomyIntervalMinutes, minInterval + 0.5, 120.0);
        double cooldown = ParseDouble(GetSetting("movement.cooldown_minutes"), defaults.AutonomyCooldownMinutes, 0.5, 60.0);

        return new MovementSettings
        {
            AutonomousMovementEnabled = autoEnabled,
            EdgeDockingEnabled = dockEnabled,
            EdgeDetectionThresholdDip = threshold,
            DockedVisibleRatio = visRatio,
            MovementSpeedMultiplier = speed,
            MinAutonomyIntervalMinutes = minInterval,
            MaxAutonomyIntervalMinutes = maxInterval,
            AutonomyCooldownMinutes = cooldown
        };
    }

    public void SaveMovementSettings(MovementSettings settings)
    {
        SetSetting("movement.autonomous_enabled", settings.AutonomousMovementEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.edge_docking_enabled", settings.EdgeDockingEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.edge_threshold_dip", settings.EdgeDetectionThresholdDip.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.docked_visible_ratio", settings.DockedVisibleRatio.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.speed_multiplier", settings.MovementSpeedMultiplier.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.min_interval_minutes", settings.MinAutonomyIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.max_interval_minutes", settings.MaxAutonomyIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting("movement.cooldown_minutes", settings.AutonomyCooldownMinutes.ToString(CultureInfo.InvariantCulture));
    }

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var val) ? val : fallback;

    private static double ParseDouble(string? raw, double fallback, double min, double max) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? Math.Clamp(val, min, max)
            : fallback;

    private string? GetSetting(string key)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM user_settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetSetting(string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
