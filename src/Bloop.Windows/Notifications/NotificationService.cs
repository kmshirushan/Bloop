using System.Collections.Concurrent;
using System.IO;
using Bloop.Core.Notifications;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Bloop.Windows.Notifications;

/// <summary>
/// Documented WinRT adapter for Windows.UI.Notifications.Management.UserNotificationListener.
/// Isolates all Windows-specific types inside Bloop.Windows and enforces per-notification fault isolation.
/// </summary>
public sealed class NotificationService : INotificationListener
{
    private readonly SemaphoreSlim _enumLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]?> _logoCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _wasPreviouslyAllowed;
    private bool _disposed;

    public bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
        CheckWinRtTypePresent();

    public bool IsPlatformChangeEventSupported { get; private set; }

    private static bool CheckWinRtTypePresent()
    {
        try
        {
            return ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener");
        }
        catch
        {
            return false;
        }
    }

    public NotificationPermissionStatus GetPermissionStatus()
    {
        if (!IsSupported)
        {
            return NotificationPermissionStatus.Unsupported;
        }

        try
        {
            var access = UserNotificationListener.Current.GetAccessStatus();
            return MapAccessStatus(access);
        }
        catch
        {
            return _wasPreviouslyAllowed
                ? NotificationPermissionStatus.Revoked
                : NotificationPermissionStatus.Denied;
        }
    }

    public async Task<NotificationPermissionStatus> RequestPermissionAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return NotificationPermissionStatus.Unsupported;
        }

        try
        {
            var access = await UserNotificationListener.Current.RequestAccessAsync().AsTask(ct);
            return MapAccessStatus(access);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _wasPreviouslyAllowed
                ? NotificationPermissionStatus.Revoked
                : NotificationPermissionStatus.Denied;
        }
    }

    public async Task<IReadOnlyList<NotificationItem>> GetCurrentNotificationsAsync(CancellationToken ct = default)
    {
        if (_disposed || GetPermissionStatus() != NotificationPermissionStatus.Allowed)
        {
            return [];
        }

        await _enumLock.WaitAsync(ct);
        try
        {
            var rawList = await UserNotificationListener.Current
                .GetNotificationsAsync(NotificationKinds.Toast)
                .AsTask(ct);

            var normalized = new List<NotificationItem>(rawList.Count);
            var nowUtc = DateTimeOffset.UtcNow;

            foreach (var raw in rawList)
            {
                if (ct.IsCancellationRequested) break;
                var item = await TryNormalizeSingleNotificationAsync(raw, nowUtc);
                if (item is not null)
                {
                    normalized.Add(item);
                }
            }

            return normalized;
        }
        finally
        {
            if (!_disposed)
            {
                _enumLock.Release();
            }
        }
    }

    /// <summary>
    /// Normalizes an individual OS notification with defensive null/exception guards so one malformed
    /// notification never prevents other notifications from being processed.
    /// </summary>
    public async Task<NotificationItem?> TryNormalizeSingleNotificationAsync(
        UserNotification? raw,
        DateTimeOffset receivedAtUtc)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            uint osId = raw.Id;
            DateTimeOffset createdUtc;
            try
            {
                createdUtc = raw.CreationTime == default ? receivedAtUtc : raw.CreationTime.ToUniversalTime();
            }
            catch
            {
                createdUtc = receivedAtUtc;
            }

            string aumid = "unknown.app";
            string displayName = "Windows Notification";
            byte[]? logoBytes = null;

            try
            {
                var appInfo = raw.AppInfo;
                if (appInfo is not null)
                {
                    if (!string.IsNullOrWhiteSpace(appInfo.AppUserModelId))
                    {
                        aumid = appInfo.AppUserModelId.Trim();
                    }

                    var displayInfo = appInfo.DisplayInfo;
                    if (displayInfo is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(displayInfo.DisplayName))
                        {
                            displayName = displayInfo.DisplayName.Trim();
                        }

                        logoBytes = await TryGetCachedLogoBytesAsync(aumid, displayInfo);
                    }
                }
            }
            catch
            {
                // AppInfo may throw on unpackaged/unregistered senders; keep fallback values
            }

            string title = string.Empty;
            string body = string.Empty;

            try
            {
                var visual = raw.Notification?.Visual;
                var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric)
                              ?? visual?.Bindings?.FirstOrDefault();

                if (binding is not null)
                {
                    var textElements = binding.GetTextElements()
                        ?.Select(t => t?.Text?.Trim() ?? string.Empty)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

                    if (textElements is { Count: > 0 })
                    {
                        title = textElements[0];
                        if (textElements.Count > 1)
                        {
                            body = string.Join(Environment.NewLine, textElements.Skip(1));
                        }
                    }
                }
            }
            catch
            {
                // Visual binding may be empty or custom
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            {
                title = displayName;
                body = "New notification";
            }
            else if (string.IsNullOrWhiteSpace(title))
            {
                title = displayName;
            }

            bool explicitUrgency = false;
            try
            {
                var hints = raw.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.Hints;
                if (hints is not null &&
                    hints.TryGetValue("scenario", out var scenario) &&
                    (string.Equals(scenario, "alarm", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scenario, "incomingCall", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scenario, "reminder", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scenario, "urgent", StringComparison.OrdinalIgnoreCase)))
                {
                    explicitUrgency = true;
                }
            }
            catch
            {
            }

            return new NotificationItem
            {
                Id = NotificationItem.BuildDeterministicId(aumid, osId, createdUtc),
                OsNotificationId = osId,
                Source = new NotificationSource(aumid, displayName, logoBytes),
                Title = title,
                Body = body,
                CreatedAtUtc = createdUtc,
                ReceivedAtUtc = receivedAtUtc,
                IsPresentInOs = true,
                HasExplicitUrgencyHint = explicitUrgency
            };
        }
        catch
        {
            // Fault isolation: skip malformed notification
            return null;
        }
    }

    private async Task<byte[]?> TryGetCachedLogoBytesAsync(string aumid, global::Windows.ApplicationModel.AppDisplayInfo displayInfo)
    {
        if (_logoCache.TryGetValue(aumid, out var cached))
        {
            return cached;
        }

        try
        {
            var streamRef = displayInfo.GetLogo(new Size(32, 32));
            if (streamRef is null)
            {
                _logoCache[aumid] = null;
                return null;
            }

            using var winrtStream = await streamRef.OpenReadAsync();
            if (winrtStream is null || winrtStream.Size == 0 || winrtStream.Size > 256 * 1024)
            {
                _logoCache[aumid] = null;
                return null;
            }

            using var netStream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream((int)winrtStream.Size);
            await netStream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            _logoCache[aumid] = bytes;
            return bytes;
        }
        catch
        {
            _logoCache[aumid] = null;
            return null;
        }
    }

    public bool TryRemoveOsNotification(uint osNotificationId)
    {
        if (_disposed || GetPermissionStatus() != NotificationPermissionStatus.Allowed)
        {
            return false;
        }

        try
        {
            UserNotificationListener.Current.RemoveNotification(osNotificationId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private NotificationPermissionStatus MapAccessStatus(UserNotificationListenerAccessStatus status)
    {
        if (status == UserNotificationListenerAccessStatus.Allowed)
        {
            _wasPreviouslyAllowed = true;
            return NotificationPermissionStatus.Allowed;
        }

        if (_wasPreviouslyAllowed)
        {
            return NotificationPermissionStatus.Revoked;
        }

        return status switch
        {
            UserNotificationListenerAccessStatus.Denied => NotificationPermissionStatus.Denied,
            UserNotificationListenerAccessStatus.Unspecified => NotificationPermissionStatus.NotRequested,
            _ => NotificationPermissionStatus.Denied
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumLock.Dispose();
    }
}
