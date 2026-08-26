# VU Needle Compensator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a throwaway measurement build where the right VU meter's command is pre-shaped by an inverse-plant biquad, so the owner can compare a compensated needle against today's uncompensated one side by side.

**Architecture:** A new detector (τ = 15 ms) and a new biquad live inside `AudioLevelSensorSource` and surface as one new pseudo-sensor, `/audio/0/needle`, which returns deflection in percent rather than dBFS. Channel 1 is pointed at it with `Min = 0`, `Max = 100`, so `ChannelMapper` becomes a pass-through and nothing downstream of the audio source changes. Channel 0 keeps today's chain untouched. Both detectors are fed the same mono `(L+R)/2` signal so the two needles differ only in ballistics.

**Tech Stack:** C# on .NET 10 (`net10.0-windows`), WinForms tray app, xUnit tests.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-26-vu-needle-compensator-throwaway-design.md`. Read it before starting.
- This is a **throwaway measurement build**. It lives on `throwaway/vu-needle-compensator` and must never be merged into `main` as code.
- `PlantZeta = 0.391`, `PlantOmegaN = 11.81`, `TargetZeta = 0.81`, `TargetOmegaN = 13.4221`, `DetectorTauMs = 15.0`. Exact values, all `const` at the top of `NeedleCompensator.cs`.
- The dB window for **both** chains is −40…0 dBFS (`VuModeSwitch.DefaultMinDbfs` / `DefaultMaxDbfs`). The scale must not differ between the two meters — that is the whole point of the experiment.
- `OnSamples` runs on the WASAPI capture thread. It must not allocate and must not take a lock. Existing comments in `AudioLevelSensorSource` explain why a lock there deadlocks the process; do not add one.
- Channel 0 (left, pin 3) keeps today's behaviour bit for bit. Any change that alters it is a bug.
- Build: `dotnet build AnalogHwMonitor.sln`. Tests: `dotnet test AnalogHwMonitor.sln`. There are 205 tests today; all must still pass.

---

### Task 1: VuIntegrator gains a settable time constant and a mono fold

The compensated chain needs a 15 ms detector, but τ is a `static readonly` shared by every instance today. It also needs `(L+R)/2` folded before rectification, which the existing per-channel `Add` cannot do.

