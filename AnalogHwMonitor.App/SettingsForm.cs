using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

// Placeholder — replaced in full by Task 12.
public sealed class SettingsForm : Form
{
    public SettingsForm(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors)
    {
        Text = "Analog Hardware Monitor";
        Width = 400;
        Height = 200;
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Settings arrive in the next task.\nEdit config.json for now.",
        });
    }
}
