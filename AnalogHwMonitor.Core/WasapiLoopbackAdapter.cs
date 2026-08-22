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

    private double _volumeDb;
    private bool _muted;

    public AudioFormat? Format { get; private set; }

    public string? DeviceId { get; private set; }

    public string? DeviceName { get; private set; }

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
                var capture = new NAudio.Wave.WasapiLoopbackCapture(device);

                var format = capture.WaveFormat;
                if (!IsSupported(format))
                {
                    capture.Dispose();
                    device.Dispose();
                    error = $"Unsupported audio format: {format.Encoding} {format.BitsPerSample}-bit.";
                    return false;
                }

                _onSamples = onSamples;
                _stopped = new ManualResetEventSlim(false);

                _dataHandler = (_, e) => OnData(e);
                _stoppedHandler = (_, _) => _stopped?.Set();
                capture.DataAvailable += _dataHandler;
                capture.RecordingStopped += _stoppedHandler;

                // Read once for the starting value, then follow notifications. Polling
                // this at the tick rate would be fifty COM calls a second on the UI
                // thread; a volume change is worth exactly one.
                Volatile.Write(ref _volumeDb, device.AudioEndpointVolume.MasterVolumeLevel);
                Volatile.Write(ref _muted, device.AudioEndpointVolume.Mute);
                _volumeHandler = OnVolumeChanged;
                device.AudioEndpointVolume.OnVolumeNotification += _volumeHandler;

                _device = device;
                _capture = capture;
                DeviceId = device.ID;
                DeviceName = device.FriendlyName;
                Format = new AudioFormat(format.SampleRate, format.Channels);

                capture.StartRecording();

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
    /// Runs on NAudio's capture thread. The buffer belongs to NAudio and is recycled, so
    /// the float case reinterprets it in place and the PCM cases convert into a scratch
    /// array that is allocated once and grown, never per callback.
    /// </summary>
    private void OnData(WaveInEventArgs e)
    {
        var handler = _onSamples;
        var capture = _capture;

        if (handler is null || capture is null || e.BytesRecorded <= 0)
        {
            return;
        }

        var format = capture.WaveFormat;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            handler(MemoryMarshal.Cast<byte, float>(bytes));
            return;
        }

        var bytesPerSample = format.BitsPerSample / 8;
        var count = e.BytesRecorded / bytesPerSample;

        if (_scratch.Length < count)
        {
            _scratch = new float[count];
        }

        for (var i = 0; i < count; i++)
        {
            var sample = bytes.Slice(i * bytesPerSample, bytesPerSample);
            _scratch[i] = format.BitsPerSample switch
            {
                16 => MemoryMarshal.Read<short>(sample) / 32_768f,
                24 => ((sample[2] << 16 | sample[1] << 8 | sample[0]) << 8 >> 8) / 8_388_608f,
                _ => MemoryMarshal.Read<int>(sample) / 2_147_483_648f,
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
        var capture = _capture;

        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
                _stopped?.Wait(StopTimeout);
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

        _stopped?.Dispose();

        _capture = null;
        _device = null;
        _stopped = null;
        _onSamples = null;
        _dataHandler = null;
        _stoppedHandler = null;
        _volumeHandler = null;
        Format = null;
        DeviceId = null;
    }
}