**Files:**
- Modify: `AnalogHwMonitor.Core/VuIntegrator.cs`
- Test: `AnalogHwMonitor.Tests/VuIntegratorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `VuIntegrator(double timeConstantSeconds)` — throws `ArgumentOutOfRangeException` when not positive.
  - `VuIntegrator()` — unchanged behaviour, uses `VuIntegrator.TimeConstantSeconds`.
  - `void AddMono(ReadOnlySpan<float> block, int channelCount, int sampleRate)`.

- [ ] **Step 1: Write the failing tests**

Add to `AnalogHwMonitor.Tests/VuIntegratorTests.cs`. The file already has a `Constant(seconds, value, channels)` helper and a `SampleRate` constant — reuse them.

```csharp
    [Fact]
    public void Add_CustomTimeConstantReachesOneMinusOneOverEAtTau()
    {
        var integrator = new VuIntegrator(0.015);

        integrator.Add(Constant(0.015, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        Assert.Equal(1.0 - (1.0 / Math.E), integrator.Level, precision: 3);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveTimeConstant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VuIntegrator(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VuIntegrator(-0.010));
    }

    /// <summary>
    /// The default must not move: channel 0 is the control in the compensator
    /// experiment and has to stay bit for bit what it is today.
    /// </summary>
    [Fact]
    public void DefaultTimeConstantIsStillThreeHundredMillisecondsToNinetyNinePercent()
    {
        Assert.Equal(0.300 / Math.Log(100.0), VuIntegrator.TimeConstantSeconds, precision: 9);

        var integrator = new VuIntegrator();
        integrator.Add(Constant(0.300, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        Assert.Equal(0.99, integrator.Level, precision: 2);
    }

    [Fact]
    public void AddMono_AveragesTheChannelsOfEachFrame()
    {
        // Left at +1.0, right at 0.0: the mono fold is 0.5.
        var integrator = new VuIntegrator(0.010);
        var samples = new float[2 * SampleRate];
        for (var frame = 0; frame < SampleRate; frame++)
        {
            samples[frame * 2] = 1.0f;
            samples[(frame * 2) + 1] = 0.0f;
        }

        integrator.AddMono(samples, channelCount: 2, SampleRate);

        Assert.Equal(0.5, integrator.Level, precision: 3);
    }

    /// <summary>
    /// The fold is (L+R)/2 before rectification, not the average of two rectified
    /// channels, so anti-phase content cancels. That is what "mono" means here and
    /// what both needles will be fed during the experiment.
    /// </summary>
    [Fact]
    public void AddMono_CancelsAntiPhaseContent()
    {
        var integrator = new VuIntegrator(0.010);
        var samples = new float[2 * SampleRate];
        for (var frame = 0; frame < SampleRate; frame++)
        {
            samples[frame * 2] = 1.0f;
            samples[(frame * 2) + 1] = -1.0f;
        }

        integrator.AddMono(samples, channelCount: 2, SampleRate);

        Assert.Equal(0.0, integrator.Level, precision: 6);
    }

    [Fact]
    public void AddMono_HandlesAMonoEndpoint()
    {
        var integrator = new VuIntegrator(0.010);

        integrator.AddMono(Constant(1.0, 1.0f, 1), channelCount: 1, SampleRate);

        Assert.Equal(1.0, integrator.Level, precision: 3);
    }

    [Fact]
    public void AddMono_IgnoresNonsenseArguments()
    {
        var integrator = new VuIntegrator(0.010);

        integrator.AddMono(Constant(0.010, 1.0f, 2), channelCount: 0, SampleRate);
        integrator.AddMono(Constant(0.010, 1.0f, 2), channelCount: 2, sampleRate: 0);

        Assert.Equal(0.0, integrator.Level);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~VuIntegratorTests`
Expected: compile errors — no `VuIntegrator(double)` constructor and no `AddMono` method.

- [ ] **Step 3: Implement**

In `AnalogHwMonitor.Core/VuIntegrator.cs`, add a field and constructors immediately above `private double _level;`:

```csharp
    private readonly double _tau;

    public VuIntegrator()
        : this(TimeConstantSeconds)
    {
    }

    /// <summary>
    /// A shorter time constant than the standard's 65 ms exists for one reason: the
    /// throwaway compensator build puts the averaging in the needle compensator
    /// instead, and leaves this filter doing nothing but rectification and ripple
    /// rejection.
    /// </summary>
    public VuIntegrator(double timeConstantSeconds)
    {
        if (timeConstantSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeConstantSeconds), timeConstantSeconds, "The time constant must be positive.");
        }

        _tau = timeConstantSeconds;
    }
```

In `Add`, replace `TimeConstantSeconds` with `_tau`:

```csharp
        var alpha = 1.0 - Math.Exp(-1.0 / (sampleRate * _tau));
```

In `Decay`, replace `TimeConstantSeconds` with `_tau`:

```csharp
        var decayed = level * Math.Exp(-elapsed.TotalSeconds / _tau);
```

Add `AddMono` immediately after `Add`:

```csharp
    /// <summary>
    /// Folds every channel of a frame to one value before rectifying — (L+R)/2, not the
    /// average of two rectified channels, so anti-phase content cancels the way it does
    /// on a mono downmix.
    ///
    /// Allocates nothing: the span is read in place and the running sum is one double,
    /// because this is called on the WASAPI capture thread.
    /// </summary>
    public void AddMono(ReadOnlySpan<float> block, int channelCount, int sampleRate)
    {
        if (channelCount <= 0 || sampleRate <= 0)
        {
            return;
        }

        var alpha = 1.0 - Math.Exp(-1.0 / (sampleRate * _tau));
        var level = Volatile.Read(ref _level);
        var frames = block.Length / channelCount;

        for (var frame = 0; frame < frames; frame++)
        {
            var start = frame * channelCount;
            var sum = 0.0;

            for (var channel = 0; channel < channelCount; channel++)
            {
                sum += block[start + channel];
            }

            level += (Math.Abs(sum / channelCount) - level) * alpha;
        }

        Volatile.Write(ref _level, level);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS, all 205 existing tests plus the 7 new ones.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/VuIntegrator.cs AnalogHwMonitor.Tests/VuIntegratorTests.cs
git commit -m "feat: let VuIntegrator take a time constant and fold to mono"
```

---

### Task 2: NeedleCompensator, the biquad itself

The inverse-plant filter. `C(s) = ωt²(s² + 2ζp·ωp·s + ωp²) / (ωp²(s² + 2ζt·ωt·s + ωt²))`, discretised by the bilinear transform over the measured elapsed time.

**Files:**
- Create: `AnalogHwMonitor.Core/NeedleCompensator.cs`
- Test: `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `NeedleCompensator.PlantZeta`, `.PlantOmegaN`, `.TargetZeta`, `.TargetOmegaN`, `.DetectorTauMs` — `const double`.
  - `NeedleCompensator.MinStep`, `.MaxStep` — `static readonly TimeSpan`.
  - `double Advance(double percent, TimeSpan elapsed)` — returns the shaped command, **unclamped**. The caller clamps.
  - `void Reset()`.

- [ ] **Step 1: Write the failing tests**

Create `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~NeedleCompensatorTests`
Expected: compile error — `NeedleCompensator` does not exist.

- [ ] **Step 3: Implement**

Create `AnalogHwMonitor.Core/NeedleCompensator.cs`:

```csharp
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
/// standard's 300 ms criterion. An inverse filter has to cancel the plant *on time*, and
/// WinForms timer jitter of -8/+16 ms takes 16 from 1.0 % overshoot to 2.0 % median and
/// 5.9 % worst across twelve simulated runs, while 13.4221 holds 1.5 % and 3.2 %. The
/// 300 ms criterion is given up deliberately; the delivered t99 is about 348 ms.
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
    /// Drops the history. Called wherever the integrators are reset, because a capture
    /// that stops and starts again would otherwise resume against a stale two-tick
    /// history and kick the needle.
    /// </summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0.0;
        _primed = false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/NeedleCompensator.cs AnalogHwMonitor.Tests/NeedleCompensatorTests.cs
git commit -m "feat: add the inverse-plant needle compensator"
```

---

### Task 3: Prove the compensator actually cancels the plant

Task 2 tests the filter against itself. This tests the thing the build exists for: run the compensator's output through a simulated needle and check what the needle does. Without this the plan has no evidence the sign conventions and the coefficient layout are right.

**Files:**
- Create: `AnalogHwMonitor.Tests/NeedleSimulation.cs`
- Modify: `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`

**Interfaces:**
- Consumes: `NeedleCompensator.Advance` from Task 2.
- Produces: `NeedleSimulation.Step(double deflection, double velocity, double command, TimeSpan elapsed)` returning `(double Deflection, double Velocity)`.

- [ ] **Step 1: Write the failing tests**

Add to `AnalogHwMonitor.Tests/NeedleCompensatorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~NeedleCompensatorTests`
Expected: compile error — `NeedleSimulation` does not exist.

- [ ] **Step 3: Write the simulator**

Create `AnalogHwMonitor.Tests/NeedleSimulation.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// The measured needle as a second-order system, advanced in closed form over a
/// zero-order hold. Exact for any step size, so the test is not measuring an
/// integrator's error on top of the compensator's.
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

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS. If `CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot` fails while
`SimulatedNeedle_OvershootsAnUncompensatedStepByAboutTwentySixPercent` passes, the
compensator from Task 2 is wrong — check the coefficient signs there before touching the
simulator.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Tests/NeedleSimulation.cs AnalogHwMonitor.Tests/NeedleCompensatorTests.cs
git commit -m "test: prove the compensator cancels the measured needle"
```

---

### Task 4: Publish the compensated chain as /audio/0/needle

**Files:**
- Modify: `AnalogHwMonitor.Core/AudioSensorIds.cs`
- Modify: `AnalogHwMonitor.Core/AudioLevelSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/AudioLevelSensorSourceTests.cs`

**Interfaces:**
- Consumes: `VuIntegrator(double)` and `AddMono` from Task 1; `NeedleCompensator` from Task 2.
- Produces: `AudioSensorIds.Needle` = `"/audio/0/needle"`, `AudioSensorIds.NeedleUnit` = `"%"`. `Read(AudioSensorIds.Needle)` returns deflection in percent, 0–100, or `null` when capture cannot start.

The spec also asks that an unknown identifier still returns `null`. The existing
`Read_IgnoresIdentifiersThatBelongToAnotherSource` already guards that, and the new needle
branch is added *above* the existing switch without changing its fall-through, so no new
test is needed — but confirm that test still passes rather than assuming it.

- [ ] **Step 1: Write the failing tests**

Add to `AnalogHwMonitor.Tests/AudioLevelSensorSourceTests.cs`:

```csharp
    [Fact]
    public void Discover_AlsoPublishesTheCompensatedNeedle()
    {
        var (source, _, _) = Build();
        using (source)
        {
            var sensors = source.Discover();

            Assert.Equal(3, sensors.Count);
            Assert.Equal(AudioSensorIds.Needle, sensors[2].Id);
            Assert.Equal("%", sensors[2].Unit);
        }
    }
```

The existing `Discover_PublishesTheTwoLevelsNamedAfterTheDevice` asserts `sensors.Count == 2`
and that every descriptor's unit is `"dBFS"`. Both now fail. Update it — do not weaken it:

```csharp
    [Fact]
    public void Discover_PublishesTheTwoLevelsNamedAfterTheDevice()
    {
        var (source, capture, _) = Build();
        capture.DeviceName = "Realtek Audio";
        using (source)
        {
            var sensors = source.Discover();

            Assert.Equal(AudioSensorIds.Left, sensors[0].Id);
            Assert.Equal(AudioSensorIds.Right, sensors[1].Id);
            Assert.All(sensors, s => Assert.Equal(SensorKind.Audio, s.Kind));

            // The needle is the throwaway build's third sensor and reports a deflection,
            // so only the two levels carry the dBFS unit.
            Assert.Equal("dBFS", sensors[0].Unit);
            Assert.Equal("dBFS", sensors[1].Unit);

            Assert.Equal("Realtek Audio · Level L", sensors[0].Display);
        }
    }

    /// <summary>
    /// The needle sensor reports deflection, not level: a full-scale sine is 0 dBFS,
    /// which is the top of the -40..0 window and therefore full deflection.
    /// </summary>
    [Fact]
    public void Read_NeedleReturnsPercentRatherThanDecibels()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Needle);
            capture.DeliverSine(0.500);
            time.Advance(TimeSpan.FromMilliseconds(40));

            var value = source.Read(AudioSensorIds.Needle);

            Assert.NotNull(value);
            Assert.InRange(value!.Value, 99.0f, 100.0f);
        }
    }

    [Fact]
    public void Read_NeedleIsSilentBeforeAnySignal()
    {
        var (source, _, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Needle);
            time.Advance(TimeSpan.FromMilliseconds(40));

            var value = source.Read(AudioSensorIds.Needle);

            Assert.NotNull(value);
            Assert.Equal(0.0f, value!.Value);
        }
    }

    /// <summary>
    /// Both meters see the same mono fold during the experiment, so the left channel's
    /// level must not change when the needle chain is added.
    /// </summary>
    [Fact]
    public void Read_NeedleDoesNotDisturbTheLeftLevel()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(0.500);
            time.Advance(TimeSpan.FromMilliseconds(40));

            var before = source.Read(AudioSensorIds.Left);
            source.Read(AudioSensorIds.Needle);
            var after = source.Read(AudioSensorIds.Left);

            Assert.Equal(before, after);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test AnalogHwMonitor.sln --filter FullyQualifiedName~AudioLevelSensorSourceTests`
Expected: compile error — `AudioSensorIds.Needle` does not exist.

- [ ] **Step 3: Add the identifier**

In `AnalogHwMonitor.Core/AudioSensorIds.cs`, add below `Right`:

```csharp
    /// <summary>
    /// THROWAWAY MEASUREMENT BUILD. Deflection in percent, already shaped by
    /// <see cref="NeedleCompensator"/> — not a level in dBFS like the two above.
    /// </summary>
    public const string Needle = "/audio/0/needle";

    /// <summary>Unit of <see cref="Needle"/>, which is a deflection and not a level.</summary>
    public const string NeedleUnit = "%";
```

- [ ] **Step 4: Wire the compensated chain into the source**

In `AnalogHwMonitor.Core/AudioLevelSensorSource.cs`:

Add fields next to `_integrators`:

```csharp
    // THROWAWAY MEASUREMENT BUILD. A third detector, deliberately much faster than the
    // two above, because the averaging the standard asks for is done by the compensator
    // rather than by this filter.
    private readonly VuIntegrator _needleDetector = new(NeedleCompensator.DetectorTauMs / 1000.0);
    private readonly NeedleCompensator _compensator = new();
    private long _lastNeedleTicks;
```

In `Discover()`, add a third descriptor:

```csharp
            new SensorDescriptor(AudioSensorIds.Left, "Level L", device, SensorKind.Audio, AudioSensorIds.Unit),
            new SensorDescriptor(AudioSensorIds.Right, "Level R", device, SensorKind.Audio, AudioSensorIds.Unit),
            new SensorDescriptor(
                AudioSensorIds.Needle, "Needle (compensated)", device, SensorKind.Audio, AudioSensorIds.NeedleUnit),
```

Extract the level-to-dBFS conversion so both the existing levels and the needle share it.
Add this private method next to `ReadNeedle` below:

```csharp
    /// <summary>
    /// Level to dBFS, shared by every audio identifier so the floor rule lives in one
    /// place. Digital silence returns the floor *before* volume compensation rather than
    /// after: the compensation would otherwise lift the floor by up to its ceiling, and
    /// silence on a quiet system would read differently from silence on a muted one.
    /// </summary>
    private double LevelToDbfs(double level)
    {
        if (level <= 0.0)
        {
            return AudioSensorIds.FloorDbfs;
        }

        var dbfs = 20.0 * Math.Log10(level);

        if (_compensateVolume())
        {
            dbfs += Math.Min(-_capture.VolumeDb, MaxCompensationDb);
        }

        return Math.Max(dbfs, AudioSensorIds.FloorDbfs);
    }
```

In the existing `Read`, replace everything from `var level = _integrators[channel].Level * AverageToPeak;`
down to the final `return (float)Math.Max(dbfs, AudioSensorIds.FloorDbfs);` — including the
`if (level <= 0.0)` early return and its comment, which now lives on `LevelToDbfs` — with:

```csharp
            return (float)LevelToDbfs(_integrators[channel].Level * AverageToPeak);
```

This must not change what `Read` returns for any input. The existing
`AudioLevelSensorSourceTests` are the guard; if any of them change behaviour, the
extraction is wrong.

Then insert this block at the very top of `Read`, before the existing `var channel = sensorId switch`:

```csharp
        if (sensorId == AudioSensorIds.Needle)
        {
            return ReadNeedle();
        }
```

Add `ReadNeedle` immediately after `Read`:

```csharp
    /// <summary>
    /// THROWAWAY MEASUREMENT BUILD. The compensated chain: a 15 ms detector, the same
    /// -40..0 dBFS window the uncompensated meter uses, and the inverse-plant biquad.
    ///
    /// The dB window is taken from <see cref="VuModeSwitch"/> rather than from the
    /// channel's Min/Max on purpose: this sensor already reports deflection, so the
    /// channel is configured 0..100 and its Min/Max can no longer carry the window.
    /// Both meters must see the same window or the experiment compares scales instead
    /// of ballistics.
    /// </summary>
    private float? ReadNeedle()
    {
        lock (_lifecycle)
        {
            _lastRead = _time.GetUtcNow();

            if (!EnsureStarted())
            {
                return null;
            }

            var now = _time.GetUtcNow();
            var previous = new DateTimeOffset(
                Interlocked.Exchange(ref _lastNeedleTicks, now.UtcTicks), TimeSpan.Zero);

            double percent;

            if (_capture.IsMuted)
            {
                percent = 0.0;
            }
            else
            {
                ApplySilenceDecay();

                var dbfs = LevelToDbfs(_needleDetector.Level * AverageToPeak);

                percent = ChannelMapper.ToPercent(
                    dbfs, VuModeSwitch.DefaultMinDbfs, VuModeSwitch.DefaultMaxDbfs);
            }

            var shaped = _compensator.Advance(percent, now - previous);

            // The compensator returns unclamped on purpose; this is where the command
            // meets a needle that has a peg at each end. On a release into digital
            // silence it asks for about -11 % for seven ticks and loses that much
            // compensation, which is a known and documented limit of the build.
            return (float)Math.Clamp(shaped, 0.0, 100.0);
        }
    }
```

In `EnsureStarted()`, extend the reset block so the needle chain is reset with the others:

```csharp
        foreach (var integrator in _integrators)
        {
            integrator.Reset();
        }

        _needleDetector.Reset();
        _compensator.Reset();
        Interlocked.Exchange(ref _lastNeedleTicks, nowTicks);
```

In `ApplySilenceDecay()`, decay the needle detector too, inside the same elapsed window:

```csharp
        foreach (var integrator in _integrators)
        {
            integrator.Decay(now - lastAdvance);
        }

        _needleDetector.Decay(now - lastAdvance);
```

In `OnSamples`, feed everything the mono fold instead of one channel each:

```csharp
        // THROWAWAY MEASUREMENT BUILD. Both meters get (L+R)/2 so the difference on the
        // dials is the difference in ballistics and nothing else. Stereo is off for the
        // duration of the experiment.
        for (var channel = 0; channel < _integrators.Length; channel++)
        {
            _integrators[channel].AddMono(samples, format.ChannelCount, format.SampleRate);
        }

        _needleDetector.AddMono(samples, format.ChannelCount, format.SampleRate);
```

This replaces the existing loop that used `Math.Min(channel, format.ChannelCount - 1)` as an offset. Delete that loop and its comment.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln`
Expected: PASS. Some existing `AudioLevelSensorSourceTests` assert per-channel behaviour; with the mono fold and `FakeAudioLoopbackCapture.DeliverSine` putting the same signal on every channel, they should read identically. If one fails because it delivered different content per channel, that test is now asserting stereo separation the experiment deliberately removed — update it to assert the mono fold and say so in the test's comment rather than reverting `OnSamples`.

- [ ] **Step 6: Commit**

```bash
git add AnalogHwMonitor.Core/AudioSensorIds.cs AnalogHwMonitor.Core/AudioLevelSensorSource.cs AnalogHwMonitor.Tests/AudioLevelSensorSourceTests.cs
git commit -m "feat: publish the compensated needle as /audio/0/needle"
```

---

### Task 5: Point the application at the throwaway configuration and the new channel

The last task, and the only one that touches the app. Two jobs: make sure `config.json` cannot be written, and force both VU channels into the experiment's layout regardless of what was saved before.

**Files:**
- Modify: `AnalogHwMonitor.App/Program.cs:14` and just after `var config = loaded.Config;`

**Interfaces:**
- Consumes: `AudioSensorIds.Needle` from Task 4.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Redirect the configuration store**

Replace line 14 of `AnalogHwMonitor.App/Program.cs`:

```csharp
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
```

with:

```csharp
        // THROWAWAY MEASUREMENT BUILD. Everything that saves configuration goes through
        // this one store — the tray's VU toggle and the settings window's Save both
        // write through MonitorService.Config — so redirecting it here is what keeps the
        // real config.json untouched. Cloning AppConfig does NOT achieve this; see the
        // conclusion of docs/superpowers/specs/2026-08-26-vu-second-order-throwaway-design.md.
        var throwawayPath = Path.Combine(directory, "config.throwaway.json");
        var realPath = Path.Combine(directory, "config.json");

        if (!File.Exists(throwawayPath) && File.Exists(realPath))
        {
            // Seeded from the real file so the build keeps the meters' calibration.
            // MinPwm/MaxPwm belong to the physical meter and the experiment needs them.
            try
            {
                File.Copy(realPath, throwawayPath);
            }
            catch (Exception ex)
            {
                log.Write($"Could not seed the throwaway configuration: {ex.Message}");
            }
        }

        var store = new ConfigStore(throwawayPath);
```

- [ ] **Step 2: Force the experiment's channel layout**

Immediately after `var config = loaded.Config;`, add:

```csharp
        // THROWAWAY MEASUREMENT BUILD. Forced rather than left to VuModeSwitch, because
        // Set() is a no-op when VU mode is already on and would then swap in a stash that
        // predates this build. Both channels get the same -40..0 window: channel 0 through
        // its Min/Max as usual, channel 1 inside the audio source, because its sensor
        // already reports deflection and its Min/Max is the 0..100 pass-through.
        log.Write(
            "THROWAWAY measurement build: needle compensator on channel 1. "
            + "config.throwaway.json is in use and config.json is not written.");

        VuModeSwitch.Set(config, true);

        config.Channels[0].Label = "VU Left";
        config.Channels[0].SensorId = AudioSensorIds.Left;
        config.Channels[0].Min = VuModeSwitch.DefaultMinDbfs;
        config.Channels[0].Max = VuModeSwitch.DefaultMaxDbfs;

        config.Channels[1].Label = "VU Right (comp)";
        config.Channels[1].SensorId = AudioSensorIds.Needle;
        config.Channels[1].Min = 0.0;
        config.Channels[1].Max = 100.0;
```

- [ ] **Step 3: Build and run the tests**

Run: `dotnet build AnalogHwMonitor.sln` then `dotnet test AnalogHwMonitor.sln`
Expected: build succeeds, all tests pass.

- [ ] **Step 4: Verify on the hardware**

Record the modification time of the real configuration first, so the claim that it is untouched is checked rather than asserted:

```bash
ls -l --time-style=full-iso publish/config.json
```

Then run the app and confirm, in order:

1. `log.txt` contains the `THROWAWAY measurement build` line.
2. `config.throwaway.json` was created next to the executable.
3. Both needles move on music, and they move together — the same mono signal.
4. The settings window shows channel 1 as `VU Right (comp)` with a `%` unit and a 0–100 range.
5. On a sharp attack the left needle visibly bounces past its resting point and the right one does not.

Then check the real file again:

```bash
ls -l --time-style=full-iso publish/config.json
```

Expected: identical timestamp. If it changed, stop — the redirect in Step 1 is incomplete and the experiment is damaging the real configuration.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.App/Program.cs
git commit -m "feat: run the throwaway build against its own configuration"
```

---

## After the build

The experiment is the point, not the code. When the owner has looked at both meters:

1. Write the verdict into the spec under a `# Výsledok` heading, the way
   `2026-08-26-vu-second-order-throwaway-design.md` does — including what the build could
   not answer, which is listed in its "Čo tento build povedať nemôže" section.
2. If the compensated needle wins, that is not a merge: it is a new spec for a
   non-throwaway implementation, with the left meter measured first, because its zeta and
   omegaN are still unknown.
3. Return with `git checkout main`, rebuild, and delete `config.throwaway.json`.
