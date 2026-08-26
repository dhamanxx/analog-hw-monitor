using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests;

public class VuIntegratorTests
{
    private const int SampleRate = 48_000;

    private static float[] Constant(double seconds, float value, int channels)
    {
        var frames = (int)(seconds * SampleRate);
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void Add_ReachesNinetyNinePercentAfterThreeHundredMilliseconds()
    {
        var integrator = new VuIntegrator();

        integrator.Add(Constant(0.300, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        // The 1942 VU standard: 99 % of the steady value within 300 ms.
        Assert.Equal(0.99, integrator.Level, precision: 2);
    }

    [Fact]
    public void Add_IsIndependentOfBlockSize()
    {
        var wholeBlock = new VuIntegrator();
        var manyBlocks = new VuIntegrator();
        var samples = Constant(0.300, 1.0f, 1);

        wholeBlock.Add(samples, offset: 0, stride: 1, SampleRate);

        // The same 300 ms handed over in 100 pieces, as WASAPI would.
        var chunk = samples.Length / 100;
        for (var i = 0; i < 100; i++)
        {
            manyBlocks.Add(samples.AsSpan(i * chunk, chunk), offset: 0, stride: 1, SampleRate);
        }

        Assert.Equal(wholeBlock.Level, manyBlocks.Level, precision: 9);
    }

    [Fact]
    public void Add_TakesOnlyItsOwnChannelOutOfAnInterleavedBlock()
    {
        // Left at full scale, right silent.
        var stereo = new float[2 * SampleRate];
        for (var i = 0; i < stereo.Length; i += 2)
        {
            stereo[i] = 1.0f;
        }

        var left = new VuIntegrator();
        var right = new VuIntegrator();

        left.Add(stereo, offset: 0, stride: 2, SampleRate);
        right.Add(stereo, offset: 1, stride: 2, SampleRate);

        Assert.True(left.Level > 0.99);
        Assert.Equal(0.0, right.Level);
    }

    [Fact]
    public void Add_RectifiesSoANegativeSignalStillDeflects()
    {
        var integrator = new VuIntegrator();

        integrator.Add(Constant(0.300, -1.0f, 1), offset: 0, stride: 1, SampleRate);

        Assert.Equal(0.99, integrator.Level, precision: 2);
    }

    [Fact]
    public void Decay_FallsWithTheSameTimeConstant()
    {
        var integrator = new VuIntegrator();
        integrator.Add(Constant(1.0, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        integrator.Decay(TimeSpan.FromMilliseconds(300));

        // Symmetric with the rise: 300 ms takes it down to 1 % of where it was.
        Assert.Equal(0.01, integrator.Level, precision: 3);
    }

    /// <summary>
    /// An exponential decay never reaches zero, so a long silence would otherwise drive the
    /// filter state down without limit — a probe build measured it past -1000 dB after a few
    /// seconds of quiet. Add() then keeps computing with that state per sample on the WASAPI
    /// capture thread, and far enough down that means denormal arithmetic on the one thread
    /// that must never be slow.
    /// </summary>
    [Fact]
    public void Decay_SnapsToZeroOnceTheLevelIsFarBelowAnythingReportable()
    {
        var integrator = new VuIntegrator();
        integrator.Add(Constant(1.0, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        integrator.Decay(TimeSpan.FromSeconds(2));

        Assert.Equal(0.0, integrator.Level);
    }

    /// <summary>
    /// The floor must sit far enough below the reported range to be invisible. -100 dBFS,
    /// the floor the audio source reports, is a level near 6.4e-6 — four orders of magnitude
    /// above where this snaps — so a needle at the bottom of its travel must still be
    /// carrying a real value, not a snapped zero.
    /// </summary>
    [Fact]
    public void Decay_LeavesALevelThatIsStillReportableAlone()
    {
        var integrator = new VuIntegrator();
        integrator.Add(Constant(1.0, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        // 700 ms lands near 1e-5, just above the -100 dBFS the source reports as its floor.
        integrator.Decay(TimeSpan.FromMilliseconds(700));

        Assert.True(
            integrator.Level > VuIntegrator.SilenceFloor,
            $"a still-reportable level was snapped to zero: {integrator.Level}");
        Assert.NotEqual(0.0, integrator.Level);
    }

    [Fact]
    public void Decay_IgnoresZeroAndNegativeElapsedTime()
    {
        var integrator = new VuIntegrator();
        integrator.Add(Constant(1.0, 0.5f, 1), offset: 0, stride: 1, SampleRate);
        var before = integrator.Level;

        integrator.Decay(TimeSpan.Zero);
        integrator.Decay(TimeSpan.FromMilliseconds(-50));

        Assert.Equal(before, integrator.Level);
    }

    [Fact]
    public void Add_IgnoresNonsenseStrideAndSampleRateInsteadOfThrowing()
    {
        var integrator = new VuIntegrator();

        integrator.Add(Constant(0.010, 1.0f, 1), offset: 0, stride: 0, SampleRate);
        integrator.Add(Constant(0.010, 1.0f, 1), offset: 0, stride: 1, sampleRate: 0);

        Assert.Equal(0.0, integrator.Level);
    }

    [Fact]
    public void Reset_ReturnsTheNeedleToZero()
    {
        var integrator = new VuIntegrator();
        integrator.Add(Constant(0.300, 1.0f, 1), offset: 0, stride: 1, SampleRate);

        integrator.Reset();

        Assert.Equal(0.0, integrator.Level);
    }

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
}
