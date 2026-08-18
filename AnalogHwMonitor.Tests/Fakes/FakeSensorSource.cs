using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

public sealed class FakeSensorSource : ISensorSource
{
    private readonly Dictionary<string, float?> _values;

    public FakeSensorSource(Dictionary<string, float?> values) => _values = values;

    public int RefreshCount { get; private set; }

    public List<string> ReadIds { get; } = new();

    public List<SensorDescriptor> Sensors { get; } = new();

    public void Refresh() => RefreshCount++;

    public IReadOnlyList<SensorDescriptor> Discover() => Sensors;

    public float? Read(string sensorId)
    {
        ReadIds.Add(sensorId);
        return _values.TryGetValue(sensorId, out var value) ? value : null;
    }

    public void Dispose()
    {
    }
}
