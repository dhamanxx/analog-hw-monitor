namespace AnalogHwMonitor.Core;

public interface IAppLog
{
    void Write(string message);
}

/// <summary>Discards everything. Used by tests and by code paths without a log.</summary>
public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();

    public void Write(string message)
    {
    }
}
