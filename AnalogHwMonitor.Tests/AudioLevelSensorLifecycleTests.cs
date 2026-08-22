using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;

namespace AnalogHwMonitor.Tests;

public class AudioLevelSensorLifecycleTests
{
    private static (AudioLevelSensorSource Source, FakeAudioLoopbackCapture Capture, FakeTimeProvider Time) Build()
    {
        var capture = new FakeAudioLoopbackCapture();
        var time = new FakeTimeProvider();
        var source = new AudioLevelSensorSource(capture, NullLog.Instance, () => false, time);
        return (source, capture, time);
    }

    [Fact]
    public void Refresh_DoesNothingBeforeAnybodyHasReadALevel()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            source.Refresh();

            Assert.Equal(0, capture.StartCount);
            Assert.Equal(0, capture.StopCount);
        }
    }

    /// <summary>
    /// This is how leaving VU meter mode releases the audio device: nothing tells the
    /// source, the reads simply stop coming.
    /// </summary>
    [Fact]
    public void Refresh_ReleasesTheDeviceAfterFiveSecondsWithoutAReader()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            Assert.Equal(1, capture.StartCount);

            time.Advance(TimeSpan.FromSeconds(6));
            source.Refresh();

            Assert.Equal(1, capture.StopCount);
            Assert.Equal(0, capture.HandlerCount);
        }
    }

    [Fact]
    public void Refresh_KeepsCaptureWhileTheLevelIsStillBeingRead()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            // A minute of VU meter mode: reads at 25 Hz, one health check a second.
            for (var second = 0; second < 60; second++)
            {
                for (var tick = 0; tick < 25; tick++)
                {
                    source.Read(AudioSensorIds.Left);
                    source.Read(AudioSensorIds.Right);
                    time.Advance(TimeSpan.FromMilliseconds(40));
                }

                source.Refresh();
            }

            Assert.Equal(1, capture.StartCount);
            Assert.Equal(0, capture.StopCount);
        }
    }

    [Fact]
    public void Refresh_RestartsOnTheNewDeviceWhenTheDefaultOutputChanges()
    {
        var (source, capture, _) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);

            capture.CurrentDefaultDeviceId = "device-2";
            source.Refresh();

            Assert.Equal(1, capture.StopCount);

            source.Read(AudioSensorIds.Left);

            Assert.Equal(2, capture.StartCount);
            Assert.Equal("device-2", capture.DeviceId);
        }
    }

    [Fact]
    public void Refresh_LogsTheDeviceChangeOnce()
    {
        var capture = new FakeAudioLoopbackCapture();
        var log = new RecordingLog();
        var time = new FakeTimeProvider();
        using var source = new AudioLevelSensorSource(capture, log, () => false, time);
        source.Read(AudioSensorIds.Left);

        capture.CurrentDefaultDeviceId = "device-2";
        source.Refresh();

        Assert.Single(log.Lines);
        Assert.Contains("device-2", log.Lines[0]);
    }

    /// <summary>
    /// The leak that would not look like a leak: a second subscription means the same
    /// buffer is integrated twice, and after ten toggles the needles read nonsense.
    /// </summary>
    [Fact]
    public void ARoundOfStartsAndStopsLeavesNothingSubscribed()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            for (var cycle = 0; cycle < 100; cycle++)
            {
                source.Read(AudioSensorIds.Left);
                Assert.Equal(1, capture.HandlerCount);

                time.Advance(TimeSpan.FromSeconds(6));
                source.Refresh();
                Assert.Equal(0, capture.HandlerCount);
            }

            Assert.Equal(100, capture.StartCount);
            Assert.Equal(100, capture.StopCount);
        }
    }

    [Fact]
    public void ANewCaptureStartsFromZeroRatherThanTheLastTracksLevel()
    {
        var (source, capture, time) = Build();
        using (source)
        {
            source.Read(AudioSensorIds.Left);
            capture.DeliverSine(seconds: 2.0);
            Assert.Equal(0.0, source.Read(AudioSensorIds.Left)!.Value, precision: 1);

            time.Advance(TimeSpan.FromSeconds(6));
            source.Refresh();

            Assert.Equal((float)AudioSensorIds.FloorDbfs, source.Read(AudioSensorIds.Left));
        }
    }

    [Fact]
    public void Dispose_StopsAndDisposesTheCapture()
    {
        var (source, capture, _) = Build();
        source.Read(AudioSensorIds.Left);

        source.Dispose();

        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(0, capture.HandlerCount);
        Assert.Null(capture.Format);
    }

    [Fact]
    public void Dispose_IsSafeWhenCaptureNeverStarted()
    {
        var (source, capture, _) = Build();

        var exception = Record.Exception(() => source.Dispose());

        Assert.Null(exception);
        Assert.Equal(1, capture.DisposeCount);
    }
}
