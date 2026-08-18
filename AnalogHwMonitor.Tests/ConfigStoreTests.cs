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
}
