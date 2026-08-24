namespace AnalogHwMonitor.Core;

/// <summary>
/// The signal half of one channel's settings, parked while the other profile is live.
///
/// Pin, MinPwm and MaxPwm are deliberately absent. They describe the physical meter —
/// which pin drives it and where its own needle sits at each end — and none of that
/// changes when the needle starts showing music instead of a processor. Keeping them
/// out means the shape of this type states what a VU mode switch actually exchanges.
/// </summary>
public sealed class ChannelProfile
{
    /// <summary>
    /// Index of the channel this profile belongs to. The index is the identity the
    /// frame position, the config order and <see cref="ChannelReading.Index"/> already
    /// use; <see cref="ChannelConfig.Pin"/> is documented as informational, and keying
    /// on it would turn a typo in an informational field into a broken switch.
    /// </summary>
    public int Channel { get; set; }

    public string Label { get; set; } = string.Empty;

    public string? SensorId { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }
}
