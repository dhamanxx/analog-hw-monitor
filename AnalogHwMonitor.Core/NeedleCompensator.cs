namespace AnalogHwMonitor.Core;

/// <summary>
/// THROWAWAY MEASUREMENT BUILD. Pre-shapes one meter's deflection command so the
/// physical needle behaves like the VU standard's movement instead of its own.
///
/// The needle was measured from 240 fps video of commanded steps: a linear second-order
/// system with zeta 0.391 and omegaN 11.81 rad/s, which makes 26 % overshoot and takes
/// about a second to settle. The standard asks for 1 to 1.5 %. This filter is the ratio
/// of the two transfer functions, so the measured plant cancels and the target replaces
/// it:
///
///          wt^2 * (s^2 + 2*zp*wp*s + wp^2)
///   C(s) = -------------------------------
///          wp^2 * (s^2 + 2*zt*wt*s + wt^2)
///
/// DC gain is exactly 1, so the compensated meter reads the same steady level as the
/// uncompensated one. High-frequency gain is wt^2/wp^2 = 1.2916 — finite, so nothing is
/// differentiated and the filter cannot run away. The price is that tick-to-tick noise
/// in the level is amplified by about 29 %.
///
/// <see cref="TargetOmegaN"/> is 13.4221 rather than the 16 that would also meet the
/// standard's 300 ms criterion. An inverse filter has to cancel the plant *on time*, so
/// it minds WinForms timer jitter in a way the plant itself does not. The design
/// document's jitter simulation (-8/+16 ms, twelve runs, lives outside this repository
/// and is not reproduced by anything here) reports 16 degrading from 1.0 % overshoot to
/// 2.0 % median and 5.9 % worst, against 1.5 % and 3.2 % for 13.4221 — that comparison is
/// why 13.4221 was chosen. The 300 ms criterion is given up deliberately as a result. The
/// one t99 this repository actually measures is 320 ms, in
/// NeedleCompensatorTests.CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot,
/// which drives the compensator straight into the simulated plant with no detector and
/// no timer jitter in the loop; the design document's simulated figure for the detector-
/// plus-jitter case is about 348 ms.
/// </summary>
public sealed class NeedleCompensator
{
    /// <summary>Measured damping ratio of the physical needle.</summary>
    public const double PlantZeta = 0.391;

    /// <summary>Measured undamped natural frequency of the physical needle, rad/s.</summary>
    public const double PlantOmegaN = 11.81;

    /// <summary>ANSI C16.5 damping: 1.305 % of overshoot.</summary>
    public const double TargetZeta = 0.81;

    /// <summary>Target bandwidth, rad/s. Chosen for jitter tolerance, not for speed.</summary>
    public const double TargetOmegaN = 13.4221;

    /// <summary>Time constant of the detector that feeds this filter, milliseconds.</summary>
    public const double DetectorTauMs = 15.0;

    /// <summary>
    /// Shortest step the bilinear transform is evaluated at. Below this, k = 2/dt grows
    /// without bound and the coefficients lose all meaning; two ticks arriving back to
    /// back must not be able to do that.
    /// </summary>
    public static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Longest step. A stall longer than this is the application being descheduled, not
    /// the needle moving; the filter is stable, so it re-converges within a few ticks.
    /// </summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(200);

    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;
    private bool _primed;

    /// <summary>
    /// Advances one tick and returns the shaped deflection command, which is deliberately
    /// NOT clamped to 0-100: the caller clamps, because the amount the command wants to
    /// go out of range is the amount of compensation being lost, and that is worth being
    /// able to see from the outside.
    /// </summary>
    public double Advance(double percent, TimeSpan elapsed)
    {
        if (!_primed)
        {
            _x1 = _x2 = _y1 = _y2 = percent;
            _primed = true;
            return percent;
        }

        var dt = Math.Clamp(elapsed.TotalSeconds, MinStep.TotalSeconds, MaxStep.TotalSeconds);
        var k = 2.0 / dt;
        var kk = k * k;

        var b0 = kk + (2.0 * PlantZeta * PlantOmegaN * k) + (PlantOmegaN * PlantOmegaN);
        var b1 = (-2.0 * kk) + (2.0 * PlantOmegaN * PlantOmegaN);
        var b2 = kk - (2.0 * PlantZeta * PlantOmegaN * k) + (PlantOmegaN * PlantOmegaN);

        var a0 = kk + (2.0 * TargetZeta * TargetOmegaN * k) + (TargetOmegaN * TargetOmegaN);
        var a1 = (-2.0 * kk) + (2.0 * TargetOmegaN * TargetOmegaN);
        var a2 = kk - (2.0 * TargetZeta * TargetOmegaN * k) + (TargetOmegaN * TargetOmegaN);

        var gain = (TargetOmegaN * TargetOmegaN) / (PlantOmegaN * PlantOmegaN);

        var y = ((gain * ((b0 * percent) + (b1 * _x1) + (b2 * _x2))) - (a1 * _y1) - (a2 * _y2)) / a0;

        _x2 = _x1;
        _x1 = percent;
        _y2 = _y1;
        _y1 = y;

        return y;
    }

    /// <summary>
    /// Drops the history. Reached only through
    /// <see cref="AudioLevelSensorSource.EnsureStarted"/>, alongside the level
    /// integrators' own reset, so it covers a capture's first start and any restart that
    /// follows a genuine <c>Stop()</c> — the idle timeout, a default-device change, or
    /// recovery from a failed start.
    ///
    /// It does NOT cover every path back into VU meter mode. Toggling VU mode off and
    /// back on within <see cref="AudioLevelSensorSource.IdleTimeout"/> (5 s) never calls
    /// <c>Stop()</c> — the capture is still running, just unread — so <c>EnsureStarted</c>
    /// returns early and this method is not called. That path resumes on whatever
    /// two-tick history was here before the gap, with the next <see cref="Advance"/>
    /// call's <c>dt</c> clamped to <see cref="MaxStep"/>: one kick on the needle, not the
    /// stale-history problem this reset exists to prevent.
    /// </summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0.0;
        _primed = false;
    }
}
