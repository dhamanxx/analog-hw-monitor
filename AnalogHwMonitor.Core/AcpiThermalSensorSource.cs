using System.Management;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Reads ACPI thermal zones through WMI. No kernel driver is involved, which is the whole
/// point: where Memory Integrity blocks LibreHardwareMonitor's driver, these zones are the
/// only temperatures available. Needs elevation; without it the query is denied and this
/// source simply reports nothing rather than failing.
/// </summary>
public sealed class AcpiThermalSensorSource : ISensorSource
{
    public const string IdPrefix = "/acpi/thermalzone/";

    /// <summary>
    /// The values and descriptors from one Refresh(). One object rather than two fields
    /// on purpose: with two separate writes a reader could see the new descriptors beside
    /// the old values. A published instance is never mutated again, so the UI thread may
    /// read it while the poll task builds the next one.
    /// </summary>
    private sealed record Snapshot(
        Dictionary<string, float> Values,
        IReadOnlyList<SensorDescriptor> Descriptors);

    private readonly IAppLog _log;

    // Array.Empty() returns a cached singleton, which is fine here — this initial value
    // is never compared for identity against a refresh from the catch path. The catch
    // path must allocate fresh to keep each failed refresh distinct.
    private Snapshot _snapshot =
        new(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>());

    private bool _faultReported;

    public AcpiThermalSensorSource(IAppLog log) => _log = log;

    public void Refresh()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            var values = new Dictionary<string, float>();
            var descriptors = new List<SensorDescriptor>();

            // The collection itself is a WMI/COM enumerator and must be disposed
            // alongside the objects it yields — Refresh runs once a second for the
            // life of the tray application, so leaving this to the finalizer would
            // leak one per tick.
            using var zones = searcher.Get();
            foreach (var zone in zones.Cast<ManagementBaseObject>())
            {
                using (zone)
                {
                    if (zone["InstanceName"] is not string instance || zone["CurrentTemperature"] is null)
                    {
                        continue;
                    }

                    // WMI reports tenths of a kelvin.
                    var kelvinTenths = Convert.ToDouble(zone["CurrentTemperature"]);
                    var celsius = (float)(kelvinTenths / 10.0 - 273.15);

                    var name = ShortName(instance);
                    var id = IdPrefix + name;

                    values[id] = celsius;
                    descriptors.Add(new SensorDescriptor(
                        id, name, "ACPI Thermal Zone", SensorKind.Temperature, "°C"));
                }
            }

            Volatile.Write(ref _snapshot, new Snapshot(values, descriptors));
            _faultReported = false;
        }
        catch (Exception ex)
        {
            if (!_faultReported)
            {
                _log.Write($"ACPI thermal zones unavailable: {ex.Message}");
                _faultReported = true;
            }

            // Same semantics as the Clear() this replaced: after a failure nothing reads.
            // A new List rather than Array.Empty<SensorDescriptor>(), which is a cached
            // singleton — two failed refreshes would hand back the same instance, and on a
            // machine without elevation this path is the common one, not the rare one.
            Volatile.Write(
                ref _snapshot,
                new Snapshot(new Dictionary<string, float>(), new List<SensorDescriptor>()));
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
        Volatile.Read(ref _snapshot).Descriptors;

    public float? Read(string sensorId) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(sensorId, out var value) ? value : null;

    public void Dispose()
    {
    }

    /// <summary>Turns "ACPI\ThermalZone\CPUZ_0" into "CPUZ_0".</summary>
    private static string ShortName(string instanceName)
    {
        var lastSeparator = instanceName.LastIndexOf('\\');
        return lastSeparator >= 0 && lastSeparator < instanceName.Length - 1
            ? instanceName[(lastSeparator + 1)..]
            : instanceName;
    }
}
