using System.Runtime.InteropServices;
using Bloop.Core.Notifications;
using Microsoft.Win32;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Bloop.Windows.Notifications;

/// <summary>
/// Documented WinRT + lifecycle + adaptive polling implementation of INotificationChangeSignal.
/// Uses zero undocumented NTDLL/WNF APIs:
/// 1. Subscribes to UserNotificationListener.NotificationChanged when supported (packaged deployment), with 350ms debouncing.
/// 2. Subscribes to SystemEvents.SessionSwitch (Lock/Unlock) and SystemEvents.PowerModeChanged (Suspend/Resume).
/// 3. Uses an adaptive timer (short fast-poll window after activity/changes, low-frequency idle poll otherwise).
/// </summary>
public sealed class AdaptiveNotificationChangeSignal : INotificationChangeSignal
{
    private readonly object _gate = new();
    private readonly Timer _pollTimer;
    private readonly Timer _debounceTimer;

    private TimeSpan _fastInterval = TimeSpan.FromSeconds(2.5);
    private TimeSpan _fastWindow = TimeSpan.FromSeconds(15.0);
    private TimeSpan _idleInterval = TimeSpan.FromSeconds(12.0);
    private DateTimeOffset _fastPollUntilUtc = DateTimeOffset.MinValue;

    private bool _isConfiguredActive;
    private bool _isStarted;
    private bool _isSessionPaused;
    private bool _winrtEventSubscribed;
    private bool _disposed;

    public event EventHandler<NotificationSyncTrigger>? ChangeSignaled;

    public bool IsWinRtNotificationChangedSubscribed => _winrtEventSubscribed;
    public bool IsInFastPollWindow => DateTimeOffset.UtcNow < _fastPollUntilUtc;

    public AdaptiveNotificationChangeSignal()
    {
        _pollTimer = new Timer(OnPollTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer = new Timer(OnDebouncedPlatformEvent, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void ConfigureIntervals(TimeSpan fastInterval, TimeSpan fastWindow, TimeSpan idleInterval, bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _fastInterval = fastInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : fastInterval;
            _fastWindow = fastWindow < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : fastWindow;
            _idleInterval = idleInterval < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : idleInterval;
            _isConfiguredActive = active;
            RescheduleTimerLocked();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _isStarted = true;
            TrySubscribeWinRtEventLocked();
            _fastPollUntilUtc = DateTimeOffset.UtcNow + _fastWindow;
            RescheduleTimerLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _isStarted = false;
            _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void NotifyActivityBoost(NotificationSyncTrigger reason)
    {
        lock (_gate)
        {
            if (_disposed || !_isStarted || !_isConfiguredActive) return;
            _fastPollUntilUtc = DateTimeOffset.UtcNow + _fastWindow;
            RescheduleTimerLocked();
        }
    }

    private void TrySubscribeWinRtEventLocked()
    {
        if (_winrtEventSubscribed) return;
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                return;
            }
            var listener = UserNotificationListener.Current;
            listener.NotificationChanged += OnWinRtNotificationChanged;
            _winrtEventSubscribed = true;
        }
        catch (COMException)
        {
            // Expected (0x80070490 E_NOTFOUND) when running unpackaged; adaptive polling + lifecycle triggers handle sync.
            _winrtEventSubscribed = false;
        }
        catch
        {
            _winrtEventSubscribed = false;
        }
    }

    private void OnWinRtNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !_isStarted || !_isConfiguredActive || _isSessionPaused) return;
            _fastPollUntilUtc = DateTimeOffset.UtcNow + _fastWindow;
            // Debounce rapid NotificationChanged bursts by 350ms
            _debounceTimer.Change(TimeSpan.FromMilliseconds(350), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebouncedPlatformEvent(object? state)
    {
        if (_disposed) return;
        ChangeSignaled?.Invoke(this, NotificationSyncTrigger.PlatformEvent);
    }

    private void OnPollTimerTick(object? state)
    {
        NotificationSyncTrigger trigger;
        lock (_gate)
        {
            if (_disposed || !_isStarted || !_isConfiguredActive || _isSessionPaused)
            {
                return;
            }

            bool isFast = DateTimeOffset.UtcNow < _fastPollUntilUtc;
            trigger = isFast ? NotificationSyncTrigger.AdaptiveFastPoll : NotificationSyncTrigger.AdaptiveIdlePoll;
            RescheduleTimerLocked();
        }

        ChangeSignaled?.Invoke(this, trigger);
    }

    private void RescheduleTimerLocked()
    {
        if (_disposed || !_isStarted || !_isConfiguredActive || _isSessionPaused)
        {
            _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var nextDelay = DateTimeOffset.UtcNow < _fastPollUntilUtc ? _fastInterval : _idleInterval;
        _pollTimer.Change(nextDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        bool shouldFireUnlock = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect)
            {
                _isSessionPaused = true;
                RescheduleTimerLocked();
            }
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect)
            {
                _isSessionPaused = false;
                if (_isStarted && _isConfiguredActive)
                {
                    _fastPollUntilUtc = DateTimeOffset.UtcNow + _fastWindow;
                    RescheduleTimerLocked();
                    shouldFireUnlock = true;
                }
            }
        }

        if (shouldFireUnlock)
        {
            ChangeSignaled?.Invoke(this, NotificationSyncTrigger.SessionUnlocked);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        bool shouldFireResume = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (e.Mode == PowerModes.Suspend)
            {
                _isSessionPaused = true;
                RescheduleTimerLocked();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _isSessionPaused = false;
                if (_isStarted && _isConfiguredActive)
                {
                    _fastPollUntilUtc = DateTimeOffset.UtcNow + _fastWindow;
                    RescheduleTimerLocked();
                    shouldFireResume = true;
                }
            }
        }

        if (shouldFireResume)
        {
            ChangeSignaled?.Invoke(this, NotificationSyncTrigger.SystemResumed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _isStarted = false;
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        if (_winrtEventSubscribed)
        {
            try
            {
                UserNotificationListener.Current.NotificationChanged -= OnWinRtNotificationChanged;
            }
            catch
            {
            }
            _winrtEventSubscribed = false;
        }

        _pollTimer.Dispose();
        _debounceTimer.Dispose();
    }
}
