using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class CompositeSensorSourceTests
{
    private static FakeSensorSource SourceWith(string id, float? value, string name)
    {
        var source = new FakeSensorSource(new Dictionary<string, float?> { [id] = value });
        source.Sensors.Add(new SensorDescriptor(id, name, "Fake", SensorKind.Temperature, "°C"));
        return source;
    }

    [Fact]
    public void Discover_ConcatenatesEverySource()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        Assert.Equal(new[] { "a", "b" }, composite.Discover().Select(s => s.Id));
    }

    [Fact]
    public void Refresh_RefreshesEverySourceExactlyOnce()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        composite.Refresh();

        Assert.Equal(1, a.RefreshCount);
        Assert.Equal(1, b.RefreshCount);
    }

    [Fact]
    public void Refresh_KeepsGoingWhenOneSourceThrows()
    {
        var healthy = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource(), healthy);

        composite.Refresh();

        Assert.Equal(1, healthy.RefreshCount);
    }

    [Fact]
    public void Discover_KeepsGoingWhenOneSourceThrows()
    {
        var healthy = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource(), healthy);

        Assert.Equal(new[] { "b" }, composite.Discover().Select(s => s.Id));
    }

    [Fact]
    public void Read_FindsTheValueInWhicheverSourceHasIt()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        Assert.Equal(2f, composite.Read("b"));
    }

    [Fact]
    public void Read_ReturnsNullForAnUnknownId()
    {
        using var composite = new CompositeSensorSource(NullLog.Instance, SourceWith("a", 1, "A"));

        Assert.Null(composite.Read("nothing"));
    }

    [Fact]
    public void Read_ReturnsNullWhenASourceThrows()
    {
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource());

        Assert.Null(composite.Read("anything"));
    }

    [Fact]
    public void Dispose_DisposesEverySource()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        composite.Dispose();

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    /// <summary>
    /// The shape of a real tick: MonitorService calls Refresh() once and then Read()
    /// once per channel, all on the same source. A latch keyed only on the last message
    /// per source alternated between the Refresh message and the Read message and
    /// logged both on every tick — roughly two lines a second, which rotated
    /// log.txt away in under two hours. Per source AND per operation, a persistent
    /// fault costs one line each, forever.
    /// </summary>
    [Fact]
    public void APersistentFaultLogsOncePerOperationNoMatterHowManyTicksRun()
    {
        var log = new RecordingLog();
        var broken = new FaultySensorSource { RefreshFault = "refresh failed", ReadFault = "read failed" };
        using var composite = new CompositeSensorSource(log, broken);

        for (var tick = 0; tick < 10; tick++)
        {
            composite.Refresh();
            for (var channel = 0; channel < FrameCodec.ChannelCount; channel++)
            {
                composite.Read($"sensor-{channel}");
            }
        }

        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void AChangedFailureOnTheSameOperationIsStillReported()
    {
        var log = new RecordingLog();
        var broken = new FaultySensorSource { RefreshFault = "the driver is gone" };
        using var composite = new CompositeSensorSource(log, broken);

        composite.Refresh();
        composite.Refresh();
        broken.RefreshFault = "the driver came back wrong";
        composite.Refresh();

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("the driver came back wrong", log.Lines[1]);
    }

    [Fact]
    public void TheSameFailureIsReportedAgainAfterTheOperationRecovers()
    {
        var log = new RecordingLog();
        var broken = new FaultySensorSource { RefreshFault = "the driver is gone" };
        using var composite = new CompositeSensorSource(log, broken);

        composite.Refresh();
        broken.RefreshFault = null;
        composite.Refresh();
        broken.RefreshFault = "the driver is gone";
        composite.Refresh();

        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void OneOperationRecoveringDoesNotUnlatchAnother()
    {
        var log = new RecordingLog();
        var broken = new FaultySensorSource { RefreshFault = "refresh failed", ReadFault = "read failed" };
        using var composite = new CompositeSensorSource(log, broken);

        composite.Refresh();
        composite.Read("sensor-0");

        // Refresh starts working again; Read is still broken the same way it was.
        broken.RefreshFault = null;
        composite.Refresh();
        composite.Read("sensor-0");

        Assert.Equal(2, log.Lines.Count);
    }
}
