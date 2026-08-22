using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AnalogHwMonitor.Core;

/// <summary>
/// The only class in this application that sees NAudio, in the same relation to
/// <see cref="IAudioLoopbackCapture"/> as <see cref="SerialPortAdapter"/> is to
/// <see cref="ISerialPort"/>: one untested edge with everything worth testing above it.
///
/// Named for what it adapts rather than what it wraps — NAudio's own type is called
/// WasapiLoopbackCapture, and two classes by that name in one project would be a
/// permanent source of wrong using directives.
/// </summary>
public sealed class WasapiLoopbackAdapter : IAudioLoopbackCapture
{
    /// <summary>
    /// How long <see cref="Stop"/> waits for NAudio's RecordingStopped before disposing.
    /// StopRecording() is asynchronous: the callback lands on the capture thread
    /// afterwards, and disposing the COM objects before it arrives races with a thread
    /// that is still using them.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    // Serializes TryStart/Stop/Dispose against each other. Held across capture.Dispose()
    // in StopLocked, which joins NAudio's capture thread while still holding the lock —
    // safe only because the sample handler this class calls neither marshals to another
    // thread and waits nor re-enters this adapter. Either would deadlock against the
    // very thread the join is waiting on.
    private readonly object _gate = new();

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private ManualResetEventSlim? _stopped;

    private AudioSamplesHandler? _onSamples;
    private EventHandler<WaveInEventArgs>? _dataHandler;
    private EventHandler<StoppedEventArgs>? _stoppedHandler;
    private AudioEndpointVolumeNotificationDelegate? _volumeHandler;

    // Reused across callbacks. A PCM endpoint needs its samples converted to float,
    // and allocating a buffer per callback would be tens of kilobytes twenty-five times
    // a second — GC churn that looks exactly like a leak in Task Manager.
    private float[] _scratch = Array.Empty<float>();

    // Cached from the format that TryStart validated. Read per callback, so they must not
    // come from capture.WaveFormat: that getter rebuilds a WaveFormat from the extensible
    // mix format on every get. Caching them here also keeps the validation in IsSupported
    // and the conversion in OnData reading the same two values, so the two cannot drift.
    private WaveFormatEncoding _encoding;
    private int _bitsPerSample;

    // Set while StopLocked is tearing down, so the RecordingStopped handler can tell a stop
    // we asked for from one NAudio raised on its own. Written on the UI thread under
    // _gate, read on the capture thread without one; volatile makes that safe by
    // construction rather than by argument.
    private volatile bool _stopRequested;

    // Set once StartRecording has actually succeeded. StopLocked's wait exists to let an
    // in-flight RecordingStopped arrive; on a capture that never started, nothing will ever
    // raise it, and waiting the full timeout would stall the UI thread for two seconds on
    // exactly the failure paths that retry once a second.
    private bool _recording;

    private double _volumeDb;
    private bool _muted;

    public AudioFormat? Format { get; private set; }

    public string? DeviceId { get; private set; }

    private string? _deviceName;

    /// <summary>
    /// Falls back to a one-off enumeration when nothing has started or polled the
    /// default endpoint yet — e.g. the settings window's first <c>Discover()</c> with
    /// VU mode off and every channel already assigned, when neither TryStart nor
    /// CurrentDefaultDeviceId has run. A COM call here is fine: Discover is called on
    /// window open and at startup, not on the audio callback path or the once-a-second
    /// tick that VolumeDb must stay allocation- and call-free for. Returns null on any
    /// failure rather than throwing — the consumer already falls back to a generic label.
    /// </summary>
    public string? DeviceName
    {
        get
        {
            if (_deviceName is not null)
            {
                return _deviceName;
            }

            try
            {
                var enumerator = _enumerator ??= new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.FriendlyName;
            }
            catch (Exception)
            {
                return null;
            }
        }
        private set => _deviceName = value;
    }

    public double VolumeDb => Volatile.Read(ref _volumeDb);

    public bool IsMuted => Volatile.Read(ref _muted);

