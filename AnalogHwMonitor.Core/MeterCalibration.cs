namespace AnalogHwMonitor.Core;

/// <summary>
/// Converts needle deflection (0-100 %) into a PWM byte using the two calibration
/// points measured for one physical meter.
/// </summary>
public static class MeterCalibration
{
    public static byte ToPwm(double percent, int minPwm, int maxPwm)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        var raw = minPwm + (maxPwm - minPwm) * clamped / 100.0;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(rounded, 0, 255);
    }
}
