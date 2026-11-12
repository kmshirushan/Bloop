namespace Bloop.Windows.Notifications;

/// <summary>
/// Boundary service for Windows UserNotificationListener integration (Milestone 2+).
/// Per Milestone 1 requirements, notification integration is scoped out of Milestone 1 execution.
/// </summary>
public sealed class NotificationService
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
}

