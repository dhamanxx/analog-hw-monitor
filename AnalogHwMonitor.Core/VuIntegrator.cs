namespace AnalogHwMonitor.Core;

/// <summary>
/// One meter's worth of VU ballistics: a full-wave rectifier followed by a one-pole
/// low-pass filter. The 1942 VU standard asks for 99 % of the steady value within
/// 300 ms, and a one-pole filter reaches 99 % after ln(100) = 4.605 time constants,
/// so the default instance's tau is 300 / 4.605 = 65 ms — <see cref="TimeConstantSeconds"/>.
/// The constructor now takes tau, so this is only the default; see its overload for why a
/// caller would ask for a different one. Rise and fall share whatever constant is chosen
/// on purpose, because the standard is symmetric — not because a one-pole filter has no
/// choice. Picking alpha from whether the sample is above or below the current level would
/// give separate attack and release, the way every compressor does; that is deliberately
/// not done here.
///
/// The consequence is worth knowing before anyone changes the default: a symmetric 65 ms
/// means the needle drops into every gap in the music, and a probe build measured the fall
/// at 67 dB per 500 ms of digital silence, which is exactly what a 65 ms tau asks for.
/// Modern meters look smoother because they release over one to three seconds instead.
/// Reading low on music is the same story from the other side — the meter is
/// average-responding and calibrated so a full-scale sine reads 0 dBFS, so a track peaking
/// at 0 dBFS sits about 10 dB lower; measured crest factors on real material were 8.7 to
/// 11.6 dB. None of that is drift to be corrected. It is what a VU meter is.
///
/// Deliberately not a peak meter. A VU meter reads perceived loudness, and the real
/// moving-coil meter downstream adds its own mechanical inertia on top, so precision
/// beyond this would be thrown away.
///
/// <see cref="Level"/> is written by two threads without a lock: the audio capture
/// thread via <see cref="Add"/> or <see cref="AddMono"/>, and the UI tick loop via
/// <see cref="Decay"/>. <see cref="Volatile.Read"/> and <see cref="Volatile.Write"/>
/// ensure each access is
/// atomic and the value is never torn, but they do not make the read-modify-write pair
/// atomic — an overlapping update can be lost. This is accepted rather than guarded,
/// because a lost update is self-correcting within one buffer: whichever write lands,
/// the next call reads it and carries on, so the level can be one buffer behind but
/// never stuck. The window is also narrow by construction: the tick loop only decays
/// after a silence gap when the capture thread is not calling <see cref="Add"/> or
/// <see cref="AddMono"/>. A lock is not worth taking on the capture path to correct
/// something no needle can show.
/// </summary>
public sealed class VuIntegrator
{
    /// <summary>Filter time constant. 99 % of a step within 300 ms.</summary>
    public static readonly double TimeConstantSeconds = 0.300 / Math.Log(100.0);

    /// <summary>
    /// Below this the level is snapped to zero instead of decaying further. An exponential
    /// decay never reaches zero, so without a floor a long silence drives the filter state
    /// down without limit — at roughly (20 / ln 10) / tau dB per second, which is about
    /// -140 dB/s for the 65 ms constant every instance in this build currently uses.
    /// A shorter tau scales that up in proportion. Either way a minute of quiet
    /// takes the level past 1e-300 and into denormal doubles, where arithmetic carries a
    /// penalty. That matters here only because <see cref="Add"/> and
    /// <see cref="AddMono"/> run per sample on the WASAPI capture thread, which is the one
    /// thread that must never be slow.
    ///
    /// 1e-9 is safe by a wide margin rather than by a hair: the reported floor,
    /// <c>AudioSensorIds.FloorDbfs</c> at -100 dBFS, corresponds to a level near 6.4e-6, so
    /// this sits about 76 dB below anything a needle or a text box can show.
    /// </summary>
    public const double SilenceFloor = 1e-9;

    private readonly double _tau;

    public VuIntegrator()
        : this(TimeConstantSeconds)
    {
    }

    /// <summary>
    /// A shorter time constant than the standard's 65 ms exists for one reason: the
    /// throwaway compensator build puts the averaging in the needle compensator
    /// instead, and leaves this filter doing nothing but rectification and ripple
    /// rejection.
    /// </summary>
    public VuIntegrator(double timeConstantSeconds)
    {
        if (timeConstantSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeConstantSeconds), timeConstantSeconds, "The time constant must be positive.");
        }

        _tau = timeConstantSeconds;
    }

    private double _level;

    /// <summary>Rectified, filtered amplitude. 0..1 for input samples within -1..1.</summary>
    public double Level => Volatile.Read(ref _level);

    public void Reset() => Volatile.Write(ref _level, 0.0);

    /// <summary>
    /// Folds one channel's samples out of an interleaved block. <paramref name="offset"/>
    /// is the channel's position within a frame and <paramref name="stride"/> the number
    /// of channels per frame, so the right channel of a stereo block is offset 1,
    /// stride 2.
    ///
    /// The coefficient is computed per sample from the sample rate rather than per
    /// block, so the result does not depend on how large a buffer WASAPI happened to
    /// hand over — buffer sizes vary with the device and the load, and a meter whose
    /// reading moved with them would be untestable.
    /// </summary>
    public void Add(ReadOnlySpan<float> block, int offset, int stride, int sampleRate)
    {
        if (stride <= 0 || offset < 0 || sampleRate <= 0)
        {
            return;
        }

        var alpha = 1.0 - Math.Exp(-1.0 / (sampleRate * _tau));
        var level = Volatile.Read(ref _level);

        for (var i = offset; i < block.Length; i += stride)
        {
            level += (Math.Abs(block[i]) - level) * alpha;
        }

        Volatile.Write(ref _level, level);
    }

    /// <summary>
    /// Folds every channel of a frame to one value before rectifying — (L+R)/2, not the
    /// average of two rectified channels, so anti-phase content cancels the way it does
    /// on a mono downmix.
    ///
    /// Allocates nothing: the span is read in place and the running sum is one double,
    /// because this is called on the WASAPI capture thread.
    /// </summary>
    public void AddMono(ReadOnlySpan<float> block, int channelCount, int sampleRate)
    {
        if (channelCount <= 0 || sampleRate <= 0)
        {
            return;
        }

        var alpha = 1.0 - Math.Exp(-1.0 / (sampleRate * _tau));
        var level = Volatile.Read(ref _level);
        var frames = block.Length / channelCount;

        for (var frame = 0; frame < frames; frame++)
        {
            var start = frame * channelCount;
            var sum = 0.0;

            for (var channel = 0; channel < channelCount; channel++)
            {
                sum += block[start + channel];
            }

            level += (Math.Abs(sum / channelCount) - level) * alpha;
        }

        Volatile.Write(ref _level, level);
    }

    /// <summary>
    /// Lets the level fall as if silence had arrived for <paramref name="elapsed"/>.
    /// WASAPI stops delivering buffers altogether when nothing is playing — not silent
    /// buffers, none at all — so without this the needle would stay parked wherever the
    /// last sample of the last track left it.
    /// </summary>
    public void Decay(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var level = Volatile.Read(ref _level);
        var decayed = level * Math.Exp(-elapsed.TotalSeconds / _tau);

        Volatile.Write(ref _level, decayed < SilenceFloor ? 0.0 : decayed);
    }
}
