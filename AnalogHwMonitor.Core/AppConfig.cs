namespace AnalogHwMonitor.Core;

public sealed class AppConfig
{
    public string? ComPort { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Which of the two profiles is live on the VU channels, and — because a VU meter
    /// is useless at one frame a second — also what tick rate the loop runs at.
    /// </summary>
    public bool VuMode { get; set; }

    /// <summary>
    /// Subtract the Windows master volume attenuation from the measured level, so the
    /// needles show the recording rather than the volume knob. Defaults to on. Whether
    /// the loopback tap includes the master volume at all depends on the endpoint, so
    /// this is a switch rather than an assumption.
    /// </summary>
    public bool VuCompensateVolume { get; set; } = true;

    /// <summary>
    /// The profiles for the VU channels that are not currently live. Empty, or one
    /// entry per <see cref="VuModeSwitch.VuChannels"/> entry.
    /// </summary>
    public List<ChannelProfile> StashedChannels { get; set; } = new();

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
