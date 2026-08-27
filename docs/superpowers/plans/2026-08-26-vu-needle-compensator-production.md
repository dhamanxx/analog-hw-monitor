# Production Needle Compensator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the inverse-plant needle compensator into the real application on both VU channels, so the meters stop overshooting, stop slamming into the zero peg, and stop resonating with the beat of the music.

**Architecture:** One stateful filter class in Core, and one hook in `MonitorService.Tick` between the mapped deflection percentage and the PWM byte. `ChannelPipeline` is deliberately left alone — the settings window calls it on every refresh and must not advance a filter's state as a side effect. The compensator runs only on the two VU channels and only while VU meter mode is on, because outside it the tick is 1 Hz and there is nothing on the other channels that steps anyway.

**Tech Stack:** C# on .NET 10 (`net10.0-windows`), WinForms tray app, xUnit tests.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-26-vu-needle-compensator-production-design.md`. Read it before starting.
- **Branch from `main`.** The measurement build lives on `throwaway/vu-needle-compensator`; that branch is kept deliberately as a reusable rig and **must not be deleted or merged**. This work is unrelated to it and starts from `main`.
- Constants are measured values, exact: `PlantZeta = 0.391`, `PlantOmegaN = 11.81`, `TargetZeta = 0.81`, `TargetOmegaN = 13.4221`. Do not round or rename them. There is no `DetectorTauMs` in this build — the detector is the ordinary `VuIntegrator`.
- `ChannelPipeline.Evaluate` must not change and must not gain a side effect. Its own comment explains that the tick loop and the settings window must not disagree about what a reading means.
- `ChannelReading` keeps reporting the **uncompensated** percent and PWM. That is exact whenever the needle is settled, because the compensator's DC gain is exactly 1.
- Build: `dotnet build AnalogHwMonitor.sln`. Tests: `dotnet test AnalogHwMonitor.sln`. The baseline on `main` is 177 tests, 7 of them skipped by design; all must still pass.

---

### Task 1: NeedleCompensator and its proven tests

The filter itself, plus the tests that already earned their keep on the measurement branch. Nothing uses it yet.

`NeedleCompensator.cs`, `NeedleCompensatorTests.cs` and `NeedleSimulation.cs` exist on `throwaway/vu-needle-compensator` and were reviewed there. You are **not** cherry-picking them — the throwaway versions carry a `DetectorTauMs` constant and a `Reset()` doc comment describing a call path that does not exist here. Type the versions below.

**Files:**
- Create: `AnalogHwMonitor.Core/NeedleCompensator.cs`
- Create: `AnalogHwMonitor.Tests/NeedleSimulation.cs`
- Test: `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `NeedleCompensator.PlantZeta`, `.PlantOmegaN`, `.TargetZeta`, `.TargetOmegaN` — `const double`.
  - `NeedleCompensator.MinStep`, `.MaxStep` — `static readonly TimeSpan`, 5 ms and 200 ms.
  - `double Advance(double percent, TimeSpan elapsed)` — returns the shaped command, **unclamped**.
  - `void Reset()`.
  - `NeedleSimulation.Step(double deflection, double velocity, double command, TimeSpan elapsed)` returning `(double Deflection, double Velocity)`.

- [ ] **Step 1: Write the failing tests**

Create `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~NeedleCompensatorTests`
Expected: compile errors — neither `NeedleCompensator` nor `NeedleSimulation` exists.

- [ ] **Step 3: Write the compensator**

Create `AnalogHwMonitor.Core/NeedleCompensator.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Pre-shapes a VU meter's deflection command so the physical needle behaves like the VU
/// standard's movement instead of its own.
///
/// The needle was measured from 240 fps video of commanded steps: a linear second-order
/// system with zeta 0.391 and omegaN 11.81 rad/s, which makes 26 % overshoot and takes
/// about a second to settle. The standard asks for 1 to 1.5 %. This filter is the ratio of
/// the two transfer functions, so the measured plant cancels and the target replaces it:
///
///          wt^2 * (s^2 + 2*zp*wp*s + wp^2)
///   C(s) = -------------------------------
///          wp^2 * (s^2 + 2*zt*wt*s + wt^2)
///
/// DC gain is exactly 1, so a compensated meter reads the same steady level as an
/// uncompensated one — which is why the settings window can keep showing the uncompensated
/// percentage. High-frequency gain is wt^2/wp^2 = 1.2916: finite, so nothing is
/// differentiated and the filter cannot run away. The price is that tick-to-tick noise in
/// the level is amplified by about 29 %.
///
/// The overshoot is not the main prize. At zeta 0.391 the needle's own response peaks
/// +2.86 dB at 1.57 Hz — 94 BPM — so an ordinary music envelope mechanically pumps it, and
/// that is what "the needle swings wildly" actually is. TargetZeta 0.81 is above
/// 1/sqrt(2) = 0.707, above which a second-order response has no peak at all, and the
/// resonance goes away with it. NeedleCompensatorTests.Resonance_* pins that; keep
/// TargetZeta above 0.707 or it comes back.
///
/// TargetOmegaN is 13.4221 rather than the 16 that would also meet the standard's 300 ms
/// criterion. An inverse filter must cancel the plant *on time*, so it minds WinForms
/// timer jitter in a way the plant itself does not, and 13.4221 was the jitter-tolerant
/// choice. The 300 ms criterion is given up deliberately as a result. That trade-off is a
/// property of the bilinear discretisation below, not of the physics — see the production
/// design document's closing section.
/// </summary>
public sealed class NeedleCompensator
{
    /// <summary>Measured damping ratio of the physical needle.</summary>
    public const double PlantZeta = 0.391;

