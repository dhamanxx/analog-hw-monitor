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
    public void Refresh_IsSuppressedUntilTheIntervalHasPassed()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time);

        // Driven at the VU meter's tick rate for just under the interval, whatever the
        // interval currently is. Reading MinimumInterval rather than hardcoding a second
        // keeps this test honest when the constant is retuned -- it is a policy value and
        // it has been changed once already.
        var step = TimeSpan.FromMilliseconds(40);
        for (var elapsed = TimeSpan.Zero;
             elapsed + step < ThrottledSensorSource.MinimumInterval;
             elapsed += step)
        {
            throttled.Refresh();
            time.Advance(step);
        }

        Assert.Equal(1, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_PassesThroughAgainAfterTheInterval()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time);

        throttled.Refresh();
        time.Advance(ThrottledSensorSource.MinimumInterval);
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
