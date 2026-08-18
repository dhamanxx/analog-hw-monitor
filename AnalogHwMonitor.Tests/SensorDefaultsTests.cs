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

    [Fact]
    public void AssignSensors_NeverBindsGpuChannelsToAMemorySensor()
    {
        var gpuWithOnlyMemorySensors = new[]
        {
            new SensorDescriptor("/amdcpu/0/load/0",        "CPU Total",        "AMD Ryzen 7 5800X", SensorKind.Load,        "%"),
            new SensorDescriptor("/amdcpu/0/temperature/0", "Core (Tctl/Tdie)", "AMD Ryzen 7 5800X", SensorKind.Temperature, "°C"),
            new SensorDescriptor("/gpu-nvidia/0/load/3",    "GPU Memory",       "NVIDIA RTX 3070",    SensorKind.Load,        "%"),
            new SensorDescriptor("/gpu-nvidia/0/temperature/1", "GPU Memory Junction Temperature", "NVIDIA RTX 3070", SensorKind.Temperature, "°C"),
            new SensorDescriptor("/ram/load/0",             "Memory",           "Generic Memory",     SensorKind.Load,        "%"),
        };
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, gpuWithOnlyMemorySensors);

        Assert.Null(config.Channels[1].SensorId);
        Assert.Null(config.Channels[4].SensorId);
    }

    private static readonly SensorDescriptor[] HvciBlockedMachine =
    {
        new("/intelcpu/0/load/0",             "CPU Total",    "Intel Core i7-1355U", SensorKind.Load,        "%"),
        new("/intelcpu/0/temperature/12",     "CPU Package",  "Intel Core i7-1355U", SensorKind.Temperature, "°C"),
        new("/intelcpu/0/temperature/1",      "Core Average", "Intel Core i7-1355U", SensorKind.Temperature, "°C"),
        new("/gpu-intel-integrated/x/load/7", "D3D 3D",       "Intel Iris Xe",       SensorKind.Load,        "%"),
        new("/gpu-intel-integrated/x/load/8", "D3D Copy",     "Intel Iris Xe",       SensorKind.Load,        "%"),
        new("/ram/load/0",                    "Memory",       "Total Memory",        SensorKind.Load,        "%"),
        new("/acpi/thermalzone/CPUZ_0",       "CPUZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
        new("/acpi/thermalzone/GFXZ_0",       "GFXZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
        new("/acpi/thermalzone/PCHZ_0",       "PCHZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
    };

    /// <summary>On the blocked machine every CPU-package temperature reads null.</summary>
    private static bool ReadableOnBlockedMachine(string id) =>
        !id.StartsWith("/intelcpu/0/temperature", StringComparison.Ordinal);

    [Fact]
    public void AssignSensors_FindsIntelIntegratedGpuLoadByItsD3dName()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.Equal("/gpu-intel-integrated/x/load/7", config.Channels[1].SensorId);
    }

    [Fact]
    public void AssignSensors_PrefersAReadableAcpiZoneOverADeadCpuPackageSensor()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.Equal("/acpi/thermalzone/CPUZ_0", config.Channels[3].SensorId);
        Assert.Equal("/acpi/thermalzone/GFXZ_0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_StillPrefersTheVendorSensorWhenItIsReadable()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, _ => true);

        Assert.Equal("/intelcpu/0/temperature/12", config.Channels[3].SensorId);
    }

    [Fact]
    public void AssignSensors_NeverPicksAnUnrelatedThermalZone()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.DoesNotContain("PCHZ_0", string.Join(",", config.Channels.Select(c => c.SensorId)));
    }

    [Fact]
    public void AssignSensors_WithoutAReadabilityPredicateBehavesAsBefore()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/temperature/0", config.Channels[3].SensorId);
    }

    /// <summary>
    /// The memory rule's id hints are ["physical-memory", "/ram"]: a preference hint
    /// alongside a mandatory one, with the mandatory hint deliberately not first, so an
    /// implementation that only inspected the first hint could not fake its way past
    /// this. Neither hint appears anywhere in this machine's sensor ids, so the rule
    /// must not widen to "any Load sensor" — which would otherwise hand the memory
    /// channel a GPU memory-load sensor.
    /// </summary>
    [Fact]
    public void AssignSensors_MandatoryHintStillBlocksWideningWhenPairedWithAPreferenceHint()
    {
        var machineWithNoMatchingMemoryHint = new[]
        {
            new SensorDescriptor("/amdcpu/0/load/0",     "CPU Total",  "AMD Ryzen 7 5800X", SensorKind.Load, "%"),
            new SensorDescriptor("/gpu-nvidia/0/load/3", "GPU Memory", "NVIDIA RTX 3070",    SensorKind.Load, "%"),
        };
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, machineWithNoMatchingMemoryHint);

        Assert.Null(config.Channels[2].SensorId);
    }
}
