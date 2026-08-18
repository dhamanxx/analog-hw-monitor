using System.Globalization;
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
    private readonly TextBox _simValue = new() { Width = 55, Enabled = false };
    private readonly Button _simApply = new() { Text = "Apply", Width = 55, Enabled = false };
    private readonly Button _saveMin = new() { Text = "Save as min", Width = 90, Enabled = false };
    private readonly Button _saveMax = new() { Text = "Save as max", Width = 90, Enabled = false };
    private readonly Label _calibration = new() { Width = 80, TextAlign = ContentAlignment.MiddleLeft };
    private readonly string _label;

    private int _minPwm;
    private int _maxPwm;

    public ChannelRowControl(ChannelConfig channel, IReadOnlyList<SensorDescriptor> sensors)
    {
        _minPwm = channel.MinPwm;
        _maxPwm = channel.MaxPwm;
        _label = channel.Label;

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
            _simValue.Enabled = _test.Checked;
            _simApply.Enabled = _test.Checked;
            if (!_test.Checked)
            {
                // Turning Test off releases this row — nothing on screen should
                // still claim a channel is under simulation once it is not.
                SimulationReported?.Invoke(this, string.Empty);
            }

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

        _simApply.Click += (_, _) => ApplySimulatedValue();

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
        layout.Controls.Add(_simValue);
        layout.Controls.Add(_simApply);
        layout.Controls.Add(_saveMin);
        layout.Controls.Add(_saveMax);
        layout.Controls.Add(_calibration);

        Controls.Add(layout);

        // AutoSize on both this control and the single FlowLayoutPanel it wraps means
        // the row's own Width/Height are always exactly what its current content
        // needs (e.g. the TrackBar's default height, which exceeds a hand-picked
        // guess) rather than a fixed number that silently clips a control or leaves
        // dead space when the content changes.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    /// <summary>Raised with a PWM value while Test is on, and with null when it goes off.</summary>
    public event EventHandler<byte?>? TestPwmChanged;

    /// <summary>
    /// Raised after Apply (success or failure) with the message the shared readout
    /// below the grid should show, naming this row's channel since that readout is
    /// shared across all five rows. Also raised with an empty string the moment this
    /// row leaves test mode (Test unchecked, whether by hand, StopTest(), or
    /// StopAllTests()), so the shared label never keeps describing a channel that is
    /// no longer under simulation — any row leaving test mode is enough to clear it,
    /// without tracking which row last wrote to it.
    /// </summary>
    public event EventHandler<string>? SimulationReported;

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
            : reading.Value is { } value ? WithUnit(value) : "—";
        _value.ForeColor = reading.SensorMissing ? Color.Firebrick : SystemColors.ControlText;
        _pwm.Text = reading.Pwm.ToString();
    }

    /// <summary>
    /// "34.0 %" rather than a bare "34.0". The unit is what tells a load apart from a
    /// temperature at a glance, both sensor sources fill
    /// <see cref="SensorDescriptor.Unit"/> in, and the spec's settings table shows it.
    /// A sensor that reports no unit — or the "(none)" entry, or a sensor that
    /// Discover() did not return — simply gets no suffix.
    /// </summary>
    private string WithUnit(float value)
    {
        var text = value.ToString("0.0");
        return CurrentUnit() is { } unit ? $"{text} {unit}" : text;
    }

    private void UpdateCalibrationLabel() => _calibration.Text = $"{_minPwm}–{_maxPwm}";

    /// <summary>
    /// The selected sensor's unit, or null for "(none)", an unavailable sensor, or a
    /// sensor that reports no unit at all. Shared by the live Value column and the
    /// simulated-value chain message so neither ever disagrees with the other about
    /// what unit is showing.
    /// </summary>
    private string? CurrentUnit() =>
        _sensor.SelectedItem is SensorDescriptor { Unit: { Length: > 0 } unit } ? unit : null;

    /// <summary>
    /// Runs a typed sensor value through the same chain the tick loop uses, against
    /// this row's CURRENT on-screen Min/Max and calibration — not what was last saved —
    /// so editing a range and re-applying immediately is meaningful. Moving the slider
    /// raises <see cref="TestPwmChanged"/> through its own ValueChanged handler, which
    /// keeps the slider and this input from ever disagreeing about what the meter is
    /// being told. Unparseable input changes nothing and never throws; either way,
    /// <see cref="SimulationReported"/> carries the one line the shared readout below
    /// the grid shows, naming this row's channel since that readout is shared.
    /// </summary>
    private void ApplySimulatedValue()
    {
        if (!TryParseValue(_simValue.Text, out var value))
        {
            SimulationReported?.Invoke(this, $"{_label}: could not read \"{_simValue.Text}\" as a number.");
            return;
        }

        var (percent, pwm) = ChannelPipeline.Evaluate(value, (double)_min.Value, (double)_max.Value, _minPwm, _maxPwm);

        var valueText = value.ToString("0.###", CultureInfo.CurrentCulture);
        var chain = CurrentUnit() is { } unit
            ? $"{valueText} {unit} -> {percent:0.#} % -> PWM {pwm}"
            : $"{valueText} -> {percent:0.#} % -> PWM {pwm}";

        SimulationReported?.Invoke(this, $"{_label}: {chain}");
        _slider.Value = pwm;
    }

    /// <summary>
    /// Accepts both a comma and a full stop as the decimal separator, regardless of the
    /// machine's locale: this project runs on a Slovak-locale machine where sensors
    /// display as "62,1", and a user who types "62.1" out of habit must not be told it
    /// is invalid. The current culture is tried first, so "62,1" always works.
    /// </summary>
    private static bool TryParseValue(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        // The current-culture attempt failed. Retrying under the invariant culture is
        // only safe when the text could not also be a validly grouped integer under
        // the current culture's OWN group separator and group size (e.g. Slovak
        // "1.234" reading as the integer 1234 on a machine where "." is configured as
        // the thousands separator) — reinterpreting a grouped number as a plain
        // decimal under a different culture would silently turn it into a different
        // value. "62.1" never matches that pattern (its second segment is one digit,
        // not a full group), so it still falls through and parses as 62.1.
        if (LooksLikeGroupedInteger(text, CultureInfo.CurrentCulture.NumberFormat))
        {
            value = 0;
            return false;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool LooksLikeGroupedInteger(string text, NumberFormatInfo format)
    {
        var separator = format.NumberGroupSeparator;
        if (string.IsNullOrEmpty(separator) || !text.Contains(separator, StringComparison.Ordinal))
        {
            return false;
        }

        var groupSize = format.NumberGroupSizes.Length > 0 && format.NumberGroupSizes[0] > 0
            ? format.NumberGroupSizes[0]
            : 3;

        var segments = text.Split(separator);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
            {
                return false;
            }

            if (i == 0 ? segment.Length > groupSize : segment.Length != groupSize)
            {
                return false;
            }
        }

        return segments.Length > 1;
    }

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
