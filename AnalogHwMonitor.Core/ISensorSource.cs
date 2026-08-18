namespace AnalogHwMonitor.Core;

public interface ISensorSource : IDisposable
{
    /// <summary>Polls the hardware once. Called at the start of every tick.</summary>
    void Refresh();

    IReadOnlyList<SensorDescriptor> Discover();

    /// <summary>Last refreshed value, or null when the sensor is unknown or unreadable.</summary>
    float? Read(string sensorId);
}
