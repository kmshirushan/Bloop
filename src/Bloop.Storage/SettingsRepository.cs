using System.Globalization;
using System.Text.Json;
using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;
using Bloop.Core.Productivity;

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
        double minInterval = ParseDouble(GetSetting("movement.min_interval_minutes"), defaults.MinAutonomyIntervalMinutes, 0.05, 60.0);
        double maxInterval = ParseDouble(GetSetting("movement.max_interval_minutes"), defaults.MaxAutonomyIntervalMinutes, minInterval + 0.05, 120.0);
        double cooldown = ParseDouble(GetSetting("movement.cooldown_minutes"), defaults.AutonomyCooldownMinutes, 0.05, 60.0);

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

    public BehaviourSettings LoadBehaviourSettings()
    {
        var d = BehaviourSettings.Default;
        return new BehaviourSettings
        {
            GlobalBehavioursPaused = ParseBool(GetSetting("behaviour.global_paused"), d.GlobalBehavioursPaused),
            AutonomousEdgeHoppingEnabled = ParseBool(GetSetting("behaviour.edge_hopping_enabled"), d.AutonomousEdgeHoppingEnabled),
            WindowBoundaryExplorationEnabled = ParseBool(GetSetting("behaviour.window_exploration_enabled"), d.WindowBoundaryExplorationEnabled),
            CornerExplorationEnabled = ParseBool(GetSetting("behaviour.corner_exploration_enabled"), d.CornerExplorationEnabled),
            EdgeBalancingEnabled = ParseBool(GetSetting("behaviour.edge_balancing_enabled"), d.EdgeBalancingEnabled),
            PeekabooEnabled = ParseBool(GetSetting("behaviour.peekaboo_enabled"), d.PeekabooEnabled),
            CursorChasingEnabled = ParseBool(GetSetting("behaviour.cursor_chasing_enabled"), d.CursorChasingEnabled),
            AttentionSeekingEnabled = ParseBool(GetSetting("behaviour.attention_seeking_enabled"), d.AttentionSeekingEnabled),
            SleepModeEnabled = ParseBool(GetSetting("behaviour.sleep_mode_enabled"), d.SleepModeEnabled),
            WelcomeBackReactionEnabled = ParseBool(GetSetting("behaviour.welcome_back_enabled"), d.WelcomeBackReactionEnabled),
            RandomClickReactionsEnabled = ParseBool(GetSetting("behaviour.random_click_enabled"), d.RandomClickReactionsEnabled),
            RepeatedClickAnnoyanceEnabled = ParseBool(GetSetting("behaviour.annoyance_enabled"), d.RepeatedClickAnnoyanceEnabled),
            NotificationReactionsEnabled = ParseBool(GetSetting("behaviour.notification_reactions_enabled"), d.NotificationReactionsEnabled),
            AnnoyanceMildThreshold = (int)ParseDouble(GetSetting("behaviour.annoyance_mild_threshold"), d.AnnoyanceMildThreshold, 2, 10),
            AnnoyancePeakThreshold = (int)ParseDouble(GetSetting("behaviour.annoyance_peak_threshold"), d.AnnoyancePeakThreshold, 3, 15),
            AnnoyanceWindowSeconds = ParseDouble(GetSetting("behaviour.annoyance_window_seconds"), d.AnnoyanceWindowSeconds, 1.0, 15.0),
            AnnoyanceRecoverySeconds = ParseDouble(GetSetting("behaviour.annoyance_recovery_seconds"), d.AnnoyanceRecoverySeconds, 1.5, 30.0),
            PeekabooCooldownMinutes = ParseDouble(GetSetting("behaviour.peekaboo_cooldown_minutes"), d.PeekabooCooldownMinutes, 0.1, 30.0),
            AttentionSeekMinutes = ParseDouble(GetSetting("behaviour.attention_seek_minutes"), d.AttentionSeekMinutes, 0.1, 30.0),
            SleepInactivityMinutes = ParseDouble(GetSetting("behaviour.sleep_inactivity_minutes"), d.SleepInactivityMinutes, 0.2, 60.0),
            CursorChaseCooldownSeconds = ParseDouble(GetSetting("behaviour.cursor_chase_cooldown_seconds"), d.CursorChaseCooldownSeconds, 5.0, 300.0)
        };
    }

    public void SaveBehaviourSettings(BehaviourSettings settings)
    {
        SetSetting("behaviour.global_paused", settings.GlobalBehavioursPaused.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.edge_hopping_enabled", settings.AutonomousEdgeHoppingEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.window_exploration_enabled", settings.WindowBoundaryExplorationEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.corner_exploration_enabled", settings.CornerExplorationEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.edge_balancing_enabled", settings.EdgeBalancingEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.peekaboo_enabled", settings.PeekabooEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.cursor_chasing_enabled", settings.CursorChasingEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.attention_seeking_enabled", settings.AttentionSeekingEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.sleep_mode_enabled", settings.SleepModeEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.welcome_back_enabled", settings.WelcomeBackReactionEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.random_click_enabled", settings.RandomClickReactionsEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.annoyance_enabled", settings.RepeatedClickAnnoyanceEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.notification_reactions_enabled", settings.NotificationReactionsEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.annoyance_mild_threshold", settings.AnnoyanceMildThreshold.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.annoyance_peak_threshold", settings.AnnoyancePeakThreshold.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.annoyance_window_seconds", settings.AnnoyanceWindowSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.annoyance_recovery_seconds", settings.AnnoyanceRecoverySeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.peekaboo_cooldown_minutes", settings.PeekabooCooldownMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.attention_seek_minutes", settings.AttentionSeekMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.sleep_inactivity_minutes", settings.SleepInactivityMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting("behaviour.cursor_chase_cooldown_seconds", settings.CursorChaseCooldownSeconds.ToString(CultureInfo.InvariantCulture));
    }

    public AwarenessSettings LoadAwarenessSettings()
    {
        var d = AwarenessSettings.Default;
        var overrideStr = GetSetting("awareness.manual_idle_override");
        var manualIdleOverride = Enum.TryParse<ManualIdleOverride>(overrideStr, ignoreCase: true, out var parsedOverride)
            ? parsedOverride
            : d.ManualIdleOverride;

        var raw = new AwarenessSettings
        {
            IsEnabled = ParseBool(GetSetting("awareness.enabled"), d.IsEnabled),
            FullScreenDetectionEnabled = ParseBool(GetSetting("awareness.fullscreen_detection_enabled"), d.FullScreenDetectionEnabled),
            AutoHideOnFullScreen = ParseBool(GetSetting("awareness.auto_hide_fullscreen"), d.AutoHideOnFullScreen),
            FullScreenEnterDelayMs = (int)ParseDouble(GetSetting("awareness.fullscreen_enter_delay_ms"), d.FullScreenEnterDelayMs, 100, 3000),
            FullScreenRestoreDelayMs = (int)ParseDouble(GetSetting("awareness.fullscreen_restore_delay_ms"), d.FullScreenRestoreDelayMs, 200, 5000),
            FullScreenGeometryTolerancePx = (int)ParseDouble(GetSetting("awareness.fullscreen_tolerance_px"), d.FullScreenGeometryTolerancePx, 0, 16),
            PostRestoreSettlingSeconds = ParseDouble(GetSetting("awareness.post_restore_settling_seconds"), d.PostRestoreSettlingSeconds, 1.0, 30.0),
            CatchUpNotificationReactionAfterFullScreen = ParseBool(GetSetting("awareness.catch_up_notification_after_fullscreen"), d.CatchUpNotificationReactionAfterFullScreen),
            InactivityDetectionEnabled = ParseBool(GetSetting("awareness.inactivity_detection_enabled"), d.InactivityDetectionEnabled),
            AutoQuietModeEnabled = ParseBool(GetSetting("awareness.auto_quiet_enabled"), d.AutoQuietModeEnabled),
            AutoSleepEnabled = ParseBool(GetSetting("awareness.auto_sleep_enabled"), d.AutoSleepEnabled),
            SleepAfterSeconds = ParseDouble(GetSetting("awareness.sleep_after_seconds"), d.SleepAfterSeconds, 5.0, 3600.0),
            PriorityNotificationWakeEnabled = ParseBool(GetSetting("awareness.priority_notification_wake_enabled"), d.PriorityNotificationWakeEnabled),
            WelcomeBackReactionEnabled = ParseBool(GetSetting("awareness.welcome_back_enabled"), d.WelcomeBackReactionEnabled),
            QuietThresholdSeconds = ParseDouble(GetSetting("awareness.quiet_threshold_seconds"), d.QuietThresholdSeconds, 10.0, 1800.0),
            AwayThresholdSeconds = ParseDouble(GetSetting("awareness.away_threshold_seconds"), d.AwayThresholdSeconds, 25.0, 3600.0),
            SleepThresholdSeconds = ParseDouble(GetSetting("awareness.sleep_threshold_seconds"), d.SleepThresholdSeconds, 60.0, 7200.0),
            ManualIdleOverride = manualIdleOverride,
            ManualFocusModeEnabled = ParseBool(GetSetting("awareness.manual_focus_enabled"), d.ManualFocusModeEnabled),
            AutoFocusInferenceEnabled = ParseBool(GetSetting("awareness.auto_focus_enabled"), d.AutoFocusInferenceEnabled),
            AutoFocusConfidenceThreshold = ParseDouble(GetSetting("awareness.auto_focus_confidence"), d.AutoFocusConfidenceThreshold, 0.50, 0.95),
            SuppressRandomClickInFocus = ParseBool(GetSetting("awareness.suppress_random_click_in_focus"), d.SuppressRandomClickInFocus),
            SessionAwarenessEnabled = ParseBool(GetSetting("awareness.session_awareness_enabled"), d.SessionAwarenessEnabled),
            PowerAwarenessEnabled = ParseBool(GetSetting("awareness.power_awareness_enabled"), d.PowerAwarenessEnabled),
            FullScreenExcludedProcessesCsv = GetSetting("awareness.fullscreen_excluded_csv") ?? d.FullScreenExcludedProcessesCsv,
            AppRules = AwarenessValidator.ParseAppRules(GetSetting("awareness.app_rules_csv"))
        };

        return AwarenessValidator.Normalize(raw);
    }

    public void SaveAwarenessSettings(AwarenessSettings settings)
    {
        var norm = AwarenessValidator.Normalize(settings);
        SetSetting("awareness.enabled", norm.IsEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.fullscreen_detection_enabled", norm.FullScreenDetectionEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.auto_hide_fullscreen", norm.AutoHideOnFullScreen.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.fullscreen_enter_delay_ms", norm.FullScreenEnterDelayMs.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.fullscreen_restore_delay_ms", norm.FullScreenRestoreDelayMs.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.fullscreen_tolerance_px", norm.FullScreenGeometryTolerancePx.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.post_restore_settling_seconds", norm.PostRestoreSettlingSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.catch_up_notification_after_fullscreen", norm.CatchUpNotificationReactionAfterFullScreen.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.inactivity_detection_enabled", norm.InactivityDetectionEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.auto_quiet_enabled", norm.AutoQuietModeEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.auto_sleep_enabled", norm.AutoSleepEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.sleep_after_seconds", norm.SleepAfterSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.priority_notification_wake_enabled", norm.PriorityNotificationWakeEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.welcome_back_enabled", norm.WelcomeBackReactionEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.quiet_threshold_seconds", norm.QuietThresholdSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.away_threshold_seconds", norm.AwayThresholdSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.sleep_threshold_seconds", norm.SleepThresholdSeconds.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.manual_idle_override", norm.ManualIdleOverride.ToString());
        SetSetting("awareness.manual_focus_enabled", norm.ManualFocusModeEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.auto_focus_enabled", norm.AutoFocusInferenceEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.auto_focus_confidence", norm.AutoFocusConfidenceThreshold.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.suppress_random_click_in_focus", norm.SuppressRandomClickInFocus.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.session_awareness_enabled", norm.SessionAwarenessEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.power_awareness_enabled", norm.PowerAwarenessEnabled.ToString(CultureInfo.InvariantCulture));
        SetSetting("awareness.fullscreen_excluded_csv", norm.FullScreenExcludedProcessesCsv);
        SetSetting("awareness.app_rules_csv", AwarenessValidator.FormatAppRules(norm.AppRules));
    }

    public IReadOnlyList<LauncherEntry> LoadRecentLauncherEntries()
    {
        var json = GetSetting("launcher.recent_entries_json");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LauncherEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveRecentLauncherEntries(IReadOnlyList<LauncherEntry> entries)
    {
        var trimmed = (entries ?? [])
            .Take(25)
            .ToList();
        var json = JsonSerializer.Serialize(trimmed);
        SetSetting("launcher.recent_entries_json", json);
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
