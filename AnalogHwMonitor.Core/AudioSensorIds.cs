namespace AnalogHwMonitor.Core;

/// <summary>
/// The pseudo-sensors the audio source publishes, and the constants that describe
/// their scale. Two levels in dBFS, plus — for the duration of the throwaway
/// measurement build — one compensated needle in percent.
///
/// The "/audio/" prefix has to stay disjoint from every other source's identifiers.
/// <see cref="CompositeSensorSource.Read"/> returns the first non-null value across
/// its sources and documents that this is safe only while the prefixes do not overlap,
/// so a future source reusing "/audio/" would silently shadow these.
/// </summary>
public static class AudioSensorIds
{
    public const string Left = "/audio/0/level/0";

    public const string Right = "/audio/0/level/1";

    /// <summary>
    /// THROWAWAY MEASUREMENT BUILD. Deflection in percent, already shaped by
    /// <see cref="NeedleCompensator"/> — not a level in dBFS like the two above.
    /// </summary>
    public const string Needle = "/audio/0/needle";

    /// <summary>Unit of <see cref="Needle"/>, which is a deflection and not a level.</summary>
    public const string NeedleUnit = "%";

    /// <summary>Shown by the settings window through SensorDescriptor.Unit, which is
    /// what turns a bare "-14.2" in the Value column into "-14.2 dBFS".</summary>
    public const string Unit = "dBFS";

    /// <summary>Reported when there is no signal at all. A floor rather than negative
    /// infinity, because it has to pass through a float, a needle and a text box.</summary>
    public const double FloorDbfs = -100.0;
}
