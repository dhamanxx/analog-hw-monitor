using System.Reflection;

namespace AnalogHwMonitor.App;

/// <summary>
/// The application's two icons — a normal dial and a warning variant with an
/// amber badge — loaded once from embedded resources and cached for the rest
/// of the process. They are loaded from resources embedded in the assembly,
/// not from files beside the executable, so a single-file publish keeps
/// working. <see cref="Normal"/> and <see cref="Warning"/> are process-wide
/// singletons and are never disposed; both live for exactly as long as the
/// process that owns the tray icon and settings window that display them.
/// </summary>
internal static class AppIcons
{
    public static Icon Normal { get; } = Load("AnalogHwMonitor.App.appicon.ico");

    public static Icon Warning { get; } = Load("AnalogHwMonitor.App.appicon-warning.ico");

    private static Icon Load(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        return new Icon(stream);
    }
}
