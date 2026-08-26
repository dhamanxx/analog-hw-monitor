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

    /// <summary>
    /// VU mode with both audio channels reading a steady -12 dBFS, which is 70 % of the
    /// -40..0 window. Channels 2-4 keep ordinary sensors so the frame stays five wide.
    /// </summary>
    private static (AppConfig Config, FakeSensorSource Sensors) VuModeAt(float dbfs)
    {
        var config = AppConfig.CreateDefault();
        VuModeSwitch.Set(config, true);
        config.Channels[2].SensorId = "ram-load";
        config.Channels[3].SensorId = "cpu-temp";
        config.Channels[4].SensorId = "gpu-temp";

        var sensors = new FakeSensorSource(new Dictionary<string, float?>
        {
            [AudioSensorIds.Left] = dbfs,
            [AudioSensorIds.Right] = dbfs,
            ["ram-load"] = 0f,
            ["cpu-temp"] = 30f,
            ["gpu-temp"] = 30f,
        });

        return (config, sensors);
    }

    [Fact]
    public void Tick_SendsOneFrameWithAllFiveChannels()
    {
        var sensors = SensorsAt(0, 50, 100, 30, 90);
        var link = new FakeMeterLink();
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(new[] { "V:0,128,255,0,255\n" }, link.Frames);
    }

    /// <summary>
    /// Refresh() is owned by SensorRefreshLoop on its own task. If Tick() called it too,
    /// in VU mode 99 ms of work would run 21 times a second on the UI thread.
    /// </summary>
    [Fact]
    public void Tick_DoesNotRefreshTheHardware()
    {
        var sensors = SensorsAt(10, 10, 10, 40, 40);
        using var service = new MonitorService(sensors, new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(0, sensors.RefreshCount);
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

    [Fact]
    public void Config_AssigningWrongChannelCountThrows()
    {
        using var service = new MonitorService(
            SensorsAt(0, 0, 0, 0, 0), new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);
        var badConfig = ConfigWithSensors();
        badConfig.Channels.RemoveAt(0);

        Assert.Throws<ArgumentException>(() => service.Config = badConfig);
    }

    [Fact]
    public void Constructor_WithWrongChannelCountThrows()
    {
        var badConfig = ConfigWithSensors();
        badConfig.Channels.RemoveAt(0);

        Assert.Throws<ArgumentException>(() =>
            new MonitorService(SensorsAt(0, 0, 0, 0, 0), new FakeMeterLink(), badConfig, NullLog.Instance));
    }

    /// <summary>
    /// The compensator's DC gain is 1, so a level that has not moved produces a command
    /// that has stopped moving too. This is the invariant that lets ChannelReading keep
    /// reporting the uncompensated percentage.
    ///
    /// The frame settles one LSB below the uncompensated byte, and that is arithmetic
    /// rather than a design error worth chasing. -12 dBFS is 70 % of the -40..0 window,
    /// and 70 % of 0..255 is 178.5 — the exact midpoint MeterCalibration rounds away from
    /// zero to 179. DC gain is 1 in exact arithmetic; in doubles the biquad's fixed point
    /// lands 4e-16 low, which is the last bit of a double and enough to fall off the far
    /// side of that midpoint. It costs 1/255 of full scale, on a panel meter whose own
    /// accuracy is a couple of percent, and only at levels sitting on a rounding midpoint.
    /// </summary>
    [Fact]
    public void Tick_CompensatedChannelsSettleOnTheUncompensatedValue()
    {
        var (config, sensors) = VuModeAt(-12f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        IReadOnlyList<ChannelReading> readings = Array.Empty<ChannelReading>();
        service.Updated += (_, r) => readings = r;

        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(40));
            service.Tick();
        }

        Assert.Equal("V:178,178,0,0,0\n", link.Frames[^1]);

        // Settled, so it is not still on its way anywhere: the previous frame is the same.
        Assert.Equal(link.Frames[^2], link.Frames[^1]);

        // And the reading is untouched by any of it — 70 %, PWM 179.
        Assert.Equal(70.0, readings[0].Percent, precision: 3);
        Assert.Equal(179, readings[0].Pwm);
    }

    /// <summary>
    /// On the tick where the level steps, the compensator must lead: the needle is
    /// under-damped, so cancelling it means over-driving first. A frame identical to the
    /// uncompensated one here means the compensator is not in the path at all.
    /// </summary>
    [Fact]
    public void Tick_CompensatedChannelsLeadAStep()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        sensors.Set(AudioSensorIds.Right, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        // 74.43 % rather than 70 %, which is PWM 190 rather than 179.
        Assert.Equal("V:190,190,0,0,0\n", link.Frames[^1]);
    }

    /// <summary>
    /// The settings window reads ChannelReading and uses it to calibrate. It must show what
    /// the level means, not the transient control signal on its way to the meter.
    /// </summary>
    [Fact]
    public void Tick_ReportsTheUncompensatedValueEvenWhileLeadingAStep()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        IReadOnlyList<ChannelReading> readings = Array.Empty<ChannelReading>();
        service.Updated += (_, r) => readings = r;

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.Equal(70.0, readings[0].Percent, precision: 3);
        Assert.Equal(179, readings[0].Pwm);
    }

    /// <summary>
    /// Outside VU mode the tick is 1 Hz, far below what the needle's 578 ms ring period
    /// needs, and the channels that stay there do not step anyway. Compensating them would
    /// only add error.
    /// </summary>
    [Fact]
    public void Tick_DoesNotCompensateOutsideVuMode()
    {
        var sensors = SensorsAt(0, 50, 100, 30, 90);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(1000));
        service.Tick();
        sensors.Set("cpu-load", 100f);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        service.Tick();

        // Straight through, exactly as it was before the compensator existed.
        Assert.Equal("V:255,128,255,0,255\n", link.Frames[^1]);
    }

    /// <summary>
    /// A channel under the calibration slider is pinned to a raw PWM value. If the
    /// compensator shaped it, the slider would not hold.
    /// </summary>
    [Fact]
    public void Tick_DoesNotCompensateAChannelUnderTestPwm()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        service.SetTestPwm(0, 200);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();
        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.StartsWith("V:200,", link.Frames[^1]);

        // Releasing the slider must not resume on history from before it was touched:
        // the compensator was reset every pinned tick, so the first free tick primes.
        service.SetTestPwm(0, null);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.StartsWith("V:179,", link.Frames[^1]);
    }

    /// <summary>
    /// A dead channel must not leave history that kicks the needle when it comes back. The
    /// reset makes the first tick after recovery prime rather than lead, so it sends the
    /// uncompensated value.
    /// </summary>
    [Fact]
    public void Tick_ResetsACompensatorWhileItsSensorIsUnreadable()
    {
        var (config, sensors) = VuModeAt(-12f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, null);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        // Primed, not leading: 70 % straight through.
        Assert.StartsWith("V:179,", link.Frames[^1]);
    }

    /// <summary>
    /// The tray and the settings window turn VU mode on by mutating the configuration the
    /// service already holds, so the Config setter never runs and cannot be what drops the
    /// history. Ticking through a mode change must still prime rather than lead, otherwise
    /// the first VU tick is kicked by a percentage left over from a load sensor.
    /// </summary>
    [Fact]
    public void Tick_ResetsTheCompensatorsWhenVuModeIsToggledInPlace()
    {
        var (config, sensors) = VuModeAt(-12f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        // Settle in VU mode, so the compensators hold a full 70 % history.
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(40));
            service.Tick();
        }

        // Out of VU mode and straight back, exactly as the tray menu does it.
        VuModeSwitch.Set(service.Config, false);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        VuModeSwitch.Set(service.Config, true);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.StartsWith("V:179,179,", link.Frames[^1]);
    }
}
