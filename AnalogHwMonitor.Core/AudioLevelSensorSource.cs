namespace AnalogHwMonitor.Core;

/// <summary>
/// Publishes what is coming out of the speakers as two ordinary sensors in dBFS, so a
/// VU meter needs no new path through the application: the value goes through the same
/// mapping, the same calibration and the same frame as a CPU temperature.
///
/// Nobody starts or stops this from outside. The first <see cref="Read"/> of an audio
/// identifier starts capture and <see cref="IdleTimeout"/> without one releases it, so
/// leaving VU meter mode hands the audio device back simply by nobody asking for the
/// level any more. <see cref="Discover"/> never starts capture — it is what fills the
/// settings window's dropdown, and opening that window is no reason to seize a device.
/// </summary>
public sealed class AudioLevelSensorSource : ISensorSource
{
    /// <summary>Capture is released after this long with nobody reading a level.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a gap in the buffers means silence rather than jitter. WASAPI delivers
    /// nothing at all when playback stops, so past this the level is decayed by elapsed
    /// time. The gap matters in both directions: decaying while buffers are still
    /// arriving would double-count time the integrator already advanced through in
    /// sample time, and reading a low value.
    /// </summary>
    public static readonly TimeSpan SilenceGap = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Ceiling on volume compensation. At 5 % volume the correction is about +26 dB;
    /// without a limit, a quiet machine's dither noise floor would be pulled up to full
    /// scale and peg both needles.
    /// </summary>
    public const double MaxCompensationDb = 40.0;

    /// <summary>
    /// A one-pole filter over the rectified signal reports the average, and the average
    /// of a rectified sine is 2/pi of its amplitude. Scaling by the reciprocal puts a
    /// full-scale sine at 0 dBFS: average-responding, peak-calibrated, which is what a
    /// VU meter has always been.
    /// </summary>
    private const double AverageToPeak = Math.PI / 2.0;

    private readonly IAudioLoopbackCapture _capture;
    private readonly IAppLog _log;
    private readonly Func<bool> _compensateVolume;
    private readonly TimeProvider _time;
    private readonly VuIntegrator[] _integrators = { new(), new() };
    private readonly AudioSamplesHandler _onSamples;

    private bool _started;
    private string? _reportedError;
    private DateTimeOffset _lastRead;

    // Written by the capture thread, read by the tick loop. Ticks rather than
    // DateTimeOffset because only a long can be exchanged atomically.
    private long _lastBufferTicks;
    private long _lastAdvanceTicks;

    public AudioLevelSensorSource(
        IAudioLoopbackCapture capture,
        IAppLog log,
        Func<bool>? compensateVolume = null,
        TimeProvider? time = null)
    {
        _capture = capture;
        _log = log;
        _compensateVolume = compensateVolume ?? (() => true);
        _time = time ?? TimeProvider.System;
        _onSamples = OnSamples;
    }

