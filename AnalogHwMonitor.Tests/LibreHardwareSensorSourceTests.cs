using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// These need real hardware and an elevated session, so they only run when
/// AHM_HARDWARE_TESTS=1. Everywhere else they report themselves as skipped.
/// </summary>
public class LibreHardwareSensorSourceTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("AHM_HARDWARE_TESTS") == "1";

    [SkippableFact]
    public void Discover_FindsLoadAndTemperatureSensors()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var sensors = source.Discover();

        Assert.Contains(sensors, s => s.Kind == SensorKind.Load);
        Assert.Contains(sensors, s => s.Kind == SensorKind.Temperature);
        Assert.All(sensors, s => Assert.False(string.IsNullOrWhiteSpace(s.Id)));
    }

    [SkippableFact]
    public void Read_ReturnsAValueForADiscoveredSensor()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var cpuLoad = source.Discover().First(s => s.Kind == SensorKind.Load);

        Assert.NotNull(source.Read(cpuLoad.Id));
    }

    [SkippableFact]
    public void Read_ReturnsNullForAnUnknownSensor()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();

        Assert.Null(source.Read("/nothing/like/this"));
    }

    [SkippableFact]
    public void DefaultAssignment_FillsEveryChannelOnThisMachine()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, source.Discover());

        Assert.All(config.Channels, c => Assert.False(string.IsNullOrEmpty(c.SensorId)));
    }

    /// <summary>
    /// Discover() and Read() must read a snapshot built in Refresh(), not walk
    /// the live tree — otherwise the tree walk goes back on the UI thread and Refresh()
    /// on the poll task mutates it under their feet. The identity of the returned list
    /// proves it: today it creates a new List on every call, after the change it is the
    /// same object until the next Refresh().
    /// </summary>
    [SkippableFact]
    public void Discover_ReturnsTheSameSnapshotUntilTheNextRefresh()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();

        var first = source.Discover();

        Assert.Same(first, source.Discover());

        source.Refresh();

        Assert.NotSame(first, source.Discover());
    }
}
