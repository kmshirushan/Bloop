using System.IO;
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

    [Fact]
    public void M7_1_WindowsLauncherCatalogService_DiscoversStartMenuShortcuts_SkipsUninstallers_AndValidatesMissingTargets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"bloop_m71_launcher_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(tempRoot, "launcher_test.db");
        var startMenuDir = Path.Combine(tempRoot, "Programs", "Productivity");
        Directory.CreateDirectory(startMenuDir);

        try
        {
            var validShortcut = Path.Combine(startMenuDir, "Bloop Notes Studio.lnk");
            var uninstallShortcut = Path.Combine(startMenuDir, "Uninstall Bloop Notes.lnk");
            File.WriteAllText(validShortcut, "dummy shortcut");
            File.WriteAllText(uninstallShortcut, "dummy uninstaller");

            using var db = new Bloop.Storage.DatabaseService(dbPath);
            var settingsRepo = new Bloop.Storage.SettingsRepository(db);
            var service = new Bloop.Windows.Productivity.WindowsLauncherCatalogService(
                settingsRepo,
                [Path.Combine(tempRoot, "Programs")]);

            var catalog = service.GetCatalogSnapshot(forceRefresh: true);
            Assert.Single(catalog);
            Assert.Equal("Bloop Notes Studio", catalog[0].DisplayName);
            Assert.Equal("Productivity", catalog[0].Category);

            var missingEntry = new Bloop.Core.Productivity.LauncherEntry(
                "file:missing",
                "Missing File",
                Path.Combine(tempRoot, "does_not_exist.txt"),
                Bloop.Core.Productivity.LauncherEntryKind.File);
            bool launchedMissing = service.TryLaunchEntry(missingEntry, out var err);
            Assert.False(launchedMissing);
            Assert.NotNull(err);
            Assert.Contains("no longer exists", err);

            Assert.False(service.TryOpenFilePath(Path.Combine(tempRoot, "missing.md"), out _, out var fileErr));
            Assert.NotNull(fileErr);

            Assert.False(service.TryOpenFolderPath(Path.Combine(tempRoot, "missing_folder"), out _, out var folderErr));
            Assert.NotNull(folderErr);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }
}
