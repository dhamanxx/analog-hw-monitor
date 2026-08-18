namespace AnalogHwMonitor.Core;

public sealed class AppConfig
{
    public string? ComPort { get; set; }

    public bool StartWithWindows { get; set; }

    public List<ChannelConfig> Channels { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Channels =
        {
            new ChannelConfig { Pin = 3,  Label = "CPU Load",   Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 5,  Label = "GPU Load",   Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 6,  Label = "Memory",     Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 9,  Label = "CPU Temp",   Min = 30, Max = 90,  MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 10, Label = "GPU Temp",   Min = 30, Max = 90,  MinPwm = 0, MaxPwm = 255 },
        },
    };
}
