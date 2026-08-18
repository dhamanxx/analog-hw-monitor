using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>A serial port scripted with the lines it will hand back.</summary>
public sealed class FakeSerialPort : ISerialPort
{
    private readonly Queue<string?> _linesToRead;

    public FakeSerialPort(params string?[] linesToRead) =>
        _linesToRead = new Queue<string?>(linesToRead);

    public bool IsOpen { get; private set; }

    public bool Disposed { get; private set; }

    public List<string> Written { get; } = new();

    public Exception? ThrowOnWrite { get; set; }

    public Exception? ThrowOnOpen { get; set; }

    public void Open()
    {
        if (ThrowOnOpen is not null)
        {
            throw ThrowOnOpen;
        }

        IsOpen = true;
    }

    public string? ReadLine() => _linesToRead.Count > 0 ? _linesToRead.Dequeue() : null;

    public void Write(string text)
    {
        if (ThrowOnWrite is not null)
        {
            IsOpen = false;
            throw ThrowOnWrite;
        }

        Written.Add(text);
    }

    public void Dispose()
    {
        IsOpen = false;
        Disposed = true;
    }
}

public sealed class FakeSerialPortFactory : ISerialPortFactory
{
    private readonly Dictionary<string, Func<FakeSerialPort>> _ports = new();

    public List<string> CreatedPortNames { get; } = new();

    public FakeSerialPort? Last { get; private set; }

    public void AddPort(string name, Func<FakeSerialPort> port) => _ports[name] = port;

    public IReadOnlyList<string> GetPortNames() => _ports.Keys.ToList();

    public ISerialPort Create(string portName)
    {
        CreatedPortNames.Add(portName);
        Last = _ports.TryGetValue(portName, out var factory)
            ? factory()
            : new FakeSerialPort();
        return Last;
    }
}
