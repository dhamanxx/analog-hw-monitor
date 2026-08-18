namespace AnalogHwMonitor.Core;

/// <summary>One channel's state after a tick, for the settings window to display.</summary>
/// <param name="Value">Raw sensor reading, or null when the channel is unassigned,
/// unreadable, or under manual test control.</param>
public sealed record ChannelReading(
    int Index,
    string Label,
    float? Value,
    double Percent,
    byte Pwm,
    bool SensorMissing,
    bool TestMode);
