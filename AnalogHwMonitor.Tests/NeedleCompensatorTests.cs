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

    /// <summary>
    /// The uncompensated needle overshoots a step by about 26 %. This is the control
    /// for the test below it — if this ever stops holding, the simulator is wrong and
    /// nothing the compensated test says can be trusted.
    /// </summary>
    [Fact]
    public void SimulatedNeedle_OvershootsAnUncompensatedStepByAboutTwentySixPercent()
    {
        var deflection = 0.0;
        var velocity = 0.0;
        var peak = 0.0;

        for (var i = 0; i < 120; i++)
        {
            (deflection, velocity) = NeedleSimulation.Step(deflection, velocity, 70.0, Tick);
            peak = Math.Max(peak, deflection);
        }

        Assert.InRange((peak - 70.0) / 70.0 * 100.0, 24.0, 28.0);
    }

    /// <summary>
    /// The test the build exists for: the compensator's output, driven into the measured
    /// needle, must leave the needle behaving like the standard's movement.
    ///
    /// The numbers are what the discretisation actually delivers, not the continuous
    /// target: sampled at tick edges the overshoot is 0.875 % against the target's
    /// 1.305 %, and t99 is 320 ms against 300 ms. Both are inside "the tolerance of
    /// discretisation" the spec asks for. What matters is that 26 % became under 1.5 %.
    /// </summary>
    [Fact]
    public void CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot()
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(0.0, Tick);

        var deflection = 0.0;
        var velocity = 0.0;
        var peak = 0.0;
        var t99 = TimeSpan.Zero;
        var trajectory = new List<double>();

        for (var i = 0; i < 120; i++)
        {
            var command = Math.Clamp(compensator.Advance(70.0, Tick), 0.0, 100.0);
            (deflection, velocity) = NeedleSimulation.Step(deflection, velocity, command, Tick);
            trajectory.Add(deflection);
            peak = Math.Max(peak, deflection);

            if (t99 == TimeSpan.Zero && deflection >= 0.99 * 70.0)
            {
                t99 = Tick * (i + 1);
            }
        }

        Assert.Equal(70.0, trajectory[^1], precision: 3);
        Assert.InRange((peak - 70.0) / 70.0 * 100.0, 0.5, 1.5);
        Assert.InRange(t99.TotalMilliseconds, 280.0, 360.0);
    }

    /// <summary>
    /// Cancelling an under-damped plant on a release means asking the needle to go
    /// backwards briefly, and a needle has a peg at zero. This pins the size of that
    /// ask, because <c>AudioLevelSensorSource.ReadNeedle</c> clamps it away and the
    /// amount clamped is the amount of compensation lost.
    ///
    /// Released from full deflection at the 40 ms VU tick the command dips to about
    /// -6.3 % on the first tick and is positive again on the next one — one tick, not a
    /// run of them. The filter is linear, so the dip is proportional to the deflection
    /// released from, and it grows as the tick shortens (about -13 % at 25 ms, -25 % at
    /// 5 ms). The figure is therefore specific to this tick rate and worth re-measuring
    /// if the VU timer changes.
    /// </summary>
    [Fact]
    public void Release_AsksForANegativeCommandTheClampMustDiscard()
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(100.0, Tick);

        // Settle, so the release starts from a genuine steady state rather than from
        // the priming call's history.
        for (var i = 0; i < 60; i++)
        {
            compensator.Advance(100.0, Tick);
        }

        var release = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            release.Add(compensator.Advance(0.0, Tick));
        }

        // Meaningfully negative, and on the first tick of the release.
        Assert.InRange(release[0], -8.0, -5.0);
        Assert.Equal(release.Min(), release[0]);

        // The clamp turns that into zero: this is the compensation being lost.
        Assert.Equal(0.0, Math.Clamp(release[0], 0.0, 100.0));

        // It recovers immediately rather than staying negative for several ticks.
        Assert.True(
            release[1] > 0.0,
            $"expected the command back above zero on the second tick, it was {release[1]}");
    }
}
