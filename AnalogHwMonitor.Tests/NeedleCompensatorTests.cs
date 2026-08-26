using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

public class NeedleCompensatorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The first call primes the filter to whatever it is handed, so a meter that is
    /// already deflected when compensation starts does not get a kick.
    /// </summary>
    [Fact]
    public void Advance_ReturnsTheInputOnTheFirstCall()
    {
        var compensator = new NeedleCompensator();

        Assert.Equal(42.0, compensator.Advance(42.0, Tick));
    }

    /// <summary>
    /// DC gain is exactly 1 — the plant's poles are cancelled and the target's
    /// substituted, and neither changes the steady state. This is also what lets
    /// ChannelReading keep reporting the uncompensated percent: at rest the two agree.
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
    /// The needle is under-damped, so cancelling it means pushing past the target and
    /// then pulling back hard. Both halves are what stops it overshooting.
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
        Assert.Equal(
            FirstShapedStep(NeedleCompensator.MinStep),
            FirstShapedStep(TimeSpan.FromTicks(1)),
            precision: 9);
    }

    [Fact]
    public void Advance_ClampsAStepLongerThanMaxStep()
    {
        Assert.Equal(
            FirstShapedStep(NeedleCompensator.MaxStep),
            FirstShapedStep(TimeSpan.FromSeconds(5)),
            precision: 9);
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

    /// <summary>
    /// The uncompensated needle overshoots a step by about 26 %. This is the control for
    /// the test below it — if this stops holding, the simulator is wrong and nothing the
    /// compensated test says can be trusted.
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
    /// The load-bearing test: the compensator's output, driven into the measured needle,
    /// must leave the needle behaving like the standard's movement.
    ///
    /// The numbers are what the 40 ms discretisation actually delivers, not the continuous
    /// target's 1.305 % and 300 ms: sampled at tick edges the overshoot is 0.875 % and t99
    /// is 320 ms. Do not "correct" them toward the continuous figures.
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

    private static double FirstShapedStep(TimeSpan step)
    {
        var compensator = new NeedleCompensator();
        compensator.Advance(0.0, step);
        return compensator.Advance(70.0, step);
    }

    /// <summary>
    /// Frequency of the needle's own mechanical resonance: at zeta 0.391 the peak of a
    /// second-order response sits at omegaN*sqrt(1 - 2*zeta^2), which is 9.84 rad/s or
    /// 1.57 Hz. That is 94 BPM, so an ordinary music envelope drives it directly.
    /// </summary>
    private const double NeedleResonanceHertz = 1.57;

    /// <summary>
    /// The uncompensated needle amplifies at its resonance — this is the thing being
    /// removed, and the test below is meaningless if it is not actually present.
    /// </summary>
    [Fact]
    public void SimulatedNeedle_AmplifiesAtItsOwnResonance()
    {
        var swing = SteadySwing(NeedleResonanceHertz, compensated: false);

        Assert.InRange(20.0 * Math.Log10(swing / 25.0), 2.0, 3.5);
    }

    /// <summary>
    /// The property the owner actually judged the build on: "the needle is less erratic and
    /// does not oscillate like crazy". That is not transient overshoot and no step test
    /// measures it. TargetZeta 0.81 is above 1/sqrt(2), above which a second-order response
    /// has no resonant peak at all, and this pins the roughly 5 dB that buys at 94 BPM.
    ///
    /// If this fails and the step tests still pass, someone has retuned TargetZeta below
    /// 0.707. Do not relax the range — the resonance is the point.
    /// </summary>
    [Fact]
    public void Resonance_IsFlattenedByAboutFiveDecibels()
    {
        var uncompensated = SteadySwing(NeedleResonanceHertz, compensated: false);
        var compensated = SteadySwing(NeedleResonanceHertz, compensated: true);

        Assert.InRange(20.0 * Math.Log10(compensated / uncompensated), -6.0, -4.0);
    }

    /// <summary>
    /// Half the peak-to-peak deflection the needle settles into for a sinusoidal command
    /// of amplitude 25 about mid-scale, measured over the second half of the run so the
    /// start-up transient is excluded.
    ///
    /// 25 about 50 is chosen so the shaped command stays inside 0-100 — the compensator's
    /// high-frequency gain is 1.2916, so it swings roughly 36 to 68 and never meets the
    /// caller's clamp. A larger amplitude would measure clipping instead of ballistics.
    /// </summary>
    private static double SteadySwing(double hertz, bool compensated)
    {
        var compensator = new NeedleCompensator();
        var deflection = 0.0;
        var velocity = 0.0;
        var low = double.MaxValue;
        var high = double.MinValue;

        for (var tick = 0; tick < 400; tick++)
        {
            var seconds = tick * Tick.TotalSeconds;
            var command = 50.0 + (25.0 * Math.Sin(2.0 * Math.PI * hertz * seconds));
            var driven = compensated ? compensator.Advance(command, Tick) : command;

            (deflection, velocity) = NeedleSimulation.Step(
                deflection, velocity, Math.Clamp(driven, 0.0, 100.0), Tick);

            if (tick >= 200)
            {
                low = Math.Min(low, deflection);
                high = Math.Max(high, deflection);
            }
        }

        return (high - low) / 2.0;
    }
}
