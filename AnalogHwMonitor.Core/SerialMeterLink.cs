namespace AnalogHwMonitor.Core;

/// <summary>
/// Owns the serial port: opens it, waits for the boot banner, writes frames, and
/// quietly retries after a failure. Never throws at the caller — a dead link is a
/// state, not an exception, because the meters already report it by dropping to zero.
/// </summary>
public sealed class SerialMeterLink : IMeterLink
{
    /// <summary>Read attempts while waiting for the banner after the UNO reboots.</summary>
    public const int BannerReadAttempts = 5;

    /// <summary>Ticks between reconnect attempts. At 1 Hz this is the 5 s from the spec.</summary>
    public const int ReconnectEveryTicks = 5;

    private readonly ISerialPortFactory _factory;
    private readonly IAppLog _log;
    private ISerialPort? _port;
    private int _ticksSinceAttempt = ReconnectEveryTicks - 1;
    private string? _reportedError;

    public SerialMeterLink(ISerialPortFactory factory, string? portName, IAppLog log)
    {
        _factory = factory;
        PortName = portName;
        _log = log;
    }

    /// <summary>Changing this drops the current connection.</summary>
    public string? PortName
    {
        get => _portName;
        set
        {
            if (_portName == value)
            {
                return;
            }

            _portName = value;

            // A new port is a new story: whatever was already reported about the old
            // one must not silence the first failure of this one.
            _reportedError = null;
            Disconnect();
        }
    }

    private string? _portName;

    public bool IsConnected => _port?.IsOpen == true;

    public string? LastError { get; private set; }

    public bool TryConnect()
    {
        Disconnect();

        if (string.IsNullOrWhiteSpace(PortName))
        {
            LastError = "No COM port configured.";
            return false;
        }

        ISerialPort? port = null;
        try
        {
            port = _factory.Create(PortName);
            port.Open();

            for (var attempt = 0; attempt < BannerReadAttempts; attempt++)
            {
                var line = port.ReadLine();
                if (line?.Trim() == FrameCodec.Banner)
                {
                    _port = port;
                    LastError = null;
                    _reportedError = null;
                    _log.Write($"Connected to {PortName}.");
                    return true;
                }
            }

            port.Dispose();
            Report($"{PortName} did not identify itself as {FrameCodec.Banner}.");
            return false;
        }
        catch (Exception ex)
        {
            try
            {
                port?.Dispose();
            }
            catch (Exception)
            {
            }

            Report($"{PortName}: {ex.Message}");
            return false;
        }
    }

    public void Send(string frame)
    {
        if (!IsConnected)
        {
            _ticksSinceAttempt++;
            if (_ticksSinceAttempt < ReconnectEveryTicks)
            {
                return;
            }

            _ticksSinceAttempt = 0;
            if (!TryConnect())
            {
                return;
            }
        }

        try
        {
            _port!.Write(frame);
        }
        catch (Exception ex)
        {
            Report($"{PortName}: {ex.Message}");
            Disconnect();
        }
    }

    /// <summary>
    /// Records a failure and logs it only when it differs from the one already
    /// reported. An unplugged USB cable fails the same way every five seconds
    /// forever; logging each attempt filled log.txt at about a megabyte a day and
    /// rotated the interesting history away. A changed failure, or a recovery (which
    /// clears the latch and logs its own "Connected to ..." line), reports again.
    /// </summary>
    private void Report(string message)
    {
        LastError = message;

        if (_reportedError == message)
        {
            return;
        }

        _log.Write(message);
        _reportedError = message;
    }

    private void Disconnect()
    {
        try
        {
            _port?.Dispose();
        }
        catch (Exception)
        {
        }
        finally
        {
            _port = null;
        }
    }

    public void Dispose() => Disconnect();
}
