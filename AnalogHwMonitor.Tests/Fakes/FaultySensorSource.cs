using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>
/// A source whose Refresh and Read fail independently with a message the test
/// chooses, so the composite's fault latch can be driven the way one real tick drives
/// it: Refresh once, then Read per channel. A null message makes that operation
/// succeed.
/// </summary>
public sealed class FaultySensorSource : ISensorSource
{
    public string? RefreshFault { get; set; }

    public string? ReadFault { get; set; }

    public void Refresh()
    {
        if (RefreshFault is { } message)
        {
            throw new InvalidOperationException(message);
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() => Array.Empty<SensorDescriptor>();

    public float? Read(string sensorId)
    {
        if (ReadFault is { } message)
        {
            throw new InvalidOperationException(message);
        }

        return 1f;
    }

    public void Dispose()
    {
    }
}
