namespace Bloop.Core.Awareness;

/// <summary>
/// Central, deterministic desktop context classifier, full-screen hysteresis tracker,
/// M6.1 Intelligent Sleep controller (mouse-only countdown, hover-only / priority-notification wake),
/// and 8-level policy precedence resolver.
/// </summary>
public sealed class ContextPolicyResolver : IContextPolicyResolver
{
    private string? _lastExternalProcessName;
    private string? _lastExternalAppName;
    private IntPtr _lastExternalHwnd;
    private string _lastMonitorId = "primary";
    private bool _lastExternalIsMaximized;
    private bool _lastExternalIsRawFullScreen;
    private double _lastExternalWorkAreaCoverageRatio;
    private DateTimeOffset _currentForegroundEnteredAtUtc = DateTimeOffset.MinValue;

    private DateTimeOffset? _fullScreenCandidateSinceUtc;
    private DateTimeOffset? _fullScreenClearedSinceUtc;
    private bool _isConfirmedFullScreen;

    // M6.1 Intelligent Sleep latch: once true, desktop mouse movement outside Bloop CANNOT clear it.
    private bool _isIntelligentSleeping;
    private DateTimeOffset _lastWakeOrResetUtc = DateTimeOffset.MinValue;
    private string? _lastSleepSuppressionReason;

    private DesktopContext _previousContext = DesktopContext.Unknown;
    private UserInactivityState _previousInactivityState = UserInactivityState.Active;
    private bool _previousSessionLocked;
    private bool _previousSystemSuspended;
    private string _previousPolicyReason = string.Empty;

    public bool IsSleeping => _isIntelligentSleeping;

    public void ResetState(DateTimeOffset nowUtc)
    {
        _fullScreenCandidateSinceUtc = null;
        _fullScreenClearedSinceUtc = null;
        _isConfirmedFullScreen = false;
        _isIntelligentSleeping = false;
        _lastWakeOrResetUtc = nowUtc;
        _currentForegroundEnteredAtUtc = nowUtc;
    }

    public bool TryWakeFromPointerEntry(DateTimeOffset nowUtc, out AwarenessDiagnosticEvent? wakeEvent)
    {
        _lastWakeOrResetUtc = nowUtc;
        if (!_isIntelligentSleeping)
        {
            wakeEvent = null;
            return false;
        }

        _isIntelligentSleeping = false;
        _previousContext = DesktopContext.Normal;
        _previousInactivityState = UserInactivityState.Active;

        wakeEvent = new AwarenessDiagnosticEvent(
            "SleepExited",
            "Pointer entered mascot hit area — waking Bloop and resetting mouse inactivity countdown",
            DesktopContext.Normal,
            _lastExternalProcessName,
            1.0,
            "PointerEnteredMascot",
            nowUtc);
        return true;
    }

    public bool TryWakeFromPriorityNotification(
        string sourceSummary,
        AwarenessSettings rawSettings,
        bool globalBehavioursPaused,
        out AwarenessDiagnosticEvent? wakeEvent,
        DateTimeOffset? timestampUtc = null)
    {
        var nowUtc = timestampUtc ?? DateTimeOffset.UtcNow;
        var settings = AwarenessValidator.Normalize(rawSettings);

        if (!_isIntelligentSleeping)
        {
            wakeEvent = null;
            return false;
        }

        if (_previousSystemSuspended || _previousSessionLocked || _isConfirmedFullScreen || globalBehavioursPaused)
        {
            string blockReason = _previousSystemSuspended ? "SystemSuspended"
                : _previousSessionLocked ? "SessionLocked"
                : _isConfirmedFullScreen ? "FullScreenHidden"
                : "GlobalPause";

            wakeEvent = new AwarenessDiagnosticEvent(
                "NotificationWakeRejected",
                $"Priority notification wake rejected due to higher-priority state: {blockReason}",
                _previousContext,
                _lastExternalProcessName,
                1.0,
                blockReason,
                nowUtc);
            return false;
        }

        if (!settings.PriorityNotificationWakeEnabled)
        {
            wakeEvent = new AwarenessDiagnosticEvent(
                "NotificationWakeRejected",
                $"Priority notification wake disabled in settings ({sourceSummary})",
                DesktopContext.Sleeping,
                _lastExternalProcessName,
                1.0,
                "PriorityNotificationWakeDisabled",
                nowUtc);
            return false;
        }

        _isIntelligentSleeping = false;
        _lastWakeOrResetUtc = nowUtc;
        _previousContext = DesktopContext.Normal;
        _previousInactivityState = UserInactivityState.Active;

        wakeEvent = new AwarenessDiagnosticEvent(
            "NotificationWakeAccepted",
            $"Priority notification woke Bloop ({sourceSummary}); mouse inactivity timer restarted",
            DesktopContext.Normal,
            _lastExternalProcessName,
            1.0,
            "PriorityNotificationWake",
            nowUtc);
        return true;
    }

