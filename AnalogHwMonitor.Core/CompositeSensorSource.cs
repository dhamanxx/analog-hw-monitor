namespace AnalogHwMonitor.Core;

/// <summary>
/// Presents several sensor sources as one. A source that fails is skipped rather than
/// allowed to take the others down with it — losing the ACPI zones must not cost us the
/// CPU load, and vice versa.
///
/// <see cref="Read"/> returns the first non-null value across sources, which is safe
/// only because every source in this application uses a disjoint sensor id prefix
/// (e.g. <c>/acpi/thermalzone/</c> versus LibreHardwareMonitor's own prefixes). A future
/// third source that reuses another source's ids would silently shadow it here.
/// </summary>
public sealed class CompositeSensorSource : ISensorSource
{
    private readonly IAppLog _log;
    private readonly ISensorSource[] _sources;
    private readonly string?[] _lastFault;

    public CompositeSensorSource(IAppLog log, params ISensorSource[] sources)
    {
        _log = log;
        _sources = sources;
        _lastFault = new string?[sources.Length];
    }

    public void Refresh()
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            Try(i, source =>
            {
                source.Refresh();
                return true;
            });
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover()
    {
        var all = new List<SensorDescriptor>();
        for (var i = 0; i < _sources.Length; i++)
        {
            var discovered = Try(i, source => source.Discover());
            if (discovered is not null)
            {
                all.AddRange(discovered);
            }
        }

        return all;
    }

    public float? Read(string sensorId)
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            var value = Try(i, source => source.Read(sensorId));
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            Try(i, source =>
            {
                source.Dispose();
                return true;
            });
        }
    }

    /// <summary>
    /// Runs one source's operation, logging a fault only the first time it appears.
    /// The latch remembers the last-reported failure message per source rather than
    /// just whether one occurred, so an unchanged fault stays silent on every later
    /// tick while a genuinely different failure on that same source is still logged.
    /// </summary>
    private T? Try<T>(int index, Func<ISensorSource, T> operation)
    {
        try
        {
            var result = operation(_sources[index]);
            _lastFault[index] = null;
            return result;
        }
        catch (Exception ex)
        {
            if (_lastFault[index] != ex.Message)
            {
                _log.Write($"Sensor source {_sources[index].GetType().Name} failed: {ex.Message}");
                _lastFault[index] = ex.Message;
            }

            return default;
        }
    }
}
