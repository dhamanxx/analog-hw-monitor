namespace AnalogHwMonitor.Core;

/// <summary>
/// Picks a sensible sensor per channel on first run. Sensor names differ by vendor,
/// so each channel has an ordered list of patterns plus a hint about which device
/// the sensor should belong to.
/// </summary>
public static class SensorDefaults
{
    private sealed record Rule(SensorKind Kind, string[] NamePatterns, string IdHint);

    private static readonly Rule[] Rules =
    {
        new(SensorKind.Load,        new[] { "CPU Total", "CPU" },                              "cpu"),
        new(SensorKind.Load,        new[] { "GPU Core", "GPU" },                               "gpu"),
        new(SensorKind.Load,        new[] { "Memory" },                                        "/ram"),
        new(SensorKind.Temperature, new[] { "CPU Package", "Tctl", "Core Average", "CPU" },    "cpu"),
        new(SensorKind.Temperature, new[] { "GPU Core", "GPU" },                               "gpu"),
    };

    public static void AssignSensors(AppConfig config, IReadOnlyList<SensorDescriptor> sensors)
    {
        for (var i = 0; i < config.Channels.Count && i < Rules.Length; i++)
        {
            if (!string.IsNullOrEmpty(config.Channels[i].SensorId))
            {
                continue;
            }

            config.Channels[i].SensorId = Match(sensors, Rules[i])?.Id;
        }
    }

    private static SensorDescriptor? Match(IReadOnlyList<SensorDescriptor> sensors, Rule rule)
    {
        var onHintedDevice = sensors
            .Where(s => s.Kind == rule.Kind &&
                        s.Id.Contains(rule.IdHint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A hint starting with "/" is mandatory: without it, "GPU Memory" would
        // satisfy the memory channel on a machine that reports no system RAM sensor.
        var candidates = onHintedDevice.Count > 0
            ? onHintedDevice
            : rule.IdHint.StartsWith('/')
                ? new List<SensorDescriptor>()
                : sensors.Where(s => s.Kind == rule.Kind).ToList();

        foreach (var pattern in rule.NamePatterns)
        {
            var exact = candidates.FirstOrDefault(
                s => string.Equals(s.Name, pattern, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var partial = candidates.FirstOrDefault(
                s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }
        }

        return null;
    }
}
