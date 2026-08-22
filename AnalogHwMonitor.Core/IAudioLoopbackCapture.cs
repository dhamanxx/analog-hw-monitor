namespace AnalogHwMonitor.Core;

/// <summary>
/// Receives one buffer of interleaved samples, normalised to -1..1, one frame per
/// <see cref="AudioFormat.ChannelCount"/> values.
///
/// The span is valid only for the duration of the call: implementations hand over a
/// buffer they reuse, and copying it per callback would allocate tens of kilobytes
/// twenty-five times a second for no reason at all.
/// </summary>
public delegate void AudioSamplesHandler(ReadOnlySpan<float> samples);

/// <param name="SampleRate">Frames per second, e.g. 48000.</param>
/// <param name="ChannelCount">Values per frame. Also the stride through the buffer.</param>
public sealed record AudioFormat(int SampleRate, int ChannelCount);

/// <summary>
/// What the audio level source needs from the operating system, and nothing more.
/// The only implementation that talks to a real audio stack is
/// <c>WasapiLoopbackAdapter</c>, which stands in the same relation to this interface
/// as <c>SerialPortAdapter</c> does to <c>ISerialPort</c>: one untested edge, with
/// everything worth testing above it.
/// </summary>
public interface IAudioLoopbackCapture : IDisposable
{
    /// <summary>
    /// Begins delivering samples on a capture thread. Returns false with a reason
    /// rather than throwing — a missing, busy or unreadable device is a state, exactly
    /// as a serial port that will not open is a state.
    /// </summary>
    bool TryStart(AudioSamplesHandler onSamples, out string? error);

    /// <summary>
    /// Stops delivering samples and releases the device. Must not return until the
    /// handler passed to <see cref="TryStart"/> can no longer be called.
    /// </summary>
    void Stop();

    /// <summary>
    /// The running stream's format, or null while stopped. The sample handler reads
    /// this on every buffer on the no-allocation capture path, so an implementation
    /// must return a cached instance rather than constructing one per call.
    /// </summary>
    AudioFormat? Format { get; }

    /// <summary>
    /// Endpoint id of the device being captured, or null while stopped. An implementation
    /// must also clear this when capture stops for a reason the caller did not ask for —
    /// another application taking the endpoint into exclusive mode, a format change, or
    /// the sample handler throwing — because that is what lets a consumer's device
    /// comparison notice a capture that died underneath it. That noticing is the
    /// specification's promised self-recovery from an endpoint held in exclusive mode.
    /// </summary>
    string? DeviceId { get; }

    /// <summary>
    /// Friendly name of the device that is being captured, or would be. Resolving it
    /// must not start capture: this is what fills the settings window's dropdown, and
    /// opening that window is no reason to seize the audio device.
    /// </summary>
    string? DeviceName { get; }

    /// <summary>
    /// The endpoint id Windows currently calls the default render device, or null when
    /// there is none. Compared against <see cref="DeviceId"/> once a second to notice
    /// headphones being plugged in.
    /// </summary>
    string? CurrentDefaultDeviceId { get; }

    /// <summary>
    /// Master volume attenuation in dB: 0 at full scale, negative below it. Cached from
    /// a volume-change notification, so reading this makes no COM call — at 25 Hz over
    /// two channels a polled property would be fifty COM calls a second on the UI
    /// thread.
    /// </summary>
    double VolumeDb { get; }

    bool IsMuted { get; }
}
