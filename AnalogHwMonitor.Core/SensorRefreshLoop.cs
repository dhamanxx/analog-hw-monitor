namespace AnalogHwMonitor.Core;

/// <summary>
/// Owns the cadence of <see cref="ISensorSource.Refresh"/>. Refresh() is the most
/// expensive thing in the application and it used to run on the UI thread, where 99 ms
/// stalled a VU meter needle — longer than the 65 ms time constant of
/// <see cref="VuIntegrator"/>. Hence a task of its own.
///
/// Where those 99 ms sit, so nobody has to measure it again: 77 ms is the NVIDIA GPU's
/// update, almost entirely Windows' own GPU Engine performance counters; 13 ms is the WMI
/// thermal-zone query; 7 ms is the audio health check; and the whole AMD CPU, PawnIO and
/// all, is 1.6 ms. It was 218 ms before one needless COM call was removed from the audio
/// check.
///
/// The interval is one second in both modes. This class's predecessor
/// (ThrottledSensorSource) held three seconds in VU meter mode, but not for CPU — for the
/// fact that a refresh on the UI thread stalled a needle in motion. That reason does not
/// exist here, and the extra two seconds buy something: the audio health check notices a
/// default-device change (headphones in, speakers out) within a second instead of three.
/// The price is 99 ms of work per second, about 10 % of one core continuously; almost all
/// of it is those GPU counters rather than anything this application computes.
///
/// The class owns no logic, only cadence — <see cref="RefreshOnce"/> is the part that can
/// be tested without a clock, the same cut <see cref="MonitorService"/> already makes.
/// </summary>
public sealed class SensorRefreshLoop
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ISensorSource _sensors;
    private readonly IAppLog _log;
    private string? _reportedError;

    public SensorRefreshLoop(ISensorSource sensors, IAppLog log)
    {
        _sensors = sensors;
        _log = log;
    }

    /// <summary>
    /// One refresh. <see cref="CompositeSensorSource"/> already absorbs and latches each
    /// individual source's fault, so only a failure of the composite itself reaches here —
    /// and that must not kill the loop. An unhandled exception in a fire-and-forget task
    /// means frozen values and needles that look like a working application.
    /// </summary>
    public void RefreshOnce()
    {
        try
        {
            _sensors.Refresh();
            _reportedError = null;
        }
        catch (Exception ex)
        {
            if (_reportedError != ex.Message)
            {
                _log.Write($"Sensor refresh failed: {ex.Message}");
                _reportedError = ex.Message;
            }
        }
    }

    /// <summary>
    /// Must be started through <c>Task.Run</c>. Called straight from the UI thread, the
    /// first await would marshal its continuation back onto it through the WinForms
    /// SynchronizationContext and Refresh() would keep running there — the whole change
    /// would be a no-op.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                RefreshOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // A normal shutdown. Caught here so the task reaches Completed and shutdown
            // does not have to unwrap an AggregateException.
        }
    }
}
