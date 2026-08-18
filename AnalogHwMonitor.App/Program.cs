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

        ISensorSource sensors;
        try
        {
            sensors = new LibreHardwareSensorSource();
            sensors.Refresh();
        }
        catch (Exception ex)
        {
            log.Write($"Cannot open the hardware monitor: {ex.Message}");
            MessageBox.Show(
                $"Cannot read hardware sensors.\n\n{ex.Message}\n\nRun the application as administrator.",
                "Analog Hardware Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var hadUnassignedChannels = config.Channels.Any(c => string.IsNullOrEmpty(c.SensorId));
        SensorDefaults.AssignSensors(config, sensors.Discover());
        if (hadUnassignedChannels)
        {
            store.Save(config);
        }

        var link = new SerialMeterLink(new SerialPortFactory(), config.ComPort, log);
        var monitor = new MonitorService(sensors, link, config, log);

        Application.Run(new TrayApplicationContext(monitor, link, store, sensors, log));
    }
}
