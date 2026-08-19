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

**Every temperature, clock and power is `NULL` while loads still work**, or on AMD
several read a constant `0.0` and the `[Motherboard]` section is empty. Same cause,
two faces: the kernel driver is not being reached. Loads come from Windows performance
counters and need no driver; temperatures come from model-specific registers and do.
Elevation is not the missing piece.

**Check PawnIO first.** Version 0.9.6 of the library dropped WinRing0 entirely and
talks only to PawnIO — there is no `WinRing0` string left in the assembly. It carries
the bytecode modules itself (`IntelMSR`, `AMDFamily17`, `RyzenSMU`, `LpcIO`), finds the
driver through the registry and opens `\\?\GLOBALROOT\Device\PawnIO`, but it contains no installer and never
prompts. Running LibreHardwareMonitor's own GUI offers to install it; consuming the
library does not. Install it from https://pawnio.eu, then check:

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" -ErrorAction SilentlyContinue |
  Select-Object DisplayName, DisplayVersion
```

`Get-Service` will not find it — it is a kernel driver, not a Win32 service.

**The quickest tell** is the `[Motherboard]` section. Its sensors are read through the
`LpcIO` module, so if it lists nothing at all while loads work, the driver is not being
reached and a dead CPU temperature is a symptom rather than the problem. Once PawnIO is
installed, temperatures, clocks, package power and the board's own sensors all appear
together.

**Comparing against HWiNFO or HWMonitor proves nothing here.** They ship their own
drivers, so they keep reading correctly whether or not PawnIO is installed. Their
success tells you the hardware is fine, not that this library can reach it.

**If PawnIO is installed and a temperature still reads `0.0`,** that is a library bug
rather than a configuration problem — see
https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/2348 for the AMD
case. Access to the AMD SMU is also exclusive, so close HWiNFO, HWMonitor, Ryzen
Master, Armoury Crate or a board vendor's utility before concluding anything.

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
