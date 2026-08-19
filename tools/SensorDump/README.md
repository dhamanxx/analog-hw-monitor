# SensorDump

Prints every piece of hardware and every sensor LibreHardwareMonitor can see on the
machine it runs on, with each sensor's value and its exact identifier — the same
identifier that goes into `config.json` as `sensorId`.

It exists because "the temperature channel shows nothing" has several very different
causes, and the dropdown in the settings window cannot tell them apart. This tool can.

## Running it

It needs administrator rights (its manifest asks for them) because that is the only
way LibreHardwareMonitor can reach the hardware at all.

```powershell
dotnet run --project tools\SensorDump
```

To take it to another machine that has no .NET installed, publish it as one file:

```powershell
dotnet publish tools\SensorDump -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

This project is deliberately **not** part of `AnalogHwMonitor.sln`. It is a diagnostic
run by hand and must never take part in the application's build.

## Reading the output

Look at the values, not just the names. The failure modes look alike in a dropdown and
completely different here.

**Everything reads plausibly.** Nothing is wrong with the machine — find the sensor you
want and select it in the settings window by the name shown here.

**Every temperature, clock and power is `NULL` while loads still work.** The kernel
driver did not load. Loads come from Windows performance counters and need no driver;
temperatures come from model-specific registers and do. Elevation is not the missing
piece — something is blocking the driver, typically Memory Integrity:

```powershell
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity" -Name Enabled
```

**Temperatures read a constant `0.0` rather than `NULL`, voltages are all identical,
powers are all zero, and the motherboard section is empty.** The same underlying
problem wearing a different face: the reads are failing and returning zero instead of
nothing. On AMD this also shows up as per-core clocks reading `NULL` beside effective
clocks reading `0.0`. Check the vulnerable-driver blocklist and whether another
monitoring tool holds the driver — access to the AMD SMU is exclusive, so HWiNFO,
HWMonitor, Ryzen Master, Armoury Crate or a board vendor's utility running in the
background will starve this one:

```powershell
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config" -Name VulnerableDriverBlocklistEnable
Get-Process | Where-Object { $_.ProcessName -match 'HWiNFO|Ryzen|Afterburner|GPU-Z|CPUID|HWMonitor|Armoury|EasyTune|GCC|AIDA' }
```

**No temperature exists at all for an integrated GPU.** Nothing to find; the hardware
does not report one. Intel integrated graphics report load as `D3D 3D` rather than
`GPU Core`, which is why `SensorDefaults` matches both.

## What it cannot see

ACPI thermal zones. LibreHardwareMonitor does not read them at any version, with every
provider enabled — verified. They come from WMI instead, which is what
`AcpiThermalSensorSource` in the application uses, and they need no driver, so they are
often the only temperatures available on a locked-down machine:

```powershell
Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature |
  ForEach-Object { "{0} = {1:N1} C" -f $_.InstanceName, (($_.CurrentTemperature/10) - 273.15) }
```

Those appear in the application's own sensor list under `ACPI Thermal Zone`, with names
like `CPUZ_0` and `GFXZ_0`, but they will never appear in this tool's output.
