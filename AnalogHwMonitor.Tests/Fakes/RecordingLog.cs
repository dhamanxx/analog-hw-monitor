using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>Keeps every line written, so a test can count them. Fault latches are
/// only observable through how much they log.</summary>
public sealed class RecordingLog : IAppLog
{
    public List<string> Lines { get; } = new();

    public void Write(string message) => Lines.Add(message);
}
