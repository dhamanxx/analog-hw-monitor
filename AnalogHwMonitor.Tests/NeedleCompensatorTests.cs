using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

public class NeedleCompensatorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The first call primes the filter to whatever it is handed, so a build that
    /// starts up with the needle already deflected does not get a kick.
    /// </summary>
    [Fact]
    public void Advance_ReturnsTheInputOnTheFirstCall()
    {
        var compensator = new NeedleCompensator();

        Assert.Equal(42.0, compensator.Advance(42.0, Tick));
    }

    /// <summary>
    /// DC gain is exactly 1 — the plant's poles are cancelled and the target's
    /// substituted, and neither changes the steady state. A drifting steady state
    /// would make the compensated meter read a different level from the control.
    /// </summary>
    [Fact]
    public void Advance_HoldsASteadyInputExactly()
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(70.0, Tick);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(70.0, compensator.Advance(70.0, Tick), precision: 9);
        }
    }

    /// <summary>
    /// The plant is under-damped, so cancelling it means pushing past the target and
    /// then pulling back hard. Both halves are what stops the needle overshooting.
    /// </summary>
    [Fact]
    public void Advance_LeadsAStepAndThenPullsBackBelowIt()
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(0.0, Tick);

        var first = compensator.Advance(70.0, Tick);
        var second = compensator.Advance(70.0, Tick);

        Assert.Equal(74.4287, first, precision: 3);
        Assert.Equal(52.7886, second, precision: 3);
    }

    [Fact]
    public void Advance_ClampsAStepShorterThanMinStep()
    {
        var tiny = FirstShapedStep(TimeSpan.FromTicks(1));
        var atFloor = FirstShapedStep(NeedleCompensator.MinStep);

        Assert.Equal(atFloor, tiny, precision: 9);
    }

    [Fact]
    public void Advance_ClampsAStepLongerThanMaxStep()
    {
        var huge = FirstShapedStep(TimeSpan.FromSeconds(5));
        var atCeiling = FirstShapedStep(NeedleCompensator.MaxStep);

        Assert.Equal(atCeiling, huge, precision: 9);
    }

    [Fact]
    public void Reset_ClearsTheStateSoTheNextCallPrimesAgain()
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(0.0, Tick);
        compensator.Advance(70.0, Tick);

        compensator.Reset();

        Assert.Equal(70.0, compensator.Advance(70.0, Tick));
    }

    private static double FirstShapedStep(TimeSpan step)
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(0.0, step);
        return compensator.Advance(70.0, step);
    }
}
