namespace AnalogHwMonitor.Core;

/// <summary>Finds the port whose device answers with the AHM1 banner.</summary>
public static class PortDetector
{
    public static string? FindMonitorPort(ISerialPortFactory factory, IAppLog log)
    {
        foreach (var name in factory.GetPortNames())
        {
            using var link = new SerialMeterLink(factory, name, log);
            if (link.TryConnect())
            {
                return name;
            }
        }

        return null;
    }
}
