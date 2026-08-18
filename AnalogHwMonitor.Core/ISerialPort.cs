namespace AnalogHwMonitor.Core;

/// <summary>The slice of a serial port this application needs, so it can be faked.</summary>
public interface ISerialPort : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Write(string text);

    /// <summary>One line, or null if nothing arrived before the read timeout.</summary>
    string? ReadLine();
}

public interface ISerialPortFactory
{
    IReadOnlyList<string> GetPortNames();

    ISerialPort Create(string portName);
}
