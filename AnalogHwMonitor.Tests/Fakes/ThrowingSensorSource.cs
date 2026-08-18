using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>Fails at everything, to prove the composite survives a broken source.</summary>
public sealed class ThrowingSensorSource : ISensorSource
{
    public void Refresh() => throw new InvalidOperationException("refresh failed");

    public IReadOnlyList<SensorDescriptor> Discover() => throw new InvalidOperationException("discover failed");

    public float? Read(string sensorId) => throw new InvalidOperationException("read failed");

    public void Dispose()
    {
    }
}