    /// <summary>
    /// Resolves the default render endpoint without starting anything. Returns null
    /// rather than throwing when there is no output device at all, which is a state the
    /// caller already handles.
    /// </summary>
    public string? CurrentDefaultDeviceId
    {
        get
        {
            try
            {
                var enumerator = _enumerator ??= new MMDeviceEnumerator();

                // GetDefaultAudioEndpoint hands back a new COM object every call, and
                // this one is called once a second for the life of the process.
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                DeviceName = device.FriendlyName;
                return device.ID;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool TryStart(AudioSamplesHandler onSamples, out string? error)
    {
        lock (_gate)
        {
            try
            {
                StopLocked();

                var enumerator = _enumerator ??= new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                // Hand ownership to the field the moment the device exists, so StopLocked
                // owns it from here on — including if anything below throws. Without this,
                // a constructor or endpoint-volume failure leaks the device, and since
                // AudioLevelSensorSource retries once a second, a persistently failing
                // endpoint would leak one COM device per second.
                _device = device;

                // NAudio's WasapiCapture constructor captures SynchronizationContext.Current
                // and posts RecordingStopped to it rather than raising it inline. TryStart
                // runs on the WinForms timer tick, so capturing that context would make
                // StopLocked's wait block the very thread that has to pump the post — a
                // guaranteed two-second stall of the whole application on every stop.
                // Constructed without a context, the callback arrives on the capture thread
                // and the wait completes in tens of milliseconds.
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                NAudio.Wave.WasapiLoopbackCapture capture;
                try
                {
                    capture = new NAudio.Wave.WasapiLoopbackCapture(device);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }

                // Same reasoning as _device above: own it from construction so a failure a
                // few lines below (e.g. the endpoint disappearing right as its volume
                // interface is queried) does not leak an already-constructed IAudioClient.
                _capture = capture;

                var format = capture.WaveFormat;
                if (!IsSupported(format))
                {
                    error = $"Unsupported audio format: {format.Encoding} {format.BitsPerSample}-bit.";
                    StopLocked();
                    return false;
                }

                _onSamples = onSamples;
                _stopped = new ManualResetEventSlim(false);

                _dataHandler = (_, e) => OnData(e);
                _stoppedHandler = (_, args) => OnRecordingStopped(args);
                capture.DataAvailable += _dataHandler;
                capture.RecordingStopped += _stoppedHandler;

                // Read once for the starting value, then follow notifications. Polling
                // this at the tick rate would be fifty COM calls a second on the UI
                // thread; a volume change is worth exactly one.
                Volatile.Write(ref _volumeDb, device.AudioEndpointVolume.MasterVolumeLevel);
                Volatile.Write(ref _muted, device.AudioEndpointVolume.Mute);
                _volumeHandler = OnVolumeChanged;
                device.AudioEndpointVolume.OnVolumeNotification += _volumeHandler;

                DeviceId = device.ID;
                DeviceName = device.FriendlyName;
                Format = new AudioFormat(format.SampleRate, format.Channels);
                _encoding = format.Encoding;
                _bitsPerSample = format.BitsPerSample;

                capture.StartRecording();
                _recording = true;

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                StopLocked();
                error = ex.Message;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();

            try
            {
                _enumerator?.Dispose();
            }
            catch (Exception)
            {
            }
            finally
            {
                _enumerator = null;
            }
        }
    }

    /// <summary>
    /// IEEE float 32 is what a loopback mix format almost always is; PCM 16, 24 and 32
    /// cover the configurations where it is not. Anything else is reported as a failure
    /// rather than reinterpreted, because a wrong guess here does not look like a bug —
    /// it looks like music.
    /// </summary>
    private static bool IsSupported(WaveFormat format) =>
        format.Channels >= 1
        && (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32
            || format.Encoding == WaveFormatEncoding.Pcm
            && format.BitsPerSample is 16 or 24 or 32);

    private void OnVolumeChanged(AudioVolumeNotificationData data)
    {
        Volatile.Write(ref _muted, data.Muted);

        // The notification carries the scalar on the volume taper, not the attenuation
        // in dB. Reading the endpoint back is one COM call per volume change and gives
        // the number that can simply be added to a dBFS reading.
        try
        {
            if (_device is { } device)
            {
                Volatile.Write(ref _volumeDb, device.AudioEndpointVolume.MasterVolumeLevel);
            }
        }
        catch (Exception)
        {
            // A device disappearing mid-notification is the health check's problem, not
            // this callback's.
        }
    }

    /// <summary>
    /// Fires on the capture thread whenever NAudio stops recording, whether we asked for
    /// it (via StopLocked) or not — another application taking the endpoint into
    /// exclusive mode, a format change, or the sample handler throwing all end capture
    /// silently. When it is unsolicited, clearing DeviceId is the only signal this class
    /// can raise without widening IAudioLoopbackCapture: the consumer's health check
    /// compares CurrentDefaultDeviceId against DeviceId once a second, and a null
    /// DeviceId next to a non-null default device id reads as "device changed," which
    /// makes the consumer stop and the next Read start a fresh capture. That is the
    /// specification's promised self-recovery from an endpoint held in exclusive mode.
    /// </summary>
    private void OnRecordingStopped(StoppedEventArgs args)
    {
        if (!_stopRequested)
        {
            DeviceId = null;
        }

        // Narrowed by nulling the field before disposing it, but not closed: the load,
        // the null test and the call are three instructions, and a teardown whose wait
        // times out in between disposes the handle under us. NAudio raises this event
        // outside its capture thread's try/catch, so the exception would be unhandled
        // on a background thread and would end the process. Taking _gate here instead
        // would deadlock against the Join inside StopLocked.
        try
        {
            _stopped?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Runs on NAudio's capture thread. The buffer belongs to NAudio and is recycled, so
    /// the float case reinterprets it in place and the PCM cases convert into a scratch
    /// array that is allocated once and grown, never per callback.
    ///
    /// Reads <see cref="_encoding"/> and <see cref="_bitsPerSample"/> rather than
    /// <c>capture.WaveFormat</c>: that getter reconstructs a WaveFormat from the WASAPI
    /// mix format's WaveFormatExtensible on every call — measured at 40 bytes per
    /// callback — which is exactly the per-buffer allocation this class exists to avoid.
    /// </summary>
    private void OnData(WaveInEventArgs e)
    {
        var handler = _onSamples;

        if (handler is null || e.BytesRecorded <= 0)
        {
            return;
        }

        var encoding = _encoding;
        var bitsPerSample = _bitsPerSample;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);

        if (encoding == WaveFormatEncoding.IeeeFloat)
        {
            handler(MemoryMarshal.Cast<byte, float>(bytes));
            return;
        }

        var bytesPerSample = bitsPerSample / 8;
        var count = e.BytesRecorded / bytesPerSample;

        if (_scratch.Length < count)
        {
            _scratch = new float[count];
        }

        for (var i = 0; i < count; i++)
        {
            var sample = bytes.Slice(i * bytesPerSample, bytesPerSample);
            _scratch[i] = bitsPerSample switch
            {
                16 => MemoryMarshal.Read<short>(sample) / 32_768f,

                // Little-endian 24-bit has no native integer type: pack the three bytes
                // into the top of an int, then use the arithmetic right shift (>>, not
                // >>>) to sign-extend from bit 23 back down to bit 0. The divisor is
                // 2^23, the full-scale magnitude of a signed 24-bit sample.
                24 => ((sample[2] << 16 | sample[1] << 8 | sample[0]) << 8 >> 8) / 8_388_608f,

                // Explicit rather than a catch-all default: IsSupported restricts PCM to
                // 16/24/32-bit today, but if that ever grows to include 8-bit, an
                // unmatched width here must produce silence rather than an
                // ArgumentOutOfRangeException from MemoryMarshal.Read on the capture
                // thread.
                32 => MemoryMarshal.Read<int>(sample) / 2_147_483_648f,
                _ => 0f,
            };
        }

        handler(_scratch.AsSpan(0, count));
    }

    /// <summary>
    /// Unsubscribes before disposing, and waits for RecordingStopped in between. Each of
    /// those three steps is a leak of its own if skipped: a live DataAvailable handler
    /// keeps integrating into a source that has moved on, a live volume notification
    /// keeps a device alive, and a Dispose that overtakes RecordingStopped leaves the
    /// IAudioClient behind.
    /// </summary>
    private void StopLocked()
    {
        // Marks this teardown as solicited before touching anything, so a
        // RecordingStopped that lands mid-StopRecording (or from an unsolicited stop
        // that races with a caller-initiated one) is not mistaken for the unsolicited
        // kind and does not clear DeviceId out from under a teardown already in
        // progress.
        _stopRequested = true;

        var capture = _capture;

        if (capture is not null)
        {
            try
            {
                capture.StopRecording();

                // StopRecording() is harmless on a capture that never started, but the
                // wait is not: nothing raises RecordingStopped for a capture that was
                // never recording, so waiting unconditionally would block for the full
                // StopTimeout on every TryStart failure between construction and
                // StartRecording (endpoint-volume throwing, StartRecording itself
                // throwing) — exactly the failure paths AudioLevelSensorSource retries
                // once a second.
                if (_recording)
                {
                    _stopped?.Wait(StopTimeout);
                }
            }
            catch (Exception)
            {
            }

            if (_dataHandler is not null)
            {
                capture.DataAvailable -= _dataHandler;
            }

            if (_stoppedHandler is not null)
            {
                capture.RecordingStopped -= _stoppedHandler;
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception)
            {
            }
        }

        if (_device is { } device)
        {
            try
            {
                if (_volumeHandler is not null)
                {
                    device.AudioEndpointVolume.OnVolumeNotification -= _volumeHandler;
                }

                device.Dispose();
            }
            catch (Exception)
            {
            }
        }

        // Null the field before disposing: OnRecordingStopped (and anything else reading
        // _stopped) must never observe a disposed-but-non-null handle. NAudio raises
        // RecordingStopped outside CaptureThread's own try/catch, so Set() throwing
        // ObjectDisposedException there would be an unhandled exception on a background
        // thread and would take the process down.
        var stopped = _stopped;
        _stopped = null;
        try
        {
            stopped?.Dispose();
        }
        catch (Exception)
        {
        }

        _capture = null;
        _device = null;
        _onSamples = null;
        _dataHandler = null;
        _stoppedHandler = null;
        _volumeHandler = null;
        Format = null;
        DeviceId = null;
        _encoding = default;
        _bitsPerSample = 0;
        _stopRequested = false;
        _recording = false;
    }
}