    public static bool IsFullScreenWindowCandidate(
        ForegroundWindowObservation obs,
        AwarenessSettings settings,
        out string? exclusionOrRejectionReason)
    {
        if (!settings.IsEnabled || !settings.FullScreenDetectionEnabled)
        {
            exclusionOrRejectionReason = "FullScreenDetectionDisabled";
            return false;
        }

        if (!obs.IsValidExternalWindow || obs.IsBloopOwnedWindow || obs.IsShellOrDesktopSurface)
        {
            exclusionOrRejectionReason = "NonExternalOrShellWindow";
            return false;
        }

        if (!obs.IsVisible || obs.IsMinimized)
        {
            exclusionOrRejectionReason = "HiddenOrMinimized";
            return false;
        }

        if (obs.MonitorWidthPx <= 0 || obs.MonitorHeightPx <= 0)
        {
            exclusionOrRejectionReason = "InvalidMonitorBounds";
            return false;
        }

        var procKey = AwarenessValidator.NormalizeProcessKey(obs.ProcessName);
        if (!string.IsNullOrWhiteSpace(procKey))
        {
            if (settings.AppRules.TryGetValue(procKey, out var rule) &&
                rule is AppAwarenessBehavior.Excluded or AppAwarenessBehavior.ExcludeFullScreenSuppression)
            {
                exclusionOrRejectionReason = $"ExcludedByAppRule({procKey}={rule})";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(settings.FullScreenExcludedProcessesCsv))
            {
                var excludedList = settings.FullScreenExcludedProcessesCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var item in excludedList)
                {
                    if (string.Equals(AwarenessValidator.NormalizeProcessKey(item), procKey, StringComparison.OrdinalIgnoreCase))
                    {
                        exclusionOrRejectionReason = $"ExcludedByFullScreenCsv({procKey})";
                        return false;
                    }
                }
            }
        }

        int tol = Math.Clamp(settings.FullScreenGeometryTolerancePx, 0, 16);
        bool coversLeft = obs.WindowLeftPx <= obs.MonitorLeftPx + tol;
        bool coversTop = obs.WindowTopPx <= obs.MonitorTopPx + tol;
        bool coversRight = obs.WindowRightPx >= obs.MonitorRightPx - tol;
        bool coversBottom = obs.WindowBottomPx >= obs.MonitorBottomPx - tol;

        if (!coversLeft || !coversTop || !coversRight || !coversBottom)
        {
            exclusionOrRejectionReason = "DoesNotCoverMonitorBounds";
            return false;
        }

        if (obs.IsMaximized && obs.HasCaptionOrThickFrame && !obs.MonitorHasVisibleTaskbar)
        {
            exclusionOrRejectionReason = "MaximizedStandardWindowWithCaption";
            return false;
        }

