using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

/// <summary>
/// Owns the 1 Hz timer and the tray icon. The timer runs on the UI thread, so
/// MonitorService and the settings window never need to marshal anything.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly MonitorService _monitor;
    private readonly SerialMeterLink _link;
    private readonly ConfigStore _store;
    private readonly ISensorSource _sensors;
    private readonly IAppLog _log;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private SettingsForm? _settings;
    private bool _tickFailureReported;

    /// <summary>VU meter mode needs frames far faster than a sensor does; a needle that
    /// moves once a second is not a VU meter. The WinForms timer's ~15.6 ms granularity
    /// makes this land near 21 Hz in practice, which a 300 ms integration and a meter's
    /// own mechanical inertia make indistinguishable from 25.</summary>
    private const int VuIntervalMs = 40;

    private const int SensorIntervalMs = 1000;

    private readonly ToolStripMenuItem _vuModeItem;
    private Icon? _shownIcon;
    private string? _shownText;

    public TrayApplicationContext(
        MonitorService monitor,
        SerialMeterLink link,
        ConfigStore store,
        ISensorSource sensors,
        IAppLog log)
    {
        _monitor = monitor;
        _link = link;
        _store = store;
        _sensors = sensors;
        _log = log;

        var menu = new ContextMenuStrip();

        _vuModeItem = new ToolStripMenuItem("VU meter", null, (_, _) => SetVuMode(!IsVuMode))
        {
            Checked = monitor.Config.VuMode,
        };

        menu.Items.Add(_vuModeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = AppIcons.Normal,
            Text = "Analog Hardware Monitor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = monitor.Config.VuMode ? VuIntervalMs : SensorIntervalMs,
        };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        // Dispose writes "Stopped."; without this line a clean run leaves log.txt with
        // one entry and no way to tell when the session it ended actually began.
        _log.Write($"Started on {_link.PortName ?? "no configured port"}.");
    }

    public bool IsVuMode => _monitor.Config.VuMode;

    /// <summary>
    /// Swaps the two channel profiles, follows with the tick rate, saves, and rebuilds
    /// the settings window's rows if one is open. The rows are built from the
    /// configuration, and VU mode changes two channels' sensor, range and label
    /// underneath them.
    /// </summary>
    public void SetVuMode(bool enabled)
    {
        VuModeSwitch.Set(_monitor.Config, enabled);

        _vuModeItem.Checked = IsVuMode;
        _timer.Interval = IsVuMode ? VuIntervalMs : SensorIntervalMs;
        _log.Write(IsVuMode ? "VU meter mode on." : "VU meter mode off.");

        // Saving is a convenience, not a precondition — the same rule Program.cs and the
        // settings window already follow for a read-only install directory.
        try
        {
            _store.Save(_monitor.Config);
        }
        catch (Exception ex)
        {
            _log.Write($"Could not save the VU meter setting: {ex.Message}");
        }

        if (_settings is { IsDisposed: false } settings)
        {
            settings.ReloadChannels(_sensors.Discover());
        }
    }

    public void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_monitor, _link, _store, _sensors, _log, SetVuMode);
        }

        _settings.Show();
        _settings.BringToFront();
    }

    private void OnTick()
    {
        // MonitorService.Tick() reaches into the LibreHardwareMonitor driver and the
        // serial link, both of which can fail at runtime, not only at startup. This
        // handler fires every second for the life of the app, so one bad tick must
        // not take the process down — the next tick gets another chance.
        try
        {
            _monitor.Tick();
        }
        catch (Exception ex)
        {
            if (!_tickFailureReported)
            {
                _log.Write($"Tick failed: {ex.Message}");
                _tickFailureReported = true;
            }

            ShowTrayState(
                AppIcons.Warning,
                Truncate($"Analog Hardware Monitor — tick failed: {ex.Message}"));
            return;
        }

        if (_tickFailureReported)
        {
            _log.Write("Tick recovered.");
            _tickFailureReported = false;
        }

        // A warning overlay plus the reason in the tooltip: the needles say the link is
        // dead, the tray says why. Assigned only on a change — at 25 Hz, handing
        // NotifyIcon the same icon and the same string forty times a second is pure
        // waste and makes the icon flicker on some shells.
        ShowTrayState(
            _link.IsConnected ? AppIcons.Normal : AppIcons.Warning,
            _link.IsConnected
                ? $"Analog Hardware Monitor — {_link.PortName}"
                : Truncate($"Analog Hardware Monitor — {_link.LastError ?? "disconnected"}"));
    }

    /// <summary>
    /// AppIcons.Normal and AppIcons.Warning are process-wide singletons, so reference
    /// equality is the right comparison here.
    /// </summary>
    private void ShowTrayState(Icon icon, string text)
    {
        if (!ReferenceEquals(_shownIcon, icon))
        {
            _icon.Icon = icon;
            _shownIcon = icon;
        }

        if (_shownText != text)
        {
            _icon.Text = text;
            _shownText = text;
        }
    }

    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    private static string Truncate(string text) =>
        text.Length <= 63 ? text : text[..60] + "...";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _monitor.Dispose();
            _log.Write("Stopped.");
        }

        base.Dispose(disposing);
    }
}
