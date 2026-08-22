namespace AnalogHwMonitor.Core;

/// <summary>
/// Passes everything through to another source, but lets <see cref="Refresh"/> reach
/// it at most once per <see cref="MinimumInterval"/>.
///
/// VU meter mode runs the tick loop at 25 Hz, and Refresh() is the expensive half of a
/// tick — but not for the reason this comment used to give. It claimed the cost was
/// LibreHardwareMonitor's ring0 driver. Measured, the driver is cheap: a whole AMD CPU
/// update through PawnIO is 1.6 ms. The cost is Windows' own GPU Engine performance
/// counters, read as part of the NVIDIA GPU's update, at 77 ms — and it does not get
/// cheaper when calls are spaced further apart, so the only lever is how often.
///
/// Reads are not throttled and must not be: the audio level is computed on the capture
/// thread rather than fetched by Refresh(), so it is live at every tick, while
/// temperatures and load simply repeat their last reading into a frame that is
/// identical apart from the two audio channels.
///
/// This wraps the whole composite, which puts the audio source's health check on the
/// same interval as the hardware. That was free while the interval was one second and
/// is not any more — the check wants a second, the hardware wants ten. See
/// <see cref="MinimumInterval"/> for what that costs and what the clean fix would be.
/// </summary>
public sealed class ThrottledSensorSource : ISensorSource
{
    /// <summary>
    /// Three seconds rather than one. A refresh was measured at 218 ms, which at one second
    /// is a fifth of the wall clock and leaves a gap in the needle's motion longer than the
    /// VU integration's own 65 ms time constant. Removing one needless COM call took it to
    /// 99 ms and this interval took the rest: 21.8 % of the wall clock became 3.3 %.
    /// Temperatures and load do not change faster than three seconds anyway; thermal mass
    /// sees to that.
    ///
    /// Where the remaining 99 ms sits, so nobody has to measure it again: 77 ms is the
    /// NVIDIA GPU's update, almost entirely Windows' GPU Engine performance counters rather
    /// than the driver; 13 ms is the WMI thermal-zone query; 7 ms is the audio health
    /// check; and the whole AMD CPU, PawnIO and all, is 1.6 ms.
    ///
    /// The cost of this interval, and it is real: the audio source's health check rides the
    /// same Refresh(), so a default-device change is noticed within three seconds rather
    /// than one, and the five-second idle release can fire up to three seconds late. The
    /// clean fix, if that ever matters, is a per-source interval — the expensive hardware
    /// wants ten seconds and the audio check wants one — not a shorter interval here.
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
