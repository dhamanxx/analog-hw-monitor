using System.IO.Ports;

namespace AnalogHwMonitor.Core;

public sealed class SerialPortAdapter : ISerialPort
{
    /// <summary>Five of these cover the ~2 s the UNO needs to reboot after the port opens.</summary>
    public const int ReadTimeoutMs = 500;

    private readonly SerialPort _port;

    public SerialPortAdapter(string portName)
    {
        _port = new SerialPort(portName, FrameCodec.BaudRate)
        {
            NewLine = "\n",
            ReadTimeout = ReadTimeoutMs,
            WriteTimeout = 1000,
            DtrEnable = true,
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        _port.Open();

        // The UNO prints its banner at upload time too, and those bytes can still be
        // sitting in the driver's receive buffer from before this Open() reset it.
        // Discard them so the caller's banner check can only see bytes sent after
        // the reset this call just triggered — a stale banner proves nothing about
        // whether the board is up right now.
        try
        {
            _port.DiscardInBuffer();
        }
        catch (Exception)
        {
        }
    }

    public void Write(string text) => _port.Write(text);

    public string? ReadLine()
    {
        try
        {
            return _port.ReadLine();
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void Dispose() => _port.Dispose();
}

public sealed class SerialPortFactory : ISerialPortFactory
{
    public IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames();

    public ISerialPort Create(string portName) => new SerialPortAdapter(portName);
}