        exclusionOrRejectionReason = null;
        return true;
    }

    public (AwarenessSnapshot Snapshot, AwarenessPolicy Policy, IReadOnlyList<AwarenessDiagnosticEvent> Events) Evaluate(
        ForegroundWindowObservation observation,
        TimeSpan rawIdleDuration,
        bool isSessionLocked,
        bool isSystemSuspended,
        AwarenessSettings rawSettings,
        bool globalBehavioursPaused,
        DateTimeOffset nowUtc,
        TimeSpan? mouseIdleDuration = null,
        bool mouseMovedOnDesktop = false)
    {
        var settings = AwarenessValidator.Normalize(rawSettings);
        var events = new List<AwarenessDiagnosticEvent>();

        if (_lastWakeOrResetUtc == DateTimeOffset.MinValue)
        {
            _lastWakeOrResetUtc = nowUtc;
        }

        // 1. Update foreground application tracking
        if (observation.IsValidExternalWindow && !observation.IsBloopOwnedWindow && !observation.IsShellOrDesktopSurface)
        {
            var procKey = AwarenessValidator.NormalizeProcessKey(observation.ProcessName);
            bool appChanged = !string.Equals(_lastExternalProcessName, procKey, StringComparison.OrdinalIgnoreCase) ||
                              _lastExternalHwnd != observation.WindowHandle;

            if (appChanged)
            {
                _lastExternalProcessName = procKey;
                _lastExternalAppName = string.IsNullOrWhiteSpace(observation.ApplicationName) ? procKey : observation.ApplicationName;
                _lastExternalHwnd = observation.WindowHandle;
                _currentForegroundEnteredAtUtc = nowUtc;

                events.Add(new AwarenessDiagnosticEvent(
                    "ForegroundApplicationChanged",
                    $"Foreground app: {_lastExternalProcessName ?? "unknown"} on {observation.MonitorId}",
                    _previousContext,
                    _lastExternalProcessName,
                    1.0,
                    _previousPolicyReason,
                    nowUtc));
            }

            _lastMonitorId = string.IsNullOrWhiteSpace(observation.MonitorId) ? "primary" : observation.MonitorId;
            _lastExternalIsMaximized = observation.IsMaximized;
            _lastExternalIsRawFullScreen = IsFullScreenWindowCandidate(observation, settings, out _);

            double workAreaPixels = Math.Max(1.0, (double)observation.WorkAreaWidthPx * observation.WorkAreaHeightPx);
            double winPixels = (double)observation.WindowWidthPx * observation.WindowHeightPx;
            _lastExternalWorkAreaCoverageRatio = Math.Clamp(winPixels / workAreaPixels, 0.0, 1.5);
        }
        else if (observation.IsShellOrDesktopSurface)
        {
            _lastExternalIsRawFullScreen = false;
            _lastExternalIsMaximized = false;
        }

        if (_currentForegroundEnteredAtUtc == DateTimeOffset.MinValue)
        {
            _currentForegroundEnteredAtUtc = nowUtc;
        }

        var fgStableDuration = nowUtc - _currentForegroundEnteredAtUtc;
        if (fgStableDuration < TimeSpan.Zero)
        {
            fgStableDuration = TimeSpan.Zero;
        }

        // 2. Evaluate Full-Screen Hysteresis
        bool isCandidateFullScreen = settings.IsEnabled && settings.FullScreenDetectionEnabled && _lastExternalIsRawFullScreen;
        UpdateFullScreenHysteresis(isCandidateFullScreen, settings, nowUtc, events);

        // 3. Session & Power Transitions
        bool effectiveLocked = settings.IsEnabled && settings.SessionAwarenessEnabled && isSessionLocked;
        bool effectiveSuspended = settings.IsEnabled && settings.PowerAwarenessEnabled && isSystemSuspended;

        if (effectiveLocked != _previousSessionLocked)
        {
            _previousSessionLocked = effectiveLocked;
            events.Add(new AwarenessDiagnosticEvent(
                effectiveLocked ? "SessionLocked" : "SessionUnlocked",
                effectiveLocked ? "Windows session locked — hiding mascot" : "Windows session unlocked — re-evaluating desktop context",
                effectiveLocked ? DesktopContext.SessionLocked : DesktopContext.Normal,
                _lastExternalProcessName,
                1.0,
                effectiveLocked ? "SessionLocked" : "SessionUnlocked",
                nowUtc));
        }

        if (effectiveSuspended != _previousSystemSuspended)
        {
            _previousSystemSuspended = effectiveSuspended;
            events.Add(new AwarenessDiagnosticEvent(
                effectiveSuspended ? "SystemSuspended" : "SystemResumed",
                effectiveSuspended ? "System entering suspend — pausing timers and hiding mascot" : "System resumed — refreshing desktop state",
                effectiveSuspended ? DesktopContext.SystemSuspended : DesktopContext.Normal,
                _lastExternalProcessName,
                1.0,
                effectiveSuspended ? "SystemSuspended" : "SystemResumed",
                nowUtc));
        }

        // 4. Evaluate Manual & Confidence-Based Focus Mode
        var osIdleDuration = rawIdleDuration < TimeSpan.Zero ? TimeSpan.Zero : rawIdleDuration;
        var effectiveMouseIdle = mouseIdleDuration ?? osIdleDuration;
        if (effectiveMouseIdle < TimeSpan.Zero)
        {
            effectiveMouseIdle = TimeSpan.Zero;
        }

        // While AWAKE, desktop mouse movement resets the sleep countdown.
        // Once SLEEPING, desktop mouse movement outside Bloop MUST NOT wake him!
        if (!_isIntelligentSleeping && mouseMovedOnDesktop)
        {
            _lastWakeOrResetUtc = nowUtc;
            events.Add(new AwarenessDiagnosticEvent(
                "InactivityCountdownReset",
                "Mouse activity detected on desktop — sleep countdown reset",
                _previousContext,
                _lastExternalProcessName,
                1.0,
                _previousPolicyReason,
                nowUtc));
        }

        // Also clamp effectiveMouseIdle by time elapsed since last wake (e.g., after pointer-entry wake or notification wake)
        var elapsedSinceWake = nowUtc - _lastWakeOrResetUtc;
        if (elapsedSinceWake >= TimeSpan.Zero && mouseIdleDuration.HasValue && elapsedSinceWake < effectiveMouseIdle)
        {
            effectiveMouseIdle = elapsedSinceWake;
        }

        bool manualFocusActive = settings.IsEnabled &&
                                 settings.ManualFocusModeEnabled &&
                                 (!settings.ManualFocusExpiresAtUtc.HasValue || nowUtc < settings.ManualFocusExpiresAtUtc.Value);

        // Use OS-wide idle duration (keyboard + mouse) for Focus engagement, but mouse-only idle for Intelligent Sleep
        bool isUserActivelyEngagedForFocus = osIdleDuration.TotalSeconds < settings.QuietThresholdSeconds;
        double focusConfidence = CalculateAutoFocusConfidence(
            _lastExternalProcessName,
            isUserActivelyEngagedForFocus ? UserInactivityState.Active : UserInactivityState.Away,
            fgStableDuration,
            _lastExternalIsMaximized,
            _lastExternalWorkAreaCoverageRatio,
            settings);

        bool autoFocusActive = settings.IsEnabled &&
                               settings.AutoFocusInferenceEnabled &&
                               !_isConfirmedFullScreen &&
                               !_isIntelligentSleeping &&
                               isUserActivelyEngagedForFocus &&
                               focusConfidence >= settings.AutoFocusConfidenceThreshold;

        // 5. Evaluate M6.1 Intelligent Sleep State & Inactivity Tiers
        bool higherPriorityBlocksSleep = effectiveSuspended ||
                                         effectiveLocked ||
                                         globalBehavioursPaused ||
                                         _isConfirmedFullScreen ||
                                         manualFocusActive ||
                                         autoFocusActive;

        if (settings.ManualIdleOverride == ManualIdleOverride.ForceAwake)
        {
            if (_isIntelligentSleeping)
            {
                _isIntelligentSleeping = false;
                _lastWakeOrResetUtc = nowUtc;
                events.Add(new AwarenessDiagnosticEvent(
                    "SleepExited",
                    "Exited sleep via Manual ForceAwake override",
                    DesktopContext.Normal,
                    _lastExternalProcessName,
                    1.0,
                    "ManualForceAwake",
                    nowUtc));
            }
        }
        else if (settings.ManualIdleOverride == ManualIdleOverride.ForceSleep)
        {
            if (!_isIntelligentSleeping)
            {
                _isIntelligentSleeping = true;
                events.Add(new AwarenessDiagnosticEvent(
                    "SleepEntered",
                    "Entered sleep via Manual ForceSleep override",
                    DesktopContext.Sleeping,
                    _lastExternalProcessName,
                    1.0,
                    "ManualForceSleep",
                    nowUtc));
            }
        }
        else if (!settings.IsEnabled || !settings.InactivityDetectionEnabled || !settings.AutoSleepEnabled)
        {
            if (_isIntelligentSleeping)
            {
                _isIntelligentSleeping = false;
                _lastWakeOrResetUtc = nowUtc;
            }
        }
        else if (!_isIntelligentSleeping)
        {
            // Check if mouse inactivity has reached SleepAfterSeconds (default 30s) or SleepThresholdSeconds
            double effectiveSleepThresholdSec = mouseIdleDuration.HasValue
                ? settings.SleepAfterSeconds
                : settings.SleepThresholdSeconds;

            if (effectiveMouseIdle.TotalSeconds >= effectiveSleepThresholdSec)
            {
                if (higherPriorityBlocksSleep)
                {
                    string supReason = effectiveSuspended ? "SystemSuspended"
                        : effectiveLocked ? "SessionLocked"
                        : globalBehavioursPaused ? "GlobalPause"
                        : _isConfirmedFullScreen ? "FullScreen"
                        : "FocusMode";

                    if (_lastSleepSuppressionReason != supReason)
                    {
                        _lastSleepSuppressionReason = supReason;
                        events.Add(new AwarenessDiagnosticEvent(
                            "SleepSuppressedByHigherPriorityState",
                            $"Mouse idle reached {effectiveMouseIdle.TotalSeconds:0}s, but sleep suppressed by {supReason}",
                            _previousContext,
                            _lastExternalProcessName,
                            1.0,
                            supReason,
                            nowUtc));
                    }
                }
                else
                {
                    _isIntelligentSleeping = true;
                    _lastSleepSuppressionReason = null;
                    events.Add(new AwarenessDiagnosticEvent(
                        "SleepEntered",
                        $"Entered Intelligent Sleep after {effectiveMouseIdle.TotalSeconds:0}s of mouse inactivity",
                        DesktopContext.Sleeping,
                        _lastExternalProcessName,
                        0.98,
                        "IntelligentSleep",
                        nowUtc));
                }
            }
            else
            {
                _lastSleepSuppressionReason = null;
            }
        }

        var inactivityState = ClassifyInactivityState(
            effectiveMouseIdle,
            settings,
            _isIntelligentSleeping,
            mouseIdleDuration.HasValue,
            higherPriorityBlocksSleep);

        if (inactivityState != _previousInactivityState)
        {
            _previousInactivityState = inactivityState;
            events.Add(new AwarenessDiagnosticEvent(
                "IdleStateChanged",
                $"User inactivity state -> {inactivityState} (MouseIdle={effectiveMouseIdle.TotalSeconds:0}s)",
                _previousContext,
                _lastExternalProcessName,
                1.0,
                _previousPolicyReason,
                nowUtc));
        }

        // 6. Determine DesktopContext & Confidence (8-Level Precedence)
        DesktopContext context;
        double confidence;

        if (!settings.IsEnabled)
        {
            context = DesktopContext.Normal;
            confidence = 1.0;
        }
        else if (effectiveSuspended)
        {
            context = DesktopContext.SystemSuspended;
            confidence = 1.0;
        }
        else if (effectiveLocked)
        {
            context = DesktopContext.SessionLocked;
            confidence = 1.0;
        }
        else if (_isConfirmedFullScreen)
        {
            context = DesktopContext.FullScreen;
            confidence = 0.98;
        }
        else if (manualFocusActive)
        {
            context = DesktopContext.Focus;
            confidence = 1.0;
        }
        else if (autoFocusActive)
        {
            context = DesktopContext.Focus;
            confidence = focusConfidence;
        }
        else if (_isIntelligentSleeping || inactivityState == UserInactivityState.Sleeping)
        {
            context = DesktopContext.Sleeping;
            confidence = 0.98;
        }
        else if (inactivityState == UserInactivityState.Away)
        {
            context = DesktopContext.Away;
            confidence = 0.95;
        }
        else
        {
            context = DesktopContext.Normal;
            confidence = 0.90;
        }

        if (context != _previousContext)
        {
            if (context == DesktopContext.Focus)
            {
                events.Add(new AwarenessDiagnosticEvent(
                    "FocusEntered",
                    manualFocusActive
                        ? "Entered Manual Focus Mode"
                        : $"Entered Automatic Focus Mode ({_lastExternalProcessName}, confidence={confidence:0.00})",
                    context,
                    _lastExternalProcessName,
                    confidence,
                    "FocusMode",
                    nowUtc));
            }
            else if (_previousContext == DesktopContext.Focus)
            {
                events.Add(new AwarenessDiagnosticEvent(
                    "FocusExited",
                    $"Exited Focus Mode -> {context}",
                    context,
                    _lastExternalProcessName,
                    confidence,
                    context.ToString(),
                    nowUtc));
            }

            _previousContext = context;
        }

        // 7. Resolve Central AwarenessPolicy
        var policy = ResolvePolicy(
            context,
            inactivityState,
            effectiveSuspended,
            effectiveLocked,
            globalBehavioursPaused,
            _isConfirmedFullScreen,
            manualFocusActive,
            autoFocusActive,
            _isIntelligentSleeping,
            focusConfidence,
            settings);

        if (!string.Equals(policy.Reason, _previousPolicyReason, StringComparison.Ordinal))
        {
            _previousPolicyReason = policy.Reason;
            events.Add(new AwarenessDiagnosticEvent(
                "PolicyChanged",
                $"Policy -> Show={policy.ShouldShowMascot}, Move={policy.AllowAutonomousMovement}, Quiet={policy.IsQuietMode} ({policy.Reason})",
                context,
                _lastExternalProcessName,
                confidence,
                policy.Reason,
                nowUtc));
        }

        var snapshot = new AwarenessSnapshot
        {
            IsEnabled = settings.IsEnabled,
            ForegroundProcessName = _lastExternalProcessName,
            ForegroundApplicationName = _lastExternalAppName,
            ForegroundWindowHandle = _lastExternalHwnd,
            MonitorId = _lastMonitorId,
            Context = context,
            InactivityState = inactivityState,
            IsFullScreenCandidate = isCandidateFullScreen,
            IsFullScreen = _isConfirmedFullScreen,
            IsMaximizedWindow = _lastExternalIsMaximized,
            IsUserIdle = inactivityState != UserInactivityState.Active,
            IsSleeping = _isIntelligentSleeping || inactivityState == UserInactivityState.Sleeping,
            IdleDuration = osIdleDuration,
            MouseIdleDuration = effectiveMouseIdle,
            ForegroundStableDuration = fgStableDuration,
            IsSessionLocked = effectiveLocked,
            IsSystemSuspended = effectiveSuspended,
            LastUpdatedUtc = nowUtc,
            ContextConfidence = confidence
        };

        return (snapshot, policy, events);
    }

    private void UpdateFullScreenHysteresis(
        bool isCandidate,
        AwarenessSettings settings,
        DateTimeOffset nowUtc,
        List<AwarenessDiagnosticEvent> events)
    {
        if (isCandidate)
        {
            _fullScreenClearedSinceUtc = null;
            if (!_isConfirmedFullScreen)
            {
                if (_fullScreenCandidateSinceUtc is null)
                {
                    _fullScreenCandidateSinceUtc = nowUtc;
                    events.Add(new AwarenessDiagnosticEvent(
                        "FullScreenCandidateDetected",
                        $"Full-screen candidate detected ({_lastExternalProcessName ?? "unknown"} on {_lastMonitorId})",
                        _previousContext,
                        _lastExternalProcessName,
                        0.75,
                        _previousPolicyReason,
                        nowUtc));
                }
                else if ((nowUtc - _fullScreenCandidateSinceUtc.Value).TotalMilliseconds >= settings.FullScreenEnterDelayMs)
                {
                    _isConfirmedFullScreen = true;
                    events.Add(new AwarenessDiagnosticEvent(
                        "FullScreenConfirmed",
                        $"Full-screen confirmed after {(nowUtc - _fullScreenCandidateSinceUtc.Value).TotalMilliseconds:0}ms ({_lastExternalProcessName ?? "unknown"})",
                        DesktopContext.FullScreen,
                        _lastExternalProcessName,
                        0.98,
                        "FullScreenActive",
                        nowUtc));
                }
            }
        }
        else
        {
            _fullScreenCandidateSinceUtc = null;
            if (_isConfirmedFullScreen)
            {
                if (_fullScreenClearedSinceUtc is null)
                {
                    _fullScreenClearedSinceUtc = nowUtc;
                }
                else if ((nowUtc - _fullScreenClearedSinceUtc.Value).TotalMilliseconds >= settings.FullScreenRestoreDelayMs)
                {
                    _isConfirmedFullScreen = false;
                    _fullScreenClearedSinceUtc = null;
                    events.Add(new AwarenessDiagnosticEvent(
                        "FullScreenCleared",
                        "Full-screen cleared; restoring desktop context",
                        DesktopContext.Normal,
                        _lastExternalProcessName,
                        0.95,
                        "FullScreenCleared",
                        nowUtc));
                }
            }
        }
    }

    private static UserInactivityState ClassifyInactivityState(
        TimeSpan idleDuration,
        AwarenessSettings settings,
        bool isIntelligentSleeping,
        bool isMouseSpecificSample,
        bool higherPriorityBlocksSleep = false)
    {
        if (settings.ManualIdleOverride == ManualIdleOverride.ForceAwake)
        {
            return UserInactivityState.Active;
        }
        if (settings.ManualIdleOverride == ManualIdleOverride.ForceSleep || isIntelligentSleeping)
        {
            return higherPriorityBlocksSleep ? UserInactivityState.Quiet : UserInactivityState.Sleeping;
        }

        if (!settings.IsEnabled || !settings.InactivityDetectionEnabled)
        {
            return UserInactivityState.Active;
        }

        double sec = idleDuration.TotalSeconds;
        double sleepThreshold = isMouseSpecificSample ? settings.SleepAfterSeconds : settings.SleepThresholdSeconds;

        if (settings.AutoSleepEnabled && sec >= sleepThreshold && !higherPriorityBlocksSleep)
        {
            return UserInactivityState.Sleeping;
        }
        if (sec >= settings.AwayThresholdSeconds)
        {
            return UserInactivityState.Away;
        }
        if (settings.AutoQuietModeEnabled && sec >= settings.QuietThresholdSeconds)
        {
            return UserInactivityState.Quiet;
        }
        return UserInactivityState.Active;
    }

    private static double CalculateAutoFocusConfidence(
        string? processKey,
        UserInactivityState inactivityState,
        TimeSpan fgStableDuration,
        bool isMaximized,
        double workAreaCoverage,
        AwarenessSettings settings)
    {
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return 0.0;
        }

        if (settings.AppRules.TryGetValue(processKey, out var rule))
        {
            if (rule is AppAwarenessBehavior.Normal or AppAwarenessBehavior.Excluded)
            {
                return 0.0;
            }
            if (rule == AppAwarenessBehavior.Focus)
            {
                double score = 0.55;
                if (inactivityState == UserInactivityState.Active) score += 0.25;
                if (fgStableDuration.TotalSeconds >= 10.0) score += 0.15;
                if (isMaximized || workAreaCoverage >= 0.65) score += 0.10;
                return Math.Clamp(score, 0.0, 1.0);
            }
        }

        if (!AwarenessValidator.DefaultFocusCandidateProcesses.Contains(processKey))
        {
            return 0.15;
        }

        double confidence = 0.42;
        if (inactivityState == UserInactivityState.Active)
        {
            confidence += 0.22;
        }
        if (fgStableDuration.TotalSeconds >= 30.0)
        {
            confidence += 0.20;
        }
        else if (fgStableDuration.TotalSeconds >= 12.0)
        {
            confidence += 0.10;
        }
        if (isMaximized || workAreaCoverage >= 0.65)
        {
            confidence += 0.12;
        }

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    private AwarenessPolicy ResolvePolicy(
        DesktopContext context,
        UserInactivityState inactivityState,
        bool isSystemSuspended,
        bool isSessionLocked,
        bool globalBehavioursPaused,
        bool isConfirmedFullScreen,
        bool manualFocusActive,
        bool autoFocusActive,
        bool isIntelligentSleeping,
        double focusConfidence,
        AwarenessSettings settings)
    {
        // Precedence 1: System Suspension
        if (isSystemSuspended)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = false,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = false,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = false,
                AllowRepeatedClickAnnoyance = false,
                AllowNotificationReaction = false,
                AllowCenterScreenEntrance = false,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.SystemSuspended,
                InactivityState = inactivityState,
                Reason = "SystemSuspended"
            };
        }

        // Precedence 2: Windows Session Lock
        if (isSessionLocked)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = false,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = false,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = false,
                AllowRepeatedClickAnnoyance = false,
                AllowNotificationReaction = false,
                AllowCenterScreenEntrance = false,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.SessionLocked,
                InactivityState = inactivityState,
                Reason = "SessionLocked"
            };
        }

        bool hideForFullScreen = isConfirmedFullScreen && settings.AutoHideOnFullScreen;

        // Precedence 3: Global Pause
        if (globalBehavioursPaused)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = !hideForFullScreen,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = !hideForFullScreen,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = true,
                AllowRepeatedClickAnnoyance = false,
                AllowNotificationReaction = false,
                AllowCenterScreenEntrance = false,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = hideForFullScreen ? DesktopContext.FullScreen : context,
                InactivityState = inactivityState,
                Reason = hideForFullScreen ? "GlobalPause+FullScreenHidden" : "GlobalPause"
            };
        }

        // Precedence 4: Full-Screen Visibility Suppression
        if (isConfirmedFullScreen)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = !settings.AutoHideOnFullScreen,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = !settings.AutoHideOnFullScreen,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = false,
                AllowRepeatedClickAnnoyance = false,
                AllowNotificationReaction = false,
                AllowCenterScreenEntrance = false,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.FullScreen,
                InactivityState = inactivityState,
                Reason = $"FullScreen({_lastExternalProcessName ?? "App"})"
            };
        }

        // Precedence 5 & 6: Manual Focus Mode or Automatic Focus Mode
        if (manualFocusActive || autoFocusActive)
        {
            string focusReason = manualFocusActive
                ? "ManualFocusMode"
                : $"AutoFocus({_lastExternalProcessName ?? "App"},conf={focusConfidence:0.00})";

            return new AwarenessPolicy
            {
                ShouldShowMascot = true,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = true,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = !settings.SuppressRandomClickInFocus,
                AllowRepeatedClickAnnoyance = false,
                AllowNotificationReaction = true,
                AllowCenterScreenEntrance = true,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.Focus,
                InactivityState = inactivityState,
                Reason = focusReason
            };
        }

        // Precedence 7: Explicit Sleeping State (M6.1 Intelligent Sleep — visible, hover/priority-notification wake only)
        if (isIntelligentSleeping || inactivityState == UserInactivityState.Sleeping)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = true, // Never SW_HIDE for ordinary sleep!
                AllowAutonomousMovement = false,
                AllowIdleAnimations = true,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = true,
                AllowRepeatedClickAnnoyance = true,
                AllowNotificationReaction = settings.PriorityNotificationWakeEnabled,
                AllowCenterScreenEntrance = settings.PriorityNotificationWakeEnabled,
                IsQuietMode = true,
                ShouldEnterSleepMode = true,
                EffectiveContext = DesktopContext.Sleeping,
                InactivityState = UserInactivityState.Sleeping,
                Reason = "IntelligentSleep"
            };
        }

        if (inactivityState == UserInactivityState.Away)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = true,
                AllowAutonomousMovement = false,
                AllowIdleAnimations = true,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = true,
                AllowRepeatedClickAnnoyance = true,
                AllowNotificationReaction = true,
                AllowCenterScreenEntrance = true,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.Away,
                InactivityState = UserInactivityState.Away,
                Reason = "UserInactivity(Away)"
            };
        }

        if (inactivityState == UserInactivityState.Quiet)
        {
            return new AwarenessPolicy
            {
                ShouldShowMascot = true,
                AllowAutonomousMovement = true,
                AllowIdleAnimations = true,
                AllowAttentionSeeking = false,
                AllowCursorChasing = false,
                AllowPeekaboo = false,
                AllowRandomClickReactions = true,
                AllowRepeatedClickAnnoyance = true,
                AllowNotificationReaction = true,
                AllowCenterScreenEntrance = true,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.Normal,
                InactivityState = UserInactivityState.Quiet,
                Reason = "UserInactivity(Quiet)"
            };
        }

        // Precedence 8: Normal Autonomous Desktop Behaviour
        return new AwarenessPolicy
        {
            ShouldShowMascot = true,
            AllowAutonomousMovement = true,
            AllowIdleAnimations = true,
            AllowAttentionSeeking = true,
            AllowCursorChasing = true,
            AllowPeekaboo = true,
            AllowRandomClickReactions = true,
            AllowRepeatedClickAnnoyance = true,
            AllowNotificationReaction = true,
            AllowCenterScreenEntrance = true,
            IsQuietMode = false,
            ShouldEnterSleepMode = false,
            EffectiveContext = DesktopContext.Normal,
            InactivityState = UserInactivityState.Active,
            Reason = "NormalDesktop"
        };
    }
}
