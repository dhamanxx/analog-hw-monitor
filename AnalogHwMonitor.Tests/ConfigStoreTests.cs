using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahm-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void CreateDefault_DescribesTheFiveChannelsInOrder()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(FrameCodec.ChannelCount, config.Channels.Count);
        Assert.Equal(new[] { 3, 5, 6, 9, 10 }, config.Channels.Select(c => c.Pin));
        Assert.Equal(0, config.Channels[0].Min);
        Assert.Equal(100, config.Channels[0].Max);
        Assert.Equal(30, config.Channels[3].Min);
        Assert.Equal(90, config.Channels[3].Max);
        Assert.All(config.Channels, c => Assert.Equal(0, c.MinPwm));
        Assert.All(config.Channels, c => Assert.Equal(255, c.MaxPwm));
        Assert.All(config.Channels, c => Assert.Null(c.SensorId));
    }

    [Fact]
    public void Load_WritesDefaultsWhenFileIsMissing()
    {
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.CreatedDefault, result.Outcome);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }

    [Fact]
    public void Save_UsesCamelCaseJson()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault();
        config.ComPort = "COM7";

        store.Save(config);

        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"comPort\": \"COM7\"", json);
        Assert.Contains("\"minPwm\"", json);
    }

    [Fact]
    public void Load_RoundTripsASavedConfiguration()
    {
        var store = new ConfigStore(ConfigPath);
        var saved = AppConfig.CreateDefault();
        saved.ComPort = "COM7";
        saved.StartWithWindows = true;
        saved.Channels[0].SensorId = "/amdcpu/0/load/0";
        saved.Channels[0].MinPwm = 12;
        store.Save(saved);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Equal("COM7", result.Config.ComPort);
        Assert.True(result.Config.StartWithWindows);
        Assert.Equal("/amdcpu/0/load/0", result.Config.Channels[0].SensorId);
        Assert.Equal(12, result.Config.Channels[0].MinPwm);
    }

    [Fact]
    public void Load_BacksUpAndReplacesCorruptedJson()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath + ".bak"));
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
        Assert.Contains("\"channels\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_TreatsAWrongChannelCountAsCorrupt()
    {
        File.WriteAllText(ConfigPath, "{ \"comPort\": \"COM1\", \"channels\": [] }");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }

    [Fact]
    public void Load_TreatsANullChannelsListAsCorrupt()
    {
        File.WriteAllText(ConfigPath, "{ \"comPort\": \"COM1\", \"channels\": null }");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }

    [Fact]
    public void Load_TreatsANullChannelElementAsCorrupt()
    {
        File.WriteAllText(ConfigPath, "{ \"channels\": [null, null, null, null, null] }");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }

    [Fact]
    public void Load_OverwritesAPreExistingBackupFile()
    {
        File.WriteAllText(ConfigPath + ".bak", "stale backup content");
        File.WriteAllText(ConfigPath, "{ this is not json");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void CreateDefault_StartsWithVuModeOffAndVolumeCompensationOn()
    {
        var config = AppConfig.CreateDefault();

        Assert.False(config.VuMode);
        Assert.True(config.VuCompensateVolume);
        Assert.Empty(config.StashedChannels);
    }

    [Fact]
    public void Load_RoundTripsTheVuFieldsAndTheStash()
    {
        var written = AppConfig.CreateDefault();
        written.VuMode = true;
        written.VuCompensateVolume = false;
        written.StashedChannels = new List<ChannelProfile>
        {
            new() { Channel = 0, Label = "CPU Load", SensorId = "/amdcpu/0/load/0", Min = 0, Max = 100 },
            new() { Channel = 1, Label = "GPU Load", SensorId = "/gpu-nvidia/0/load/0", Min = 0, Max = 100 },
        };
        new ConfigStore(ConfigPath).Save(written);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.True(result.Config.VuMode);
        Assert.False(result.Config.VuCompensateVolume);
        Assert.Equal(2, result.Config.StashedChannels.Count);
        Assert.Equal(1, result.Config.StashedChannels[1].Channel);
        Assert.Equal("/amdcpu/0/load/0", result.Config.StashedChannels[0].SensorId);
    }

    /// <summary>
    /// Every config.json written before this feature existed has none of the three new
    /// fields. Volume compensation is the only one whose sensible default is not the
    /// zero value, so it is the one that would break by being read as false.
    /// </summary>
    [Fact]
    public void Load_DefaultsVolumeCompensationToTrueInAConfigThatPredatesIt()
    {
        File.WriteAllText(ConfigPath, """
        {
          "comPort": "COM3",
          "startWithWindows": false,
          "channels": [
            { "pin": 3,  "label": "CPU Load", "min": 0,  "max": 100, "minPwm": 0, "maxPwm": 255 },
            { "pin": 5,  "label": "GPU Load", "min": 0,  "max": 100, "minPwm": 0, "maxPwm": 255 },
            { "pin": 6,  "label": "Memory",   "min": 0,  "max": 100, "minPwm": 0, "maxPwm": 255 },
            { "pin": 9,  "label": "CPU Temp", "min": 30, "max": 90,  "minPwm": 0, "maxPwm": 255 },
            { "pin": 10, "label": "GPU Temp", "min": 30, "max": 90,  "minPwm": 0, "maxPwm": 255 }
          ]
        }
        """);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.False(result.Config.VuMode);
        Assert.True(result.Config.VuCompensateVolume);
        Assert.Empty(result.Config.StashedChannels);
    }

    /// <summary>
    /// The stash is a convenience beside ten calibration points that cost real time
    /// with a screwdriver. A hand-edit that breaks the stash must cost the stash and
    /// nothing else — which is why it is sanitized rather than run through IsValid.
    /// </summary>
    [Fact]
    public void Load_DropsAnUnusableStashAndKeepsEverythingElse()
    {
        var written = AppConfig.CreateDefault();
        written.VuMode = true;
        written.Channels[0].MinPwm = 4;
        written.Channels[0].MaxPwm = 249;
        written.StashedChannels = new List<ChannelProfile>
        {
            new() { Channel = 0, Label = "CPU Load" },
            new() { Channel = 0, Label = "GPU Load" },   // duplicate index
        };
        new ConfigStore(ConfigPath).Save(written);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Empty(result.Config.StashedChannels);
        Assert.True(result.Config.VuMode);
        Assert.Equal(4, result.Config.Channels[0].MinPwm);
        Assert.Equal(249, result.Config.Channels[0].MaxPwm);
    }
}
