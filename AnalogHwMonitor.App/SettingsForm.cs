using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

public sealed class SettingsForm : Form
{
    private readonly MonitorService _monitor;
    private readonly SerialMeterLink _link;
    private readonly ConfigStore _store;
    private readonly IAppLog _log;
    private readonly StartupRegistration _startup = new();
    private readonly ComboBox _ports = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _startWithWindows = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<ChannelRowControl> _rows = new();

    public SettingsForm(
        MonitorService monitor,
        SerialMeterLink link,
        ConfigStore store,
        ISensorSource sensors,
        IAppLog log)
    {
        _monitor = monitor;
        _link = link;
        _store = store;
        _log = log;

        Text = "Analog Hardware Monitor";
        Icon = AppIcons.Normal;
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
        _startWithWindows.Checked = ReadStartupRegistration();

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

        // Any channel left pinned by the Test slider must be released the moment
        // this window stops being visible — whether that's the X (via the
        // FormClosing handler above calling Hide()), the Close button calling
        // Hide() directly, or anything else that hides the form. Otherwise a
        // needle stays parked at an arbitrary value with no UI left open to fix it.
        VisibleChanged += (_, _) =>
        {
            if (!Visible)
            {
                StopAllTests();
            }
        };
    }

    private void RefreshPorts()
    {
        var names = ListPortNames();

        _ports.Items.Clear();
        foreach (var name in names)
        {
            _ports.Items.Add(name);
        }

        SelectPort(_link.PortName);
    }

    /// <summary>
    /// Enumerating the serial ports goes through the Windows driver stack and can
    /// throw. This window was opened by a tray-menu click, so a failure belongs in the
    /// status bar rather than in an unhandled exception.
    /// </summary>
    private IReadOnlyList<string> ListPortNames()
    {
        try
        {
            return new SerialPortFactory().GetPortNames();
        }
        catch (Exception ex)
        {
            Report($"Could not list the serial ports: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Selects a port, adding a placeholder entry first when that port is not currently
    /// enumerated. The combo is a DropDownList: without the placeholder a
    /// configured-but-absent port leaves nothing selected, and Save then writes null
    /// over a perfectly good setting. Same defect, same cure as
    /// <c>ChannelRowControl.MissingSensor</c>. Nothing here overrides a deliberate
    /// choice — picking another port from the list still changes it.
    /// </summary>
    private void SelectPort(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        if (_ports.Items.Contains(portName))
        {
            _ports.SelectedItem = portName;
            return;
        }

        var missing = new MissingPort(portName);
        _ports.Items.Add(missing);
        _ports.SelectedItem = missing;
    }

    private string? SelectedPortName() => _ports.SelectedItem switch
    {
        string name => name,
        MissingPort missing => missing.Name,
        _ => null,
    };

    /// <summary>
    /// Reading HKCU can be denied by policy. A settings window that cannot answer
    /// "does this start with Windows?" still has every other job to do.
    /// </summary>
    private bool ReadStartupRegistration()
    {
        try
        {
            return _startup.IsEnabled();
        }
        catch (Exception ex)
        {
            Report($"Could not read the startup registration: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shows a message in the status bar and records it in log.txt.</summary>
    private void Report(string message)
    {
        _status.Text = message;
        _log.Write(message);
    }

    private void Detect()
    {
        // The link keeps the working port open for the life of the process, so probing
        // it would only ever get UnauthorizedAccessException back — Detect used to
        // report "no device" for the board sitting right there. A connected link has
        // already proved which port answers AHM1, so that port *is* the answer.
        // RefreshPorts() selects it, and none of this takes measurable time: the 1 Hz
        // tick is not delayed, no frame is missed, and no needle moves.
        if (_link.IsConnected && _link.PortName is { } connected)
        {
            RefreshPorts();
            Report($"Already connected to the monitor on {connected}.");
            return;
        }

        // Not connected means nothing is being sent, so the Arduino watchdog pulled the
        // needles to zero at least three seconds ago. A slow scan here cannot make them
        // fall — there is nothing left to fall from — which is why blocking the
        // UI thread for its duration is acceptable in this branch and only this one.
        _status.Text = "Scanning ports…";
        Cursor = Cursors.WaitCursor;
        Application.DoEvents();

        string? found;
        try
        {
            found = PortDetector.FindMonitorPort(new SerialPortFactory(), _log);
        }
        catch (Exception ex)
        {
            Report($"Port scan failed: {ex.Message}");
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        RefreshPorts();

        if (found is null)
        {
            Report("No device answered with the AHM1 banner.");
            return;
        }

        SelectPort(found);
        Report($"Found the monitor on {found}.");
    }

    private void Save()
    {
        foreach (var (row, index) in _rows.Select((r, i) => (r, i)))
        {
            row.ApplyTo(_monitor.Config.Channels[index]);
            row.StopTest();
            _monitor.SetTestPwm(index, null);
        }

        var previousPort = _monitor.Config.ComPort;
        _monitor.Config.ComPort = SelectedPortName();
        _monitor.Config.StartWithWindows = _startWithWindows.Checked;

        if (previousPort != _monitor.Config.ComPort)
        {
            _log.Write(
                $"COM port changed from {previousPort ?? "<none>"} to {_monitor.Config.ComPort ?? "<none>"}.");
        }

        _link.PortName = _monitor.Config.ComPort;

        // These are the only writes a user can trigger by hand, and both fail for
        // reasons that say nothing about whether the application is still usable: the
        // Run key can be locked down by policy, the install directory can be read-only.
        // Program.cs already refuses to die over a failed config write — the same
        // rule holds here, with the reason in the status bar, not in a stack trace.
        var problems = new List<string>();

        try
        {
            _startup.SetEnabled(_startWithWindows.Checked, Application.ExecutablePath);
            _log.Write(_startWithWindows.Checked
                ? "Registered to start with Windows."
                : "Removed the start-with-Windows registration.");
        }
        catch (Exception ex)
        {
            problems.Add($"startup registration ({ex.Message})");
            _log.Write($"Could not change the startup registration: {ex.Message}");
        }

        try
        {
            _store.Save(_monitor.Config);
            _log.Write($"Settings saved to {_store.Path}.");
        }
        catch (Exception ex)
        {
            problems.Add($"config.json ({ex.Message})");
            _log.Write($"Could not save the configuration: {ex.Message}");
        }

        _status.Text = problems.Count == 0
            ? $"Saved to {_store.Path}"
            : "Not fully saved — " + string.Join("; ", problems);
    }

    private void StopAllTests()
    {
        foreach (var row in _rows)
        {
            row.StopTest();
        }
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

    /// <summary>
    /// Stands in for a configured COM port that GetPortNames() did not return this time
    /// (board unplugged, driver not loaded yet), so Save writes it back unchanged
    /// instead of erasing it.
    /// </summary>
    private sealed record MissingPort(string Name)
    {
        public override string ToString() => $"{Name} (currently unavailable)";
    }
}
