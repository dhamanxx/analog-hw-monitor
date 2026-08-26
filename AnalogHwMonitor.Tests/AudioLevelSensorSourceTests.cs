using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;

namespace AnalogHwMonitor.Tests;

public class AudioLevelSensorSourceTests
{
    private static (AudioLevelSensorSource Source, FakeAudioLoopbackCapture Capture, FakeTimeProvider Time)
        Build(bool compensateVolume = false)
    {
        var capture = new FakeAudioLoopbackCapture();
        var time = new FakeTimeProvider();
        var source = new AudioLevelSensorSource(capture, NullLog.Instance, () => compensateVolume, time);
        return (source, capture, time);
    }

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
    /// The two tests above only ever assert the clamp rails — 99-100 % and 0.0 % are what
    /// <c>Math.Clamp(shaped, 0.0, 100.0)</c> returns for any dB window ReadNeedle happens
    /// to use, so neither would notice if the window were wrong. This is the one that
    /// would: a sine at -12 dBFS lands at 70 % of the dial on the -40..0 window
    /// (<see cref="VuModeSwitch.DefaultMinDbfs"/>/<see cref="VuModeSwitch.DefaultMaxDbfs"/>)
    /// and nowhere near that on any other plausible window — -60..0, for instance, would
    /// settle at 80 %.
    ///
    /// The needle is a filter with memory, not a snapshot: the detector's own time
    /// constant is <see cref="NeedleCompensator.DetectorTauMs"/> (15 ms, well inside one
    /// 40 ms tick given a steady signal), but the compensator downstream of it is a
    /// second-order filter whose own settling time is about 320 ms /
    /// 8 ticks (<c>NeedleCompensatorTests.CompensatedStep_LeavesTheNeedleWithinTheStandardsOvershoot</c>).
    /// A single read after the step lands mid-overshoot, not at the settled value, so this
    /// re-delivers the same steady sine every tick (keeping the buffer fresh enough that
    /// <c>ApplySilenceDecay</c> never fires past <see cref="AudioLevelSensorSource.SilenceGap"/>)
    /// and reads across 60 ticks — 2.4 s, well past the compensator's t99 — before
    /// asserting on the last value.
    /// </summary>
    [Fact]
    public void Read_NeedleSettlesAtTheMidScaleWindowForAMidScaleSignal()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            // Primes the compensator at 0, same as Read_NeedleIsSilentBeforeAnySignal.
            source.Read(AudioSensorIds.Needle);

            // -12 dBFS: peak = 10^(-12/20), the inverse of the pi/2 peak calibration
            // Read_ReportsHalfScaleAsAboutMinusSixDecibels pins for -6 dBFS.
            var peak = (float)Math.Pow(10.0, -12.0 / 20.0);

            var value = 0.0f;
            for (var tick = 0; tick < 60; tick++)
            {
                capture.DeliverSine(seconds: 0.1, peak: peak);
                time.Advance(TimeSpan.FromMilliseconds(40));
                value = source.Read(AudioSensorIds.Needle)!.Value;
            }

