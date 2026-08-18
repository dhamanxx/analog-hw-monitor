using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class ChannelPipelineTests
{
    [Theory]
    // A temperature channel ranged 30-90 on a meter calibrated 12-240.
    [InlineData(30, 30, 90, 12, 240, 0, 12)]
    [InlineData(60, 30, 90, 12, 240, 50, 126)]
    [InlineData(90, 30, 90, 12, 240, 100, 240)]
    // Below and above the range clamp, which is how a user discovers a bad range.
    [InlineData(20, 30, 90, 12, 240, 0, 12)]
    [InlineData(120, 30, 90, 12, 240, 100, 240)]
    // A load channel on an uncalibrated meter.
    [InlineData(75, 0, 100, 0, 255, 75, 191)]
    // Inverted range and inverted calibration both stay legal.
    [InlineData(45, 90, 30, 0, 255, 75, 191)]
    [InlineData(100, 0, 100, 240, 12, 100, 12)]
    // Degenerate range yields zero deflection, not a divide by zero.
    [InlineData(50, 50, 50, 0, 255, 0, 0)]
    public void Evaluate_RunsTheWholeChain(
        double value, double min, double max, int minPwm, int maxPwm,
        double expectedPercent, byte expectedPwm)
    {
        var (percent, pwm) = ChannelPipeline.Evaluate(value, min, max, minPwm, maxPwm);

        Assert.Equal(expectedPercent, percent, 3);
        Assert.Equal(expectedPwm, pwm);
    }

    [Fact]
    public void Evaluate_MatchesTheTwoStepsItReplaces()
    {
        var percent = ChannelMapper.ToPercent(63.5, 30, 90);
        var pwm = MeterCalibration.ToPwm(percent, 12, 240);

        var result = ChannelPipeline.Evaluate(63.5, 30, 90, 12, 240);

        Assert.Equal(percent, result.Percent, 6);
        Assert.Equal(pwm, result.Pwm);
    }
}
