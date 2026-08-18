using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// Reading ACPI thermal zones needs an elevated session, so the two hardware tests run
/// only when AHM_HARDWARE_TESTS=1 and report themselves as skipped otherwise. The third
/// runs everywhere on purpose: degrading to nothing is the required behaviour.
/// </summary>
public class AcpiThermalSensorSourceTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("AHM_HARDWARE_TESTS") == "1";

    [SkippableFact]
    public void Discover_FindsThermalZones()
    {
        Skip.IfNot(Enabled);

        using var source = new AcpiThermalSensorSource(NullLog.Instance);
        source.Refresh();
        var zones = source.Discover();

        Assert.NotEmpty(zones);
        Assert.All(zones, z => Assert.Equal(SensorKind.Temperature, z.Kind));
        Assert.All(zones, z => Assert.StartsWith(AcpiThermalSensorSource.IdPrefix, z.Id));
    }

    [SkippableFact]
    public void Read_ReturnsAPlausibleTemperature()
    {
        Skip.IfNot(Enabled);

        using var source = new AcpiThermalSensorSource(NullLog.Instance);
        source.Refresh();
        var zone = source.Discover().First();

        var value = source.Read(zone.Id);

        Assert.NotNull(value);
        Assert.InRange(value!.Value, -50f, 150f);
    }

    [Fact]
    public void Refresh_DegradesToNothingWhenTheQueryIsDenied()
    {
        using var source = new AcpiThermalSensorSource(NullLog.Instance);

        source.Refresh();

        Assert.Null(source.Read(AcpiThermalSensorSource.IdPrefix + "NOPE"));
    }
}
