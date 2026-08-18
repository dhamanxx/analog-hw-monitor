namespace AnalogHwMonitor.Core;

/// <summary>The wire format shared with the Arduino sketch.</summary>
public static class FrameCodec
{
    public const int ChannelCount = 5;
    public const int BaudRate = 115200;

    /// <summary>Printed by the sketch on boot so we can tell our device from a printer.</summary>
    public const string Banner = "AHM1";

    public static string Encode(IReadOnlyList<byte> pwmValues)
    {
        if (pwmValues.Count != ChannelCount)
        {
            throw new ArgumentException(
                $"Expected {ChannelCount} PWM values, got {pwmValues.Count}.", nameof(pwmValues));
        }

        return "V:" + string.Join(',', pwmValues) + "\n";
    }
}
