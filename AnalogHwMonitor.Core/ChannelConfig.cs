namespace AnalogHwMonitor.Core;

/// <summary>Settings for one meter: which sensor it shows and how it is scaled.</summary>
public sealed class ChannelConfig
{
    /// <summary>Arduino PWM pin. Informational on the PC side; the frame is positional.</summary>
    public int Pin { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>LibreHardwareMonitor sensor identifier, or null when nothing is assigned.</summary>
    public string? SensorId { get; set; }

    /// <summary>Sensor value that means zero deflection, in the sensor's own unit.</summary>
    public double Min { get; set; }

    /// <summary>Sensor value that means full deflection.</summary>
    public double Max { get; set; }

    /// <summary>PWM value at which this physical meter reads zero.</summary>
    public int MinPwm { get; set; }

    /// <summary>PWM value at which this physical meter reads full scale.</summary>
    public int MaxPwm { get; set; }
}
