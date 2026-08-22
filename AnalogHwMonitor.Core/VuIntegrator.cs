namespace AnalogHwMonitor.Core;

/// <summary>
/// One meter's worth of VU ballistics: a full-wave rectifier followed by a one-pole
/// low-pass filter. The 1942 VU standard asks for 99 % of the steady value within
/// 300 ms, and a one-pole filter reaches 99 % after ln(100) = 4.605 time constants,
/// so tau is 300 / 4.605 = 65 ms. Rise and fall share that constant because a linear
/// filter cannot tell them apart — which is also why this class needs no separate
/// attack and release.
///
/// Deliberately not a peak meter. A VU meter reads perceived loudness, and the real
/// moving-coil meter downstream adds its own mechanical inertia on top, so precision
/// beyond this would be thrown away.
///
/// <see cref="Level"/> is written by two threads without a lock: the audio capture
/// thread via <see cref="Add"/>, and the UI tick loop via <see cref="Decay"/>.
/// <see cref="Volatile.Read"/> and <see cref="Volatile.Write"/> ensure each access is
/// atomic and the value is never torn, but they do not make the read-modify-write pair
/// atomic — an overlapping update can be lost. This is accepted rather than guarded,
/// because a lost update is self-correcting within one buffer: whichever write lands,
/// the next call reads it and carries on, so the level can be one buffer behind but
/// never stuck. The window is also narrow by construction: the tick loop only decays
/// after a silence gap when the capture thread is not calling <see cref="Add"/>.
/// A lock is not worth taking on the capture path to correct something no needle can show.
/// </summary>
public sealed class VuIntegrator
{
    /// <summary>Filter time constant. 99 % of a step within 300 ms.</summary>
    public static readonly double TimeConstantSeconds = 0.300 / Math.Log(100.0);

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

        var alpha = 1.0 - Math.Exp(-1.0 / (sampleRate * TimeConstantSeconds));
        var level = Volatile.Read(ref _level);

        for (var i = offset; i < block.Length; i += stride)
        {
            level += (Math.Abs(block[i]) - level) * alpha;
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
        Volatile.Write(ref _level, level * Math.Exp(-elapsed.TotalSeconds / TimeConstantSeconds));
    }
}
