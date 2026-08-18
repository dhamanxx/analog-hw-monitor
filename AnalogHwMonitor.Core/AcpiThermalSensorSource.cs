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

    private readonly IAppLog _log;
    private readonly Dictionary<string, float> _values = new();
    private readonly List<SensorDescriptor> _descriptors = new();
    private bool _faultReported;

    public AcpiThermalSensorSource(IAppLog log) => _log = log;

    public void Refresh()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            _values.Clear();
            _descriptors.Clear();

            foreach (var zone in searcher.Get().Cast<ManagementBaseObject>())
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

                    _values[id] = celsius;
                    _descriptors.Add(new SensorDescriptor(
                        id, name, "ACPI Thermal Zone", SensorKind.Temperature, "°C"));
                }
            }

            _faultReported = false;
        }
        catch (Exception ex)
        {
            if (!_faultReported)
            {
                _log.Write($"ACPI thermal zones unavailable: {ex.Message}");
                _faultReported = true;
            }

            _values.Clear();
            _descriptors.Clear();
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() => _descriptors;

    public float? Read(string sensorId) =>
        _values.TryGetValue(sensorId, out var value) ? value : null;

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
