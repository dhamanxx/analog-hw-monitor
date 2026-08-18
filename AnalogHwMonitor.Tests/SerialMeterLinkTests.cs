using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class SerialMeterLinkTests
{
    private static FakeSerialPortFactory FactoryWith(string name, Func<FakeSerialPort> port)
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort(name, port);
        return factory;
    }

    [Fact]
    public void TryConnect_SucceedsWhenTheDeviceAnnouncesItself()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.True(link.TryConnect());
        Assert.True(link.IsConnected);
        Assert.Null(link.LastError);
    }

    [Fact]
    public void TryConnect_IgnoresNoiseBeforeTheBanner()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort(null, "\u0000garbage", "AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.True(link.TryConnect());
    }

    [Fact]
    public void TryConnect_RejectsADeviceThatNeverAnnouncesItself()
    {
        var port = new FakeSerialPort("READY", "OK", "42");
        var factory = FactoryWith("COM3", () => port);
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.False(link.IsConnected);
        Assert.Contains("AHM1", link.LastError);
        Assert.True(port.Disposed);
    }

    [Fact]
    public void TryConnect_ReportsAPortThatCannotBeOpened()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort
        {
            ThrowOnOpen = new UnauthorizedAccessException("Access to the port is denied."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.Contains("Access to the port is denied.", link.LastError);
    }

    [Fact]
    public void TryConnect_FailsWithoutAConfiguredPort()
    {
        using var link = new SerialMeterLink(new FakeSerialPortFactory(), null, NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.Contains("No COM port", link.LastError);
    }

    [Fact]
    public void Send_ConnectsOnTheFirstCallAndWritesTheFrame()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        link.Send("V:1,2,3,4,5\n");

        Assert.True(link.IsConnected);
        Assert.Equal(new[] { "V:1,2,3,4,5\n" }, factory.Last!.Written);
    }

    [Fact]
    public void Send_MarksTheLinkDeadWhenTheWriteFails()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1")
        {
            ThrowOnWrite = new IOException("The device is not connected."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        link.Send("V:1,2,3,4,5\n");

        Assert.False(link.IsConnected);
        Assert.Contains("The device is not connected.", link.LastError);
    }

    [Fact]
    public void Send_RetriesTheConnectionOnlyEveryFifthTick()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("nothing"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        for (var i = 0; i < 6; i++)
        {
            link.Send("V:0,0,0,0,0\n");
        }

        Assert.Equal(2, factory.CreatedPortNames.Count);   // ticks 1 and 6
    }

    [Fact]
    public void Send_DoesNotThrowWhenTheFailedPortAlsoThrowsOnDispose()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1")
        {
            ThrowOnWrite = new IOException("The device is not connected."),
            ThrowOnDispose = new InvalidOperationException("The port handle is invalid."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        var exception = Record.Exception(() => link.Send("V:1,2,3,4,5\n"));

        Assert.Null(exception);
        Assert.False(link.IsConnected);
    }

    [Fact]
    public void PortName_DoesNotThrowWhenTheCurrentPortFailsToDispose()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1")
        {
            ThrowOnDispose = new InvalidOperationException("The port handle is invalid."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);
        link.TryConnect();

        var exception = Record.Exception(() => link.PortName = "COM4");

        Assert.Null(exception);
    }

    [Fact]
    public void TryConnect_ReportsARepeatedIdenticalFailureOnlyOnce()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort
        {
            ThrowOnOpen = new UnauthorizedAccessException("Access to the port is denied."),
        });
        var log = new RecordingLog();
        using var link = new SerialMeterLink(factory, "COM3", log);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            link.TryConnect();
        }

        Assert.Single(log.Lines);
        Assert.Contains("Access to the port is denied.", log.Lines[0]);
    }

    [Fact]
    public void TryConnect_ReportsARepeatedBannerFailureOnlyOnce()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("PRINTER READY"));
        var log = new RecordingLog();
        using var link = new SerialMeterLink(factory, "COM3", log);

        link.TryConnect();
        link.TryConnect();

        Assert.Single(log.Lines);
        Assert.Contains("AHM1", log.Lines[0]);
    }

    [Fact]
    public void TryConnect_ReportsTheFailureAgainWhenItChanges()
    {
        Exception? fault = new UnauthorizedAccessException("Access to the port is denied.");
        var factory = FactoryWith("COM3", () => new FakeSerialPort { ThrowOnOpen = fault });
        var log = new RecordingLog();
        using var link = new SerialMeterLink(factory, "COM3", log);

        link.TryConnect();
        link.TryConnect();
        fault = new IOException("The device is not connected.");
        link.TryConnect();

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("The device is not connected.", log.Lines[1]);
    }

    /// <summary>
    /// A cable that is plugged back in and pulled out again is news both times, so the
    /// successful connection re-arms the latch. The "Connected to ..." line itself is
    /// unconditional and stays.
    /// </summary>
    [Fact]
    public void TryConnect_ReportsTheSameFailureAgainAfterTheLinkRecovers()
    {
        Exception? fault = new UnauthorizedAccessException("Access to the port is denied.");
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1") { ThrowOnOpen = fault });
        var log = new RecordingLog();
        using var link = new SerialMeterLink(factory, "COM3", log);

        link.TryConnect();
        link.TryConnect();

        fault = null;
        Assert.True(link.TryConnect());

        fault = new UnauthorizedAccessException("Access to the port is denied.");
        link.TryConnect();

        Assert.Equal(3, log.Lines.Count);
        Assert.Equal("Connected to COM3.", log.Lines[1]);
        Assert.Contains("Access to the port is denied.", log.Lines[2]);
    }

    [Fact]
    public void TryConnect_StillLogsEverySuccessfulConnection()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1"));
        var log = new RecordingLog();
        using var link = new SerialMeterLink(factory, "COM3", log);

        Assert.True(link.TryConnect());

        Assert.Equal(new[] { "Connected to COM3." }, log.Lines);
    }
}
