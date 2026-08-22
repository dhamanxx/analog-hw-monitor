namespace AnalogHwMonitor.Core;

public enum SensorKind
{
    Load,
    Temperature,
    Audio,
    Other,
}

/// <param name="Id">LibreHardwareMonitor identifier, e.g. "/amdcpu/0/load/0".</param>
/// <param name="Name">Sensor name as reported, e.g. "CPU Total".</param>
/// <param name="Hardware">Owning device name, e.g. "AMD Ryzen 7 5800X".</param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    string Hardware,
    SensorKind Kind,
    string Unit)
{
    /// <summary>What the settings window shows in its dropdown.</summary>
    public string Display => $"{Hardware} · {Name}";
}
