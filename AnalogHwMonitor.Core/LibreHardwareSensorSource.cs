using LibreHardwareMonitor.Hardware;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Reads the machine's sensors through LibreHardwareMonitor. Needs administrator
/// rights: the library loads a kernel driver, and without it most temperatures
/// are simply absent.
/// </summary>
public sealed class LibreHardwareSensorSource : ISensorSource
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            // Update() has hardware granularity: asking for one GPU temperature pays for
            // every value that GPU exposes. Measured on an RTX 4070 that is 77 ms, almost
            // all of it Windows' own GPU Engine performance counters rather than anything
            // the driver does — see SensorRefreshLoop for how rarely this is called.
            hardware.Update();

            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    /// <summary>
    /// Values and descriptors from one Refresh(). One object and not two fields on purpose:
    /// with two separate writes a reader could see new descriptors beside old values.
    /// A published instance is never mutated again, so the UI thread may read it while
    /// the poll task builds the next one.
    /// </summary>
    private sealed record Snapshot(
        Dictionary<string, float> Values,
        IReadOnlyList<SensorDescriptor> Descriptors);

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    private Snapshot _snapshot =
        new(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>());

    public LibreHardwareSensorSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
        };
        _computer.Open();
    }

    /// <summary>
    /// Only place where the tree is walked. Update() and reading values happen in the
    /// same pass; building the snapshot lazily on first Read() would put the tree walk
    /// back on the UI thread and solve nothing.
    ///
    /// Update() has hardware granularity: asking for one GPU temperature pays for every
    /// value that GPU exposes. Measured on an RTX 4070 it is 77 ms, almost all of it
    /// Windows' own GPU Engine performance counters, not the driver. All of AMD CPU
    /// through PawnIO is 1.6 ms by comparison. That is why this is on the poll task
    /// and once per second.
    /// </summary>
    public void Refresh()
    {
        _computer.Accept(_visitor);

        var values = new Dictionary<string, float>();
        var descriptors = new List<SensorDescriptor>();

        foreach (var (hardware, sensor) in EnumerateSensors())
        {
            var id = sensor.Identifier.ToString();

            // A sensor without a value does not go in the dictionary, so TryGetValue
            // returns false and Read() null — exactly what the prior sensor.Value did.
            if (sensor.Value is { } value)
            {
                values[id] = value;
            }

            descriptors.Add(new SensorDescriptor(
                id,
                sensor.Name,
                hardware.Name,
                ToKind(sensor.SensorType),
                ToUnit(sensor.SensorType)));
        }

        Volatile.Write(ref _snapshot, new Snapshot(values, descriptors));
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
        Volatile.Read(ref _snapshot).Descriptors;

    public float? Read(string sensorId) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(sensorId, out var value) ? value : null;

    public void Dispose() => _computer.Close();

    private IEnumerable<(IHardware Hardware, ISensor Sensor)> EnumerateSensors()
    {
        foreach (var hardware in _computer.Hardware)
        {
            foreach (var sensor in hardware.Sensors)
            {
                yield return (hardware, sensor);
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                foreach (var sensor in subHardware.Sensors)
                {
                    yield return (subHardware, sensor);
                }
            }
        }
    }

    private static SensorKind ToKind(SensorType type) => type switch
    {
        SensorType.Load => SensorKind.Load,
        SensorType.Temperature => SensorKind.Temperature,
        _ => SensorKind.Other,
    };

    private static string ToUnit(SensorType type) => type switch
    {
        SensorType.Load => "%",
        SensorType.Temperature => "°C",
        _ => string.Empty,
    };
}
