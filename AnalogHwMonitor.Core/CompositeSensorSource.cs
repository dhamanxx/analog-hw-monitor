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
    private static readonly int OperationCount = Enum.GetValues<SourceOperation>().Length;

    private readonly IAppLog _log;
    private readonly ISensorSource[] _sources;
    private readonly string?[,] _lastFault;

    public CompositeSensorSource(IAppLog log, params ISensorSource[] sources)
    {
        _log = log;
        _sources = sources;
        _lastFault = new string?[sources.Length, OperationCount];
    }

    public void Refresh()
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            Try(i, SourceOperation.Refresh, source =>
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
            var discovered = Try(i, SourceOperation.Discover, source => source.Discover());
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
            var value = Try(i, SourceOperation.Read, source => source.Read(sensorId));
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
            Try(i, SourceOperation.Dispose, source =>
            {
                source.Dispose();
                return true;
            });
        }
    }

    /// <summary>
    /// Runs one source's operation, logging a fault only the first time it appears.
    /// The latch is keyed on the source, the operation, and the last-reported message
    /// for that pair. Keying on the source alone was not enough: a single tick calls
    /// Refresh() and then Read() on the same source, a persistently broken source
    /// throws a different message from each, and a message-only latch therefore
    /// alternated and logged both on every tick forever. With one slot per operation
    /// an unchanged fault stays silent no matter how the calls interleave, while a
    /// genuinely different failure of the same operation is still reported.
    /// </summary>
    private T? Try<T>(int index, SourceOperation operation, Func<ISensorSource, T> work)
    {
        try
        {
            var result = work(_sources[index]);
            _lastFault[index, (int)operation] = null;
            return result;
        }
        catch (Exception ex)
        {
            if (_lastFault[index, (int)operation] != ex.Message)
            {
                _log.Write(
                    $"Sensor source {_sources[index].GetType().Name} failed on {operation}: {ex.Message}");
                _lastFault[index, (int)operation] = ex.Message;
            }

            return default;
        }
    }

    /// <summary>
    /// Which call into a source failed. One tick calls <see cref="Refresh"/> once and
    /// <see cref="Read"/> up to five times on the same source, and a broken source
    /// rarely fails all of them with the same message, so the fault latch has to be
    /// per operation as well as per source.
    /// </summary>
    private enum SourceOperation
    {
        Refresh,
        Discover,
        Read,
        Dispose,
    }
}
