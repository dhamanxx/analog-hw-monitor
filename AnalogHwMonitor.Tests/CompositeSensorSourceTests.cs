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
}
