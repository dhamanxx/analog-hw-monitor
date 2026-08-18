namespace AnalogHwMonitor.Core;

/// <summary>
/// Picks a sensible sensor per channel on first run. Sensor names differ by vendor,
/// so each channel has an ordered list of patterns plus a hint about which device
/// the sensor should belong to.
/// </summary>
public static class SensorDefaults
{
    private sealed record Rule(SensorKind Kind, string[] NamePatterns, string[] IdHints, string[]? Exclude = null);

    private static readonly Rule[] Rules =
    {
        new(SensorKind.Load,        new[] { "CPU Total", "CPU" },                                   new[] { "cpu" }),
        new(SensorKind.Load,        new[] { "GPU Core", "D3D 3D", "GPU" },                          new[] { "gpu" },
            Exclude: new[] { "Memory" }),
        new(SensorKind.Load,        new[] { "Memory" },                                             new[] { "physical-memory", "/ram" }),
        new(SensorKind.Temperature, new[] { "CPU Package", "Tctl", "Core Average", "CPUZ", "CPU" }, new[] { "cpu" }),
        new(SensorKind.Temperature, new[] { "GPU Core", "GFXZ", "GPU" },                            new[] { "gpu", "gfx" },
            Exclude: new[] { "Memory" }),
    };

    public static void AssignSensors(
        AppConfig config,
        IReadOnlyList<SensorDescriptor> sensors,
        Func<string, bool>? isReadable = null)
    {
        for (var i = 0; i < config.Channels.Count && i < Rules.Length; i++)
        {
            if (!string.IsNullOrEmpty(config.Channels[i].SensorId))
            {
                continue;
            }

            // A sensor that exists but never returns a value is worse than none: it parks
            // a needle at zero and looks like a working channel reading nothing.
            var readable = isReadable is null
                ? sensors
                : sensors.Where(s => isReadable(s.Id)).ToList();

            config.Channels[i].SensorId =
                (Match(readable, Rules[i]) ?? Match(sensors, Rules[i]))?.Id;
        }
    }

    private static SensorDescriptor? Match(IReadOnlyList<SensorDescriptor> sensors, Rule rule)
    {
        var onHintedDevice = sensors
            .Where(s => s.Kind == rule.Kind &&
                        rule.IdHints.Any(hint => s.Id.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // A hint starting with "/" is mandatory: without it, "GPU Memory" would
        // satisfy the memory channel on a machine that reports no system RAM sensor.
        var candidates = onHintedDevice.Count > 0
            ? onHintedDevice
            : rule.IdHints.Any(hint => hint.StartsWith('/'))
                ? new List<SensorDescriptor>()
                : sensors.Where(s => s.Kind == rule.Kind).ToList();

        // A rule's Exclude list rules out sensors by name regardless of how broad its
        // patterns are, e.g. the GPU rules must never bind to a "GPU Memory" sensor
        // even though "GPU" alone is one of their patterns.
        if (rule.Exclude is { Length: > 0 })
        {
            candidates = candidates
                .Where(s => !rule.Exclude.Any(excluded =>
                    s.Name.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

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
