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
        /// <summary>TEMPORARY, optional. Set only while measuring; see TickProfiler.</summary>
        public TickProfiler? Profiler { get; set; }

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            // Timed per hardware, because Update() has hardware granularity: asking for one
            // CPU temperature pays for every value that CPU exposes -- every core's
            // temperature, the package, the clocks, the power -- each one an ioctl round
            // trip through PawnIO. Which hardware owns the cost decides whether the answer
            // is "refresh this one less often" or "move it off the UI thread".
            var start = TickProfiler.Now;
            hardware.Update();
            Profiler?.RecordSourceRefresh("hw:" + hardware.Name, TickProfiler.MsSince(start));

            // Subhardware is visited through Accept, so it lands back here and is timed
            // separately rather than being counted inside its parent.
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

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

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

    /// <summary>TEMPORARY, optional. Set only while measuring; see TickProfiler.</summary>
    public TickProfiler? Profiler
    {
        get => _visitor.Profiler;
        set => _visitor.Profiler = value;
    }

    public void Refresh() => _computer.Accept(_visitor);

    public IReadOnlyList<SensorDescriptor> Discover() =>
        EnumerateSensors()
            .Select(pair => new SensorDescriptor(
                pair.Sensor.Identifier.ToString(),
                pair.Sensor.Name,
                pair.Hardware.Name,
                ToKind(pair.Sensor.SensorType),
                ToUnit(pair.Sensor.SensorType)))
            .ToList();

    public float? Read(string sensorId) =>
        EnumerateSensors()
            .FirstOrDefault(pair => pair.Sensor.Identifier.ToString() == sensorId)
            .Sensor?.Value;

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
