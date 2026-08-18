using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class ChannelMapperTests
{
    [Theory]
    [InlineData(0, 0, 100, 0)]        // bottom of a load channel
    [InlineData(50, 0, 100, 50)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(30, 30, 90, 0)]       // bottom of a temperature channel
    [InlineData(60, 30, 90, 50)]
    [InlineData(90, 30, 90, 100)]
    [InlineData(20, 30, 90, 0)]       // below min clamps to zero
    [InlineData(120, 30, 90, 100)]    // above max clamps to full scale
    [InlineData(45, 90, 30, 75)]      // inverted range reverses the needle
    [InlineData(50, 50, 50, 0)]       // degenerate range never divides by zero
    public void ToPercent_MapsAndClamps(double value, double min, double max, double expected)
    {
        Assert.Equal(expected, ChannelMapper.ToPercent(value, min, max), 3);
    }
}
