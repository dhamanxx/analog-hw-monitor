using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>
/// A capture the test drives by hand. Counts starts and stops and keeps at most one
/// handler, so a test can prove the source neither leaks a subscription nor starts
/// twice — the two ways the real WASAPI capture would leak.
/// </summary>
public sealed class FakeAudioLoopbackCapture : IAudioLoopbackCapture
{
    private AudioSamplesHandler? _onSamples;

    public FakeAudioLoopbackCapture(int sampleRate = 48_000, int channelCount = 2)
    {
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    /// <summary>Set to a message to make TryStart fail, as a missing device would.</summary>
    public string? StartError { get; set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>Handlers currently held. Must never exceed one.</summary>
    public int HandlerCount => _onSamples is null ? 0 : 1;

    public AudioFormat? Format { get; private set; }

    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; } = "Fake Audio";

    public string? CurrentDefaultDeviceId { get; set; } = "device-1";

    public double VolumeDb { get; set; }

    public bool IsMuted { get; set; }

    public bool TryStart(AudioSamplesHandler onSamples, out string? error)
    {
        StartCount++;

        if (StartError is not null)
        {
            error = StartError;
            return false;
        }

        _onSamples = onSamples;
        Format = new AudioFormat(SampleRate, ChannelCount);
        DeviceId = CurrentDefaultDeviceId;
        error = null;
        return true;
    }

    public void Stop()
    {
        StopCount++;
        _onSamples = null;
        Format = null;
        DeviceId = null;
    }

    public void Dispose()
    {
        DisposeCount++;
        Stop();
    }

    /// <summary>Delivers a full-scale sine on every channel, the signal a VU meter is
    /// calibrated against.</summary>
    public void DeliverSine(double seconds, double frequency = 1_000.0, float peak = 1.0f)
    {
        var frames = (int)(seconds * SampleRate);
        var samples = new float[frames * ChannelCount];

        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(peak * Math.Sin(2.0 * Math.PI * frequency * frame / SampleRate));
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                samples[(frame * ChannelCount) + channel] = value;
            }
        }

        Deliver(samples);
    }

    public void Deliver(ReadOnlySpan<float> samples) => _onSamples?.Invoke(samples);
}
