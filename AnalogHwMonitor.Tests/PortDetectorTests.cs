using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class PortDetectorTests
{
    [Fact]
    public void FindMonitorPort_SkipsPortsThatAreNotOurDevice()
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort("COM1", () => new FakeSerialPort("PRINTER READY"));
        factory.AddPort("COM4", () => new FakeSerialPort("AHM1"));

        Assert.Equal("COM4", PortDetector.FindMonitorPort(factory, NullLog.Instance));
    }

    [Fact]
    public void FindMonitorPort_ReturnsNullWhenNothingAnswers()
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort("COM1", () => new FakeSerialPort("PRINTER READY"));

        Assert.Null(PortDetector.FindMonitorPort(factory, NullLog.Instance));
    }
}
