namespace AnalogHwMonitor.Core;

/// <summary>
/// Presents several sensor sources as one. A source that fails is skipped rather than
/// allowed to take the others down with it — losing the ACPI zones must not cost us the
/// CPU load, and vice versa.
/// </summary>
public sealed class CompositeSensorSource : ISensorSource
{
    private readonly IAppLog _log;
    private readonly ISensorSource[] _sources;
    private readonly bool[] _faultReported;

    public CompositeSensorSource(IAppLog log, params ISensorSource[] sources)
    {
        _log = log;
        _sources = sources;
        _faultReported = new bool[sources.Length];
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

    /// <summary>Runs one source's operation, reporting a persistent fault only once.</summary>
    private T? Try<T>(int index, Func<ISensorSource, T> operation)
    {
        try
        {
            var result = operation(_sources[index]);
            _faultReported[index] = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_faultReported[index])
            {
                _log.Write($"Sensor source {_sources[index].GetType().Name} failed: {ex.Message}");
                _faultReported[index] = true;
            }

            return default;
        }
    }
}
