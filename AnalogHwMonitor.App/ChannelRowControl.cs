using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

/// <summary>
/// One meter's row: which sensor it shows, how it is scaled, what it currently
/// reads, and a test slider for calibration.
/// </summary>
public sealed class ChannelRowControl : UserControl
{
    private readonly ComboBox _sensor = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _min = new() { Width = 60, Minimum = -273, Maximum = 1_000_000_000, DecimalPlaces = 0 };
    private readonly NumericUpDown _max = new() { Width = 60, Minimum = -273, Maximum = 1_000_000_000, DecimalPlaces = 0 };
    private readonly Label _value = new() { Width = 90, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _pwm = new() { Width = 45, TextAlign = ContentAlignment.MiddleRight };
    private readonly CheckBox _test = new() { Text = "Test", Width = 55 };
    private readonly TrackBar _slider = new() { Width = 150, Minimum = 0, Maximum = 255, TickFrequency = 32, Enabled = false };
    private readonly Button _saveMin = new() { Text = "Save as min", Width = 90, Enabled = false };
    private readonly Button _saveMax = new() { Text = "Save as max", Width = 90, Enabled = false };
    private readonly Label _calibration = new() { Width = 80, TextAlign = ContentAlignment.MiddleLeft };

    private int _minPwm;
    private int _maxPwm;

    public ChannelRowControl(ChannelConfig channel, IReadOnlyList<SensorDescriptor> sensors)
    {
        _minPwm = channel.MinPwm;
        _maxPwm = channel.MaxPwm;

        _sensor.Items.Add("(none)");
        foreach (var sensor in sensors)
        {
            _sensor.Items.Add(sensor);
        }

        _sensor.DisplayMember = nameof(SensorDescriptor.Display);
        _sensor.SelectedIndex = 0;

        var matched = false;
        for (var i = 0; i < sensors.Count; i++)
        {
            if (sensors[i].Id == channel.SensorId)
            {
                _sensor.SelectedIndex = i + 1;
                matched = true;
                break;
            }
        }

        if (!matched && channel.SensorId is { } unavailableId)
        {
            // The saved sensor wasn't found by Discover() this time (unplugged
            // device, driver hasn't enumerated it yet, ...). Keep it selectable
            // and intact so an unrelated Save doesn't silently erase it.
            var missing = new MissingSensor(unavailableId);
            _sensor.Items.Add(missing);
            _sensor.SelectedItem = missing;
        }

        _min.Value = (decimal)channel.Min;
        _max.Value = (decimal)channel.Max;
        UpdateCalibrationLabel();

        _test.CheckedChanged += (_, _) =>
        {
            _slider.Enabled = _test.Checked;
            _saveMin.Enabled = _test.Checked;
            _saveMax.Enabled = _test.Checked;
            TestPwmChanged?.Invoke(this, _test.Checked ? (byte)_slider.Value : null);
        };

        _slider.ValueChanged += (_, _) =>
        {
            if (_test.Checked)
            {
                TestPwmChanged?.Invoke(this, (byte)_slider.Value);
            }
        };

        _saveMin.Click += (_, _) =>
        {
            _minPwm = _slider.Value;
            UpdateCalibrationLabel();
        };

        _saveMax.Click += (_, _) =>
        {
            _maxPwm = _slider.Value;
            UpdateCalibrationLabel();
        };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };

        layout.Controls.Add(new Label { Text = $"Pin {channel.Pin}", Width = 45, TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(new Label { Text = channel.Label, Width = 80, TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(_sensor);
        layout.Controls.Add(_min);
        layout.Controls.Add(_max);
        layout.Controls.Add(_value);
        layout.Controls.Add(_pwm);
        layout.Controls.Add(_test);
        layout.Controls.Add(_slider);
        layout.Controls.Add(_saveMin);
        layout.Controls.Add(_saveMax);
        layout.Controls.Add(_calibration);

        Controls.Add(layout);
        Height = 40;
        Dock = DockStyle.Top;
    }

    /// <summary>Raised with a PWM value while Test is on, and with null when it goes off.</summary>
    public event EventHandler<byte?>? TestPwmChanged;

    public void ApplyTo(ChannelConfig channel)
    {
        channel.SensorId = _sensor.SelectedItem switch
        {
            SensorDescriptor descriptor => descriptor.Id,
            MissingSensor missing => missing.Id,
            _ => null,
        };
        channel.Min = (double)_min.Value;
        channel.Max = (double)_max.Value;
        channel.MinPwm = _minPwm;
        channel.MaxPwm = _maxPwm;
    }

    /// <summary>
    /// Returns this row to normal operation: unticks Test, which the existing
    /// CheckedChanged handler uses to disable the slider and calibration buttons
    /// and to raise <see cref="TestPwmChanged"/>(null) so the model releases the
    /// channel too. Safe to call when the row is already out of test mode.
    /// </summary>
    public void StopTest() => _test.Checked = false;

    public void ShowReading(ChannelReading reading)
    {
        _value.Text = reading.TestMode
            ? "test"
            : reading.Value is { } value ? value.ToString("0.0") : "—";
        _value.ForeColor = reading.SensorMissing ? Color.Firebrick : SystemColors.ControlText;
        _pwm.Text = reading.Pwm.ToString();
    }

    private void UpdateCalibrationLabel() => _calibration.Text = $"{_minPwm}–{_maxPwm}";

    /// <summary>
    /// Stands in for a saved <see cref="ChannelConfig.SensorId"/> that Discover()
    /// didn't return this time, so ApplyTo can write it back unchanged instead of
    /// treating the row as unassigned.
    /// </summary>
    private sealed record MissingSensor(string Id)
    {
        public string Display => $"{Id} (currently unavailable)";
    }
}
