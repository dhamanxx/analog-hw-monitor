using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// Tests RefreshOnce(), not RunAsync(). RunAsync contains a PeriodicTimer, which is not
/// our code, and this repo's FakeTimeProvider overrides only GetUtcNow(), not
/// CreateTimer() — a test through RunAsync would wait on the wall clock. That is exactly
/// why the logic is separated from the cadence.
/// </summary>
public class SensorRefreshLoopTests
{
    [Fact]
    public void RefreshOnce_RefreshesTheSource()
    {
        var sensors = new FakeSensorSource(new Dictionary<string, float?>());
        var loop = new SensorRefreshLoop(sensors, NullLog.Instance);

        loop.RefreshOnce();

        Assert.Equal(1, sensors.RefreshCount);
    }

    /// <summary>
    /// An unhandled exception in a fire-and-forget task would mean the sensors quietly
    /// stop refreshing and the needles freeze forever on their last values — software that
    /// looks like it works and does not. Hence the try inside the loop, not around it.
    /// </summary>
    [Fact]
    public void RefreshOnce_SurvivesAThrowingSource()
    {
        var loop = new SensorRefreshLoop(new ThrowingSensorSource(), NullLog.Instance);

        var exception = Record.Exception(() => loop.RefreshOnce());

        Assert.Null(exception);
    }

    /// <summary>
    /// The loop runs once a second for the life of the tray application. An unlatched write
    /// would fill log.txt at about a megabyte a day and rotate the interesting history
    /// away — the same discipline SerialMeterLink and CompositeSensorSource already keep.
    /// </summary>
    [Fact]
    public void RefreshOnce_LogsAPersistentFaultOnlyOnce()
    {
        var log = new RecordingLog();
        var loop = new SensorRefreshLoop(new ThrowingSensorSource(), log);

        for (var tick = 0; tick < 5; tick++)
        {
            loop.RefreshOnce();
        }

        Assert.Single(log.Lines);
        Assert.Contains("refresh failed", log.Lines[0]);
    }

    /// <summary>
    /// The latch's reset path is otherwise unproven: a success has to clear
    /// <c>_reportedError</c>, or a source that recovers and then fails again stays silent
    /// forever — the one failure mode that hides every other one, because nothing would
    /// ever appear in log.txt again.
    /// </summary>
    [Fact]
    public void RefreshOnce_LogsAgainWhenTheSameFaultReturnsAfterARecovery()
    {
        var log = new RecordingLog();
        var sensors = new FaultySensorSource { RefreshFault = "fault A" };
        var loop = new SensorRefreshLoop(sensors, log);

        loop.RefreshOnce();

        sensors.RefreshFault = null;
        loop.RefreshOnce();

        // The SAME message again, deliberately. A different one would log twice whether or
        // not the success in between cleared the latch, so it would prove nothing — the
        // first version of this test made exactly that mistake. Repeating the message means
        // the second line can only appear because the reset happened.
        sensors.RefreshFault = "fault A";
        loop.RefreshOnce();

        Assert.Equal(2, log.Lines.Count);
        Assert.All(log.Lines, line => Assert.Contains("fault A", line));
    }
}