    /// <summary>Measured undamped natural frequency of the physical needle, rad/s.</summary>
    public const double PlantOmegaN = 11.81;

    /// <summary>ANSI C16.5 damping: 1.305 % of overshoot, and no resonant peak.</summary>
    public const double TargetZeta = 0.81;

    /// <summary>Target bandwidth, rad/s. Chosen for jitter tolerance, not for speed.</summary>
    public const double TargetOmegaN = 13.4221;

    /// <summary>
    /// Shortest step the bilinear transform is evaluated at. Below this, k = 2/dt grows
    /// without bound and the coefficients lose all meaning; two ticks arriving back to
    /// back must not be able to do that.
    /// </summary>
    public static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Longest step. A gap longer than this is the application being descheduled, not the
    /// needle moving; the filter is stable, so it re-converges within a few ticks.
    /// </summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(200);

    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;
    private bool _primed;

    /// <summary>
    /// Advances one tick and returns the shaped deflection command, which is deliberately
    /// NOT clamped to 0-100: the caller clamps, because the amount the command wants to go
    /// out of range is the amount of compensation being lost, and that is worth being able
    /// to see from the outside.
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
    /// Drops the history, so the next <see cref="Advance"/> primes on whatever it is
    /// handed instead of resuming against a stale two-tick history from before a gap.
    /// <see cref="MonitorService"/> owns when this happens.
    /// </summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0.0;
        _primed = false;
    }
}
```

- [ ] **Step 4: Write the needle simulator**

Create `AnalogHwMonitor.Tests/NeedleSimulation.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS. Baseline is 177 tests (7 skipped), so expect 185.

If `CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot` fails while
`SimulatedNeedle_OvershootsAnUncompensatedStepByAboutTwentySixPercent` passes, the
compensator's arithmetic is wrong — check the signs of `b1`/`a1` and where `gain` sits.

- [ ] **Step 6: Commit**

```bash
git add AnalogHwMonitor.Core/NeedleCompensator.cs AnalogHwMonitor.Tests/NeedleCompensator*.cs AnalogHwMonitor.Tests/NeedleSimulation.cs
git commit -m "feat: add the inverse-plant needle compensator"
```

---

### Task 2: Pin the resonance, which is what actually won the experiment

Every test in Task 1 is a step test. None of them would notice if someone retuned
`TargetZeta` below `1/sqrt(2) = 0.707` and quietly brought the needle's resonance with the
beat back. This task adds the test that would.

**Files:**
- Test: `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`

**Interfaces:**
- Consumes: `NeedleCompensator.Advance` and `NeedleSimulation.Step` from Task 1.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Write the failing tests**

Add to `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`, inside the existing class so
`Tick` is in scope:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS, 187 tests. These are written against a compensator that already works, so
they should pass immediately — that is expected, not a smell. The failing case they exist
for is a future retuning, and you can see them fail on purpose in the next step.

- [ ] **Step 3: Prove the test actually guards the property**

