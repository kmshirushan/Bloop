using Bloop.Windows.System;
using Xunit;

namespace Bloop.Windows.Tests;

public sealed class NativeDisplayTests
{
    [Fact]
    public void GetWorkAreaForPoint_ReturnsValidMonitorBounds()
    {
        var (left, top, right, bottom, devName) = NativeMethods.GetMonitorWorkAreaAndDevice(100, 100);
        Assert.True(right > left);
        Assert.True(bottom > top);
        Assert.False(string.IsNullOrWhiteSpace(devName));
    }

    [Fact]
    public void GetCursorPos_HandlesDesktopAndSandboxedSessionsGracefully()
    {
        bool ok = NativeMethods.GetCursorPos(out var pt);
        if (ok)
        {
            Assert.True(pt.X >= -32768 && pt.Y >= -32768);
        }
        else
        {
            Assert.Equal(0, pt.X);
            Assert.Equal(0, pt.Y);
        }
    }

    [Fact]
    public async Task NotificationService_AndAdaptiveSignal_InitializeAndDisposeCleanly()
    {
        using var service = new Bloop.Windows.Notifications.NotificationService();
        Assert.True(service.IsSupported);

        var status = service.GetPermissionStatus();
        Assert.Contains(status, new[]
        {
            Bloop.Core.Notifications.NotificationPermissionStatus.Allowed,
            Bloop.Core.Notifications.NotificationPermissionStatus.Denied,
            Bloop.Core.Notifications.NotificationPermissionStatus.NotRequested
        });

        // Null normalization guard
        var normalizedNull = await service.TryNormalizeSingleNotificationAsync(null, DateTimeOffset.UtcNow);
        Assert.Null(normalizedNull);

        using var signal = new Bloop.Windows.Notifications.AdaptiveNotificationChangeSignal();
        var triggers = new List<Bloop.Core.Notifications.NotificationSyncTrigger>();
        signal.ChangeSignaled += (_, t) => triggers.Add(t);
        signal.ConfigureIntervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), active: true);
        signal.Start();
        Assert.True(signal.IsInFastPollWindow);
        signal.NotifyActivityBoost(Bloop.Core.Notifications.NotificationSyncTrigger.UserInteraction);
        signal.Stop();
    }

    [Fact]
    public void ApplyGlassWindowBackdrop_HandlesInvalidHwndGracefully()
    {
        var (active, summary) = NativeMethods.ApplyGlassWindowBackdrop(
            IntPtr.Zero,
            enableAcrylicBackdrop: true,
            isLightTheme: false,
            isToolWindow: true,
            isNoActivate: true);

        Assert.False(active);
        Assert.Equal("InvalidHwnd", summary);
    }

    [Fact]
    public void WindowsAwarenessProvider_ObservesForegroundWindow_CalculatesIdleDuration_AndDisposesCleanly()
    {
        for (int i = 0; i < 3; i++)
        {
            using var provider = new Bloop.Windows.Awareness.WindowsAwarenessProvider();
            var idle = provider.GetUserIdleDuration();
            Assert.True(idle >= TimeSpan.Zero);

            var obs = provider.ObserveForegroundWindow(ReadOnlySpan<IntPtr>.Empty);
            // If a foreground window exists on the desktop, its monitor dimensions must be positive
            if (obs.WindowHandle != IntPtr.Zero && !obs.IsBloopOwnedWindow)
            {
                Assert.True(obs.MonitorWidthPx > 0);
                Assert.True(obs.MonitorHeightPx > 0);

                // Passing the foreground HWND in bloopOwnedWindowHandles must mark IsBloopOwnedWindow = true
                Span<IntPtr> owned = [obs.WindowHandle];
                var excludedObs = provider.ObserveForegroundWindow(owned);
                Assert.True(excludedObs.IsBloopOwnedWindow);
                Assert.False(excludedObs.IsValidExternalWindow);
            }
        }
    }
}
