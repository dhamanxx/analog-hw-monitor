using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;

namespace AnalogHwMonitor.Tests;

public class ThrottledSensorSourceTests
{
    private static FakeSensorSource Inner() =>
        new(new Dictionary<string, float?> { ["/cpu/load"] = 42f });

    [Fact]
    public void Refresh_ReachesTheInnerSourceOnTheFirstCall()
    {
        var inner = Inner();
        using var throttled = new ThrottledSensorSource(inner, new FakeTimeProvider());

        throttled.Refresh();

        Assert.Equal(1, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_IsSuppressedForTheRestOfTheSecond()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time);

        // One second of VU meter mode: 25 ticks, one hardware refresh.
        for (var i = 0; i < 25; i++)
        {
            throttled.Refresh();
            time.Advance(TimeSpan.FromMilliseconds(40));
        }

        Assert.Equal(1, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_PassesThroughAgainAfterASecond()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time);

        throttled.Refresh();
        time.Advance(TimeSpan.FromSeconds(1));
        throttled.Refresh();

        Assert.Equal(2, inner.RefreshCount);
    }

    [Fact]
    public void Read_IsNeverThrottled()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time);

        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(42f, throttled.Read("/cpu/load"));
            time.Advance(TimeSpan.FromMilliseconds(40));
        }

        Assert.Equal(25, inner.ReadIds.Count);
    }

    [Fact]
    public void Discover_IsNeverThrottled()
    {
        var inner = Inner();
        inner.Sensors.Add(new SensorDescriptor("/cpu/load", "CPU Total", "CPU", SensorKind.Load, "%"));
        using var throttled = new ThrottledSensorSource(inner, new FakeTimeProvider());

        Assert.Single(throttled.Discover());
        Assert.Single(throttled.Discover());
    }

    [Fact]
    public void Dispose_DisposesTheInnerSource()
    {
        var inner = Inner();
        var throttled = new ThrottledSensorSource(inner, new FakeTimeProvider());

        throttled.Dispose();

        Assert.True(inner.Disposed);
    }
}
