using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

public sealed class SettingsForm : Form
{
    private readonly MonitorService _monitor;
    private readonly SerialMeterLink _link;
    private readonly ConfigStore _store;
    private readonly StartupRegistration _startup = new();
    private readonly ComboBox _ports = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _startWithWindows = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<ChannelRowControl> _rows = new();

    public SettingsForm(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors)
    {
        _monitor = monitor;
        _link = link;
        _store = store;

        Text = "Analog Hardware Monitor";
        Width = 1100;
        Height = 320;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var available = sensors.Discover();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        top.Controls.Add(new Label { Text = "COM port", Width = 65, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_ports);

        var detect = new Button { Text = "Detect", Width = 70 };
        detect.Click += (_, _) => Detect();
        top.Controls.Add(detect);
        top.Controls.Add(_startWithWindows);

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 24, WrapContents = false };
        foreach (var (text, width) in new[]
                 {
                     ("Pin", 45), ("Channel", 80), ("Sensor", 266), ("Min", 63), ("Max", 63),
                     ("Value", 93), ("PWM", 48), ("", 58), ("Calibrate", 153), ("", 186), ("Cal. range", 80),
                 })
        {
            header.Controls.Add(new Label { Text = text, Width = width, TextAlign = ContentAlignment.MiddleLeft });
        }

        var rows = new Panel { Dock = DockStyle.Fill };

        // Added in reverse because Dock = Top stacks the last-added control on top.
        for (var i = _monitor.Config.Channels.Count - 1; i >= 0; i--)
        {
            var index = i;
            var row = new ChannelRowControl(_monitor.Config.Channels[i], available);
            row.TestPwmChanged += (_, pwm) => _monitor.SetTestPwm(index, pwm);
            _rows.Insert(0, row);
            rows.Controls.Add(row);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
        };

        var close = new Button { Text = "Close", Width = 80 };
        close.Click += (_, _) => Hide();

        var save = new Button { Text = "Save", Width = 80 };
        save.Click += (_, _) => Save();

        buttons.Controls.Add(close);
        buttons.Controls.Add(save);

        Controls.Add(rows);
        Controls.Add(header);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(_status);

        RefreshPorts();
        _startWithWindows.Checked = _startup.IsEnabled();

        _monitor.Updated += OnUpdated;
        FormClosing += (_, e) =>
        {
            // The tray owns the lifetime; closing the window just hides it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void RefreshPorts()
    {
        _ports.Items.Clear();
        foreach (var name in new SerialPortFactory().GetPortNames())
        {
            _ports.Items.Add(name);
        }

        if (_link.PortName is { } current && _ports.Items.Contains(current))
        {
            _ports.SelectedItem = current;
        }
    }

    private void Detect()
    {
        _status.Text = "Scanning ports…";
        Application.DoEvents();

        var found = PortDetector.FindMonitorPort(new SerialPortFactory(), NullLog.Instance);
        RefreshPorts();

        if (found is null)
        {
            _status.Text = "No device answered with the AHM1 banner.";
            return;
        }

        _ports.SelectedItem = found;
        _status.Text = $"Found the monitor on {found}.";
    }

    private void Save()
    {
        foreach (var (row, index) in _rows.Select((r, i) => (r, i)))
        {
            row.ApplyTo(_monitor.Config.Channels[index]);
            _monitor.SetTestPwm(index, null);
        }

        _monitor.Config.ComPort = _ports.SelectedItem as string;
        _monitor.Config.StartWithWindows = _startWithWindows.Checked;

        _link.PortName = _monitor.Config.ComPort;
        _startup.SetEnabled(_startWithWindows.Checked, Application.ExecutablePath);
        _store.Save(_monitor.Config);

        _status.Text = $"Saved to {_store.Path}";
    }

    private void OnUpdated(object? sender, IReadOnlyList<ChannelReading> readings)
    {
        if (!Visible)
        {
            return;
        }

        foreach (var reading in readings)
        {
            _rows[reading.Index].ShowReading(reading);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Updated -= OnUpdated;
        }

        base.Dispose(disposing);
    }
}
