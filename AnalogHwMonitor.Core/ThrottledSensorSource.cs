namespace AnalogHwMonitor.Core;

/// <summary>
/// Passes everything through to another source, but lets <see cref="Refresh"/> reach
/// it at most once per <see cref="MinimumInterval"/>.
///
/// VU meter mode runs the tick loop at 25 Hz, and Refresh() is where
/// LibreHardwareMonitor talks to its kernel driver through PawnIO — by far the most
/// expensive call in a tick, and one that would show up as a permanent slice of CPU
/// in a tray application that runs all day. Reads are not throttled and must not be:
/// the audio level is computed on the capture thread rather than fetched by Refresh(),
/// so it is live at every tick, while temperatures and load simply repeat last
/// second's value into a frame that is identical apart from the two audio channels.
///
/// Wrapping the whole composite rather than the hardware sources individually keeps
/// the policy in one place, and costs nothing: the audio source's own Refresh() does
/// nothing but a once-a-second health check, which is exactly the rate it wants.
/// </summary>
public sealed class ThrottledSensorSource : ISensorSource
{
    /// <summary>
    /// Three seconds rather than one, because measurement showed a refresh costs about
    /// 215 ms — a fifth of the wall clock at one second, and a 215-380 ms gap in the
    /// needle's motion every second, which is longer than the VU integration's own time
    /// constant. Temperatures and load do not change faster than this; thermal mass sees
    /// to that.
    ///
    /// The cost of the choice, and it is real: the audio source's health check rides on
    /// the same Refresh(), so a default-device change is now noticed within three seconds
    /// rather than one, and the five-second idle release can fire up to three seconds
    /// late. If the 215 ms turns out to be one identifiable source that can simply be
    /// made cheap, this should go back to one second and get the responsiveness back.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(3);

    private readonly ISensorSource _inner;
    private readonly TimeProvider _time;

    // MinValue, so the first Refresh() reaches the hardware rather than waiting out
    // an interval before the first reading exists.
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public ThrottledSensorSource(ISensorSource inner, TimeProvider? time = null)
    {
        _inner = inner;
        _time = time ?? TimeProvider.System;
    }

    public void Refresh()
    {
        var now = _time.GetUtcNow();
        if (now - _lastRefresh < MinimumInterval)
        {
            return;
        }

        _lastRefresh = now;
        _inner.Refresh();
    }

    public IReadOnlyList<SensorDescriptor> Discover() => _inner.Discover();

    public float? Read(string sensorId) => _inner.Read(sensorId);

    public void Dispose() => _inner.Dispose();
}
