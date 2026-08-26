namespace AnalogHwMonitor.Core;

/// <summary>
/// Publishes what is coming out of the speakers as two ordinary sensors in dBFS, so a
/// VU meter needs no new path through the application: the value goes through the same
/// mapping, the same calibration and the same frame as a CPU temperature.
///
/// THROWAWAY MEASUREMENT BUILD adds a third, <see cref="AudioSensorIds.Needle"/>, which
/// breaks that symmetry deliberately: it reports deflection in percent rather than a
/// level, because the compensator has to do the dB-to-percent mapping itself before it
/// can shape the command. It rides the same capture and the same lifecycle as the two
/// levels, and for the duration of the experiment all three see the same (L+R)/2 fold.
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
    /// Ceiling on volume compensation. Without a limit, a quiet machine's dither noise
    /// floor would be pulled up to full scale and peg both needles.
    ///
    /// What gets compensated is <c>IAudioLoopbackCapture.VolumeDb</c>, which the adapter
    /// reads from the endpoint's <c>MasterVolumeLevel</c> — the attenuation in decibels,
    /// so negating it recovers the level exactly. Note that this is not the same as
    /// 20·log10 of the volume slider's position: Windows maps the slider to decibels
    /// through a taper that is device-dependent, so there is no fixed slider percentage
    /// at which this ceiling starts to bite. A probe build on one machine read -6.07 dB
    /// with the compensation mirroring it exactly and the ceiling never reached.
    /// </summary>
    public const double MaxCompensationDb = 40.0;

    /// <summary>
    /// Gap between attempts to start a capture that has just refused to start. Without
    /// it a machine with no reachable endpoint would be re-enumerated fifty times a
    /// second on the UI thread — the log latch keeps log.txt clean but says nothing
    /// about the cost of the attempt itself.
    /// </summary>
    public static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(1);

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

    // THROWAWAY MEASUREMENT BUILD, CONTROL CONFIGURATION. A third detector, identical to
    // the two above by construction — see NeedleCompensator.DetectorTauMs for why it is
    // bound to their time constant rather than given one of its own. With this setting the
    // compensated chain differs from the uncompensated one by the biquad and nothing else.
    //
    // The SilenceGap limitation that applied when this ran at 15 ms is gone with it: at
    // the shared 65 ms constant this filter absorbs the 150 ms gap at 2.3 tau exactly as
    // its neighbours do, so a release into digital silence is now a fair measurement
    // rather than a measurement of the gap policy. Restoring 15 ms restores the caveat.
    private readonly VuIntegrator _needleDetector = new(NeedleCompensator.DetectorTauMs / 1000.0);
    private readonly NeedleCompensator _compensator = new();
    private long _lastNeedleTicks;

    private readonly AudioSamplesHandler _onSamples;

    private bool _started;
    private string? _reportedError;
    private DateTimeOffset _lastRead;

    // MinValue, so a first start and a restart after a clean stop both happen at once;
    // only a failure arms the gate.
    private DateTimeOffset _lastFailedStart = DateTimeOffset.MinValue;

    // Written by the capture thread, read by the tick loop. Ticks rather than
    // DateTimeOffset because only a long can be exchanged atomically.
    private long _lastBufferTicks;
    private long _lastAdvanceTicks;

    /// <summary>
    /// Serialises the capture lifecycle. Refresh() runs on the poll task and Read() on the UI
    /// thread, so without this a Stop() from the health check and a TryStart() from a read could
    /// interleave and leave _started == true over a stopped capture — both needles dead until
    /// something shakes them.
    ///
    /// OnSamples deliberately does NOT take this lock: doing so would deadlock the process, not
    /// just add latency. WasapiLoopbackAdapter.StopLocked holds the adapter's own gate across
    /// capture.Dispose(), which joins the capture thread — and Stop() is reached with
    /// _lifecycle already held (Read()/Refresh() -> Stop() -> _capture.Stop() -> StopLocked).
    /// If OnSamples, running on the capture thread, ever blocked on _lifecycle, the thread
    /// StopLocked is joining would be waiting on a lock held by the very thread doing the
    /// joining: a permanent freeze of the tray app, not a slow one. It communicates solely
    /// through Volatile/Interlocked (_lastBufferTicks, _lastAdvanceTicks, VuIntegrator._level),
    /// and that is enough.
    /// </summary>
    private readonly object _lifecycle = new();

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
    /// default output device when it changes. Both ride the Refresh() that
    /// <see cref="SensorRefreshLoop"/> drives once a second — the right rate for a health
    /// check, and the reason neither needs a COM notification client.
    /// </summary>
    public void Refresh()
    {
        lock (_lifecycle)
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
            // immediately because both needles go dead. But the same comparison also catches
            // a capture that died under us — the adapter clears its device id on an
            // unsolicited stop, so a mismatch here means either the default device moved or
            // the capture is gone, and both want the same response. Narrowing this condition
            // to require a non-null device id would silently delete the exclusive-mode
            // recovery: it would pass every existing test and stop noticing the second case.
            var current = _capture.CurrentDefaultDeviceId;
            if (current is not null && current != _capture.DeviceId)
            {
                _log.Write($"The audio capture is no longer on the default device ({current}); restarting it.");
                Stop();   // the next Read starts it again, on the new device
            }
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover()
    {
        var device = _capture.DeviceName ?? "Windows Audio";

        return new[]
        {
            new SensorDescriptor(AudioSensorIds.Left, "Level L", device, SensorKind.Audio, AudioSensorIds.Unit),
            new SensorDescriptor(AudioSensorIds.Right, "Level R", device, SensorKind.Audio, AudioSensorIds.Unit),
            new SensorDescriptor(
                AudioSensorIds.Needle, "Needle (compensated)", device, SensorKind.Audio, AudioSensorIds.NeedleUnit),
        };
    }

    public float? Read(string sensorId)
    {
        if (sensorId == AudioSensorIds.Needle)
        {
            return ReadNeedle();
        }

        var channel = sensorId switch
        {
            AudioSensorIds.Left => 0,
            AudioSensorIds.Right => 1,
            _ => -1,
        };

        // The composite asks every source for every identifier, so most calls here are
        // about somebody else's sensor. This early return sits before the lock on purpose:
        // of the five channels only two are audio, so three calls per tick never lock at all.
        if (channel < 0)
        {
            return null;
        }

        lock (_lifecycle)
        {
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

            return (float)LevelToDbfs(_integrators[channel].Level * AverageToPeak);
        }
    }

    /// <summary>
    /// THROWAWAY MEASUREMENT BUILD. The compensated chain: a detector matching the
    /// uncompensated meter's (see <see cref="NeedleCompensator.DetectorTauMs"/>), the same
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
            // meets a needle that has a peg at each end, and what gets clamped away is
            // compensation lost. Released from full deflection at the 40 ms VU tick the
            // command dips to about -6.3 % on the first tick and is back above zero on
            // the next, so the loss is one tick of a slightly delayed fall rather than a
            // sustained one. Measured by
            // NeedleCompensatorTests.Release_AsksForANegativeCommandTheClampMustDiscard;
            // the dip scales with the deflection released from and grows as the tick
            // shortens, so re-measure it if the VU timer ever changes.
            return (float)Math.Clamp(shaped, 0.0, 100.0);
        }
    }

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

    public void Dispose()
    {
        lock (_lifecycle)
        {
            Stop();
        }

        _capture.Dispose();
    }

    // Holds no lock of its own: called only from Refresh() and Read(), which already hold
    // _lifecycle before reaching here.
    private bool EnsureStarted()
    {
        if (_started)
        {
            return true;
        }

        var now = _time.GetUtcNow();
        if (now - _lastFailedStart < StartRetryInterval)
        {
            return false;
        }

        var nowTicks = now.UtcTicks;
        Interlocked.Exchange(ref _lastBufferTicks, nowTicks);
        Interlocked.Exchange(ref _lastAdvanceTicks, nowTicks);

        foreach (var integrator in _integrators)
        {
            integrator.Reset();
        }

        _needleDetector.Reset();
        _compensator.Reset();
        Interlocked.Exchange(ref _lastNeedleTicks, nowTicks);

        if (!_capture.TryStart(_onSamples, out var error))
        {
            _lastFailedStart = now;
            Report(error ?? "The audio capture could not be started.");
            return false;
        }

        _lastFailedStart = DateTimeOffset.MinValue;
        _reportedError = null;
        _started = true;
        return true;
    }

    // Holds no lock of its own: called only from Refresh(), Read() and Dispose(), which
    // already hold _lifecycle before reaching here.
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

        _needleDetector.Decay(now - lastAdvance);
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

        // THROWAWAY MEASUREMENT BUILD. Both meters get (L+R)/2 so the difference on the
        // dials is the difference in ballistics and nothing else. Stereo is off for the
        // duration of the experiment.
        for (var channel = 0; channel < _integrators.Length; channel++)
        {
            _integrators[channel].AddMono(samples, format.ChannelCount, format.SampleRate);
        }

        _needleDetector.AddMono(samples, format.ChannelCount, format.SampleRate);

        var now = _time.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastBufferTicks, now);
        Interlocked.Exchange(ref _lastAdvanceTicks, now);
    }
}
