namespace AnalogHwMonitor.Core;

/// <summary>
/// The thread model every implementation must satisfy: <see cref="Refresh"/> is called on
/// a background poll task at 1 Hz (<see cref="SensorRefreshLoop"/>), while
/// <see cref="Discover"/> and <see cref="Read"/> are called on the UI thread — from
/// <see cref="MonitorService.Tick"/> and the settings window. An implementation must be
/// safe for a Refresh() on the poll task and a Read()/Discover() on the UI thread
/// happening at the same time. <see cref="LibreHardwareSensorSource"/> and
/// <see cref="AcpiThermalSensorSource"/> do this by having Refresh() build a new immutable
/// snapshot and publish it with a single <c>Volatile.Write</c>, so a concurrent reader
/// either sees the old snapshot or the new one, never a mix. <see cref="AudioLevelSensorSource"/>
/// instead takes a lifecycle lock around Refresh() and Read(), because it starts and stops
/// a capture rather than swapping a value.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>
    /// Polls the hardware once. Called on the poll task, never on the UI thread — see the
    /// interface's thread model above.
    /// </summary>
    void Refresh();

    /// <summary>
    /// Lists the sensors this source currently exposes, for the settings window's
    /// dropdown. Called on the UI thread; must be safe alongside a concurrent Refresh().
    /// </summary>
    IReadOnlyList<SensorDescriptor> Discover();

    /// <summary>
    /// Last refreshed value, or null when the sensor is unknown or unreadable. Called on
    /// the UI thread, once per channel per tick; must be safe alongside a concurrent
    /// Refresh().
    /// </summary>
    float? Read(string sensorId);
}
