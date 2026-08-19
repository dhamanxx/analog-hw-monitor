using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

var identity = WindowsIdentity.GetCurrent();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"elevated: {elevated}");
Console.WriteLine("LibreHardwareMonitorLib: " + typeof(Computer).Assembly.GetName().Version);
Console.WriteLine();

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
    IsStorageEnabled = true,
    IsNetworkEnabled = true,
    IsPsuEnabled = true,
    IsBatteryEnabled = true,
};
computer.Open();
computer.Accept(new UpdateVisitor());

foreach (var hw in computer.Hardware)
{
    Dump(hw, string.Empty);
}
Console.WriteLine();
Console.WriteLine("--- any sensor whose name or identifier mentions a thermal zone ---");
var found = false;
void Scan(IHardware h)
{
    foreach (var s in h.Sensors)
        if (s.Name.Contains("zone", StringComparison.OrdinalIgnoreCase) || s.Identifier.ToString().Contains("acpi", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("CPUZ", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine($"    {s.Name} = {s.Value} {s.Identifier}"); found = true; }
    foreach (var sub in h.SubHardware) Scan(sub);
}
foreach (var hw in computer.Hardware) Scan(hw);
if (!found) Console.WriteLine("    (none)");

computer.Close();

static void Dump(IHardware hw, string indent)
{
    Console.WriteLine($"{indent}[{hw.HardwareType}] {hw.Name}");
    foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
    {
        var value = s.Value.HasValue ? s.Value.Value.ToString("0.0") : "NULL";
        Console.WriteLine($"{indent}    {s.SensorType,-12} {s.Name,-32} = {value,8}   {s.Identifier}");
    }
    foreach (var sub in hw.SubHardware)
    {
        Dump(sub, indent + "  ");
    }
}

sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer c) => c.Traverse(this);
    public void VisitHardware(IHardware h)
    {
        h.Update();
        foreach (var sub in h.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor s) { }
    public void VisitParameter(IParameter p) { }
}