Temporarily change `TargetZeta` in `AnalogHwMonitor.Core/NeedleCompensator.cs` from `0.81`
to `0.60` — still stable, still cancels the plant, but back below `1/sqrt(2)`.

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~NeedleCompensatorTests`
Expected: `Resonance_IsFlattenedByAboutFiveDecibels` FAILS. If it passes, the test does not
guard what it claims and must be fixed before going on.

Then put `TargetZeta` back to `0.81` and re-run: all pass. Record both outcomes in your
report.

- [ ] **Step 4: Commit**

```bash
git add AnalogHwMonitor.Tests/NeedleCompensatorTests.cs
git commit -m "test: pin the needle resonance the compensator flattens"
```

---

### Task 3: Wire the compensator into the tick

**Files:**
- Modify: `AnalogHwMonitor.Core/MonitorService.cs`
- Test: `AnalogHwMonitor.Tests/MonitorServiceTests.cs`

**Interfaces:**
- Consumes: `NeedleCompensator` from Task 1.
- Produces: `MonitorService(ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log, TimeProvider? time = null)`.

Read `AnalogHwMonitor.Core/MonitorService.cs` before starting. `Tick()` currently loops the
five channels, calls `ChannelPipeline.Evaluate` for each, collects PWM bytes and
`ChannelReading`s, then sends one frame. You are adding a step between the percentage and
the PWM byte, for VU channels only.

- [ ] **Step 1: Write the failing tests**

Add to `AnalogHwMonitor.Tests/MonitorServiceTests.cs`. The file already has
`ConfigWithSensors()` and `SensorsAt(...)` helpers; these tests need a VU-mode config
instead, so add this helper next to them:

```csharp
    /// <summary>
    /// VU mode with both audio channels reading a steady -12 dBFS, which is 70 % of the
    /// -40..0 window. Channels 2-4 keep ordinary sensors so the frame stays five wide.
    /// </summary>
    private static (AppConfig Config, FakeSensorSource Sensors) VuModeAt(float dbfs)
    {
        var config = AppConfig.CreateDefault();
        VuModeSwitch.Set(config, true);
        config.Channels[2].SensorId = "ram-load";
        config.Channels[3].SensorId = "cpu-temp";
        config.Channels[4].SensorId = "gpu-temp";

        var sensors = new FakeSensorSource(new Dictionary<string, float?>
        {
            [AudioSensorIds.Left] = dbfs,
            [AudioSensorIds.Right] = dbfs,
            ["ram-load"] = 0f,
            ["cpu-temp"] = 30f,
            ["gpu-temp"] = 30f,
        });

        return (config, sensors);
    }
```

Then the tests:

```csharp
    /// <summary>
    /// The compensator's DC gain is 1, so a level that has not moved produces a command
    /// identical to the uncompensated one. This is the invariant that lets ChannelReading
    /// keep reporting the uncompensated percentage.
    /// </summary>
    [Fact]
    public void Tick_CompensatedChannelsSettleOnTheUncompensatedValue()
    {
        var (config, sensors) = VuModeAt(-12f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(40));
            service.Tick();
        }

        // -12 dBFS on the -40..0 window is 70 %, and 70 % of 0..255 is PWM 179.
        Assert.Equal("V:179,179,0,0,0\n", link.Frames[^1]);
    }

    /// <summary>
    /// On the tick where the level steps, the compensator must lead: the needle is
    /// under-damped, so cancelling it means over-driving first. A frame identical to the
    /// uncompensated one here means the compensator is not in the path at all.
    /// </summary>
    [Fact]
    public void Tick_CompensatedChannelsLeadAStep()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        sensors.Set(AudioSensorIds.Right, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        // 74.43 % rather than 70 %, which is PWM 190 rather than 179.
        Assert.Equal("V:190,190,0,0,0\n", link.Frames[^1]);
    }

    /// <summary>
    /// The settings window reads ChannelReading and uses it to calibrate. It must show what
    /// the level means, not the transient control signal on its way to the meter.
    /// </summary>
    [Fact]
    public void Tick_ReportsTheUncompensatedValueEvenWhileLeadingAStep()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        IReadOnlyList<ChannelReading> readings = Array.Empty<ChannelReading>();
        service.Updated += (_, r) => readings = r;

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.Equal(70.0, readings[0].Percent, precision: 3);
        Assert.Equal(179, readings[0].Pwm);
    }

    /// <summary>
    /// Outside VU mode the tick is 1 Hz, far below what the needle's 578 ms ring period
    /// needs, and the channels that stay there do not step anyway. Compensating them would
    /// only add error.
    /// </summary>
    [Fact]
    public void Tick_DoesNotCompensateOutsideVuMode()
    {
        var sensors = SensorsAt(0, 50, 100, 30, 90);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(1000));
        service.Tick();
        sensors.Set("cpu-load", 100f);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        service.Tick();

        // Straight through, exactly as it was before the compensator existed.
        Assert.Equal("V:255,128,255,0,255\n", link.Frames[^1]);
    }

    /// <summary>
    /// A channel under the calibration slider is pinned to a raw PWM value. If the
    /// compensator shaped it, the slider would not hold.
    /// </summary>
    [Fact]
    public void Tick_DoesNotCompensateAChannelUnderTestPwm()
    {
        var (config, sensors) = VuModeAt(-40f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        service.SetTestPwm(0, 200);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();
        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.StartsWith("V:200,", link.Frames[^1]);

        // Releasing the slider must not resume on history from before it was touched:
        // the compensator was reset every pinned tick, so the first free tick primes.
        service.SetTestPwm(0, null);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        Assert.StartsWith("V:179,", link.Frames[^1]);
    }

    /// <summary>
    /// A dead channel must not leave history that kicks the needle when it comes back. The
    /// reset makes the first tick after recovery prime rather than lead, so it sends the
    /// uncompensated value.
    /// </summary>
    [Fact]
    public void Tick_ResetsACompensatorWhileItsSensorIsUnreadable()
    {
        var (config, sensors) = VuModeAt(-12f);
        var link = new FakeMeterLink();
        var time = new FakeTimeProvider();
        using var service = new MonitorService(sensors, link, config, NullLog.Instance, time);

        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, null);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        sensors.Set(AudioSensorIds.Left, -12f);
        time.Advance(TimeSpan.FromMilliseconds(40));
        service.Tick();

        // Primed, not leading: 70 % straight through.
        Assert.StartsWith("V:179,", link.Frames[^1]);
    }
