using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

public sealed class FakeMeterLink : IMeterLink
{
    public List<string> Frames { get; } = new();

    public bool IsConnected { get; set; } = true;

    public string? LastError { get; set; }

    public void Send(string frame) => Frames.Add(frame);

    public void Dispose()
    {
    }
}
