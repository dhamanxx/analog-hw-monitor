using System.Text.Json;

namespace AnalogHwMonitor.Core;

public enum ConfigLoadOutcome
{
    Loaded,
    CreatedDefault,
    RecoveredFromCorrupt,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadOutcome Outcome);

/// <summary>Reads and writes config.json next to the executable.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public ConfigStore(string path) => Path = path;

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    public ConfigLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, ConfigLoadOutcome.CreatedDefault);
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options)
                         ?? throw new InvalidDataException("config.json contains null.");

            if (config.Channels.Count != FrameCodec.ChannelCount)
            {
                throw new InvalidDataException(
                    $"config.json must contain exactly {FrameCodec.ChannelCount} channels.");
            }

            return new ConfigLoadResult(config, ConfigLoadOutcome.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            File.Move(Path, BackupPath, overwrite: true);
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, ConfigLoadOutcome.RecoveredFromCorrupt);
        }
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Options));
}