```

`FakeSensorSource` has no way to change a value after construction. Add one to
`AnalogHwMonitor.Tests/Fakes/FakeSensorSource.cs`:

```csharp
    /// <summary>Changes a reading mid-test; null makes the sensor unreadable.</summary>
    public void Set(string sensorId, float? value) => _values[sensorId] = value;
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~MonitorServiceTests`
Expected: compile error — `MonitorService` has no five-argument constructor.

- [ ] **Step 3: Give MonitorService a clock and the compensators**

In `AnalogHwMonitor.Core/MonitorService.cs`, add to the fields:

```csharp
    private readonly TimeProvider _time;

    // One per VU channel, indexed the same as Config.Channels. Non-VU channels never get
    // one: outside VU mode the tick is 1 Hz against a 578 ms ring period, and the sensors
    // that live there — temperatures, memory — do not step in the first place.
    private readonly NeedleCompensator?[] _compensators = new NeedleCompensator?[FrameCodec.ChannelCount];

    private DateTimeOffset _lastTick;
```

Extend the constructor, keeping the old signature working for every existing caller:

```csharp
    public MonitorService(
        ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log, TimeProvider? time = null)
    {
        _sensors = sensors;
        _link = link;
        _log = log;
        _time = time ?? TimeProvider.System;
        _lastTick = _time.GetUtcNow();
        Config = config;

        foreach (var index in VuModeSwitch.VuChannels)
        {
            _compensators[index] = new NeedleCompensator();
        }
    }
```

- [ ] **Step 4: Apply the compensator in Tick**

In `Tick()`, immediately after `var pwmValues = ...` and `var readings = ...`, measure the
step:

```csharp
        var now = _time.GetUtcNow();
        var elapsed = now - _lastTick;
        _lastTick = now;
```

Then replace the body of the `else` branch — the one that today reads
`(percent, pwmValues[i]) = ChannelPipeline.Evaluate(...)` — with:

```csharp
            else
            {
                (percent, pwmValues[i]) = ChannelPipeline.Evaluate(
                    value!.Value, channel.Min, channel.Max, channel.MinPwm, channel.MaxPwm);

                // The compensated command goes to the meter; `percent` and pwmValues[i]
                // above stay uncompensated and are what ChannelReading reports below. At
                // rest the two are identical, because the compensator's DC gain is 1 —
                // they part only during a transient, where the settings window's number
                // would be unreadable anyway and calibration does not use it.
                if (Config.VuMode && _compensators[i] is { } compensator)
                {
                    var shaped = Math.Clamp(compensator.Advance(percent, elapsed), 0.0, 100.0);
                    pwmValues[i] = MeterCalibration.ToPwm(shaped, channel.MinPwm, channel.MaxPwm);
                }
            }
```

The `readings.Add(...)` line below is unchanged and still receives `percent` — leave it
alone.

Finally, two places that must drop the history rather than keep it.

In the `if (missing)` branch, next to `percent = 0; pwmValues[i] = 0;`:

```csharp
                _compensators[i]?.Reset();
