using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class MeterCalibrationTests
{
    [Theory]
    [InlineData(0, 0, 255, 0)]
    [InlineData(100, 0, 255, 255)]
    [InlineData(50, 0, 255, 128)]     // 127.5 rounds away from zero
    [InlineData(0, 12, 240, 12)]      // calibrated meter starts above zero
    [InlineData(100, 12, 240, 240)]   // ...and stops short of full PWM
    [InlineData(50, 12, 240, 126)]
    [InlineData(100, 240, 12, 12)]    // inverted calibration reverses the needle
    [InlineData(150, 0, 255, 255)]    // percent above 100 clamps
    [InlineData(-10, 0, 255, 0)]      // percent below 0 clamps
    public void ToPwm_InterpolatesBetweenCalibrationPoints(double percent, int minPwm, int maxPwm, byte expected)
    {
        Assert.Equal(expected, MeterCalibration.ToPwm(percent, minPwm, maxPwm));
    }
}
