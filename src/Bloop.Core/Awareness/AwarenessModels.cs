using System.Text.Json.Serialization;

namespace Bloop.Core.Awareness;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DesktopContext
{
    Unknown = 0,
    Normal = 1,
    Focus = 2,
    FullScreen = 3,
    Away = 4,
    SessionLocked = 5,
    SystemSuspended = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserInactivityState
{
    Active = 0,
    Quiet = 1,
    Away = 2,
    Sleeping = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualIdleOverride
{
    None = 0,
    ForceAwake = 1,
    ForceSleep = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppAwarenessBehavior
{
    Normal = 0,
    Focus = 1,
    Excluded = 2,
    ExcludeFullScreenSuppression = 3
}

/// <summary>
/// Raw platform observation of the foreground window and monitor geometry (in physical pixels).
/// Separates raw OS observations from policy conclusions.
/// </summary>
public readonly record struct ForegroundWindowObservation(
    IntPtr WindowHandle,
    uint ProcessId,
    string? ProcessName,
    string? ApplicationName,
    string? WindowClassName,
    bool IsValidExternalWindow,
    bool IsBloopOwnedWindow,
    bool IsShellOrDesktopSurface,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized,
    bool HasCaptionOrThickFrame,
    int WindowLeftPx,
    int WindowTopPx,
    int WindowRightPx,
    int WindowBottomPx,
    int MonitorLeftPx,
    int MonitorTopPx,
    int MonitorRightPx,
    int MonitorBottomPx,
    int WorkAreaLeftPx,
    int WorkAreaTopPx,
    int WorkAreaRightPx,
    int WorkAreaBottomPx,
    string MonitorId
)
{
    public int WindowWidthPx => Math.Max(0, WindowRightPx - WindowLeftPx);
    public int WindowHeightPx => Math.Max(0, WindowBottomPx - WindowTopPx);
    public int MonitorWidthPx => Math.Max(0, MonitorRightPx - MonitorLeftPx);
    public int MonitorHeightPx => Math.Max(0, MonitorBottomPx - MonitorTopPx);
    public int WorkAreaWidthPx => Math.Max(0, WorkAreaRightPx - WorkAreaLeftPx);
    public int WorkAreaHeightPx => Math.Max(0, WorkAreaBottomPx - WorkAreaTopPx);
    public bool MonitorHasVisibleTaskbar =>
        MonitorLeftPx != WorkAreaLeftPx ||
        MonitorTopPx != WorkAreaTopPx ||
        MonitorRightPx != WorkAreaRightPx ||
        MonitorBottomPx != WorkAreaBottomPx;
}

/// <summary>
/// Immutable snapshot of the observed and classified desktop awareness state.
/// </summary>
public sealed record AwarenessSnapshot
{
    public bool IsEnabled { get; init; } = true;
    public string? ForegroundProcessName { get; init; }
    public string? ForegroundApplicationName { get; init; }
    public IntPtr ForegroundWindowHandle { get; init; }
    public string MonitorId { get; init; } = "primary";
    public DesktopContext Context { get; init; } = DesktopContext.Normal;
    public UserInactivityState InactivityState { get; init; } = UserInactivityState.Active;
    public bool IsFullScreenCandidate { get; init; }
    public bool IsFullScreen { get; init; }
    public bool IsMaximizedWindow { get; init; }
    public bool IsUserIdle { get; init; }
    public TimeSpan IdleDuration { get; init; } = TimeSpan.Zero;
    public TimeSpan ForegroundStableDuration { get; init; } = TimeSpan.Zero;
    public bool IsSessionLocked { get; init; }
    public bool IsSystemSuspended { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public double ContextConfidence { get; init; } = 1.0;

    public static AwarenessSnapshot Default { get; } = new();
}

/// <summary>
/// Deterministic policy output produced by the central ContextPolicyResolver.
/// Consumed by MascotWindow, MovementController, BehaviourScheduler, and NotificationReactionScheduler.
/// </summary>
public sealed record AwarenessPolicy
{
    public bool ShouldShowMascot { get; init; } = true;
    public bool AllowAutonomousMovement { get; init; } = true;
    public bool AllowIdleAnimations { get; init; } = true;
    public bool AllowAttentionSeeking { get; init; } = true;
    public bool AllowCursorChasing { get; init; } = true;
    public bool AllowPeekaboo { get; init; } = true;
    public bool AllowRandomClickReactions { get; init; } = true;
    public bool AllowRepeatedClickAnnoyance { get; init; } = true;
    public bool AllowNotificationReaction { get; init; } = true;
    public bool AllowCenterScreenEntrance { get; init; } = true;
    public bool IsQuietMode { get; init; }
    public bool ShouldEnterSleepMode { get; init; }
    public DesktopContext EffectiveContext { get; init; } = DesktopContext.Normal;
    public UserInactivityState InactivityState { get; init; } = UserInactivityState.Active;
    public string Reason { get; init; } = "NormalDesktop";

    public static AwarenessPolicy Default { get; } = new();
}

public sealed record AwarenessSettings
{
    public bool IsEnabled { get; init; } = true;
    public bool FullScreenDetectionEnabled { get; init; } = true;
    public bool AutoHideOnFullScreen { get; init; } = true;
    public int FullScreenEnterDelayMs { get; init; } = 400;
    public int FullScreenRestoreDelayMs { get; init; } = 700;
    public int FullScreenGeometryTolerancePx { get; init; } = 2;
    public double PostRestoreSettlingSeconds { get; init; } = 4.0;
    public bool CatchUpNotificationReactionAfterFullScreen { get; init; } = true;

    public bool InactivityDetectionEnabled { get; init; } = true;
    public bool AutoQuietModeEnabled { get; init; } = true;
    public bool AutoSleepEnabled { get; init; } = true;
    public bool WelcomeBackReactionEnabled { get; init; } = true;
    public double QuietThresholdSeconds { get; init; } = 60.0;
    public double AwayThresholdSeconds { get; init; } = 300.0;
    public double SleepThresholdSeconds { get; init; } = 900.0;
    public ManualIdleOverride ManualIdleOverride { get; init; } = ManualIdleOverride.None;

    public bool ManualFocusModeEnabled { get; init; } = false;
    public DateTimeOffset? ManualFocusExpiresAtUtc { get; init; }
    public bool AutoFocusInferenceEnabled { get; init; } = true;
    public double AutoFocusConfidenceThreshold { get; init; } = 0.70;
    public bool SuppressRandomClickInFocus { get; init; } = false;

    public bool SessionAwarenessEnabled { get; init; } = true;
    public bool PowerAwarenessEnabled { get; init; } = true;

    public string FullScreenExcludedProcessesCsv { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, AppAwarenessBehavior> AppRules { get; init; } =
        new Dictionary<string, AppAwarenessBehavior>(StringComparer.OrdinalIgnoreCase);

    public static AwarenessSettings Default { get; } = new();
}

public static class AwarenessValidator
{
    public static readonly IReadOnlySet<string> DefaultFocusCandidateProcesses = new HashSet<string>(
        [
            "devenv",
            "code",
            "rider64",
            "idea64",
            "pycharm64",
            "clion64",
            "winword",
            "excel",
            "powerpnt",
            "obsidian",
            "notion",
            "figma",
            "blender",
            "photoshop"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static AwarenessSettings Normalize(AwarenessSettings? raw)
    {
        if (raw is null)
        {
            return AwarenessSettings.Default;
        }

        int enterDelay = Math.Clamp(raw.FullScreenEnterDelayMs, 100, 3000);
        int restoreDelay = Math.Clamp(raw.FullScreenRestoreDelayMs, 200, 5000);
        int tolerancePx = Math.Clamp(raw.FullScreenGeometryTolerancePx, 0, 16);
        double settlingSec = double.IsFinite(raw.PostRestoreSettlingSeconds)
            ? Math.Clamp(raw.PostRestoreSettlingSeconds, 1.0, 30.0)
            : 4.0;

        double quietSec = double.IsFinite(raw.QuietThresholdSeconds)
            ? Math.Clamp(raw.QuietThresholdSeconds, 10.0, 1800.0)
            : 60.0;
        double awaySec = double.IsFinite(raw.AwayThresholdSeconds)
            ? Math.Clamp(raw.AwayThresholdSeconds, quietSec + 15.0, 3600.0)
            : Math.Max(300.0, quietSec + 60.0);
        double sleepSec = double.IsFinite(raw.SleepThresholdSeconds)
            ? Math.Clamp(raw.SleepThresholdSeconds, awaySec + 30.0, 7200.0)
            : Math.Max(900.0, awaySec + 180.0);

        double focusConf = double.IsFinite(raw.AutoFocusConfidenceThreshold)
            ? Math.Clamp(raw.AutoFocusConfidenceThreshold, 0.50, 0.95)
            : 0.70;

        var idleOverride = Enum.IsDefined(raw.ManualIdleOverride)
            ? raw.ManualIdleOverride
            : ManualIdleOverride.None;

        var normalizedRules = new Dictionary<string, AppAwarenessBehavior>(StringComparer.OrdinalIgnoreCase);
        if (raw.AppRules is not null)
        {
            foreach (var kv in raw.AppRules)
            {
                var key = NormalizeProcessKey(kv.Key);
                if (!string.IsNullOrWhiteSpace(key) && Enum.IsDefined(kv.Value))
                {
                    normalizedRules[key] = kv.Value;
                }
            }
        }

        return raw with
        {
            FullScreenEnterDelayMs = enterDelay,
            FullScreenRestoreDelayMs = restoreDelay,
            FullScreenGeometryTolerancePx = tolerancePx,
            PostRestoreSettlingSeconds = settlingSec,
            QuietThresholdSeconds = quietSec,
            AwayThresholdSeconds = awaySec,
            SleepThresholdSeconds = sleepSec,
            AutoFocusConfidenceThreshold = focusConf,
            ManualIdleOverride = idleOverride,
            FullScreenExcludedProcessesCsv = raw.FullScreenExcludedProcessesCsv?.Trim() ?? string.Empty,
            AppRules = normalizedRules
        };
    }

    public static string NormalizeProcessKey(string? processOrExeName)
    {
        if (string.IsNullOrWhiteSpace(processOrExeName))
        {
            return string.Empty;
        }

        var trimmed = processOrExeName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    public static string FormatAppRules(IReadOnlyDictionary<string, AppAwarenessBehavior> rules)
    {
        if (rules is null || rules.Count == 0) return string.Empty;
        return string.Join(", ", rules.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public static IReadOnlyDictionary<string, AppAwarenessBehavior> ParseAppRules(string? csv)
    {
        var dict = new Dictionary<string, AppAwarenessBehavior>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return dict;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2)
            {
                var key = NormalizeProcessKey(kv[0]);
                if (!string.IsNullOrWhiteSpace(key) &&
                    Enum.TryParse<AppAwarenessBehavior>(kv[1], ignoreCase: true, out var behavior))
                {
                    dict[key] = behavior;
                }
            }
        }

        return dict;
    }
}

public sealed record AwarenessDiagnosticEvent(
    string EventName,
    string Summary,
    DesktopContext Context,
    string? ProcessName,
    double Confidence,
    string PolicyReason,
    DateTimeOffset TimestampUtc
);

public interface IAwarenessProvider : IDisposable
{
    bool IsSessionLocked { get; }
    bool IsSystemSuspended { get; }
    event EventHandler<bool>? SessionLockChanged;
    event EventHandler<bool>? PowerSuspendChanged;

    ForegroundWindowObservation ObserveForegroundWindow(ReadOnlySpan<IntPtr> bloopOwnedWindowHandles);
    TimeSpan GetUserIdleDuration();
}

public interface IContextPolicyResolver
{
    (AwarenessSnapshot Snapshot, AwarenessPolicy Policy, IReadOnlyList<AwarenessDiagnosticEvent> Events) Evaluate(
        ForegroundWindowObservation observation,
        TimeSpan rawIdleDuration,
        bool isSessionLocked,
        bool isSystemSuspended,
        AwarenessSettings settings,
        bool globalBehavioursPaused,
        DateTimeOffset nowUtc);

    void ResetState(DateTimeOffset nowUtc);
}
