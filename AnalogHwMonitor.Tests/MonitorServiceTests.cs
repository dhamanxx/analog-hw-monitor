using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class MonitorServiceTests
{
    private static AppConfig ConfigWithSensors()
    {
        var config = AppConfig.CreateDefault();
        config.Channels[0].SensorId = "cpu-load";
        config.Channels[1].SensorId = "gpu-load";
        config.Channels[2].SensorId = "ram-load";
        config.Channels[3].SensorId = "cpu-temp";
        config.Channels[4].SensorId = "gpu-temp";
        return config;
    }

    private static FakeSensorSource SensorsAt(float cpuLoad, float gpuLoad, float ram, float cpuTemp, float gpuTemp) =>
        new(new Dictionary<string, float?>
        {
            ["cpu-load"] = cpuLoad,
            ["gpu-load"] = gpuLoad,
            ["ram-load"] = ram,
            ["cpu-temp"] = cpuTemp,
            ["gpu-temp"] = gpuTemp,
        });

    [Fact]
    public void Tick_SendsOneFrameWithAllFiveChannels()
    {
        var sensors = SensorsAt(0, 50, 100, 30, 90);
        var link = new FakeMeterLink();
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(new[] { "V:0,128,255,0,255\n" }, link.Frames);
    }

    [Fact]
    public void Tick_RefreshesTheHardwareExactlyOnce()
    {
        var sensors = SensorsAt(10, 10, 10, 40, 40);
        using var service = new MonitorService(sensors, new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(1, sensors.RefreshCount);
    }

    [Fact]
    public void Tick_SendsZeroForAMissingSensorAndKeepsTheOthersRunning()
    {
        var sensors = new FakeSensorSource(new Dictionary<string, float?>
        {
            ["cpu-load"] = 50,
            ["gpu-load"] = null,      // GPU was swapped out
            ["ram-load"] = 50,
            ["cpu-temp"] = 60,
            ["gpu-temp"] = null,
        });
        var link = new FakeMeterLink();
        IReadOnlyList<ChannelReading>? readings = null;
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance);
        service.Updated += (_, r) => readings = r;

        service.Tick();

        Assert.Equal(new[] { "V:128,0,128,128,0\n" }, link.Frames);
        Assert.False(readings![0].SensorMissing);
        Assert.True(readings[1].SensorMissing);
        Assert.Equal(0, readings[1].Pwm);
    }

    [Fact]
    public void Tick_DoesNotQueryAnUnassignedChannel()
    {
        var config = ConfigWithSensors();
        config.Channels[4].SensorId = null;
        var sensors = SensorsAt(0, 0, 0, 30, 30);
        using var service = new MonitorService(sensors, new FakeMeterLink(), config, NullLog.Instance);

        service.Tick();

        Assert.DoesNotContain("gpu-temp", sensors.ReadIds);
    }

    [Fact]
    public void Tick_RespectsPerChannelCalibration()
    {
        var config = ConfigWithSensors();
        config.Channels[0].MinPwm = 12;
        config.Channels[0].MaxPwm = 240;
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(50, 0, 0, 30, 30), link, config, NullLog.Instance);

        service.Tick();

        Assert.StartsWith("V:126,", link.Frames[0]);
    }

    [Fact]
    public void SetTestPwm_OverridesOneChannelAndLeavesTheRestOnTheirSensors()
    {
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(0, 50, 0, 30, 30), link, ConfigWithSensors(), NullLog.Instance);
        IReadOnlyList<ChannelReading>? readings = null;
        service.Updated += (_, r) => readings = r;

        service.SetTestPwm(0, 200);
        service.Tick();

        Assert.StartsWith("V:200,128,", link.Frames[0]);
        Assert.True(readings![0].TestMode);
        Assert.False(readings[1].TestMode);
    }

    [Fact]
    public void SetTestPwm_WithNullReturnsTheChannelToItsSensor()
    {
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(100, 0, 0, 30, 30), link, ConfigWithSensors(), NullLog.Instance);

        service.SetTestPwm(0, 200);
        service.SetTestPwm(0, null);
        service.Tick();

        Assert.StartsWith("V:255,", link.Frames[0]);
    }

    [Fact]
    public void Updated_ReportsEveryChannelWithItsRawValue()
    {
        IReadOnlyList<ChannelReading>? readings = null;
        using var service = new MonitorService(
            SensorsAt(25, 0, 0, 60, 30), new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);
        service.Updated += (_, r) => readings = r;

        service.Tick();

        Assert.Equal(FrameCodec.ChannelCount, readings!.Count);
        Assert.Equal("CPU Load", readings[0].Label);
        Assert.Equal(25f, readings[0].Value);
        Assert.Equal(25, readings[0].Percent, 3);
        Assert.Equal(50, readings[3].Percent, 3);   // 60 °C on a 30-90 range
    }
}
