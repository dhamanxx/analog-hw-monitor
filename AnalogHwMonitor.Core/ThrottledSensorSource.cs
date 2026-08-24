namespace AnalogHwMonitor.Core;

/// <summary>
/// Passes everything through to another source, but lets <see cref="Refresh"/> reach it
/// at most once per interval — and which interval depends on the mode, because the two
/// modes want opposite things from the same call.
///
/// Refresh() is the expensive half of a tick, but not for the reason this comment used to
/// give. It claimed the cost was LibreHardwareMonitor's ring0 driver. Measured, the driver
/// is cheap: a whole AMD CPU update through PawnIO is 1.6 ms. The cost is Windows' own GPU
/// Engine performance counters, read as part of the NVIDIA GPU's update, at 77 ms — and it
/// does not get cheaper when calls are spaced further apart, so the only lever is how often.
///
/// Reads are not throttled and must not be: the audio level is computed on the capture
/// thread rather than fetched by Refresh(), so it is live at every tick, while temperatures
/// and load simply repeat their last reading into a frame that is identical apart from the
/// two audio channels.
///
/// This wraps the whole composite, so the audio source's health check rides the same
/// interval as the hardware. That is what makes the split worth the extra constant rather
/// than a detail of it — see <see cref="SensorModeInterval"/> and <see cref="VuModeInterval"/>.
/// </summary>
public sealed class ThrottledSensorSource : ISensorSource
{
    /// <summary>
    /// VU meter mode off. The tick itself is 1 Hz here, so a longer interval buys nothing
    /// and costs freshness twice over: temperatures and load would move once in three ticks
    /// while every tick still writes a frame, so two frames in three would carry numbers the
    /// app already sent. Nothing is in motion for the 99 ms refresh to interrupt either —
    /// the needles only move once a second in this mode, and the refresh is the reason they
    /// move at all.
    ///
    /// The price, and it is the honest one: 99 ms of work per second is about 10 % of one
    /// core, continuously, for the life of the tray app — roughly 1 % of an eight-core
    /// machine. Almost all of it is Windows' GPU Engine performance counters rather than
    /// anything this app computes.
    /// </summary>
    public static readonly TimeSpan SensorModeInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// VU meter mode on. The tick is ~21 Hz here and the needles are following music, so the
    /// 99 ms refresh stops being a cost in CPU and starts being a cost in motion: it is
    /// longer than the VU integration's own 65 ms time constant, so a needle moving through
    /// it visibly stalls. Three seconds puts that stall in one tick of sixty-odd instead of
    /// one in ten, and takes the burden from 21.8 % of the wall clock to 3.3 %. Temperatures
    /// and load do not change faster than three seconds anyway; thermal mass sees to that.
    ///
    /// Where the 99 ms sits, so nobody has to measure it again: 77 ms is the NVIDIA GPU's
    /// update, almost entirely those GPU Engine counters; 13 ms is the WMI thermal-zone
    /// query; 7 ms is the audio health check; and the whole AMD CPU, PawnIO and all, is
    /// 1.6 ms. A refresh was 218 ms before one needless COM call was removed from the audio
    /// check.
    ///
    /// What the three seconds costs is one thing, not the three it used to cost. The audio
    /// health check rides this same Refresh(), so a default-device change — headphones in,
    /// speakers out — is noticed within three seconds rather than one, and both needles sit
    /// dead until it is. The five-second idle release is no longer part of the bill: nothing
    /// reads a level once VU meter mode is off, and by then this interval is one second, so
    /// the release fires on time.
    /// </summary>
    public static readonly TimeSpan VuModeInterval = TimeSpan.FromSeconds(3);

    private readonly ISensorSource _inner;
    private readonly Func<bool> _vuMode;
    private readonly TimeProvider _time;

    // MinValue, so the first Refresh() reaches the hardware rather than waiting out
    // an interval before the first reading exists.
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    /// <param name="vuMode">Asked on every <see cref="Refresh"/> rather than captured, so
    /// the tray menu's toggle needs no wiring here: it writes the configuration and the next
    /// tick reads the new interval. Defaults to sensor mode.</param>
    public ThrottledSensorSource(
        ISensorSource inner, Func<bool>? vuMode = null, TimeProvider? time = null)
    {
        _inner = inner;
        _vuMode = vuMode ?? (() => false);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The interval in force right now. Public because it is the whole policy of
    /// this class, and because a test that hardcodes a second goes stale silently.</summary>
    public TimeSpan CurrentInterval => _vuMode() ? VuModeInterval : SensorModeInterval;

    /// <summary>
    /// A mode change is felt immediately in both directions, and that is the useful
    /// behaviour rather than an accident. Leaving VU meter mode two seconds into its three
    /// does not sit out the third: the elapsed time is already past the one second sensor
    /// mode asks for, so the next tick refreshes.
    /// </summary>
    public void Refresh()
    {
        var now = _time.GetUtcNow();
        if (now - _lastRefresh < CurrentInterval)
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
