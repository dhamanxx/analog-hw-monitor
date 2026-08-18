namespace AnalogHwMonitor.Core;

/// <summary>
/// The two steps every channel goes through, in one place: a sensor value becomes a
/// deflection percentage, and that becomes a PWM byte for one particular meter. The
/// tick loop and the settings window both need exactly this, and they must not be
/// allowed to disagree about it.
/// </summary>
public static class ChannelPipeline
{
    public static (double Percent, byte Pwm) Evaluate(
        double value, double min, double max, int minPwm, int maxPwm)
    {
        var percent = ChannelMapper.ToPercent(value, min, max);
        return (percent, MeterCalibration.ToPwm(percent, minPwm, maxPwm));
    }
}
