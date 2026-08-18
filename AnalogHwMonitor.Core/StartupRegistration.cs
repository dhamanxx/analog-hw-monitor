using Microsoft.Win32;

namespace AnalogHwMonitor.Core;

/// <summary>Adds or removes the application from the current user's Run key.</summary>
public sealed class StartupRegistration
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _subKey;
    private readonly string _valueName;

    public StartupRegistration(string subKey = RunSubKey, string valueName = "AnalogHwMonitor")
    {
        _subKey = subKey;
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(_valueName) is not null;
    }

    public void SetEnabled(bool enabled, string exePath)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_subKey);
            key.SetValue(_valueName, $"\"{exePath}\"");
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        existing?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
