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

    /// <summary>
    /// The one shared readout for every row's "Apply" button — there is no per-row
    /// result label, so this names the channel itself (e.g. "CPU Temp: 60 °C -> 50 %
    /// -> PWM 126"). Sits just above <see cref="_status"/> so the two don't compete
    /// for the same line: Save/Detect results on one, calibration simulation on the
    /// other.
    /// </summary>
    private readonly Label _simulation = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    private readonly List<ChannelRowControl> _rows = new();

    private readonly CheckBox _vuMode = new() { Text = "VU meter mode", AutoSize = true };
    private readonly CheckBox _compensateVolume = new() { Text = "Compensate Windows volume", AutoSize = true };
    private readonly Action<bool> _setVuMode;

    // AutoScroll rather than a plain Panel: a plain Panel with Dock = Fill forces every
    // docked child to the visible viewport size, so overflow in either dimension is
    // simply invisible rather than reachable. AutoScroll is what turns "doesn't fit"
    // into a scrollbar instead of a silently missing channel or a silently clipped
    // column — regardless of how the sizing numbers turn out to be wrong on some
    // future machine.
    private readonly FlowLayoutPanel _rowsPanel = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
    };

    /// <summary>
    /// Guards against the toggle firing when the checkbox is being brought in line with
    /// the configuration rather than clicked.
    /// </summary>
    private bool _followingConfig;

    /// <summary>In VU meter mode the tick runs at 25 Hz. The numbers are worth watching
    /// at that rate; the dropdowns and sliders around them are not worth repainting.</summary>
    private const int RepaintIntervalMs = 200;

    private long _lastRepaint;

    public SettingsForm(
        MonitorService monitor,
        SerialMeterLink link,
        ConfigStore store,
        ISensorSource sensors,
        IAppLog log,
        Action<bool> setVuMode)
    {
        _monitor = monitor;
        _link = link;
        _store = store;
        _log = log;
        _setVuMode = setVuMode;

        Text = "Analog Hardware Monitor";
        Icon = AppIcons.Normal;

        // Set explicitly rather than inherited from whatever the template defaults to:
        // every size in this window is a hand-picked pixel value, and AutoScaleMode.Dpi
        // scales all of them together by the monitor's actual DPI, so the layout stays
        // proportionally the same at 125% and 150% instead of drifting relative to
        // whatever the default happened to be.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

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

        top.Controls.Add(_vuMode);
        top.Controls.Add(_compensateVolume);

        _vuMode.Checked = _monitor.Config.VuMode;
        _compensateVolume.Checked = _monitor.Config.VuCompensateVolume;

        // Applied on the click rather than on Save, so it matches the tray menu item and
        // so a user who just wants to watch music does not have to find the Save button.
        _vuMode.CheckedChanged += (_, _) =>
        {
            if (_followingConfig)
            {
                return;
            }

            _setVuMode(_vuMode.Checked);
        };

        _compensateVolume.CheckedChanged += (_, _) =>
            _monitor.Config.VuCompensateVolume = _compensateVolume.Checked;

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        // Each width matches the corresponding row control(s) exactly (e.g. "Sensor"
        // is 260 because the sensor ComboBox itself is 260 wide, not because the
        // dropdown was shrunk) so the only gap between a header caption and its
        // column is FlowLayoutPanel's own per-control margin, not hand-added padding.
        foreach (var (text, width) in new[]
                 {
                     ("Pin", 45), ("Channel", 80), ("Sensor", 260), ("Min", 60), ("Max", 60),
                     ("Value", 90), ("PWM", 45), ("", 55), ("Calibrate", 150), ("Simulate", 110),
                     ("", 180), ("Cal. range", 80),
                 })
        {
            header.Controls.Add(new Label { Text = text, Width = width, TextAlign = ContentAlignment.MiddleLeft });
        }

        BuildChannelRows(available);

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

        Controls.Add(_rowsPanel);
        Controls.Add(header);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(_simulation);
        Controls.Add(_status);

        // The window's size is derived from what its content actually measures,
        // rather than a pixel count that may or may not still be enough after the
        // next change to a row. header.PreferredSize and _rowsPanel.PreferredSize are
        // real layout queries, not a guess baked in at design time. Querying
        // _rowsPanel.PreferredSize directly — rather than summing each row's own
        // PreferredSize — matters: summing the rows alone ignores the margins
        // FlowLayoutPanel puts between them and undercounts the real height by
        // exactly that much, which is the same kind of hand-arithmetic mistake that
        // clipped the fifth row in the first place. The already-fixed chrome bars
        // (top/buttons/status/simulation) keep their known-good heights. ClientSize
        // does the border/title-bar arithmetic that would otherwise have to be
        // hand-guessed.
        var contentWidth = Math.Max(header.PreferredSize.Width, _rowsPanel.PreferredSize.Width);
        var contentHeight = top.Height + header.PreferredSize.Height + _rowsPanel.PreferredSize.Height
            + buttons.Height + _simulation.Height + _status.Height;

        ClientSize = new Size(contentWidth, contentHeight);

        // Clamped to the screen it is about to open on so the window itself can never
        // be wider or taller than what is actually available — a FixedDialog that
        // exceeds the work area centres itself partly off-screen with no way for the
        // user to drag it back. Whatever content the clamp cuts off is still reachable
        // through the rows panel's own scrollbar(s) rather than simply hidden.
        var workArea = Screen.PrimaryScreen?.WorkingArea;
        if (workArea is { } area)
        {
            if (Width > area.Width)
            {
                Width = area.Width;
            }

            if (Height > area.Height)
            {
                Height = area.Height;
            }
        }

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

        // DoEvents() can pump a pending timer tick that reconnects the link — if that
        // just happened, the scan below would probe the port the app now holds and
        // wrongly report silence. Re-check rather than trusting the state from before
        // the pump.
        if (_link.IsConnected && _link.PortName is { } reconnected)
        {
            RefreshPorts();
            Report($"Already connected to the monitor on {reconnected}.");
            return;
        }

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
        _monitor.Config.VuCompensateVolume = _compensateVolume.Checked;

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

    private void BuildChannelRows(IReadOnlyList<SensorDescriptor> available)
    {
        for (var i = 0; i < _monitor.Config.Channels.Count; i++)
        {
            var index = i;
            var row = new ChannelRowControl(_monitor.Config.Channels[i], available);
            row.TestPwmChanged += (_, pwm) => _monitor.SetTestPwm(index, pwm);
            row.SimulationReported += (_, message) => _simulation.Text = message;
            _rows.Add(row);
            _rowsPanel.Controls.Add(row);
        }
    }

    /// <summary>
    /// Rebuilds the rows from the current configuration. VU meter mode changes two
    /// channels' sensor, range and label, and every row read those values in its own
    /// constructor — repainting would show the old ones. Rebuilding rather than
    /// recreating the window keeps the window's position, its selected COM port and its
    /// status line.
    /// </summary>
    public void ReloadChannels(IReadOnlyList<SensorDescriptor> available)
    {
        StopAllTests();

        _rowsPanel.Controls.Clear();
        foreach (var row in _rows)
        {
            row.Dispose();
        }

        _rows.Clear();
        BuildChannelRows(available);

        _followingConfig = true;
        _vuMode.Checked = _monitor.Config.VuMode;
        _followingConfig = false;
    }

    private void OnUpdated(object? sender, IReadOnlyList<ChannelReading> readings)
    {
        if (!Visible)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastRepaint < RepaintIntervalMs)
        {
            return;
        }

        _lastRepaint = now;

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
