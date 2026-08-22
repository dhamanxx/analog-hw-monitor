using System.Diagnostics;

namespace AnalogHwMonitor.Core;

/// <summary>
/// TEMPORARY diagnostic — delete this file and the calls to it in <see cref="MonitorService"/>
/// once the question it exists to answer has been answered.
///
/// The question: where does a tick's time actually go? VU meter mode ticks every 40 ms, and
/// once a second one of those ticks carries the hardware refresh — LibreHardwareMonitor's
/// driver I/O plus a WMI query for the ACPI thermal zones. Nobody has measured either. Until
/// somebody does, "move the sensor reading onto another thread" is a guess about which of
/// three different costs is the one that matters: the 1 Hz refresh, the per-tick sensor
/// reads, or the serial write.
///
/// Uses <see cref="Stopwatch.GetTimestamp"/> rather than <see cref="Stopwatch.StartNew"/> on
/// purpose: a Stopwatch is a class, and instrumenting an allocation-sensitive loop with
/// something that allocates five objects per tick would measure the instrument.
/// </summary>
public sealed class TickProfiler
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly IAppLog _log;
    private readonly TimeSpan _reportEvery;

    private long _windowStart;
    private long _previousTickStamp;
    private int _ticks;

    private Phase _interval;
    private Phase _total;
    private Phase _refresh;
    private Phase _read;
    private Phase _send;
    private Phase _ui;

    // Keyed on the source's type name. Fixed set, so no allocation after the first refresh.
    private readonly Dictionary<string, Phase> _sources = new();

    public TickProfiler(IAppLog log, TimeSpan reportEvery)
    {
        _log = log;
        _reportEvery = reportEvery;
    }

    /// <summary>A timestamp to measure from. No allocation.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Milliseconds elapsed since a stamp taken by <see cref="Now"/>.</summary>
    public static double MsSince(long stamp) => (Stopwatch.GetTimestamp() - stamp) * TicksToMs;

    /// <summary>
    /// One source's share of a refresh. Recorded per source rather than per tick, so the
    /// mean here is the true cost of that source's Refresh() rather than the cost
    /// amortised over every tick — which is what made the first round of measurement
    /// ambiguous. The whole point is to tell the WMI query apart from the driver I/O:
    /// one of them is a five-line fix and the other is a thread.
    /// </summary>
    public void RecordSourceRefresh(string source, double ms)
    {
        var phase = _sources.TryGetValue(source, out var existing) ? existing : default;
        phase.Add(ms);
        _sources[source] = phase;
    }

    public void RecordTick(long tickStart, double refreshMs, double readMs, double sendMs, double uiMs)
    {
        if (_windowStart == 0)
        {
            _windowStart = tickStart;
        }

        // The interval between successive ticks is the jitter the needle actually sees. A
        // System.Windows.Forms.Timer asks for 40 ms and gets whatever the message pump has
        // time for, so this is the number that says whether the tick rate is real.
        if (_previousTickStamp != 0)
        {
            _interval.Add((tickStart - _previousTickStamp) * TicksToMs);
        }

        _previousTickStamp = tickStart;
        _ticks++;

        _refresh.Add(refreshMs);
        _read.Add(readMs);
        _send.Add(sendMs);
        _ui.Add(uiMs);
        _total.Add(MsSince(tickStart));

        var windowMs = MsSince(_windowStart);
        if (windowMs < _reportEvery.TotalMilliseconds)
        {
            return;
        }

        var perSource = _sources.Count == 0
            ? string.Empty
            : " | " + string.Join(", ", _sources.Select(pair => $"{pair.Key} {pair.Value}"));

        _log.Write(
            $"TICK {_ticks} ticks in {windowMs / 1000.0:F1}s | "
            + $"interval {_interval} | total {_total} | refresh {_refresh} | "
            + $"read {_read} | send {_send} | ui {_ui}{perSource} (mean/max ms)");

        _ticks = 0;
        _windowStart = 0;
        _interval = default;
        _total = default;
        _refresh = default;
        _read = default;
        _send = default;
        _ui = default;
        _sources.Clear();
    }

    /// <summary>Mean and maximum of one phase over the reporting window.</summary>
    private struct Phase
    {
        private double _sum;
        private double _max;
        private int _count;

        public void Add(double ms)
        {
            _sum += ms;
            _count++;

            if (ms > _max)
            {
                _max = ms;
            }
        }

        public override string ToString() =>
            _count == 0 ? "-/-" : $"{_sum / _count:F1}/{_max:F1}";
    }
}
