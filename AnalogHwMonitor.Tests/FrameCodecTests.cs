using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_ProducesCommaSeparatedFrameWithLineFeed()
    {
        var frame = FrameCodec.Encode(new byte[] { 128, 200, 64, 30, 255 });

        Assert.Equal("V:128,200,64,30,255\n", frame);
    }

    [Fact]
    public void Encode_ProducesZeroFrameForZeroedChannels()
    {
        var frame = FrameCodec.Encode(new byte[] { 0, 0, 0, 0, 0 });

        Assert.Equal("V:0,0,0,0,0\n", frame);
    }

    [Fact]
    public void Encode_RejectsWrongChannelCount()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ProtocolConstants_MatchTheSketch()
    {
        Assert.Equal(5, FrameCodec.ChannelCount);
        Assert.Equal("AHM1", FrameCodec.Banner);
        Assert.Equal(115200, FrameCodec.BaudRate);
    }
}
