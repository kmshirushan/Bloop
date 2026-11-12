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
}
