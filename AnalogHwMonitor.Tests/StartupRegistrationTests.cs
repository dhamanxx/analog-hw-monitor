using AnalogHwMonitor.Core;
using Microsoft.Win32;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class StartupRegistrationTests : IDisposable
{
    // A scratch key so the tests never touch the real Run key.
    private const string TestSubKey = @"Software\AnalogHwMonitor\StartupTests";

    private readonly StartupRegistration _registration = new(TestSubKey, "AnalogHwMonitorTest");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestSubKey, throwOnMissingSubKey: false);

    [Fact]
    public void IsEnabled_IsFalseBeforeAnythingIsRegistered()
    {
        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void SetEnabled_WritesTheQuotedExecutablePath()
    {
        _registration.SetEnabled(true, @"C:\Program Files\Analog HW Monitor\AnalogHwMonitor.App.exe");

        Assert.True(_registration.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(TestSubKey);
        Assert.Equal(
            "\"C:\\Program Files\\Analog HW Monitor\\AnalogHwMonitor.App.exe\"",
            key!.GetValue("AnalogHwMonitorTest"));
    }

    [Fact]
    public void SetEnabled_FalseRemovesTheEntry()
    {
        _registration.SetEnabled(true, @"C:\app.exe");
        _registration.SetEnabled(false, @"C:\app.exe");

        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void SetEnabled_FalseIsHarmlessWhenNothingIsRegistered()
    {
        _registration.SetEnabled(false, @"C:\app.exe");

        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void RunSubKey_PointsAtTheWindowsRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunSubKey);
    }
}
