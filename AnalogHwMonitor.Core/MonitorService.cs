namespace AnalogHwMonitor.Core;

/// <summary>
/// One tick of the whole system: turn five readings into five PWM bytes and push one
/// frame down the link. Owns no timer and no threads — the caller decides when a tick
/// happens.
///
/// Does not refresh. Hardware is refreshed by <see cref="SensorRefreshLoop"/> on its own
/// task once a second, because Refresh() is 99 ms and stalled the VU meter needle on the
/// UI thread. Tick() therefore reads whatever the last refresh left there — which is
/// exactly fine for temperatures and load, and the audio level is live anyway, since it
/// is computed on the capture thread.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly ISensorSource _sensors;
    private readonly IMeterLink _link;
    private readonly IAppLog _log;
    private readonly byte?[] _testPwm = new byte?[FrameCodec.ChannelCount];
    private readonly bool[] _missingReported = new bool[FrameCodec.ChannelCount];
    private readonly TimeProvider _time;

    // One per VU channel, indexed the same as Config.Channels. Non-VU channels never get
    // one: outside VU mode the tick is 1 Hz against a 578 ms ring period, and the sensors
    // that live there — temperatures, memory — do not step in the first place.
    private readonly NeedleCompensator?[] _compensators = new NeedleCompensator?[FrameCodec.ChannelCount];

    private AppConfig _config = null!;
    private DateTimeOffset _lastTick;
    private bool _vuModeLastTick;

    public MonitorService(
        ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log, TimeProvider? time = null)
    {
        _sensors = sensors;
        _link = link;
        _log = log;
        _time = time ?? TimeProvider.System;
        _lastTick = _time.GetUtcNow();
        Config = config;

        foreach (var index in VuModeSwitch.VuChannels)
        {
            _compensators[index] = new NeedleCompensator();
        }
    }

    /// <summary>Must be non-null and hold exactly <see cref="FrameCodec.ChannelCount"/>
    /// channels — <see cref="Tick"/> relies on that invariant without checking it.
    ///
    /// The setter accepts a wholesale swap, but nothing in the application performs one:
    /// the tray and the settings window both mutate the instance the getter hands them.
    /// Anything that has to notice a change therefore cannot live here alone.</summary>
    public AppConfig Config
    {
        get => _config;
        set
        {
            if (value is null)
            {
                throw new ArgumentException("Config cannot be null.", nameof(value));
            }

            if (value.Channels is null || value.Channels.Count != FrameCodec.ChannelCount)
            {
                throw new ArgumentException(
                    $"Config must have exactly {FrameCodec.ChannelCount} channels, but had {value.Channels?.Count ?? 0}.",
                    nameof(value));
            }

            _config = value;

            // A wholesale swap may have turned VU mode on or off, or moved a channel's
            // range. Either way the two-tick history the compensators hold describes a
            // chain that no longer exists.
            //
            // This covers an assignment only. Every caller in the application mutates the
            // object this property already returns instead of assigning a new one, so the
            // VU mode check in Tick, not this loop, is what catches a mode change today.
            _vuModeLastTick = value.VuMode;

            foreach (var compensator in _compensators)
            {
                compensator?.Reset();
            }
        }
    }

    public event EventHandler<IReadOnlyList<ChannelReading>>? Updated;

    /// <summary>Pins a channel to a raw PWM value for calibration; null releases it.</summary>
    public void SetTestPwm(int channelIndex, byte? pwm) => _testPwm[channelIndex] = pwm;

    public void Tick()
    {
        var pwmValues = new byte[FrameCodec.ChannelCount];
        var readings = new List<ChannelReading>(FrameCodec.ChannelCount);

        var now = _time.GetUtcNow();
        var elapsed = now - _lastTick;
        _lastTick = now;

        // Leaving or entering VU mode retasks these two channels from a load sensor to an
        // audio level and back, and only the VU half of that is compensated. Whatever the
        // compensators were holding when the mode last changed describes the wrong signal,
        // so it goes rather than leading the first tick of the new one.
        if (Config.VuMode != _vuModeLastTick)
        {
            _vuModeLastTick = Config.VuMode;

            foreach (var compensator in _compensators)
            {
                compensator?.Reset();
            }
        }

        for (var i = 0; i < FrameCodec.ChannelCount; i++)
        {
            var channel = Config.Channels[i];

            if (_testPwm[i] is { } testPwm)
            {
                pwmValues[i] = testPwm;
                readings.Add(new ChannelReading(i, channel.Label, null, 0, testPwm, false, true));

                // A channel held at a raw PWM value for calibration is not being driven by
                // its sensor, so whatever history the compensator holds describes a level
                // from before the slider was touched.
                _compensators[i]?.Reset();
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

            // A missing sensor parks the needle below its calibrated zero, so a dead
            // channel never looks like a healthy idle one.
            double percent;

            // What the frame carries and what the reading reports are two variables
            // because they can disagree: obviously mid-transient on a compensated channel,
            // but also at rest — the biquad's fixed point settles about one ULP low, and a
            // level sitting on an exact PWM rounding midpoint (70 % of 0..255 is 178.5)
            // falls to the byte below what the reading reports. That is at most one step of
            // 255, never a drift. See Tick_CompensatedChannelsSettleOnTheUncompensatedValue.
            byte reportedPwm;

            if (missing)
            {
                percent = 0;
                pwmValues[i] = 0;
                reportedPwm = 0;
                _compensators[i]?.Reset();
            }
            else
            {
                (percent, pwmValues[i]) = ChannelPipeline.Evaluate(
                    value!.Value, channel.Min, channel.Max, channel.MinPwm, channel.MaxPwm);
                reportedPwm = pwmValues[i];

                // The compensated command goes to the meter; `percent` and reportedPwm
                // stay uncompensated and are what ChannelReading carries below. The
                // compensator's DC gain is exactly 1, so at rest the two agree to within
                // one PWM step of 255 — see
                // Tick_CompensatedChannelsSettleOnTheUncompensatedValue for the one-ULP
                // rounding case that keeps them from being byte-identical. They part
                // further during a transient, where the settings window's number would be
                // unreadable anyway and calibration does not use it.
                if (Config.VuMode && _compensators[i] is { } compensator)
                {
                    if (double.IsFinite(percent))
                    {
                        var shaped = Math.Clamp(compensator.Advance(percent, elapsed), 0.0, 100.0);
                        pwmValues[i] = MeterCalibration.ToPwm(shaped, channel.MinPwm, channel.MaxPwm);
                    }
                    else
                    {
                        // percent is only non-finite here if the sensor answered with NaN or
                        // infinity — unreachable from the audio source, which floors its own
                        // output, but reachable from a channel re-pointed at another sensor
                        // from the settings window while VU mode stays on. `missing` is false
                        // (the sensor did answer), so the branch above never fires for this.
                        // Feeding a non-finite value into Advance would poison the biquad's
                        // doubles permanently: every later tick would return NaN too, which
                        // ToPwm turns into 0, and nothing would ever reset it. Resetting here
                        // instead means the compensator reprimes clean on the next finite
                        // tick, and this tick's frame is left exactly as ChannelPipeline left
                        // it above — the same as an uncompensated channel already behaves.
                        compensator.Reset();
                    }
                }
            }

            readings.Add(new ChannelReading(i, channel.Label, value, percent, reportedPwm, missing, false));
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
