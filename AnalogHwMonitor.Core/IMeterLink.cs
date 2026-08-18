namespace AnalogHwMonitor.Core;

public interface IMeterLink : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Why the link is down, for the tray tooltip. Null while healthy.</summary>
    string? LastError { get; }

    void Send(string frame);
}
