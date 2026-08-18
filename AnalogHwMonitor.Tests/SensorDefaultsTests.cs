using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class SensorDefaultsTests
{
    private static readonly SensorDescriptor[] AmdMachine =
    {
        new("/amdcpu/0/load/0",       "CPU Total",         "AMD Ryzen 7 5800X", SensorKind.Load,        "%"),
        new("/amdcpu/0/load/1",       "CPU Core #1",       "AMD Ryzen 7 5800X", SensorKind.Load,        "%"),
        new("/amdcpu/0/temperature/0","Core (Tctl/Tdie)",  "AMD Ryzen 7 5800X", SensorKind.Temperature, "°C"),
        new("/gpu-nvidia/0/load/0",   "GPU Core",          "NVIDIA RTX 3070",   SensorKind.Load,        "%"),
        new("/gpu-nvidia/0/load/3",   "GPU Memory",        "NVIDIA RTX 3070",   SensorKind.Load,        "%"),
        new("/gpu-nvidia/0/temperature/0", "GPU Core",     "NVIDIA RTX 3070",   SensorKind.Temperature, "°C"),
        new("/ram/load/0",            "Memory",            "Generic Memory",    SensorKind.Load,        "%"),
        new("/lpc/nct6798d/fan/1",    "Fan #2",            "Motherboard",       SensorKind.Other,       "RPM"),
    };

    private static readonly SensorDescriptor[] IntelMachine =
    {
        new("/intelcpu/0/load/0",        "CPU Total",   "Intel Core i7-12700K", SensorKind.Load,        "%"),
        new("/intelcpu/0/temperature/8", "CPU Package", "Intel Core i7-12700K", SensorKind.Temperature, "°C"),
        new("/gpu-intel/0/load/0",       "GPU Core",    "Intel UHD 770",        SensorKind.Load,        "%"),
        new("/gpu-intel/0/temperature/0","GPU Core",    "Intel UHD 770",        SensorKind.Temperature, "°C"),
        new("/ram/load/0",               "Memory",      "Generic Memory",       SensorKind.Load,        "%"),
    };

    [Fact]
    public void AssignSensors_PicksTheExpectedSensorsOnAnAmdMachine()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/load/0", config.Channels[0].SensorId);
        Assert.Equal("/gpu-nvidia/0/load/0", config.Channels[1].SensorId);
        Assert.Equal("/ram/load/0", config.Channels[2].SensorId);
        Assert.Equal("/amdcpu/0/temperature/0", config.Channels[3].SensorId);
        Assert.Equal("/gpu-nvidia/0/temperature/0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_PicksTheExpectedSensorsOnAnIntelMachine()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, IntelMachine);

        Assert.Equal("/intelcpu/0/load/0", config.Channels[0].SensorId);
        Assert.Equal("/gpu-intel/0/load/0", config.Channels[1].SensorId);
        Assert.Equal("/ram/load/0", config.Channels[2].SensorId);
        Assert.Equal("/intelcpu/0/temperature/8", config.Channels[3].SensorId);
        Assert.Equal("/gpu-intel/0/temperature/0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_DoesNotMistakeGpuMemoryForSystemMemory()
    {
        var withoutSystemRam = AmdMachine.Where(s => s.Id != "/ram/load/0").ToArray();
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, withoutSystemRam);

        Assert.Null(config.Channels[2].SensorId);
    }

    [Fact]
    public void AssignSensors_LeavesChannelsEmptyWhenNothingMatches()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, Array.Empty<SensorDescriptor>());

        Assert.All(config.Channels, c => Assert.Null(c.SensorId));
    }

    [Fact]
    public void AssignSensors_NeverOverwritesAChoiceTheUserAlreadyMade()
    {
        var config = AppConfig.CreateDefault();
        config.Channels[0].SensorId = "/amdcpu/0/load/1";

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/load/1", config.Channels[0].SensorId);
    }

    [Fact]
    public void Display_CombinesHardwareAndSensorName()
    {
        Assert.Equal("AMD Ryzen 7 5800X · CPU Total", AmdMachine[0].Display);
    }
}
