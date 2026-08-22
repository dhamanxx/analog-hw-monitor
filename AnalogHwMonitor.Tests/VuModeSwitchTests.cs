using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

public class VuModeSwitchTests
{
    /// <summary>A config as it looks after a first run: sensors assigned, meters
    /// calibrated by hand, VU mode never touched.</summary>
    private static AppConfig Calibrated()
    {
        var config = AppConfig.CreateDefault();
        config.Channels[0].SensorId = "/amdcpu/0/load/0";
        config.Channels[1].SensorId = "/gpu-nvidia/0/load/0";
        config.Channels[2].SensorId = "/ram/load/0";
        config.Channels[0].MinPwm = 4;
        config.Channels[0].MaxPwm = 249;
        config.Channels[1].MinPwm = 2;
        config.Channels[1].MaxPwm = 251;
        return config;
    }

    [Fact]
    public void Set_TurningOnPutsTheAudioLevelsOnTheFirstTwoChannels()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);

        Assert.True(config.VuMode);
        Assert.Equal(AudioSensorIds.Left, config.Channels[0].SensorId);
        Assert.Equal(AudioSensorIds.Right, config.Channels[1].SensorId);
        Assert.Equal("VU Left", config.Channels[0].Label);
        Assert.Equal("VU Right", config.Channels[1].Label);
        Assert.Equal(-40, config.Channels[0].Min);
        Assert.Equal(0, config.Channels[0].Max);
    }

    [Fact]
    public void Set_TurningOnParksTheSensorProfilesInTheStash()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);

        Assert.Equal(2, config.StashedChannels.Count);
        Assert.Equal(0, config.StashedChannels[0].Channel);
        Assert.Equal("CPU Load", config.StashedChannels[0].Label);
        Assert.Equal("/amdcpu/0/load/0", config.StashedChannels[0].SensorId);
        Assert.Equal(1, config.StashedChannels[1].Channel);
        Assert.Equal("/gpu-nvidia/0/load/0", config.StashedChannels[1].SensorId);
    }

    /// <summary>
    /// The calibration points belong to the physical meter, not to the signal. A
    /// switch that reset them would cost the user a screwdriver and ten minutes.
    /// </summary>
    [Fact]
    public void Set_NeverTouchesThePinOrTheCalibration()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);

        Assert.Equal(3, config.Channels[0].Pin);
        Assert.Equal(4, config.Channels[0].MinPwm);
        Assert.Equal(249, config.Channels[0].MaxPwm);
        Assert.Equal(5, config.Channels[1].Pin);
        Assert.Equal(2, config.Channels[1].MinPwm);
        Assert.Equal(251, config.Channels[1].MaxPwm);
    }

    [Fact]
    public void Set_LeavesTheOtherThreeChannelsAlone()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);

        Assert.Equal("Memory", config.Channels[2].Label);
        Assert.Equal("/ram/load/0", config.Channels[2].SensorId);
        Assert.Equal("CPU Temp", config.Channels[3].Label);
        Assert.Equal(30, config.Channels[3].Min);
    }

    [Fact]
    public void Set_TurningOffRestoresTheSensorChannels()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);
        VuModeSwitch.Set(config, false);

        Assert.False(config.VuMode);
        Assert.Equal("/amdcpu/0/load/0", config.Channels[0].SensorId);
        Assert.Equal("CPU Load", config.Channels[0].Label);
        Assert.Equal(0, config.Channels[0].Min);
        Assert.Equal(100, config.Channels[0].Max);
        Assert.Equal(4, config.Channels[0].MinPwm);
    }

    /// <summary>
    /// The whole reason the switch is an exchange rather than an overwrite: a dB range
    /// tuned while VU mode was on has to survive being switched away from and back.
    /// </summary>
    [Fact]
    public void Set_KeepsATunedRangeOnBothSidesOfTheSwitch()
    {
        var config = Calibrated();

        VuModeSwitch.Set(config, true);
        config.Channels[0].Min = -55;               // the user widens the dB window
        config.Channels[1].Min = -55;
        VuModeSwitch.Set(config, false);
        config.Channels[0].Max = 90;                // and narrows the load scale
        VuModeSwitch.Set(config, true);

        Assert.Equal(-55, config.Channels[0].Min);

        VuModeSwitch.Set(config, false);

        Assert.Equal(90, config.Channels[0].Max);
    }

    [Fact]
    public void Set_DoesNothingWhenTheModeIsAlreadyWhatWasAskedFor()
    {
        var config = Calibrated();
        VuModeSwitch.Set(config, true);
        var stashedBefore = config.StashedChannels;

        VuModeSwitch.Set(config, true);

        Assert.Same(stashedBefore, config.StashedChannels);
        Assert.Equal(AudioSensorIds.Left, config.Channels[0].SensorId);
    }

    /// <summary>
    /// A config that says VU mode is on but has lost its stash — hand-edited, or
    /// written by a version that stashed nothing. Switching off must still produce
    /// usable sensor channels; a null SensorId is what SensorDefaults fills in on the
    /// next start.
    /// </summary>
    [Fact]
    public void Set_FallsBackToTheFirstRunDefaultsWhenTheStashIsGone()
    {
        var config = Calibrated();
        VuModeSwitch.Set(config, true);
        config.StashedChannels = new List<ChannelProfile>();

        VuModeSwitch.Set(config, false);

        Assert.Equal("CPU Load", config.Channels[0].Label);
        Assert.Equal("GPU Load", config.Channels[1].Label);
        Assert.Null(config.Channels[0].SensorId);
        Assert.Equal(0, config.Channels[0].Min);
        Assert.Equal(100, config.Channels[0].Max);
        Assert.Equal(4, config.Channels[0].MinPwm);
    }

    [Fact]
    public void IsUsableStash_AcceptsExactlyOneProfilePerVuChannel()
    {
        var stash = new List<ChannelProfile>
        {
            new() { Channel = 0 },
            new() { Channel = 1 },
        };

        Assert.True(VuModeSwitch.IsUsableStash(stash));
    }

    [Fact]
    public void IsUsableStash_RejectsEmptyWrongCountDuplicateAndOutOfRange()
    {
        Assert.False(VuModeSwitch.IsUsableStash(null));
        Assert.False(VuModeSwitch.IsUsableStash(new List<ChannelProfile>()));
        Assert.False(VuModeSwitch.IsUsableStash(new List<ChannelProfile> { new() { Channel = 0 } }));
        Assert.False(VuModeSwitch.IsUsableStash(new List<ChannelProfile>
        {
            new() { Channel = 0 },
            new() { Channel = 0 },
        }));
        Assert.False(VuModeSwitch.IsUsableStash(new List<ChannelProfile>
        {
            new() { Channel = 0 },
            new() { Channel = 4 },
        }));
    }
}
