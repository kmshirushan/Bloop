namespace Bloop.Core.Awareness;

/// <summary>
/// Central, deterministic desktop context classifier, full-screen hysteresis tracker,
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

    private DesktopContext _previousContext = DesktopContext.Unknown;
    private UserInactivityState _previousInactivityState = UserInactivityState.Active;
    private bool _previousSessionLocked;
    private bool _previousSystemSuspended;
    private string _previousPolicyReason = string.Empty;

    public void ResetState(DateTimeOffset nowUtc)
    {
        _fullScreenCandidateSinceUtc = null;
        _fullScreenClearedSinceUtc = null;
        _isConfirmedFullScreen = false;
        _currentForegroundEnteredAtUtc = nowUtc;
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

        // Critical safeguard: distinguish a normal maximized window from a genuine full-screen window.
        // 1. When the monitor has a visible taskbar, a normal maximized window only covers WorkArea (rcWork),
        //    so reaching here (covering rcMonitor) already proves it is covering the taskbar (full-screen).
        // 2. When the taskbar is auto-hidden (rcMonitor == rcWork), a standard maximized window has
        //    IsMaximized=true AND HasCaptionOrThickFrame=true. Exclude it so maximized editors/browsers
        //    never trigger full-screen hiding.
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
        DateTimeOffset nowUtc)
    {
        var settings = AwarenessValidator.Normalize(rawSettings);
        var events = new List<AwarenessDiagnosticEvent>();

        // 1. Update foreground application tracking (preserving last external app when Bloop's own UI is clicked)
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
            // Focusing the Windows desktop / taskbar clears full-screen candidate state
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

        // 2. Evaluate Full-Screen Hysteresis (Enter delay vs Restore delay)
        bool isCandidateFullScreen = settings.IsEnabled && settings.FullScreenDetectionEnabled && _lastExternalIsRawFullScreen;
        UpdateFullScreenHysteresis(isCandidateFullScreen, settings, nowUtc, events);

        // 3. Evaluate User Inactivity State (Active / Quiet / Away / Sleeping) + Manual Override
        var idleDuration = rawIdleDuration < TimeSpan.Zero ? TimeSpan.Zero : rawIdleDuration;
        var inactivityState = ClassifyInactivityState(idleDuration, settings);
        if (inactivityState != _previousInactivityState)
        {
            _previousInactivityState = inactivityState;
            events.Add(new AwarenessDiagnosticEvent(
                "IdleStateChanged",
                $"User inactivity state -> {inactivityState} (Idle={idleDuration.TotalSeconds:0}s)",
                _previousContext,
                _lastExternalProcessName,
                1.0,
                _previousPolicyReason,
                nowUtc));
        }

        // 4. Session & Power Transitions
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

        // 5. Evaluate Manual & Confidence-Based Focus Mode
        bool manualFocusActive = settings.IsEnabled &&
                                 settings.ManualFocusModeEnabled &&
                                 (!settings.ManualFocusExpiresAtUtc.HasValue || nowUtc < settings.ManualFocusExpiresAtUtc.Value);

        double focusConfidence = CalculateAutoFocusConfidence(
            _lastExternalProcessName,
            inactivityState,
            fgStableDuration,
            _lastExternalIsMaximized,
            _lastExternalWorkAreaCoverageRatio,
            settings);

        bool autoFocusActive = settings.IsEnabled &&
                               settings.AutoFocusInferenceEnabled &&
                               !_isConfirmedFullScreen &&
                               inactivityState is UserInactivityState.Active or UserInactivityState.Quiet &&
                               focusConfidence >= settings.AutoFocusConfidenceThreshold;

        // 6. Determine DesktopContext & Confidence
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
        else if (inactivityState is UserInactivityState.Away or UserInactivityState.Sleeping)
        {
            context = DesktopContext.Away;
            confidence = 0.95;
        }
        else if (autoFocusActive)
        {
            context = DesktopContext.Focus;
            confidence = focusConfidence;
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

        // 7. Resolve Central AwarenessPolicy (8-Level Mandatory Precedence)
        var policy = ResolvePolicy(
            context,
            inactivityState,
            effectiveSuspended,
            effectiveLocked,
            globalBehavioursPaused,
            _isConfirmedFullScreen,
            manualFocusActive,
            autoFocusActive,
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
            IdleDuration = idleDuration,
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
                        $"Full-screen cleared; restoring desktop context",
                        DesktopContext.Normal,
                        _lastExternalProcessName,
                        0.95,
                        "FullScreenCleared",
                        nowUtc));
                }
            }
        }
    }

    private static UserInactivityState ClassifyInactivityState(TimeSpan idleDuration, AwarenessSettings settings)
    {
        if (settings.ManualIdleOverride == ManualIdleOverride.ForceAwake)
        {
            return UserInactivityState.Active;
        }
        if (settings.ManualIdleOverride == ManualIdleOverride.ForceSleep)
        {
            return UserInactivityState.Sleeping;
        }

        if (!settings.IsEnabled || !settings.InactivityDetectionEnabled)
        {
            return UserInactivityState.Active;
        }

        double sec = idleDuration.TotalSeconds;
        if (settings.AutoSleepEnabled && sec >= settings.SleepThresholdSeconds)
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
                // Explicit user Focus rule + active presence + foreground stability
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

        // Conservative multi-signal score for default focus candidates (never inferred from app name alone)
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

        // Precedence 3: Global Pause (preserves system/fullscreen visibility hiding while suspending autonomous behaviours)
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
                AllowNotificationReaction = true, // High/Critical respected; ordinary suppressed in NotificationReactionScheduler
                AllowCenterScreenEntrance = true,
                IsQuietMode = true,
                ShouldEnterSleepMode = false,
                EffectiveContext = DesktopContext.Focus,
                InactivityState = inactivityState,
                Reason = focusReason
            };
        }

        // Precedence 7: User Inactivity (Sleeping / Away / Quiet)
        if (inactivityState == UserInactivityState.Sleeping)
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
                ShouldEnterSleepMode = true,
                EffectiveContext = DesktopContext.Away,
                InactivityState = UserInactivityState.Sleeping,
                Reason = "UserInactivity(Sleeping)"
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
