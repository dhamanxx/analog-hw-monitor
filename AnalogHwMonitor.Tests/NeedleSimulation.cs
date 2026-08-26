using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// The measured needle as a second-order system, advanced in closed form over a zero-order
/// hold. Exact for any step size, so a test using it is not also measuring an integrator's
/// error.
///
/// Written on the error e = deflection - command, which decays as
/// e(t) = exp(-sigma*t) * (e0*cos(wd*t) + ((v0 + sigma*e0)/wd)*sin(wd*t)).
/// </summary>
public static class NeedleSimulation
{
    public static (double Deflection, double Velocity) Step(
        double deflection, double velocity, double command, TimeSpan elapsed)
    {
        const double wn = NeedleCompensator.PlantOmegaN;
        const double zeta = NeedleCompensator.PlantZeta;

        var sigma = zeta * wn;
        var wd = wn * Math.Sqrt(1.0 - (zeta * zeta));
        var dt = elapsed.TotalSeconds;

        var decay = Math.Exp(-sigma * dt);
        var c1 = decay * Math.Cos(wd * dt);
        var c2 = decay * Math.Sin(wd * dt);

        var e0 = deflection - command;
        var e1 = (c1 * e0) + (c2 * (velocity + (sigma * e0)) / wd);
        var v1 = (c1 * velocity) - (c2 * (((wn * wn) * e0) + (sigma * velocity)) / wd);

        return (command + e1, v1);
    }
}