    /// <summary>
    /// Releases the device when nobody has asked for a level lately, and follows the
    /// default output device when it changes. Both ride the tick loop's Refresh(), which
    /// <see cref="ThrottledSensorSource"/> holds to once a second — the right rate for a
    /// health check, and the reason neither needs a COM notification client.
    /// </summary>
    public void Refresh()
    {
        if (!_started)
        {
            return;
        }

        if (_time.GetUtcNow() - _lastRead > IdleTimeout)
        {
            Stop();
            return;
        }

        // Headphones in, speakers out: the daily case, and the one a VU meter notices
        // immediately because both needles go dead.
        var current = _capture.CurrentDefaultDeviceId;
        if (current is not null && current != _capture.DeviceId)
        {
            _log.Write($"Default audio device changed to {current}; restarting the audio capture.");
            Stop();   // the next Read starts it again, on the new device
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover()
    {
        var device = _capture.DeviceName ?? "Windows Audio";

        return new[]
        {
            new SensorDescriptor(AudioSensorIds.Left, "Level L", device, SensorKind.Audio, AudioSensorIds.Unit),
            new SensorDescriptor(AudioSensorIds.Right, "Level R", device, SensorKind.Audio, AudioSensorIds.Unit),
        };
    }

    public float? Read(string sensorId)
    {
        var channel = sensorId switch
        {
            AudioSensorIds.Left => 0,
            AudioSensorIds.Right => 1,
            _ => -1,
        };

        // The composite asks every source for every identifier, so most calls here are
        // about somebody else's sensor.
        if (channel < 0)
        {
            return null;
        }

        _lastRead = _time.GetUtcNow();

        if (!EnsureStarted())
        {
            return null;
        }

        if (_capture.IsMuted)
        {
            return (float)AudioSensorIds.FloorDbfs;
        }

        ApplySilenceDecay();

        var level = _integrators[channel].Level * AverageToPeak;
        var dbfs = level <= 0.0 ? AudioSensorIds.FloorDbfs : 20.0 * Math.Log10(level);

        if (_compensateVolume())
        {
            dbfs += Math.Min(-_capture.VolumeDb, MaxCompensationDb);
        }

        return (float)Math.Max(dbfs, AudioSensorIds.FloorDbfs);
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }

    private bool EnsureStarted()
    {
        if (_started)
        {
            return true;
        }

        var now = _time.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastBufferTicks, now);
        Interlocked.Exchange(ref _lastAdvanceTicks, now);

        foreach (var integrator in _integrators)
        {
            integrator.Reset();
        }

        if (!_capture.TryStart(_onSamples, out var error))
        {
            Report(error ?? "The audio capture could not be started.");
            return false;
        }

        _reportedError = null;
        _started = true;
        return true;
    }

    private void Stop()
    {
        if (!_started)
        {
            return;
        }

        _capture.Stop();
        _started = false;
    }

    /// <summary>
    /// Logs a failure only when it differs from the one already reported. A machine with
    /// no output device fails identically twenty-five times a second, and the same
    /// latch guards <c>SerialMeterLink</c> and <c>CompositeSensorSource</c> for the
    /// same reason: log.txt rotates at a megabyte and would carry nothing else.
    /// </summary>
    private void Report(string message)
    {
        if (_reportedError == message)
        {
            return;
        }

        _log.Write(message);
        _reportedError = message;
    }

    /// <summary>
    /// Advances the fall of the needle when no buffer has arrived for longer than
    /// <see cref="SilenceGap"/>. Two timestamps rather than one: the gap is measured
    /// from the last buffer, so it does not re-arm on every read, while the decay is
    /// measured from the last time the level moved at all, so the needle falls smoothly
    /// at the read rate instead of in gap-sized steps.
    /// </summary>
    private void ApplySilenceDecay()
    {
        var now = _time.GetUtcNow();
        var lastBuffer = new DateTimeOffset(Interlocked.Read(ref _lastBufferTicks), TimeSpan.Zero);

        if (now - lastBuffer <= SilenceGap)
        {
            return;
        }

        var lastAdvance = new DateTimeOffset(
            Interlocked.Exchange(ref _lastAdvanceTicks, now.UtcTicks), TimeSpan.Zero);

        foreach (var integrator in _integrators)
        {
            integrator.Decay(now - lastAdvance);
        }
    }

    /// <summary>
    /// Runs on the capture thread. Allocates nothing: the span is read in place, and
    /// the integrator's state is two doubles.
    /// </summary>
    private void OnSamples(ReadOnlySpan<float> samples)
    {
        if (_capture.Format is not { } format || format.ChannelCount < 1)
        {
            return;
        }

        for (var channel = 0; channel < _integrators.Length; channel++)
        {
            // A mono endpoint feeds both meters from its single channel, so the pair
            // still reads as a pair rather than leaving the right needle dead.
            var offset = Math.Min(channel, format.ChannelCount - 1);
            _integrators[channel].Add(samples, offset, format.ChannelCount, format.SampleRate);
        }

        var now = _time.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastBufferTicks, now);
        Interlocked.Exchange(ref _lastAdvanceTicks, now);
    }
}
