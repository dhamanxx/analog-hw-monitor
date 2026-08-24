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

    /// <summary>
    /// Loads the configuration. Never throws: any missing file, unparseable JSON,
    /// structurally invalid content, or filesystem failure (including failures while
    /// attempting to back up or rewrite the file) is handled internally, falling back to
    /// an in-memory default configuration rather than letting the application fail to start.
    /// </summary>
    public ConfigLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = AppConfig.CreateDefault();
            TrySave(fresh);
            return new ConfigLoadResult(fresh, ConfigLoadOutcome.CreatedDefault);
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options);
            if (IsValid(config))
            {
                SanitizeStash(config!);
                return new ConfigLoadResult(config!, ConfigLoadOutcome.Loaded);
            }
        }
        catch
        {
            // Any failure reading or parsing the file (malformed JSON, locked file,
            // permission error, ...) is treated as a corrupt config below.
        }

        TryBackup();
        var defaults = AppConfig.CreateDefault();
        TrySave(defaults);
        return new ConfigLoadResult(defaults, ConfigLoadOutcome.RecoveredFromCorrupt);
    }

    /// <summary>A structurally sound config: non-null, with exactly the expected
    /// number of non-null channels.</summary>
    private static bool IsValid(AppConfig? config) =>
        config is not null
        && config.Channels is not null
        && config.Channels.Count == FrameCodec.ChannelCount
        && config.Channels.All(c => c is not null);

    /// <summary>
    /// Discards a stash that hand-editing has made unusable, and nothing else.
    /// A broken stash must not go through <see cref="IsValid"/>: that path renames the
    /// file and replaces it with defaults, which would cost the ten calibration points
    /// in it — each one measured against a real needle with a screwdriver. The stash is
    /// a convenience beside them, and <see cref="VuModeSwitch"/> rebuilds a missing one
    /// from defaults without complaint.
    /// </summary>
    private static void SanitizeStash(AppConfig config)
    {
        if (!VuModeSwitch.IsUsableStash(config.StashedChannels))
        {
            config.StashedChannels = new List<ChannelProfile>();
        }
    }

    /// <summary>Best-effort backup of the corrupt file; swallows failures so that
    /// recovery never throws.</summary>
    private void TryBackup()
    {
        try
        {
            File.Move(Path, BackupPath, overwrite: true);
        }
        catch
        {
            // Backing up the corrupt file is best-effort; recovery must still
            // succeed (in memory) even if this fails, e.g. because the file is
            // locked or read-only.
        }
    }

    /// <summary>Best-effort write; swallows failures so that recovery never throws.</summary>
    private void TrySave(AppConfig config)
    {
        try
        {
            Save(config);
        }
        catch
        {
            // Writing config.json is best-effort during recovery; the caller
            // still gets a usable in-memory configuration even if this fails.
        }
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Options));
}
