using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var directory = AppContext.BaseDirectory;
        var log = new FileLog(Path.Combine(directory, "log.txt"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));

        var loaded = store.Load();
        if (loaded.Outcome != ConfigLoadOutcome.Loaded)
        {
            log.Write($"Configuration: {loaded.Outcome}.");
        }

        var config = loaded.Config;

        // Each sensor source is constructed under its own guard. LibreHardwareMonitor
        // opens a ring0 driver in its constructor and can fail outright; when it does,
        // the ACPI thermal zones alone still drive both temperature channels, which
        // beats refusing to start. Only a machine where nothing could be opened is a
        // dead end worth a message box.
        var sources = new List<ISensorSource>();
        var unavailable = new List<string>();

        void TryAddSource(string name, Func<ISensorSource> create)
        {
            try
            {
                sources.Add(create());
            }
            catch (Exception ex)
            {
                unavailable.Add($"{name}: {ex.Message}");
                log.Write($"Sensor source unavailable — {name}: {ex.Message}");
            }
        }

        TryAddSource("LibreHardwareMonitor", () => new LibreHardwareSensorSource());
        TryAddSource("ACPI thermal zones", () => new AcpiThermalSensorSource(log));

        if (sources.Count == 0)
        {
            MessageBox.Show(
                "Cannot read hardware sensors. No sensor source could be opened:\n\n"
                + string.Join(Environment.NewLine, unavailable)
                + "\n\nRun the application as administrator.",
                "Analog Hardware Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // From here on the composite absorbs and latches every source fault, so a
        // source that dies later costs its own readings and nothing else.
        ISensorSource sensors = new CompositeSensorSource(log, sources.ToArray());
        sensors.Refresh();

        var hadUnassignedChannels = config.Channels.Any(c => string.IsNullOrEmpty(c.SensorId));
        SensorDefaults.AssignSensors(config, sensors.Discover(), id => sensors.Read(id) is not null);
        if (hadUnassignedChannels)
        {
            // Saving the auto-detected defaults is a convenience, not a precondition
            // for running: a failed write here (e.g. a read-only install directory)
            // must not crash an app that is otherwise fully usable.
            try
            {
                store.Save(config);
            }
            catch (Exception ex)
            {
                log.Write($"Could not save the auto-detected configuration: {ex.Message}");
            }
        }

        var link = new SerialMeterLink(new SerialPortFactory(), config.ComPort, log);
        var monitor = new MonitorService(sensors, link, config, log);

        Application.Run(new TrayApplicationContext(monitor, link, store, sensors, log));
    }
}
