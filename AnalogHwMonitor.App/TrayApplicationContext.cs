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
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Analog Hardware Monitor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_monitor, _link, _store, _sensors);
        }

        _settings.Show();
        _settings.BringToFront();
    }

    private void OnTick()
    {
        _monitor.Tick();

        // A warning overlay plus the reason in the tooltip: the needles say the
        // link is dead, the tray says why.
        _icon.Icon = _link.IsConnected ? SystemIcons.Application : SystemIcons.Warning;
        _icon.Text = _link.IsConnected
            ? $"Analog Hardware Monitor — {_link.PortName}"
            : Truncate($"Analog Hardware Monitor — {_link.LastError ?? "disconnected"}");
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
