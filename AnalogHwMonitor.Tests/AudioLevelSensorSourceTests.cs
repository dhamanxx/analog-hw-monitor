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

            Assert.Equal(2, sensors.Count);
            Assert.Equal(AudioSensorIds.Left, sensors[0].Id);
            Assert.Equal(AudioSensorIds.Right, sensors[1].Id);
            Assert.All(sensors, s => Assert.Equal(SensorKind.Audio, s.Kind));
            Assert.All(sensors, s => Assert.Equal("dBFS", s.Unit));
            Assert.Equal("Realtek Audio · Level L", sensors[0].Display);
        }
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

    [Fact]
    public void Read_KeepsTheTwoChannelsApart()
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

        Assert.True(source.Read(AudioSensorIds.Left) > -5f);
        Assert.Equal((float)AudioSensorIds.FloorDbfs, source.Read(AudioSensorIds.Right));
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