```

And in the `if (_testPwm[i] is { } testPwm)` branch, before its `continue;` — a channel
held at a raw PWM value for calibration is not being driven by its sensor, so whatever
history the compensator holds describes a level from before the slider was touched:

```csharp
                _compensators[i]?.Reset();
```

- [ ] **Step 5: Reset both compensators when VU mode changes**

`VuModeSwitch.Set` is called by the tray and by the settings window, and neither goes
through `MonitorService`. The `Config` setter is what they all end up touching, so reset
there. In the `Config` property's setter, after the validation and `_config = value;`, add:

```csharp
            // Whatever swapped the configuration may have turned VU mode on or off, or
            // moved a channel's range. Either way the two-tick history the compensators
            // hold describes a chain that no longer exists.
            foreach (var compensator in _compensators)
            {
                compensator?.Reset();
            }
```

One ordering detail, because it looks like a bug and is not: the constructor sets
`Config = config` *before* the `foreach` fills `_compensators`, so the reset you just added
to the setter runs over an array of nulls. That is safe — `_compensators` is a field
initialiser, so the array itself already exists, and `compensator?.Reset()` on a null
element does nothing. Leave the field initialiser where it is rather than moving the
allocation into the constructor body.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS, 193 tests (7 skipped) — 177 on main, plus 8 from Task 1, 2 from Task 2 and 6 here.

If `Tick_CompensatedChannelsLeadAStep` reports 179 instead of 190, the compensator is not
in the path — check that `Config.VuMode` is true in `VuModeAt` and that
`VuModeSwitch.VuChannels` really is `{ 0, 1 }`.

- [ ] **Step 7: Commit**

```bash
git add AnalogHwMonitor.Core/MonitorService.cs AnalogHwMonitor.Tests/MonitorServiceTests.cs AnalogHwMonitor.Tests/Fakes/FakeSensorSource.cs
git commit -m "feat: compensate the VU needles in the tick"
```

---

### Task 4: Update the README

The README documents the VU meter mode and the calibration workflow, and both statements
change. A reader calibrating a meter needs to know why the PWM column and the needle can
disagree for a fraction of a second.

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Read the two sections you are changing**

Run: `grep -n "VU meter mode" README.md` and read the "VU meter mode" section and the
"Calibrating a meter" section in full. Match their voice — this README explains *why*, in
prose, and does not use bullet lists for reasoning.

- [ ] **Step 2: Add the compensator to the VU meter mode section**

Add these paragraphs at the end of the "VU meter mode" section:

```markdown
The needle itself is corrected. A moving-coil meter is a second-order system, and this one
was measured from 240 fps video of a commanded step: damping ratio 0.391, natural frequency
11.81 rad/s. That makes 26 % of overshoot where the VU standard asks for 1 to 1.5 %, and it
puts a 2.9 dB resonant peak at 1.57 Hz — 94 BPM, so ordinary music drives the needle at the
one frequency it is worst at. In VU meter mode both needles' commands go through a filter
that cancels those measured dynamics and substitutes the standard's, which takes the
overshoot to about 1 %, stops the needle hitting the zero stop on a release, and removes the
resonance entirely.

The correction applies to the two VU channels and only while VU meter mode is on. The other
three do not need it: a temperature does not jump to 90 °C in milliseconds and neither does
memory use, so there is no step for a needle to overshoot.
```

- [ ] **Step 3: Add the note to the calibration section**

Add this paragraph at the end of the "Calibrating a meter" section:

```markdown
On a VU channel the **PWM** column shows the value the level maps to, not always the value
on the wire. The needle correction described above shapes the command during a transient
and leaves it alone once the reading settles, so the two agree whenever the needle is
standing still — which is when you calibrate. The **Set PWM** slider bypasses the
correction completely, so a raw value stays exactly where you put it.
```

- [ ] **Step 4: Verify the claims you just wrote**

Every number above must be traceable. Check each against the source rather than against
this plan: 0.391 and 11.81 against `NeedleCompensator.PlantZeta`/`PlantOmegaN`; 26 % against
`SimulatedNeedle_OvershootsAnUncompensatedStepByAboutTwentySixPercent`; about 1 % against
`CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot`; 2.9 dB and 1.57 Hz against
`SimulatedNeedle_AmplifiesAtItsOwnResonance` and `NeedleResonanceHertz`. Report any that do
not match rather than adjusting the README to fit.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: describe the needle correction in the README"
```
