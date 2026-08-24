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

    /// <summary>
    /// Wall-clock gap between reconnect attempts. This used to be a tick count, which
    /// silently meant "five seconds" only while the loop ran at 1 Hz — VU meter mode
    /// runs it at 25 Hz and would have turned the same constant into 200 ms.
    /// </summary>
    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    private readonly ISerialPortFactory _factory;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private ISerialPort? _port;

    // MinValue rather than "now", so the first Send() attempts a connection instead
    // of sitting out the first interval with the needles at zero.
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private string? _reportedError;

    /// <summary>
    /// Serialises the port. Send() runs on the sender task while the PortName setter runs
    /// on the UI thread (the settings window changes the COM port at runtime) and disposes
    /// _port — without this the port would be disposed under a write in progress.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// A copy of _port?.IsOpen kept outside the lock. The UI thread reads IsConnected every
    /// tick for the tooltip and must not wait on a stuck write — under the lock that would
    /// move the UI stall rather than remove it. Reading a disposed SerialPort would also
    /// throw ObjectDisposedException on its own.
    /// </summary>
    private volatile bool _connected;

    public SerialMeterLink(
        ISerialPortFactory factory, string? portName, IAppLog log, TimeProvider? time = null)
    {
        _factory = factory;
        PortName = portName;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Changing this drops the current connection.</summary>
    public string? PortName
    {
        get => _portName;
        set
        {
            lock (_gate)
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
    }

    private string? _portName;

    public bool IsConnected => _connected;

    public string? LastError { get; private set; }

    public bool TryConnect()
    {
        lock (_gate)
        {
            Disconnect();

            if (string.IsNullOrWhiteSpace(PortName))
            {
                LastError = "No COM port configured.";
                _connected = false;
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
                        _connected = true;
                        LastError = null;
                        _reportedError = null;
                        _log.Write($"Connected to {PortName}.");
                        return true;
                    }
                }

                port.Dispose();
                Report($"{PortName} did not identify itself as {FrameCodec.Banner}.");
                _connected = false;
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
                _connected = false;
                return false;
            }
        }
    }

    public void Send(string frame)
    {
        lock (_gate)
        {
            if (_port?.IsOpen != true)
            {
                var now = _time.GetUtcNow();
                if (now - _lastAttempt < ReconnectInterval)
                {
                    return;
                }

                _lastAttempt = now;
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
            _connected = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Disconnect();
        }
    }
}
