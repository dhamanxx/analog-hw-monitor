namespace AnalogHwMonitor.Core;

/// <summary>Converts a raw sensor reading into needle deflection, 0-100 %.</summary>
public static class ChannelMapper
{
    public static double ToPercent(double value, double min, double max)
    {
        if (min == max)
        {
            return 0;
        }

        var percent = (value - min) / (max - min) * 100.0;
        return Math.Clamp(percent, 0.0, 100.0);
    }
}
