namespace AnalogHwMonitor.Core;

/// <summary>
/// Turns VU meter mode on and off by exchanging the signal half of the VU channels
/// with the profiles parked in <see cref="AppConfig.StashedChannels"/>.
///
/// An exchange rather than an overwrite, and that is the whole point: both sides keep
/// whatever was tuned on them, so switching to VU and back does not cost the dB window
/// the user just widened, and switching away and back does not cost the load scale he
/// narrowed. Pin, MinPwm and MaxPwm are never touched — they belong to the physical
/// meter, which does not change when the needle starts showing music.
/// </summary>
public static class VuModeSwitch
{
    /// <summary>The channels VU mode takes over: indices 0 and 1, driving pins 3 and 5.</summary>
    public static readonly IReadOnlyList<int> VuChannels = new[] { 0, 1 };

    /// <summary>
    /// Default dB window. Loud modern material integrates to about -12 dBFS, which
    /// lands near 70 % of the dial on this scale. Editable per channel afterwards like
    /// any other range, so this is a starting point rather than a decision.
    /// </summary>
    public const double DefaultMinDbfs = -40.0;

    public const double DefaultMaxDbfs = 0.0;

    /// <summary>
    /// Whether a stash can be swapped in. All or nothing on purpose: a half-valid stash
    /// would restore one meter and leave the other showing dBFS on a percentage scale,
    /// which is worse than starting over from the defaults.
    /// </summary>
    public static bool IsUsableStash(IReadOnlyList<ChannelProfile>? stash) =>
        stash is not null
        && stash.Count == VuChannels.Count
        && stash.All(profile => profile is not null)
        && stash.Select(profile => profile.Channel).Distinct().Count() == stash.Count
        && stash.All(profile => VuChannels.Contains(profile.Channel));

    public static void Set(AppConfig config, bool enabled)
    {
        if (config.VuMode == enabled)
        {
            return;
        }

        var incoming = (IsUsableStash(config.StashedChannels)
                ? config.StashedChannels
                : Defaults(enabled))
            .ToDictionary(profile => profile.Channel);

        var outgoing = new List<ChannelProfile>();

        foreach (var index in VuChannels)
        {
            var channel = config.Channels[index];

            outgoing.Add(new ChannelProfile
            {
                Channel = index,
                Label = channel.Label,
                SensorId = channel.SensorId,
                Min = channel.Min,
                Max = channel.Max,
            });

            var profile = incoming[index];
            channel.Label = profile.Label;
            channel.SensorId = profile.SensorId;
            channel.Min = profile.Min;
            channel.Max = profile.Max;
        }

        config.StashedChannels = outgoing;
        config.VuMode = enabled;
    }

    /// <summary>
    /// What to swap in when there is no usable stash: the VU profiles when switching on,
    /// and a first run's sensor channels when switching off. The latter carry no
    /// sensorId, which is exactly what <see cref="SensorDefaults.AssignSensors"/> fills
    /// in on the next start.
    /// </summary>
    private static IReadOnlyList<ChannelProfile> Defaults(bool enabled) =>
        enabled ? VuProfiles() : SensorProfiles();

    private static IReadOnlyList<ChannelProfile> VuProfiles() => new List<ChannelProfile>
    {
        new()
        {
            Channel = 0, Label = "VU Left", SensorId = AudioSensorIds.Left,
            Min = DefaultMinDbfs, Max = DefaultMaxDbfs,
        },
        new()
        {
            Channel = 1, Label = "VU Right", SensorId = AudioSensorIds.Right,
            Min = DefaultMinDbfs, Max = DefaultMaxDbfs,
        },
    };

    private static IReadOnlyList<ChannelProfile> SensorProfiles()
    {
        var defaults = AppConfig.CreateDefault();

        return VuChannels
            .Select(index => new ChannelProfile
            {
                Channel = index,
                Label = defaults.Channels[index].Label,
                SensorId = defaults.Channels[index].SensorId,
                Min = defaults.Channels[index].Min,
                Max = defaults.Channels[index].Max,
            })
            .ToList();
    }
}
