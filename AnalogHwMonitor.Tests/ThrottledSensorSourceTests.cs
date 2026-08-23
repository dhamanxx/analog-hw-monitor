using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;

namespace AnalogHwMonitor.Tests;

public class ThrottledSensorSourceTests
{
    private static FakeSensorSource Inner() =>
        new(new Dictionary<string, float?> { ["/cpu/load"] = 42f });

    /// <summary>
    /// Drives Refresh() at the VU meter's tick rate for just under <paramref name="interval"/>,
    /// whatever that interval currently is. Reading the constants rather than hardcoding a
    /// second keeps these tests honest when they are retuned -- they are policy values and
    /// they have been changed more than once.
    /// </summary>
    private static void DriveForJustUnder(
        ThrottledSensorSource throttled, FakeTimeProvider time, TimeSpan interval)
    {
        var step = TimeSpan.FromMilliseconds(40);

        for (var elapsed = TimeSpan.Zero; elapsed + step < interval; elapsed += step)
        {
            throttled.Refresh();
            time.Advance(step);
        }
    }

    [Fact]
    public void Refresh_ReachesTheInnerSourceOnTheFirstCall()
    {
        var inner = Inner();
        using var throttled = new ThrottledSensorSource(inner, time: new FakeTimeProvider());

        throttled.Refresh();

        Assert.Equal(1, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_IsSuppressedUntilTheIntervalHasPassed()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time: time);

        DriveForJustUnder(throttled, time, ThrottledSensorSource.SensorModeInterval);

        Assert.Equal(1, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_PassesThroughAgainAfterTheInterval()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time: time);

        throttled.Refresh();
        time.Advance(ThrottledSensorSource.SensorModeInterval);
        throttled.Refresh();

        Assert.Equal(2, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_HoldsTheLongerIntervalInVuMeterMode()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, () => true, time);

        DriveForJustUnder(throttled, time, ThrottledSensorSource.VuModeInterval);

        // The sensor mode interval passes several times over inside this window, so a
        // throttle that ignored the mode would already have refreshed again.
        Assert.Equal(1, inner.RefreshCount);

        time.Advance(TimeSpan.FromMilliseconds(40));
        throttled.Refresh();

        Assert.Equal(2, inner.RefreshCount);
    }

    [Fact]
    public void CurrentInterval_FollowsTheMode()
    {
        var vuMode = false;
        using var throttled = new ThrottledSensorSource(
            Inner(), () => vuMode, new FakeTimeProvider());

        Assert.Equal(ThrottledSensorSource.SensorModeInterval, throttled.CurrentInterval);

        vuMode = true;

        Assert.Equal(ThrottledSensorSource.VuModeInterval, throttled.CurrentInterval);
    }

    [Fact]
    public void Refresh_DoesNotSitOutTheRestOfTheVuIntervalWhenVuModeEnds()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        var vuMode = true;
        using var throttled = new ThrottledSensorSource(inner, () => vuMode, time);

        throttled.Refresh();
        time.Advance(ThrottledSensorSource.SensorModeInterval);
        throttled.Refresh();

        // Still inside the VU interval, so nothing has reached the hardware yet.
        Assert.Equal(1, inner.RefreshCount);

        // Unchecking the tray item mid-interval: the time already elapsed is past what
        // sensor mode asks for, so the next tick refreshes instead of waiting out a
        // window that no longer applies.
        vuMode = false;
        throttled.Refresh();

        Assert.Equal(2, inner.RefreshCount);
    }

    [Fact]
    public void Refresh_DefaultsToSensorModeWithoutAModeCallback()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, time: time);

        throttled.Refresh();
        time.Advance(ThrottledSensorSource.SensorModeInterval);
        throttled.Refresh();

        Assert.Equal(2, inner.RefreshCount);
        Assert.Equal(ThrottledSensorSource.SensorModeInterval, throttled.CurrentInterval);
    }

    [Fact]
    public void Read_IsNeverThrottled()
    {
        var inner = Inner();
        var time = new FakeTimeProvider();
        using var throttled = new ThrottledSensorSource(inner, () => true, time);

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
        using var throttled = new ThrottledSensorSource(inner, time: new FakeTimeProvider());

        Assert.Single(throttled.Discover());
        Assert.Single(throttled.Discover());
    }

    [Fact]
    public void Dispose_DisposesTheInnerSource()
    {
        var inner = Inner();
        var throttled = new ThrottledSensorSource(inner, time: new FakeTimeProvider());

        throttled.Dispose();

        Assert.True(inner.Disposed);
    }
}