            // 70 % is what -12 dBFS maps to on the -40..0 window. The tolerance covers
            // the dBFS-to-percent sensitivity (2.5 points per dB) against the sub-0.1 dB
            // rounding a heavily-filtered 1 kHz sine leaves in the detector, plus residual
            // compensator settling error — nowhere near the 10-point gap a wrong window
            // (e.g. -60..0, which would settle at 80 %) would produce.
            Assert.InRange(value, 68.5f, 71.5f);
        }
    }

    /// <summary>
    /// Both meters see the same mono fold during the experiment, so reading the needle
    /// must not change what the left channel reports.
    ///
    /// The decay path has to actually run for this to mean anything. ApplySilenceDecay
    /// is shared by all three detectors and takes _lastAdvanceTicks with it, so a needle
    /// read interleaved into a decay window consumes decay the next left read would
    /// otherwise have performed. That is only safe because the decay is a function of
    /// elapsed wall time, so the total is conserved whichever reader applies it — this
    /// test is what pins that. Both runs therefore advance the clock past SilenceGap
    /// first; inside the gap ApplySilenceDecay early-returns and the case is never
    /// exercised at all.
    /// </summary>
    [Fact]
    public void Read_NeedleDoesNotDisturbTheLeftLevel()
    {
        static float ReadLeftAcrossADecayWindow(bool interleaveNeedle)
        {
            var capture = new FakeAudioLoopbackCapture();
            var time = new FakeTimeProvider();
            using var source = new AudioLevelSensorSource(
                capture, NullLog.Instance, () => false, time);

            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(0.500);

            // Past SilenceGap, so the buffers count as stopped and the decay path runs.
            time.Advance(TimeSpan.FromMilliseconds(200));
            source.Read(AudioSensorIds.Left);

            // A further window with decay owed in it.
            time.Advance(TimeSpan.FromMilliseconds(40));

            if (interleaveNeedle)
            {
                source.Read(AudioSensorIds.Needle);
            }

            return source.Read(AudioSensorIds.Left)!.Value;
        }

        var undisturbed = ReadLeftAcrossADecayWindow(interleaveNeedle: false);
        var interleaved = ReadLeftAcrossADecayWindow(interleaveNeedle: true);

        // Guard against this test quietly going vacuous the way its first version did:
        // 240 ms is 3.7 time constants, so the level must have fallen a long way from
        // the 0 dBFS the sine was playing at. If this ever reads near zero, the decay
        // path stopped running and the comparison below stopped proving anything.
        Assert.True(
            undisturbed < -25f,
            $"expected the decay path to have run, the level read {undisturbed} dBFS");

        // The needle read consumed the 40 ms of decay, so the left read that follows it
        // gets none — and still lands on the same level, because the same wall time has
        // elapsed either way.
        Assert.Equal(undisturbed, interleaved);
    }

    /// <summary>
    /// Filling the settings window's dropdown must not seize the audio device.
    /// </summary>
    [Fact]
    public void Discover_DoesNotStartCapture()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Discover();

            Assert.Equal(0, capture.StartCount);
        }
    }

    [Fact]
    public void Read_IgnoresIdentifiersThatBelongToAnotherSource()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            Assert.Null(source.Read("/amdcpu/0/load/0"));
            Assert.Equal(0, capture.StartCount);
        }
    }

    [Fact]
    public void Read_StartsCaptureOnTheFirstCall()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            source.Read(AudioSensorIds.Right);

            Assert.Equal(1, capture.StartCount);
            Assert.Equal(1, capture.HandlerCount);
        }
    }

    /// <summary>
    /// The VU calibration: a full-scale sine reads 0 dBFS. The filter averages the
    /// rectified signal, whose mean is 2/pi of the amplitude, so the reading is scaled
    /// by pi/2 to put a sine's peak at the top of the scale. Average-responding,
    /// peak-calibrated — the classic VU convention.
    /// </summary>
    [Fact]
    public void Read_ReportsZeroDbfsForAFullScaleSine()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);

            var reading = source.Read(AudioSensorIds.Left);

            Assert.NotNull(reading);
            Assert.Equal(0.0, reading!.Value, precision: 1);
        }
    }

    [Fact]
    public void Read_ReportsTheFloorWhenNothingHasBeenPlayed()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            var reading = source.Read(AudioSensorIds.Left);

            Assert.Equal((float)AudioSensorIds.FloorDbfs, reading);
            Assert.Equal(1, capture.StartCount);
        }
    }

    [Fact]
    public void Read_ReportsHalfScaleAsAboutMinusSixDecibels()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0, peak: 0.5f);

            var reading = source.Read(AudioSensorIds.Left);

            Assert.Equal(-6.0, reading!.Value, precision: 1);
        }
    }

    /// <summary>
    /// THROWAWAY MEASUREMENT BUILD. This used to be Read_KeepsTheTwoChannelsApart and
    /// asserted that a signal on the left alone left the right needle at the floor. The
    /// experiment deliberately removes that: both meters are fed (L+R)/2 so the only
    /// difference between the two dials is their ballistics. Restore the per-channel
    /// assertion together with the per-channel fold in OnSamples when the build is
    /// thrown away.
    ///
    /// Full scale on the left and silence on the right folds to a steady 0.5, which is
    /// -2.1 dBFS once the pi/2 peak calibration is applied — and the same on both.
    /// </summary>
    [Fact]
    public void Read_FoldsTheTwoChannelsToMonoForTheExperiment()
    {
        var capture = new FakeAudioLoopbackCapture();
        using var source = new AudioLevelSensorSource(
            capture, NullLog.Instance, () => false, new FakeTimeProvider());
        source.Read(AudioSensorIds.Left);

        // Left at full scale, right silent, for two seconds.
        var frames = 2 * capture.SampleRate;
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = 1.0f;
        }

        capture.Deliver(samples);

        var left = source.Read(AudioSensorIds.Left);
        var right = source.Read(AudioSensorIds.Right);

        Assert.Equal(-2.1, left!.Value, precision: 1);
        Assert.Equal(left, right);
    }

    /// <summary>
    /// WASAPI stops delivering buffers entirely when playback stops, so without a
    /// time-based decay the needle would stay parked wherever the last track left it.
    /// </summary>
    [Fact]
    public void Read_LetsTheLevelFallOnceTheBuffersStopArriving()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);
            var playing = source.Read(AudioSensorIds.Left)!.Value;

            time.Advance(TimeSpan.FromSeconds(1));
            var quiet = source.Read(AudioSensorIds.Left)!.Value;

            Assert.Equal(0.0, playing, precision: 1);
            Assert.True(quiet < -60f, $"expected the needle to fall, it read {quiet} dBFS");
        }
    }

    /// <summary>
    /// The gap exists so that the decay does not double-count the time the integrator
    /// already advanced through in sample time. Inside it, a reading must not sag.
    /// </summary>
    [Fact]
    public void Read_DoesNotDecayWithinTheSilenceGap()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);
            var first = source.Read(AudioSensorIds.Left)!.Value;

            time.Advance(TimeSpan.FromMilliseconds(100));
            var second = source.Read(AudioSensorIds.Left)!.Value;

            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void Read_AddsBackTheVolumeAttenuationWhenCompensationIsOn()
    {
        var capture = new FakeAudioLoopbackCapture { VolumeDb = -20.0 };
        using var source = new AudioLevelSensorSource(
            capture, NullLog.Instance, () => true, new FakeTimeProvider());
        source.Read(AudioSensorIds.Left);

        capture.DeliverSine(seconds: 2.0, peak: 0.1f);   // -20 dBFS as captured

        // Measured at -20 dBFS through a -20 dB volume setting means the material
        // itself is at full scale.
        Assert.Equal(0.0, source.Read(AudioSensorIds.Left)!.Value, precision: 1);
    }

    [Fact]
    public void Read_LeavesTheLevelAloneWhenCompensationIsOff()
    {
        var capture = new FakeAudioLoopbackCapture { VolumeDb = -20.0 };
        using var source = new AudioLevelSensorSource(
            capture, NullLog.Instance, () => false, new FakeTimeProvider());
        source.Read(AudioSensorIds.Left);

        capture.DeliverSine(seconds: 2.0, peak: 0.1f);

        Assert.Equal(-20.0, source.Read(AudioSensorIds.Left)!.Value, precision: 1);
    }

    /// <summary>
    /// At 5 % volume the correction is about +26 dB, and at 1 % about +40. Without a
    /// ceiling, compensation would eventually pull a dither noise floor to full scale
    /// and peg both needles on a silent machine.
    /// </summary>
    [Fact]
    public void Read_CapsVolumeCompensation()
    {
        var capture = new FakeAudioLoopbackCapture { VolumeDb = -90.0 };
        using var source = new AudioLevelSensorSource(
            capture, NullLog.Instance, () => true, new FakeTimeProvider());
        source.Read(AudioSensorIds.Left);

        capture.DeliverSine(seconds: 2.0, peak: 0.1f);   // -20 dBFS as captured

        // -20 dBFS plus the +40 dB ceiling, not plus 90.
        Assert.Equal(20.0, source.Read(AudioSensorIds.Left)!.Value, precision: 1);
    }

    [Fact]
    public void Read_ReportsTheFloorWhileTheEndpointIsMuted()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);
            capture.IsMuted = true;

            Assert.Equal((float)AudioSensorIds.FloorDbfs, source.Read(AudioSensorIds.Left));
        }
    }

    /// <summary>
    /// Null means broken, and drives the existing dead-sensor path: needle to zero, red
    /// row, one line in the log. Silence is not broken and must never look like this.
    /// </summary>
    [Fact]
    public void Read_ReturnsNullWhenCaptureCannotStart()
    {
        var capture = new FakeAudioLoopbackCapture { StartError = "No audio endpoint." };
        var log = new RecordingLog();
        using var source = new AudioLevelSensorSource(
            capture, log, () => false, new FakeTimeProvider());

        Assert.Null(source.Read(AudioSensorIds.Left));
        Assert.Contains("No audio endpoint.", log.Lines[0]);
    }

    [Fact]
    public void Read_LogsARepeatedStartFailureOnlyOnce()
    {
        var capture = new FakeAudioLoopbackCapture { StartError = "No audio endpoint." };
        var log = new RecordingLog();
        using var source = new AudioLevelSensorSource(
            capture, log, () => false, new FakeTimeProvider());

        for (var i = 0; i < 25; i++)
        {
            source.Read(AudioSensorIds.Left);
        }

        Assert.Single(log.Lines);
    }

    /// <summary>
    /// Digital silence must read the floor whether or not the volume is turned down.
    /// The compensation used to be added to the floor sentinel, which lifted silence to
    /// -60 dBFS while the mute path returned -100 for the same absence of signal.
    /// </summary>
    [Fact]
    public void Read_ReportsTheFloorForSilenceEvenWithCompensationOn()
    {
        var capture = new FakeAudioLoopbackCapture { VolumeDb = -40.0 };
        using var source = new AudioLevelSensorSource(
            capture, NullLog.Instance, () => true, new FakeTimeProvider());

        Assert.Equal((float)AudioSensorIds.FloorDbfs, source.Read(AudioSensorIds.Left));
    }

    /// <summary>
    /// A capture that will not start must not be retried at the tick rate: in the real
    /// adapter every attempt is a COM enumeration of the audio endpoints, on the UI
    /// thread, and this state persists for as long as another application holds the
    /// endpoint in exclusive mode.
    /// </summary>
    [Fact]
    public void Read_DoesNotRetryAFailedStartAtTheTickRate()
    {
        var capture = new FakeAudioLoopbackCapture { StartError = "No audio endpoint." };
        var time = new FakeTimeProvider();
        using var source = new AudioLevelSensorSource(capture, NullLog.Instance, () => false, time);

        // One second of VU meter mode: 25 ticks, two channels read on each.
        for (var tick = 0; tick < 25; tick++)
        {
            source.Read(AudioSensorIds.Left);
            source.Read(AudioSensorIds.Right);
        }

        Assert.Equal(1, capture.StartCount);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Read(AudioSensorIds.Left);

        Assert.Equal(2, capture.StartCount);
    }

    /// <summary>
    /// The gate is measured from the last buffer and the decay from the last time the
    /// level moved, so once the buffers stop the needle keeps falling at the read rate
    /// instead of in gap-sized steps. Collapsing the two timestamps into one passes
    /// every other test in this file; this is the one that notices.
    /// </summary>
    [Fact]
    public void Read_KeepsFallingOnEveryReadOnceTheGapHasPassed()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);

            time.Advance(TimeSpan.FromMilliseconds(200));
            var firstFall = source.Read(AudioSensorIds.Left)!.Value;

            time.Advance(TimeSpan.FromMilliseconds(40));
            var secondFall = source.Read(AudioSensorIds.Left)!.Value;

            Assert.True(
                secondFall < firstFall,
                $"expected the needle to keep falling, it went {firstFall} -> {secondFall} dBFS");
        }
    }
}
