namespace AnalogHwMonitor.Core;

/// <summary>
/// One tick of the whole system: refresh the hardware, turn five readings into five
/// PWM bytes, and push one frame down the link. Owns no timer and no threads —
/// the caller decides when a tick happens.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly ISensorSource _sensors;
    private readonly IMeterLink _link;
    private readonly IAppLog _log;
    private readonly byte?[] _testPwm = new byte?[FrameCodec.ChannelCount];
    private readonly bool[] _missingReported = new bool[FrameCodec.ChannelCount];

    public MonitorService(ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log)
    {
        _sensors = sensors;
        _link = link;
        _log = log;
        Config = config;
    }

    /// <summary>Swapped wholesale when the settings window saves.</summary>
    public AppConfig Config { get; set; }

    public event EventHandler<IReadOnlyList<ChannelReading>>? Updated;

    /// <summary>Pins a channel to a raw PWM value for calibration; null releases it.</summary>
    public void SetTestPwm(int channelIndex, byte? pwm) => _testPwm[channelIndex] = pwm;

    public void Tick()
    {
        _sensors.Refresh();

        var pwmValues = new byte[FrameCodec.ChannelCount];
        var readings = new List<ChannelReading>(FrameCodec.ChannelCount);

        for (var i = 0; i < FrameCodec.ChannelCount; i++)
        {
            var channel = Config.Channels[i];

            if (_testPwm[i] is { } testPwm)
            {
                pwmValues[i] = testPwm;
                readings.Add(new ChannelReading(i, channel.Label, null, 0, testPwm, false, true));
                continue;
            }

            var value = string.IsNullOrEmpty(channel.SensorId) ? null : _sensors.Read(channel.SensorId);
            var missing = value is null;

            if (missing && !_missingReported[i])
            {
                _log.Write($"Channel {i} ({channel.Label}) has no readable sensor: {channel.SensorId ?? "<none>"}");
                _missingReported[i] = true;
            }
            else if (!missing)
            {
                _missingReported[i] = false;
            }

            var percent = missing ? 0 : ChannelMapper.ToPercent(value!.Value, channel.Min, channel.Max);

            // A missing sensor parks the needle below its calibrated zero, so a dead
            // channel never looks like a healthy idle one.
            pwmValues[i] = missing ? (byte)0 : MeterCalibration.ToPwm(percent, channel.MinPwm, channel.MaxPwm);

            readings.Add(new ChannelReading(i, channel.Label, value, percent, pwmValues[i], missing, false));
        }

        _link.Send(FrameCodec.Encode(pwmValues));
        Updated?.Invoke(this, readings);
    }

    public void Dispose()
    {
        _link.Dispose();
        _sensors.Dispose();
    }
}
