# Analog Hardware Monitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows tray application that reads five PC sensors once a second and drives five analog panel voltmeters through an Arduino UNO.

**Architecture:** A UI-free class library (`AnalogHwMonitor.Core`) holds all logic — sensor access, value→percent mapping, percent→PWM calibration, frame encoding, the serial link and the update tick. It reaches the outside world only through `ISensorSource`, `IMeterLink` and `ISerialPortFactory`, so everything is testable with fakes. A thin WinForms project owns the 1 Hz timer, the tray icon and the settings window. The Arduino sketch is a dumb actuator: parse five bytes, write five pins, zero everything when the link goes quiet.

**Tech Stack:** .NET 8 (`net8.0-windows`), WinForms, xUnit, LibreHardwareMonitorLib, `System.IO.Ports`, Arduino/C++ for the sketch.

**Spec:** `docs/superpowers/specs/2026-08-18-analog-hw-monitor-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- Target framework for all three .NET projects: `net8.0-windows`. `Nullable` and `ImplicitUsings` enabled.
- Exactly 5 channels, always in this order: index 0 CPU load (pin 3), 1 GPU load (pin 5), 2 memory usage (pin 6), 3 CPU temperature (pin 9), 4 GPU temperature (pin 10).
- Default ranges: loads and memory `0–100`, temperatures `30–90`. Default calibration `0–255`.
- Serial: 115200 baud, ASCII, LF line ending. PC→Arduino frame `V:a,b,c,d,e\n` with five integers 0–255. Arduino→PC boot banner `AHM1`.
- Sample rate 1 Hz. No smoothing, no non-linear curves — deliberately out of scope.
- Arduino watchdog: no valid frame for more than 3000 ms → write 0 to all five pins.
- Serial reconnect attempt every 5 ticks (5 s). Banner wait after opening a port: 5 read attempts × 500 ms ≈ 2.5 s, because opening the port resets the UNO.
- `config.json` and `log.txt` live next to the executable. JSON uses camelCase property names.
- Log rotates to `log.old.txt` at 1 MB.
- `AnalogHwMonitor.App` requires administrator elevation via `app.manifest`; LibreHardwareMonitorLib cannot read temperatures otherwise.
- `AnalogHwMonitor.Core` must not reference WinForms. The 1 Hz timer lives in the App project so everything runs on the UI thread.
- Inverted bounds (`min > max`, `minPwm > maxPwm`) are valid configuration, not errors. `min == max` yields 0 %.

## File Structure

```text
AnalogHwMonitor.sln
AnalogHwMonitor.Core/
  AnalogHwMonitor.Core.csproj
  ChannelMapper.cs              sensor value -> percent deflection
  MeterCalibration.cs           percent -> PWM byte
  FrameCodec.cs                 PWM bytes -> serial frame; protocol constants
  ChannelConfig.cs              one channel's settings
  AppConfig.cs                  whole configuration + factory defaults
  ConfigStore.cs                config.json load/save, .bak recovery
  IAppLog.cs                    logging contract + NullLog
  FileLog.cs                    log.txt with rotation
  SensorDescriptor.cs           sensor identity + SensorKind enum
  ISensorSource.cs              sensor access contract
  SensorDefaults.cs             auto-assign sensors to channels
  LibreHardwareSensorSource.cs  real LibreHardwareMonitor implementation
  ISerialPort.cs                serial port + factory contracts
  SerialPortAdapter.cs          real System.IO.Ports implementation
  IMeterLink.cs                 link contract
  SerialMeterLink.cs            connect, verify banner, send, reconnect
  PortDetector.cs               scan ports for the AHM1 banner
  ChannelReading.cs             one channel's state for the UI
  MonitorService.cs             one tick: read -> map -> calibrate -> send
  StartupRegistration.cs        HKCU Run key
AnalogHwMonitor.App/
  AnalogHwMonitor.App.csproj
  app.manifest                  requireAdministrator
  Program.cs                    composition root
  TrayApplicationContext.cs     tray icon, 1 Hz timer, menu
  SettingsForm.cs               port selection + five channel rows
  ChannelRowControl.cs          one channel's row of controls
AnalogHwMonitor.Tests/
  AnalogHwMonitor.Tests.csproj
  ChannelMapperTests.cs
  MeterCalibrationTests.cs
  FrameCodecTests.cs
  ConfigStoreTests.cs
  FileLogTests.cs
  SensorDefaultsTests.cs
  SerialMeterLinkTests.cs
  PortDetectorTests.cs
  MonitorServiceTests.cs
  StartupRegistrationTests.cs
  LibreHardwareSensorSourceTests.cs   opt-in, needs real hardware + elevation
  Fakes/FakeSensorSource.cs
  Fakes/FakeMeterLink.cs
  Fakes/FakeSerialPort.cs
arduino/analog_hw_monitor/
  analog_hw_monitor.ino
```

**Running tests:** `dotnet test` for everything, `dotnet test --filter "FullyQualifiedName~ChannelMapperTests"` for one class, `dotnet test --filter "FullyQualifiedName~ChannelMapperTests.ToPercent_MapsAndClamps"` for one test.

---

### Task 1: Solution scaffold and ChannelMapper

**Files:**
- Create: `AnalogHwMonitor.sln`
- Create: `AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj`
- Create: `AnalogHwMonitor.Core/ChannelMapper.cs`
- Create: `AnalogHwMonitor.Tests/AnalogHwMonitor.Tests.csproj`
- Test: `AnalogHwMonitor.Tests/ChannelMapperTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static double ChannelMapper.ToPercent(double value, double min, double max)` in namespace `AnalogHwMonitor.Core`. Returns 0–100.

- [ ] **Step 1: Create the solution and both projects**

```powershell
dotnet new sln -n AnalogHwMonitor
dotnet new classlib -n AnalogHwMonitor.Core -f net8.0
dotnet new xunit -n AnalogHwMonitor.Tests -f net8.0
dotnet sln add AnalogHwMonitor.Core AnalogHwMonitor.Tests
dotnet add AnalogHwMonitor.Tests reference AnalogHwMonitor.Core
Remove-Item AnalogHwMonitor.Core/Class1.cs
```

- [ ] **Step 2: Retarget both projects to `net8.0-windows`**

Replace the `<PropertyGroup>` of `AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj` with:

```xml
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
```

In `AnalogHwMonitor.Tests/AnalogHwMonitor.Tests.csproj`, change the single line
`<TargetFramework>net8.0</TargetFramework>` to `<TargetFramework>net8.0-windows</TargetFramework>`.
Leave the rest of the test csproj untouched.

- [ ] **Step 3: Write the failing test**

Create `AnalogHwMonitor.Tests/ChannelMapperTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class ChannelMapperTests
{
    [Theory]
    [InlineData(0, 0, 100, 0)]        // bottom of a load channel
    [InlineData(50, 0, 100, 50)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(30, 30, 90, 0)]       // bottom of a temperature channel
    [InlineData(60, 30, 90, 50)]
    [InlineData(90, 30, 90, 100)]
    [InlineData(20, 30, 90, 0)]       // below min clamps to zero
    [InlineData(120, 30, 90, 100)]    // above max clamps to full scale
    [InlineData(45, 90, 30, 75)]      // inverted range reverses the needle
    [InlineData(50, 50, 50, 0)]       // degenerate range never divides by zero
    public void ToPercent_MapsAndClamps(double value, double min, double max, double expected)
    {
        Assert.Equal(expected, ChannelMapper.ToPercent(value, min, max), 3);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ChannelMapperTests"`
Expected: build error `CS0103: The name 'ChannelMapper' does not exist in the current context`.

- [ ] **Step 5: Write the implementation**

Create `AnalogHwMonitor.Core/ChannelMapper.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>Converts a raw sensor reading into needle deflection, 0-100 %.</summary>
public static class ChannelMapper
{
    public static double ToPercent(double value, double min, double max)
    {
        if (min == max)
        {
            return 0;
        }

        var percent = (value - min) / (max - min) * 100.0;
        return Math.Clamp(percent, 0.0, 100.0);
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ChannelMapperTests"`
Expected: PASS, 10 tests.

- [ ] **Step 7: Commit**

```bash
git add AnalogHwMonitor.sln AnalogHwMonitor.Core AnalogHwMonitor.Tests
git commit -m "feat: add solution scaffold and ChannelMapper"
```

---

### Task 2: MeterCalibration

**Files:**
- Create: `AnalogHwMonitor.Core/MeterCalibration.cs`
- Test: `AnalogHwMonitor.Tests/MeterCalibrationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static byte MeterCalibration.ToPwm(double percent, int minPwm, int maxPwm)`.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/MeterCalibrationTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class MeterCalibrationTests
{
    [Theory]
    [InlineData(0, 0, 255, 0)]
    [InlineData(100, 0, 255, 255)]
    [InlineData(50, 0, 255, 128)]     // 127.5 rounds away from zero
    [InlineData(0, 12, 240, 12)]      // calibrated meter starts above zero
    [InlineData(100, 12, 240, 240)]   // ...and stops short of full PWM
    [InlineData(50, 12, 240, 126)]
    [InlineData(100, 240, 12, 12)]    // inverted calibration reverses the needle
    [InlineData(150, 0, 255, 255)]    // percent above 100 clamps
    [InlineData(-10, 0, 255, 0)]      // percent below 0 clamps
    public void ToPwm_InterpolatesBetweenCalibrationPoints(double percent, int minPwm, int maxPwm, byte expected)
    {
        Assert.Equal(expected, MeterCalibration.ToPwm(percent, minPwm, maxPwm));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MeterCalibrationTests"`
Expected: build error `CS0103: The name 'MeterCalibration' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `AnalogHwMonitor.Core/MeterCalibration.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Converts needle deflection (0-100 %) into a PWM byte using the two calibration
/// points measured for one physical meter.
/// </summary>
public static class MeterCalibration
{
    public static byte ToPwm(double percent, int minPwm, int maxPwm)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        var raw = minPwm + (maxPwm - minPwm) * clamped / 100.0;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(rounded, 0, 255);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MeterCalibrationTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/MeterCalibration.cs AnalogHwMonitor.Tests/MeterCalibrationTests.cs
git commit -m "feat: add two-point meter calibration"
```

---

### Task 3: FrameCodec and protocol constants

**Files:**
- Create: `AnalogHwMonitor.Core/FrameCodec.cs`
- Test: `AnalogHwMonitor.Tests/FrameCodecTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FrameCodec.ChannelCount` (int, 5), `FrameCodec.Banner` (string, `"AHM1"`), `FrameCodec.BaudRate` (int, 115200), `static string FrameCodec.Encode(IReadOnlyList<byte> pwmValues)`. Every later task uses `FrameCodec.ChannelCount` instead of a literal 5.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/FrameCodecTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_ProducesCommaSeparatedFrameWithLineFeed()
    {
        var frame = FrameCodec.Encode(new byte[] { 128, 200, 64, 30, 255 });

        Assert.Equal("V:128,200,64,30,255\n", frame);
    }

    [Fact]
    public void Encode_ProducesZeroFrameForZeroedChannels()
    {
        var frame = FrameCodec.Encode(new byte[] { 0, 0, 0, 0, 0 });

        Assert.Equal("V:0,0,0,0,0\n", frame);
    }

    [Fact]
    public void Encode_RejectsWrongChannelCount()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ProtocolConstants_MatchTheSketch()
    {
        Assert.Equal(5, FrameCodec.ChannelCount);
        Assert.Equal("AHM1", FrameCodec.Banner);
        Assert.Equal(115200, FrameCodec.BaudRate);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FrameCodecTests"`
Expected: build error `CS0103: The name 'FrameCodec' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `AnalogHwMonitor.Core/FrameCodec.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>The wire format shared with the Arduino sketch.</summary>
public static class FrameCodec
{
    public const int ChannelCount = 5;
    public const int BaudRate = 115200;

    /// <summary>Printed by the sketch on boot so we can tell our device from a printer.</summary>
    public const string Banner = "AHM1";

    public static string Encode(IReadOnlyList<byte> pwmValues)
    {
        if (pwmValues.Count != ChannelCount)
        {
            throw new ArgumentException(
                $"Expected {ChannelCount} PWM values, got {pwmValues.Count}.", nameof(pwmValues));
        }

        return "V:" + string.Join(',', pwmValues) + "\n";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FrameCodecTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/FrameCodec.cs AnalogHwMonitor.Tests/FrameCodecTests.cs
git commit -m "feat: add serial frame codec and protocol constants"
```

---

### Task 4: Configuration model and store

**Files:**
- Create: `AnalogHwMonitor.Core/ChannelConfig.cs`
- Create: `AnalogHwMonitor.Core/AppConfig.cs`
- Create: `AnalogHwMonitor.Core/ConfigStore.cs`
- Test: `AnalogHwMonitor.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.ChannelCount` from Task 3.
- Produces:
  - `class ChannelConfig` with `int Pin`, `string Label`, `string? SensorId`, `double Min`, `double Max`, `int MinPwm`, `int MaxPwm` (all mutable auto-properties).
  - `class AppConfig` with `string? ComPort`, `bool StartWithWindows`, `List<ChannelConfig> Channels`, and `static AppConfig CreateDefault()`.
  - `enum ConfigLoadOutcome { Loaded, CreatedDefault, RecoveredFromCorrupt }`.
  - `record ConfigLoadResult(AppConfig Config, ConfigLoadOutcome Outcome)`.
  - `class ConfigStore` with `ConfigStore(string path)`, `string Path`, `ConfigLoadResult Load()`, `void Save(AppConfig config)`.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/ConfigStoreTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahm-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void CreateDefault_DescribesTheFiveChannelsInOrder()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(FrameCodec.ChannelCount, config.Channels.Count);
        Assert.Equal(new[] { 3, 5, 6, 9, 10 }, config.Channels.Select(c => c.Pin));
        Assert.Equal(0, config.Channels[0].Min);
        Assert.Equal(100, config.Channels[0].Max);
        Assert.Equal(30, config.Channels[3].Min);
        Assert.Equal(90, config.Channels[3].Max);
        Assert.All(config.Channels, c => Assert.Equal(0, c.MinPwm));
        Assert.All(config.Channels, c => Assert.Equal(255, c.MaxPwm));
        Assert.All(config.Channels, c => Assert.Null(c.SensorId));
    }

    [Fact]
    public void Load_WritesDefaultsWhenFileIsMissing()
    {
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.CreatedDefault, result.Outcome);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }

    [Fact]
    public void Save_UsesCamelCaseJson()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault();
        config.ComPort = "COM7";

        store.Save(config);

        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"comPort\": \"COM7\"", json);
        Assert.Contains("\"minPwm\"", json);
    }

    [Fact]
    public void Load_RoundTripsASavedConfiguration()
    {
        var store = new ConfigStore(ConfigPath);
        var saved = AppConfig.CreateDefault();
        saved.ComPort = "COM7";
        saved.StartWithWindows = true;
        saved.Channels[0].SensorId = "/amdcpu/0/load/0";
        saved.Channels[0].MinPwm = 12;
        store.Save(saved);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Equal("COM7", result.Config.ComPort);
        Assert.True(result.Config.StartWithWindows);
        Assert.Equal("/amdcpu/0/load/0", result.Config.Channels[0].SensorId);
        Assert.Equal(12, result.Config.Channels[0].MinPwm);
    }

    [Fact]
    public void Load_BacksUpAndReplacesCorruptedJson()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath + ".bak"));
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
        Assert.Contains("\"channels\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_TreatsAWrongChannelCountAsCorrupt()
    {
        File.WriteAllText(ConfigPath, "{ \"comPort\": \"COM1\", \"channels\": [] }");
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal(FrameCodec.ChannelCount, result.Config.Channels.Count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: build errors `CS0103: The name 'AppConfig' does not exist in the current context`.

- [ ] **Step 3: Write the configuration model**

Create `AnalogHwMonitor.Core/ChannelConfig.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>Settings for one meter: which sensor it shows and how it is scaled.</summary>
public sealed class ChannelConfig
{
    /// <summary>Arduino PWM pin. Informational on the PC side; the frame is positional.</summary>
    public int Pin { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>LibreHardwareMonitor sensor identifier, or null when nothing is assigned.</summary>
    public string? SensorId { get; set; }

    /// <summary>Sensor value that means zero deflection, in the sensor's own unit.</summary>
    public double Min { get; set; }

    /// <summary>Sensor value that means full deflection.</summary>
    public double Max { get; set; }

    /// <summary>PWM value at which this physical meter reads zero.</summary>
    public int MinPwm { get; set; }

    /// <summary>PWM value at which this physical meter reads full scale.</summary>
    public int MaxPwm { get; set; }
}
```

Create `AnalogHwMonitor.Core/AppConfig.cs`:

```csharp
namespace AnalogHwMonitor.Core;

public sealed class AppConfig
{
    public string? ComPort { get; set; }

    public bool StartWithWindows { get; set; }

    public List<ChannelConfig> Channels { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Channels =
        {
            new ChannelConfig { Pin = 3,  Label = "CPU Load",   Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 5,  Label = "GPU Load",   Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 6,  Label = "Memory",     Min = 0,  Max = 100, MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 9,  Label = "CPU Temp",   Min = 30, Max = 90,  MinPwm = 0, MaxPwm = 255 },
            new ChannelConfig { Pin = 10, Label = "GPU Temp",   Min = 30, Max = 90,  MinPwm = 0, MaxPwm = 255 },
        },
    };
}
```

- [ ] **Step 4: Write the store**

Create `AnalogHwMonitor.Core/ConfigStore.cs`:

```csharp
using System.Text.Json;

namespace AnalogHwMonitor.Core;

public enum ConfigLoadOutcome
{
    Loaded,
    CreatedDefault,
    RecoveredFromCorrupt,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadOutcome Outcome);

/// <summary>Reads and writes config.json next to the executable.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public ConfigStore(string path) => Path = path;

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    public ConfigLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, ConfigLoadOutcome.CreatedDefault);
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options)
                         ?? throw new InvalidDataException("config.json contains null.");

            if (config.Channels.Count != FrameCodec.ChannelCount)
            {
                throw new InvalidDataException(
                    $"config.json must contain exactly {FrameCodec.ChannelCount} channels.");
            }

            return new ConfigLoadResult(config, ConfigLoadOutcome.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            File.Move(Path, BackupPath, overwrite: true);
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, ConfigLoadOutcome.RecoveredFromCorrupt);
        }
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Options));
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add AnalogHwMonitor.Core/ChannelConfig.cs AnalogHwMonitor.Core/AppConfig.cs AnalogHwMonitor.Core/ConfigStore.cs AnalogHwMonitor.Tests/ConfigStoreTests.cs
git commit -m "feat: add configuration model and config.json store"
```

---

### Task 5: File log with rotation

**Files:**
- Create: `AnalogHwMonitor.Core/IAppLog.cs`
- Create: `AnalogHwMonitor.Core/FileLog.cs`
- Test: `AnalogHwMonitor.Tests/FileLogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `interface IAppLog { void Write(string message); }`, `sealed class NullLog : IAppLog` (used by every later test), `sealed class FileLog : IAppLog` with `FileLog(string path, long maxBytes = 1_048_576, Func<DateTimeOffset>? clock = null)`, `string Path`, `string OldPath`.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/FileLogTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahm-log-" + Guid.NewGuid().ToString("N"));

    public FileLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogPath => Path.Combine(_directory, "log.txt");

    private static Func<DateTimeOffset> FixedClock =>
        () => new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Write_AppendsTimestampedLines()
    {
        var log = new FileLog(LogPath, clock: FixedClock);

        log.Write("first");
        log.Write("second");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-08-18 14:30:00 first", lines[0]);
        Assert.Equal("2026-08-18 14:30:00 second", lines[1]);
    }

    [Fact]
    public void OldPath_SitsNextToTheLog()
    {
        var log = new FileLog(LogPath);

        Assert.Equal(Path.Combine(_directory, "log.old.txt"), log.OldPath);
    }

    [Fact]
    public void Write_RotatesOnceTheLogPassesItsSizeLimit()
    {
        var log = new FileLog(LogPath, maxBytes: 40, clock: FixedClock);

        log.Write("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");   // pushes the file past 40 bytes
        log.Write("after rotation");

        Assert.Contains("aaaaaaaaaa", File.ReadAllText(log.OldPath));
        Assert.Equal("2026-08-18 14:30:00 after rotation", File.ReadAllText(LogPath).TrimEnd());
    }

    [Fact]
    public void Write_OverwritesAPreviousRotation()
    {
        File.WriteAllText(Path.Combine(_directory, "log.old.txt"), "stale");
        var log = new FileLog(LogPath, maxBytes: 40, clock: FixedClock);

        log.Write("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        log.Write("after rotation");

        Assert.DoesNotContain("stale", File.ReadAllText(log.OldPath));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FileLogTests"`
Expected: build error `CS0246: The type or namespace name 'FileLog' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `AnalogHwMonitor.Core/IAppLog.cs`:

```csharp
namespace AnalogHwMonitor.Core;

public interface IAppLog
{
    void Write(string message);
}

/// <summary>Discards everything. Used by tests and by code paths without a log.</summary>
public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();

    public void Write(string message)
    {
    }
}
```

Create `AnalogHwMonitor.Core/FileLog.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Appends to log.txt next to the executable and rotates to log.old.txt once the
/// file passes its size limit. A background process needs some way to say what
/// happened while nobody was watching.
/// </summary>
public sealed class FileLog : IAppLog
{
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public FileLog(string path, long maxBytes = 1_048_576, Func<DateTimeOffset>? clock = null)
    {
        Path = path;
        _maxBytes = maxBytes;
        _clock = clock ?? (() => DateTimeOffset.Now);

        var directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        OldPath = System.IO.Path.Combine(directory, name + ".old" + extension);
    }

    public string Path { get; }

    public string OldPath { get; }

    public void Write(string message)
    {
        lock (_gate)
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length >= _maxBytes)
            {
                File.Move(Path, OldPath, overwrite: true);
            }

            File.AppendAllText(
                Path,
                $"{_clock():yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FileLogTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/IAppLog.cs AnalogHwMonitor.Core/FileLog.cs AnalogHwMonitor.Tests/FileLogTests.cs
git commit -m "feat: add rotating file log"
```

---

### Task 6: Sensor contract and default assignment

**Files:**
- Create: `AnalogHwMonitor.Core/SensorDescriptor.cs`
- Create: `AnalogHwMonitor.Core/ISensorSource.cs`
- Create: `AnalogHwMonitor.Core/SensorDefaults.cs`
- Test: `AnalogHwMonitor.Tests/SensorDefaultsTests.cs`

**Interfaces:**
- Consumes: `AppConfig`, `ChannelConfig` from Task 4.
- Produces:
  - `enum SensorKind { Load, Temperature, Other }`.
  - `record SensorDescriptor(string Id, string Name, string Hardware, SensorKind Kind, string Unit)` with `string Display => $"{Hardware} · {Name}"`.
  - `interface ISensorSource : IDisposable` with `void Refresh()`, `IReadOnlyList<SensorDescriptor> Discover()`, `float? Read(string sensorId)`. `Refresh()` is called once per tick; `Read` must not re-poll the hardware.
  - `static void SensorDefaults.AssignSensors(AppConfig config, IReadOnlyList<SensorDescriptor> sensors)` — fills only channels whose `SensorId` is null or empty.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/SensorDefaultsTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class SensorDefaultsTests
{
    private static readonly SensorDescriptor[] AmdMachine =
    {
        new("/amdcpu/0/load/0",       "CPU Total",         "AMD Ryzen 7 5800X", SensorKind.Load,        "%"),
        new("/amdcpu/0/load/1",       "CPU Core #1",       "AMD Ryzen 7 5800X", SensorKind.Load,        "%"),
        new("/amdcpu/0/temperature/0","Core (Tctl/Tdie)",  "AMD Ryzen 7 5800X", SensorKind.Temperature, "°C"),
        new("/gpu-nvidia/0/load/0",   "GPU Core",          "NVIDIA RTX 3070",   SensorKind.Load,        "%"),
        new("/gpu-nvidia/0/load/3",   "GPU Memory",        "NVIDIA RTX 3070",   SensorKind.Load,        "%"),
        new("/gpu-nvidia/0/temperature/0", "GPU Core",     "NVIDIA RTX 3070",   SensorKind.Temperature, "°C"),
        new("/ram/load/0",            "Memory",            "Generic Memory",    SensorKind.Load,        "%"),
        new("/lpc/nct6798d/fan/1",    "Fan #2",            "Motherboard",       SensorKind.Other,       "RPM"),
    };

    private static readonly SensorDescriptor[] IntelMachine =
    {
        new("/intelcpu/0/load/0",        "CPU Total",   "Intel Core i7-12700K", SensorKind.Load,        "%"),
        new("/intelcpu/0/temperature/8", "CPU Package", "Intel Core i7-12700K", SensorKind.Temperature, "°C"),
        new("/gpu-intel/0/load/0",       "GPU Core",    "Intel UHD 770",        SensorKind.Load,        "%"),
        new("/gpu-intel/0/temperature/0","GPU Core",    "Intel UHD 770",        SensorKind.Temperature, "°C"),
        new("/ram/load/0",               "Memory",      "Generic Memory",       SensorKind.Load,        "%"),
    };

    [Fact]
    public void AssignSensors_PicksTheExpectedSensorsOnAnAmdMachine()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/load/0", config.Channels[0].SensorId);
        Assert.Equal("/gpu-nvidia/0/load/0", config.Channels[1].SensorId);
        Assert.Equal("/ram/load/0", config.Channels[2].SensorId);
        Assert.Equal("/amdcpu/0/temperature/0", config.Channels[3].SensorId);
        Assert.Equal("/gpu-nvidia/0/temperature/0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_PicksTheExpectedSensorsOnAnIntelMachine()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, IntelMachine);

        Assert.Equal("/intelcpu/0/load/0", config.Channels[0].SensorId);
        Assert.Equal("/gpu-intel/0/load/0", config.Channels[1].SensorId);
        Assert.Equal("/ram/load/0", config.Channels[2].SensorId);
        Assert.Equal("/intelcpu/0/temperature/8", config.Channels[3].SensorId);
        Assert.Equal("/gpu-intel/0/temperature/0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_DoesNotMistakeGpuMemoryForSystemMemory()
    {
        var withoutSystemRam = AmdMachine.Where(s => s.Id != "/ram/load/0").ToArray();
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, withoutSystemRam);

        Assert.Null(config.Channels[2].SensorId);
    }

    [Fact]
    public void AssignSensors_LeavesChannelsEmptyWhenNothingMatches()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, Array.Empty<SensorDescriptor>());

        Assert.All(config.Channels, c => Assert.Null(c.SensorId));
    }

    [Fact]
    public void AssignSensors_NeverOverwritesAChoiceTheUserAlreadyMade()
    {
        var config = AppConfig.CreateDefault();
        config.Channels[0].SensorId = "/amdcpu/0/load/1";

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/load/1", config.Channels[0].SensorId);
    }

    [Fact]
    public void Display_CombinesHardwareAndSensorName()
    {
        Assert.Equal("AMD Ryzen 7 5800X · CPU Total", AmdMachine[0].Display);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SensorDefaultsTests"`
Expected: build error `CS0246: The type or namespace name 'SensorDescriptor' could not be found`.

- [ ] **Step 3: Write the contracts**

Create `AnalogHwMonitor.Core/SensorDescriptor.cs`:

```csharp
namespace AnalogHwMonitor.Core;

public enum SensorKind
{
    Load,
    Temperature,
    Other,
}

/// <param name="Id">LibreHardwareMonitor identifier, e.g. "/amdcpu/0/load/0".</param>
/// <param name="Name">Sensor name as reported, e.g. "CPU Total".</param>
/// <param name="Hardware">Owning device name, e.g. "AMD Ryzen 7 5800X".</param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    string Hardware,
    SensorKind Kind,
    string Unit)
{
    /// <summary>What the settings window shows in its dropdown.</summary>
    public string Display => $"{Hardware} · {Name}";
}
```

Create `AnalogHwMonitor.Core/ISensorSource.cs`:

```csharp
namespace AnalogHwMonitor.Core;

public interface ISensorSource : IDisposable
{
    /// <summary>Polls the hardware once. Called at the start of every tick.</summary>
    void Refresh();

    IReadOnlyList<SensorDescriptor> Discover();

    /// <summary>Last refreshed value, or null when the sensor is unknown or unreadable.</summary>
    float? Read(string sensorId);
}
```

- [ ] **Step 4: Write the default assignment**

Create `AnalogHwMonitor.Core/SensorDefaults.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Picks a sensible sensor per channel on first run. Sensor names differ by vendor,
/// so each channel has an ordered list of patterns plus a hint about which device
/// the sensor should belong to.
/// </summary>
public static class SensorDefaults
{
    private sealed record Rule(SensorKind Kind, string[] NamePatterns, string IdHint);

    private static readonly Rule[] Rules =
    {
        new(SensorKind.Load,        new[] { "CPU Total", "CPU" },                              "cpu"),
        new(SensorKind.Load,        new[] { "GPU Core", "GPU" },                               "gpu"),
        new(SensorKind.Load,        new[] { "Memory" },                                        "/ram"),
        new(SensorKind.Temperature, new[] { "CPU Package", "Tctl", "Core Average", "CPU" },    "cpu"),
        new(SensorKind.Temperature, new[] { "GPU Core", "GPU" },                               "gpu"),
    };

    public static void AssignSensors(AppConfig config, IReadOnlyList<SensorDescriptor> sensors)
    {
        for (var i = 0; i < config.Channels.Count && i < Rules.Length; i++)
        {
            if (!string.IsNullOrEmpty(config.Channels[i].SensorId))
            {
                continue;
            }

            config.Channels[i].SensorId = Match(sensors, Rules[i])?.Id;
        }
    }

    private static SensorDescriptor? Match(IReadOnlyList<SensorDescriptor> sensors, Rule rule)
    {
        var onHintedDevice = sensors
            .Where(s => s.Kind == rule.Kind &&
                        s.Id.Contains(rule.IdHint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A hint starting with "/" is mandatory: without it, "GPU Memory" would
        // satisfy the memory channel on a machine that reports no system RAM sensor.
        var candidates = onHintedDevice.Count > 0
            ? onHintedDevice
            : rule.IdHint.StartsWith('/')
                ? new List<SensorDescriptor>()
                : sensors.Where(s => s.Kind == rule.Kind).ToList();

        foreach (var pattern in rule.NamePatterns)
        {
            var exact = candidates.FirstOrDefault(
                s => string.Equals(s.Name, pattern, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var partial = candidates.FirstOrDefault(
                s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }
        }

        return null;
    }
}
```

Note on the two kinds of hint: `"cpu"` and `"gpu"` are preferences — if no sensor
sits on a matching device, the search widens to every sensor of the right kind.
`"/ram"` is mandatory, because widening it would let `GPU Memory` land on channel 2.
A machine with no system-memory sensor gets an empty channel rather than a wrong one,
which is what `DoesNotMistakeGpuMemoryForSystemMemory` pins down.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SensorDefaultsTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add AnalogHwMonitor.Core/SensorDescriptor.cs AnalogHwMonitor.Core/ISensorSource.cs AnalogHwMonitor.Core/SensorDefaults.cs AnalogHwMonitor.Tests/SensorDefaultsTests.cs
git commit -m "feat: add sensor contract and default channel assignment"
```

---

### Task 7: Serial link, banner handshake and port detection

**Files:**
- Create: `AnalogHwMonitor.Core/ISerialPort.cs`
- Create: `AnalogHwMonitor.Core/SerialPortAdapter.cs`
- Create: `AnalogHwMonitor.Core/IMeterLink.cs`
- Create: `AnalogHwMonitor.Core/SerialMeterLink.cs`
- Create: `AnalogHwMonitor.Core/PortDetector.cs`
- Modify: `AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj` (add `System.IO.Ports`)
- Test: `AnalogHwMonitor.Tests/Fakes/FakeSerialPort.cs`
- Test: `AnalogHwMonitor.Tests/SerialMeterLinkTests.cs`
- Test: `AnalogHwMonitor.Tests/PortDetectorTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.Banner`, `FrameCodec.BaudRate` from Task 3; `IAppLog`, `NullLog` from Task 5.
- Produces:
  - `interface ISerialPort : IDisposable` with `bool IsOpen`, `void Open()`, `void Write(string text)`, `string? ReadLine()` (null on timeout).
  - `interface ISerialPortFactory` with `IReadOnlyList<string> GetPortNames()`, `ISerialPort Create(string portName)`.
  - `sealed class SerialPortFactory : ISerialPortFactory` — the real one.
  - `interface IMeterLink : IDisposable` with `bool IsConnected`, `string? LastError`, `void Send(string frame)`.
  - `sealed class SerialMeterLink : IMeterLink` with `SerialMeterLink(ISerialPortFactory factory, string? portName, IAppLog log)`, settable `string? PortName`, `bool TryConnect()`, and constants `BannerReadAttempts = 5`, `ReconnectEveryTicks = 5`.
  - `static string? PortDetector.FindMonitorPort(ISerialPortFactory factory, IAppLog log)`.

- [ ] **Step 1: Add the serial package**

```powershell
dotnet add AnalogHwMonitor.Core package System.IO.Ports
```

- [ ] **Step 2: Write the fake port**

Create `AnalogHwMonitor.Tests/Fakes/FakeSerialPort.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>A serial port scripted with the lines it will hand back.</summary>
public sealed class FakeSerialPort : ISerialPort
{
    private readonly Queue<string?> _linesToRead;

    public FakeSerialPort(params string?[] linesToRead) =>
        _linesToRead = new Queue<string?>(linesToRead);

    public bool IsOpen { get; private set; }

    public bool Disposed { get; private set; }

    public List<string> Written { get; } = new();

    public Exception? ThrowOnWrite { get; set; }

    public Exception? ThrowOnOpen { get; set; }

    public void Open()
    {
        if (ThrowOnOpen is not null)
        {
            throw ThrowOnOpen;
        }

        IsOpen = true;
    }

    public string? ReadLine() => _linesToRead.Count > 0 ? _linesToRead.Dequeue() : null;

    public void Write(string text)
    {
        if (ThrowOnWrite is not null)
        {
            IsOpen = false;
            throw ThrowOnWrite;
        }

        Written.Add(text);
    }

    public void Dispose()
    {
        IsOpen = false;
        Disposed = true;
    }
}

public sealed class FakeSerialPortFactory : ISerialPortFactory
{
    private readonly Dictionary<string, Func<FakeSerialPort>> _ports = new();

    public List<string> CreatedPortNames { get; } = new();

    public FakeSerialPort? Last { get; private set; }

    public void AddPort(string name, Func<FakeSerialPort> port) => _ports[name] = port;

    public IReadOnlyList<string> GetPortNames() => _ports.Keys.ToList();

    public ISerialPort Create(string portName)
    {
        CreatedPortNames.Add(portName);
        Last = _ports.TryGetValue(portName, out var factory)
            ? factory()
            : new FakeSerialPort();
        return Last;
    }
}
```

- [ ] **Step 3: Write the failing tests**

Create `AnalogHwMonitor.Tests/SerialMeterLinkTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class SerialMeterLinkTests
{
    private static FakeSerialPortFactory FactoryWith(string name, Func<FakeSerialPort> port)
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort(name, port);
        return factory;
    }

    [Fact]
    public void TryConnect_SucceedsWhenTheDeviceAnnouncesItself()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.True(link.TryConnect());
        Assert.True(link.IsConnected);
        Assert.Null(link.LastError);
    }

    [Fact]
    public void TryConnect_IgnoresNoiseBeforeTheBanner()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort(null, "\u0000garbage", "AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.True(link.TryConnect());
    }

    [Fact]
    public void TryConnect_RejectsADeviceThatNeverAnnouncesItself()
    {
        var port = new FakeSerialPort("READY", "OK", "42");
        var factory = FactoryWith("COM3", () => port);
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.False(link.IsConnected);
        Assert.Contains("AHM1", link.LastError);
        Assert.True(port.Disposed);
    }

    [Fact]
    public void TryConnect_ReportsAPortThatCannotBeOpened()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort
        {
            ThrowOnOpen = new UnauthorizedAccessException("Access to the port is denied."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.Contains("Access to the port is denied.", link.LastError);
    }

    [Fact]
    public void TryConnect_FailsWithoutAConfiguredPort()
    {
        using var link = new SerialMeterLink(new FakeSerialPortFactory(), null, NullLog.Instance);

        Assert.False(link.TryConnect());
        Assert.Contains("No COM port", link.LastError);
    }

    [Fact]
    public void Send_ConnectsOnTheFirstCallAndWritesTheFrame()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        link.Send("V:1,2,3,4,5\n");

        Assert.True(link.IsConnected);
        Assert.Equal(new[] { "V:1,2,3,4,5\n" }, factory.Last!.Written);
    }

    [Fact]
    public void Send_MarksTheLinkDeadWhenTheWriteFails()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("AHM1")
        {
            ThrowOnWrite = new IOException("The device is not connected."),
        });
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        link.Send("V:1,2,3,4,5\n");

        Assert.False(link.IsConnected);
        Assert.Contains("The device is not connected.", link.LastError);
    }

    [Fact]
    public void Send_RetriesTheConnectionOnlyEveryFifthTick()
    {
        var factory = FactoryWith("COM3", () => new FakeSerialPort("nothing"));
        using var link = new SerialMeterLink(factory, "COM3", NullLog.Instance);

        for (var i = 0; i < 6; i++)
        {
            link.Send("V:0,0,0,0,0\n");
        }

        Assert.Equal(2, factory.CreatedPortNames.Count);   // ticks 1 and 6
    }
}
```

Create `AnalogHwMonitor.Tests/PortDetectorTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class PortDetectorTests
{
    [Fact]
    public void FindMonitorPort_SkipsPortsThatAreNotOurDevice()
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort("COM1", () => new FakeSerialPort("PRINTER READY"));
        factory.AddPort("COM4", () => new FakeSerialPort("AHM1"));

        Assert.Equal("COM4", PortDetector.FindMonitorPort(factory, NullLog.Instance));
    }

    [Fact]
    public void FindMonitorPort_ReturnsNullWhenNothingAnswers()
    {
        var factory = new FakeSerialPortFactory();
        factory.AddPort("COM1", () => new FakeSerialPort("PRINTER READY"));

        Assert.Null(PortDetector.FindMonitorPort(factory, NullLog.Instance));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SerialMeterLinkTests|FullyQualifiedName~PortDetectorTests"`
Expected: build error `CS0246: The type or namespace name 'ISerialPort' could not be found`.

- [ ] **Step 5: Write the port contracts and the real adapter**

Create `AnalogHwMonitor.Core/ISerialPort.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>The slice of a serial port this application needs, so it can be faked.</summary>
public interface ISerialPort : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Write(string text);

    /// <summary>One line, or null if nothing arrived before the read timeout.</summary>
    string? ReadLine();
}

public interface ISerialPortFactory
{
    IReadOnlyList<string> GetPortNames();

    ISerialPort Create(string portName);
}
```

Create `AnalogHwMonitor.Core/SerialPortAdapter.cs`:

```csharp
using System.IO.Ports;

namespace AnalogHwMonitor.Core;

public sealed class SerialPortAdapter : ISerialPort
{
    /// <summary>Five of these cover the ~2 s the UNO needs to reboot after the port opens.</summary>
    public const int ReadTimeoutMs = 500;

    private readonly SerialPort _port;

    public SerialPortAdapter(string portName)
    {
        _port = new SerialPort(portName, FrameCodec.BaudRate)
        {
            NewLine = "\n",
            ReadTimeout = ReadTimeoutMs,
            WriteTimeout = 1000,
            DtrEnable = true,
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();

    public void Write(string text) => _port.Write(text);

    public string? ReadLine()
    {
        try
        {
            return _port.ReadLine();
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void Dispose() => _port.Dispose();
}

public sealed class SerialPortFactory : ISerialPortFactory
{
    public IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames();

    public ISerialPort Create(string portName) => new SerialPortAdapter(portName);
}
```

- [ ] **Step 6: Write the link**

Create `AnalogHwMonitor.Core/IMeterLink.cs`:

```csharp
namespace AnalogHwMonitor.Core;

public interface IMeterLink : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Why the link is down, for the tray tooltip. Null while healthy.</summary>
    string? LastError { get; }

    void Send(string frame);
}
```

Create `AnalogHwMonitor.Core/SerialMeterLink.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Owns the serial port: opens it, waits for the boot banner, writes frames, and
/// quietly retries after a failure. Never throws at the caller — a dead link is a
/// state, not an exception, because the meters already report it by dropping to zero.
/// </summary>
public sealed class SerialMeterLink : IMeterLink
{
    /// <summary>Read attempts while waiting for the banner after the UNO reboots.</summary>
    public const int BannerReadAttempts = 5;

    /// <summary>Ticks between reconnect attempts. At 1 Hz this is the 5 s from the spec.</summary>
    public const int ReconnectEveryTicks = 5;

    private readonly ISerialPortFactory _factory;
    private readonly IAppLog _log;
    private ISerialPort? _port;
    private int _ticksSinceAttempt = ReconnectEveryTicks - 1;

    public SerialMeterLink(ISerialPortFactory factory, string? portName, IAppLog log)
    {
        _factory = factory;
        PortName = portName;
        _log = log;
    }

    /// <summary>Changing this drops the current connection.</summary>
    public string? PortName
    {
        get => _portName;
        set
        {
            if (_portName == value)
            {
                return;
            }

            _portName = value;
            Disconnect();
        }
    }

    private string? _portName;

    public bool IsConnected => _port?.IsOpen == true;

    public string? LastError { get; private set; }

    public bool TryConnect()
    {
        Disconnect();

        if (string.IsNullOrWhiteSpace(PortName))
        {
            LastError = "No COM port configured.";
            return false;
        }

        ISerialPort? port = null;
        try
        {
            port = _factory.Create(PortName);
            port.Open();

            for (var attempt = 0; attempt < BannerReadAttempts; attempt++)
            {
                var line = port.ReadLine();
                if (line?.Trim() == FrameCodec.Banner)
                {
                    _port = port;
                    LastError = null;
                    _log.Write($"Connected to {PortName}.");
                    return true;
                }
            }

            port.Dispose();
            LastError = $"{PortName} did not identify itself as {FrameCodec.Banner}.";
            _log.Write(LastError);
            return false;
        }
        catch (Exception ex)
        {
            port?.Dispose();
            LastError = $"{PortName}: {ex.Message}";
            _log.Write(LastError);
            return false;
        }
    }

    public void Send(string frame)
    {
        if (!IsConnected)
        {
            _ticksSinceAttempt++;
            if (_ticksSinceAttempt < ReconnectEveryTicks)
            {
                return;
            }

            _ticksSinceAttempt = 0;
            if (!TryConnect())
            {
                return;
            }
        }

        try
        {
            _port!.Write(frame);
        }
        catch (Exception ex)
        {
            LastError = $"{PortName}: {ex.Message}";
            _log.Write(LastError);
            Disconnect();
        }
    }

    private void Disconnect()
    {
        _port?.Dispose();
        _port = null;
    }

    public void Dispose() => Disconnect();
}
```

- [ ] **Step 7: Write the port detector**

Create `AnalogHwMonitor.Core/PortDetector.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>Finds the port whose device answers with the AHM1 banner.</summary>
public static class PortDetector
{
    public static string? FindMonitorPort(ISerialPortFactory factory, IAppLog log)
    {
        foreach (var name in factory.GetPortNames())
        {
            using var link = new SerialMeterLink(factory, name, log);
            if (link.TryConnect())
            {
                return name;
            }
        }

        return null;
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SerialMeterLinkTests|FullyQualifiedName~PortDetectorTests"`
Expected: PASS, 10 tests.

- [ ] **Step 9: Commit**

```bash
git add AnalogHwMonitor.Core/ISerialPort.cs AnalogHwMonitor.Core/SerialPortAdapter.cs AnalogHwMonitor.Core/IMeterLink.cs AnalogHwMonitor.Core/SerialMeterLink.cs AnalogHwMonitor.Core/PortDetector.cs AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj AnalogHwMonitor.Tests/Fakes AnalogHwMonitor.Tests/SerialMeterLinkTests.cs AnalogHwMonitor.Tests/PortDetectorTests.cs
git commit -m "feat: add serial meter link with banner handshake and reconnect"
```

---

### Task 8: MonitorService tick

**Files:**
- Create: `AnalogHwMonitor.Core/ChannelReading.cs`
- Create: `AnalogHwMonitor.Core/MonitorService.cs`
- Test: `AnalogHwMonitor.Tests/Fakes/FakeSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/Fakes/FakeMeterLink.cs`
- Test: `AnalogHwMonitor.Tests/MonitorServiceTests.cs`

**Interfaces:**
- Consumes: `ChannelMapper` (Task 1), `MeterCalibration` (Task 2), `FrameCodec` (Task 3), `AppConfig` (Task 4), `IAppLog` (Task 5), `ISensorSource` (Task 6), `IMeterLink` (Task 7).
- Produces:
  - `record ChannelReading(int Index, string Label, float? Value, double Percent, byte Pwm, bool SensorMissing, bool TestMode)`.
  - `sealed class MonitorService : IDisposable` with `MonitorService(ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log)`, `AppConfig Config { get; set; }`, `event EventHandler<IReadOnlyList<ChannelReading>>? Updated`, `void Tick()`, `void SetTestPwm(int channelIndex, byte? pwm)`.
- The service owns no timer. The App project calls `Tick()` from a WinForms timer so everything stays on the UI thread.

- [ ] **Step 1: Write the fakes**

Create `AnalogHwMonitor.Tests/Fakes/FakeSensorSource.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

public sealed class FakeSensorSource : ISensorSource
{
    private readonly Dictionary<string, float?> _values;

    public FakeSensorSource(Dictionary<string, float?> values) => _values = values;

    public int RefreshCount { get; private set; }

    public List<string> ReadIds { get; } = new();

    public List<SensorDescriptor> Sensors { get; } = new();

    public void Refresh() => RefreshCount++;

    public IReadOnlyList<SensorDescriptor> Discover() => Sensors;

    public float? Read(string sensorId)
    {
        ReadIds.Add(sensorId);
        return _values.TryGetValue(sensorId, out var value) ? value : null;
    }

    public void Dispose()
    {
    }
}
```

Create `AnalogHwMonitor.Tests/Fakes/FakeMeterLink.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

public sealed class FakeMeterLink : IMeterLink
{
    public List<string> Frames { get; } = new();

    public bool IsConnected { get; set; } = true;

    public string? LastError { get; set; }

    public void Send(string frame) => Frames.Add(frame);

    public void Dispose()
    {
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `AnalogHwMonitor.Tests/MonitorServiceTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class MonitorServiceTests
{
    private static AppConfig ConfigWithSensors()
    {
        var config = AppConfig.CreateDefault();
        config.Channels[0].SensorId = "cpu-load";
        config.Channels[1].SensorId = "gpu-load";
        config.Channels[2].SensorId = "ram-load";
        config.Channels[3].SensorId = "cpu-temp";
        config.Channels[4].SensorId = "gpu-temp";
        return config;
    }

    private static FakeSensorSource SensorsAt(float cpuLoad, float gpuLoad, float ram, float cpuTemp, float gpuTemp) =>
        new(new Dictionary<string, float?>
        {
            ["cpu-load"] = cpuLoad,
            ["gpu-load"] = gpuLoad,
            ["ram-load"] = ram,
            ["cpu-temp"] = cpuTemp,
            ["gpu-temp"] = gpuTemp,
        });

    [Fact]
    public void Tick_SendsOneFrameWithAllFiveChannels()
    {
        var sensors = SensorsAt(0, 50, 100, 30, 90);
        var link = new FakeMeterLink();
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(new[] { "V:0,128,255,0,255\n" }, link.Frames);
    }

    [Fact]
    public void Tick_RefreshesTheHardwareExactlyOnce()
    {
        var sensors = SensorsAt(10, 10, 10, 40, 40);
        using var service = new MonitorService(sensors, new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(1, sensors.RefreshCount);
    }

    [Fact]
    public void Tick_SendsZeroForAMissingSensorAndKeepsTheOthersRunning()
    {
        var sensors = new FakeSensorSource(new Dictionary<string, float?>
        {
            ["cpu-load"] = 50,
            ["gpu-load"] = null,      // GPU was swapped out
            ["ram-load"] = 50,
            ["cpu-temp"] = 60,
            ["gpu-temp"] = null,
        });
        var link = new FakeMeterLink();
        IReadOnlyList<ChannelReading>? readings = null;
        using var service = new MonitorService(sensors, link, ConfigWithSensors(), NullLog.Instance);
        service.Updated += (_, r) => readings = r;

        service.Tick();

        Assert.Equal(new[] { "V:128,0,128,128,0\n" }, link.Frames);
        Assert.False(readings![0].SensorMissing);
        Assert.True(readings[1].SensorMissing);
        Assert.Equal(0, readings[1].Pwm);
    }

    [Fact]
    public void Tick_DoesNotQueryAnUnassignedChannel()
    {
        var config = ConfigWithSensors();
        config.Channels[4].SensorId = null;
        var sensors = SensorsAt(0, 0, 0, 30, 30);
        using var service = new MonitorService(sensors, new FakeMeterLink(), config, NullLog.Instance);

        service.Tick();

        Assert.DoesNotContain("gpu-temp", sensors.ReadIds);
    }

    [Fact]
    public void Tick_RespectsPerChannelCalibration()
    {
        var config = ConfigWithSensors();
        config.Channels[0].MinPwm = 12;
        config.Channels[0].MaxPwm = 240;
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(50, 0, 0, 30, 30), link, config, NullLog.Instance);

        service.Tick();

        Assert.StartsWith("V:126,", link.Frames[0]);
    }

    [Fact]
    public void SetTestPwm_OverridesOneChannelAndLeavesTheRestOnTheirSensors()
    {
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(0, 50, 0, 30, 30), link, ConfigWithSensors(), NullLog.Instance);
        IReadOnlyList<ChannelReading>? readings = null;
        service.Updated += (_, r) => readings = r;

        service.SetTestPwm(0, 200);
        service.Tick();

        Assert.StartsWith("V:200,128,", link.Frames[0]);
        Assert.True(readings![0].TestMode);
        Assert.False(readings[1].TestMode);
    }

    [Fact]
    public void SetTestPwm_WithNullReturnsTheChannelToItsSensor()
    {
        var link = new FakeMeterLink();
        using var service = new MonitorService(SensorsAt(100, 0, 0, 30, 30), link, ConfigWithSensors(), NullLog.Instance);

        service.SetTestPwm(0, 200);
        service.SetTestPwm(0, null);
        service.Tick();

        Assert.StartsWith("V:255,", link.Frames[0]);
    }

    [Fact]
    public void Updated_ReportsEveryChannelWithItsRawValue()
    {
        IReadOnlyList<ChannelReading>? readings = null;
        using var service = new MonitorService(
            SensorsAt(25, 0, 0, 60, 30), new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);
        service.Updated += (_, r) => readings = r;

        service.Tick();

        Assert.Equal(FrameCodec.ChannelCount, readings!.Count);
        Assert.Equal("CPU Load", readings[0].Label);
        Assert.Equal(25f, readings[0].Value);
        Assert.Equal(25, readings[0].Percent, 3);
        Assert.Equal(50, readings[3].Percent, 3);   // 60 °C on a 30-90 range
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MonitorServiceTests"`
Expected: build error `CS0246: The type or namespace name 'MonitorService' could not be found`.

- [ ] **Step 4: Write the implementation**

Create `AnalogHwMonitor.Core/ChannelReading.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>One channel's state after a tick, for the settings window to display.</summary>
/// <param name="Value">Raw sensor reading, or null when the channel is unassigned,
/// unreadable, or under manual test control.</param>
public sealed record ChannelReading(
    int Index,
    string Label,
    float? Value,
    double Percent,
    byte Pwm,
    bool SensorMissing,
    bool TestMode);
```

Create `AnalogHwMonitor.Core/MonitorService.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// One tick of the whole system: refresh the hardware, turn five readings into five
/// PWM bytes, and push one frame down the link. Owns no timer and no threads —
/// the caller decides when a tick happens.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly ISensorSource _sensors;
    private readonly IMeterLink _link;
    private readonly IAppLog _log;
    private readonly byte?[] _testPwm = new byte?[FrameCodec.ChannelCount];
    private readonly bool[] _missingReported = new bool[FrameCodec.ChannelCount];

    public MonitorService(ISensorSource sensors, IMeterLink link, AppConfig config, IAppLog log)
    {
        _sensors = sensors;
        _link = link;
        _log = log;
        Config = config;
    }

    /// <summary>Swapped wholesale when the settings window saves.</summary>
    public AppConfig Config { get; set; }

    public event EventHandler<IReadOnlyList<ChannelReading>>? Updated;

    /// <summary>Pins a channel to a raw PWM value for calibration; null releases it.</summary>
    public void SetTestPwm(int channelIndex, byte? pwm) => _testPwm[channelIndex] = pwm;

    public void Tick()
    {
        _sensors.Refresh();

        var pwmValues = new byte[FrameCodec.ChannelCount];
        var readings = new List<ChannelReading>(FrameCodec.ChannelCount);

        for (var i = 0; i < FrameCodec.ChannelCount; i++)
        {
            var channel = Config.Channels[i];

            if (_testPwm[i] is { } testPwm)
            {
                pwmValues[i] = testPwm;
                readings.Add(new ChannelReading(i, channel.Label, null, 0, testPwm, false, true));
                continue;
            }

            var value = string.IsNullOrEmpty(channel.SensorId) ? null : _sensors.Read(channel.SensorId);
            var missing = value is null;

            if (missing && !_missingReported[i])
            {
                _log.Write($"Channel {i} ({channel.Label}) has no readable sensor: {channel.SensorId ?? "<none>"}");
                _missingReported[i] = true;
            }
            else if (!missing)
            {
                _missingReported[i] = false;
            }

            var percent = missing ? 0 : ChannelMapper.ToPercent(value!.Value, channel.Min, channel.Max);

            // A missing sensor parks the needle below its calibrated zero, so a dead
            // channel never looks like a healthy idle one.
            pwmValues[i] = missing ? (byte)0 : MeterCalibration.ToPwm(percent, channel.MinPwm, channel.MaxPwm);

            readings.Add(new ChannelReading(i, channel.Label, value, percent, pwmValues[i], missing, false));
        }

        _link.Send(FrameCodec.Encode(pwmValues));
        Updated?.Invoke(this, readings);
    }

    public void Dispose()
    {
        _link.Dispose();
        _sensors.Dispose();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MonitorServiceTests"`
Expected: PASS, 8 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS, 57 tests.

- [ ] **Step 7: Commit**

```bash
git add AnalogHwMonitor.Core/ChannelReading.cs AnalogHwMonitor.Core/MonitorService.cs AnalogHwMonitor.Tests/Fakes AnalogHwMonitor.Tests/MonitorServiceTests.cs
git commit -m "feat: add monitor service tick"
```

---

### Task 9: LibreHardwareMonitor sensor source

**Files:**
- Create: `AnalogHwMonitor.Core/LibreHardwareSensorSource.cs`
- Modify: `AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj` (add `LibreHardwareMonitorLib`)
- Test: `AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs`

**Interfaces:**
- Consumes: `ISensorSource`, `SensorDescriptor`, `SensorKind` from Task 6.
- Produces: `sealed class LibreHardwareSensorSource : ISensorSource` with a parameterless constructor that opens the `Computer` with CPU, GPU and memory enabled.

This is the one class that cannot be unit tested — it talks to real silicon. Its test is opt-in and only runs on the target machine in an elevated shell.

- [ ] **Step 1: Add the package**

```powershell
dotnet add AnalogHwMonitor.Core package LibreHardwareMonitorLib
```

Record the resolved version in the commit message — the package is pulled unpinned so the build uses the current release.

- [ ] **Step 2: Write the opt-in hardware test**

Create `AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// These need real hardware and an elevated session, so they only run when
/// AHM_HARDWARE_TESTS=1. Everywhere else they report themselves as skipped.
/// </summary>
public class LibreHardwareSensorSourceTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("AHM_HARDWARE_TESTS") == "1";

    [Fact]
    public void Discover_FindsLoadAndTemperatureSensors()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var sensors = source.Discover();

        Assert.Contains(sensors, s => s.Kind == SensorKind.Load);
        Assert.Contains(sensors, s => s.Kind == SensorKind.Temperature);
        Assert.All(sensors, s => Assert.False(string.IsNullOrWhiteSpace(s.Id)));
    }

    [Fact]
    public void Read_ReturnsAValueForADiscoveredSensor()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var cpuLoad = source.Discover().First(s => s.Kind == SensorKind.Load);

        Assert.NotNull(source.Read(cpuLoad.Id));
    }

    [Fact]
    public void Read_ReturnsNullForAnUnknownSensor()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();

        Assert.Null(source.Read("/nothing/like/this"));
    }

    [Fact]
    public void DefaultAssignment_FillsEveryChannelOnThisMachine()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, source.Discover());

        Assert.All(config.Channels, c => Assert.False(string.IsNullOrEmpty(c.SensorId)));
    }
}
```

`Skip.IfNot` comes from the `Xunit.SkippableFact` package, which reports these as skipped rather than silently passing:

```powershell
dotnet add AnalogHwMonitor.Tests package Xunit.SkippableFact
```

With that package, change each `[Fact]` above to `[SkippableFact]`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LibreHardwareSensorSourceTests"`
Expected: build error `CS0246: The type or namespace name 'LibreHardwareSensorSource' could not be found`.

- [ ] **Step 4: Write the implementation**

Create `AnalogHwMonitor.Core/LibreHardwareSensorSource.cs`:

```csharp
using LibreHardwareMonitor.Hardware;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Reads the machine's sensors through LibreHardwareMonitor. Needs administrator
/// rights: the library loads a kernel driver, and without it most temperatures
/// are simply absent.
/// </summary>
public sealed class LibreHardwareSensorSource : ISensorSource
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public LibreHardwareSensorSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
        };
        _computer.Open();
    }

    public void Refresh() => _computer.Accept(_visitor);

    public IReadOnlyList<SensorDescriptor> Discover() =>
        EnumerateSensors()
            .Select(pair => new SensorDescriptor(
                pair.Sensor.Identifier.ToString(),
                pair.Sensor.Name,
                pair.Hardware.Name,
                ToKind(pair.Sensor.SensorType),
                ToUnit(pair.Sensor.SensorType)))
            .ToList();

    public float? Read(string sensorId) =>
        EnumerateSensors()
            .FirstOrDefault(pair => pair.Sensor.Identifier.ToString() == sensorId)
            .Sensor?.Value;

    public void Dispose() => _computer.Close();

    private IEnumerable<(IHardware Hardware, ISensor Sensor)> EnumerateSensors()
    {
        foreach (var hardware in _computer.Hardware)
        {
            foreach (var sensor in hardware.Sensors)
            {
                yield return (hardware, sensor);
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                foreach (var sensor in subHardware.Sensors)
                {
                    yield return (subHardware, sensor);
                }
            }
        }
    }

    private static SensorKind ToKind(SensorType type) => type switch
    {
        SensorType.Load => SensorKind.Load,
        SensorType.Temperature => SensorKind.Temperature,
        _ => SensorKind.Other,
    };

    private static string ToUnit(SensorType type) => type switch
    {
        SensorType.Load => "%",
        SensorType.Temperature => "°C",
        _ => string.Empty,
    };
}
```

- [ ] **Step 5: Verify the build and the skipped tests**

Run: `dotnet test --filter "FullyQualifiedName~LibreHardwareSensorSourceTests"`
Expected: PASS with 4 skipped tests (`AHM_HARDWARE_TESTS` is unset).

- [ ] **Step 6: Verify against real hardware**

In an **elevated** PowerShell, on the machine that will run the monitor:

```powershell
$env:AHM_HARDWARE_TESTS = "1"
dotnet test --filter "FullyQualifiedName~LibreHardwareSensorSourceTests"
```

Expected: PASS, 4 tests. If `DefaultAssignment_FillsEveryChannelOnThisMachine` fails, print the sensor names this machine reports and add the missing pattern to `SensorDefaults.Rules`, then re-run.

- [ ] **Step 7: Commit**

```bash
git add AnalogHwMonitor.Core/LibreHardwareSensorSource.cs AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs AnalogHwMonitor.Tests/AnalogHwMonitor.Tests.csproj
git commit -m "feat: read sensors through LibreHardwareMonitor"
```

---

### Task 10: Arduino sketch

**Files:**
- Create: `arduino/analog_hw_monitor/analog_hw_monitor.ino`

**Interfaces:**
- Consumes: the wire format from Task 3 — frame `V:a,b,c,d,e\n`, banner `AHM1`, 115200 baud.
- Produces: firmware. Nothing in the .NET solution references it; the protocol constants are the contract.

There is no unit test harness for the sketch. Verification is manual through the Arduino IDE's Serial Monitor, which is enough to drive every code path.

- [ ] **Step 1: Write the sketch**

Create `arduino/analog_hw_monitor/analog_hw_monitor.ino`:

```cpp
// Analog Hardware Monitor - Arduino UNO firmware.
//
// Reads frames of the form "V:a,b,c,d,e\n" where each value is 0-255, and writes
// them to five PWM pins. All scaling and calibration happen on the PC; this sketch
// deliberately knows nothing about temperatures or percentages.
//
// If no valid frame arrives for WATCHDOG_MS, every needle drops to zero, so a dead
// link never looks like an idle PC.

const uint8_t PINS[5] = {3, 5, 6, 9, 10};
const unsigned long WATCHDOG_MS = 3000;
const uint8_t BUFFER_SIZE = 32;

char buffer[BUFFER_SIZE];
uint8_t length = 0;
unsigned long lastFrameMs = 0;
bool zeroed = true;

void writeAll(uint8_t value) {
  for (uint8_t i = 0; i < 5; i++) {
    analogWrite(PINS[i], value);
  }
}

void handleLine() {
  if (buffer[0] != 'V' || buffer[1] != ':') {
    return;
  }

  int values[5];
  char* cursor = buffer + 2;

  for (uint8_t i = 0; i < 5; i++) {
    char* end;
    long value = strtol(cursor, &end, 10);
    if (end == cursor || value < 0 || value > 255) {
      return;
    }
    values[i] = (int)value;
    cursor = end;
    if (i < 4) {
      if (*cursor != ',') {
        return;
      }
      cursor++;
    }
  }

  if (*cursor != '\0') {
    return;
  }

  for (uint8_t i = 0; i < 5; i++) {
    analogWrite(PINS[i], values[i]);
  }
  lastFrameMs = millis();
  zeroed = false;
}

void setup() {
  for (uint8_t i = 0; i < 5; i++) {
    pinMode(PINS[i], OUTPUT);
  }
  writeAll(0);

  Serial.begin(115200);
  Serial.println("AHM1");
}

void loop() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\r') {
      continue;
    }
    if (c == '\n') {
      buffer[length] = '\0';
      handleLine();
      length = 0;
    } else if (length < BUFFER_SIZE - 1) {
      buffer[length++] = c;
    } else {
      length = 0;   // overlong line, drop it
    }
  }

  if (!zeroed && millis() - lastFrameMs > WATCHDOG_MS) {
    writeAll(0);
    zeroed = true;
  }
}
```

- [ ] **Step 2: Compile and upload**

Open `arduino/analog_hw_monitor/analog_hw_monitor.ino` in the Arduino IDE, select **Arduino UNO** and the right port, then Upload. Or:

```powershell
arduino-cli compile --fqbn arduino:avr:uno arduino/analog_hw_monitor
arduino-cli upload --fqbn arduino:avr:uno -p COM3 arduino/analog_hw_monitor
```

Expected: compiles with no errors and uploads.

- [ ] **Step 3: Verify the banner**

Open Serial Monitor at 115200 baud with line ending **New Line**. Press the board's reset button.
Expected: `AHM1` appears.

- [ ] **Step 4: Verify the needles follow a frame**

Type `V:0,64,128,192,255` and send.
Expected: the five needles settle at roughly 0 %, 25 %, 50 %, 75 % and 100 % of their scales. (Exact positions come later, after calibration.)

- [ ] **Step 5: Verify bad frames are ignored**

Send each of these in turn: `V:1,2,3`, `V:1,2,3,4,999`, `hello`, `V:1,2,3,4,5,6`.
Expected: the needles do not move from their previous positions for any of them.

- [ ] **Step 6: Verify the watchdog**

Send `V:255,255,255,255,255`, then stop sending and wait.
Expected: all five needles drop to zero roughly three seconds later.

- [ ] **Step 7: Commit**

```bash
git add arduino/analog_hw_monitor/analog_hw_monitor.ino
git commit -m "feat: add Arduino sketch with frame parser and watchdog"
```

---

### Task 11: Application shell — tray icon, timer, startup registration

**Files:**
- Create: `AnalogHwMonitor.Core/StartupRegistration.cs`
- Create: `AnalogHwMonitor.App/AnalogHwMonitor.App.csproj`
- Create: `AnalogHwMonitor.App/app.manifest`
- Create: `AnalogHwMonitor.App/Program.cs`
- Create: `AnalogHwMonitor.App/TrayApplicationContext.cs`
- Modify: `AnalogHwMonitor.sln`
- Test: `AnalogHwMonitor.Tests/StartupRegistrationTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 4–9.
- Produces:
  - `sealed class StartupRegistration` with `StartupRegistration(string subKey = StartupRegistration.RunSubKey, string valueName = "AnalogHwMonitor")`, `bool IsEnabled()`, `void SetEnabled(bool enabled, string exePath)`, and `const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run"`.
  - `sealed class TrayApplicationContext : ApplicationContext` with `TrayApplicationContext(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors, IAppLog log)` and `void ShowSettings()`.

After this task the whole system works end to end. There is no settings window yet, so the COM port is set by hand in `config.json`.

- [ ] **Step 1: Write the failing startup-registration test**

Create `AnalogHwMonitor.Tests/StartupRegistrationTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Microsoft.Win32;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class StartupRegistrationTests : IDisposable
{
    // A scratch key so the tests never touch the real Run key.
    private const string TestSubKey = @"Software\AnalogHwMonitor\StartupTests";

    private readonly StartupRegistration _registration = new(TestSubKey, "AnalogHwMonitorTest");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestSubKey, throwOnMissingSubKey: false);

    [Fact]
    public void IsEnabled_IsFalseBeforeAnythingIsRegistered()
    {
        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void SetEnabled_WritesTheQuotedExecutablePath()
    {
        _registration.SetEnabled(true, @"C:\Program Files\Analog HW Monitor\AnalogHwMonitor.App.exe");

        Assert.True(_registration.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(TestSubKey);
        Assert.Equal(
            "\"C:\\Program Files\\Analog HW Monitor\\AnalogHwMonitor.App.exe\"",
            key!.GetValue("AnalogHwMonitorTest"));
    }

    [Fact]
    public void SetEnabled_FalseRemovesTheEntry()
    {
        _registration.SetEnabled(true, @"C:\app.exe");
        _registration.SetEnabled(false, @"C:\app.exe");

        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void SetEnabled_FalseIsHarmlessWhenNothingIsRegistered()
    {
        _registration.SetEnabled(false, @"C:\app.exe");

        Assert.False(_registration.IsEnabled());
    }

    [Fact]
    public void RunSubKey_PointsAtTheWindowsRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunSubKey);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~StartupRegistrationTests"`
Expected: build error `CS0246: The type or namespace name 'StartupRegistration' could not be found`.

- [ ] **Step 3: Write the startup registration**

Create `AnalogHwMonitor.Core/StartupRegistration.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~StartupRegistrationTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Create the App project**

```powershell
dotnet new winforms -n AnalogHwMonitor.App -f net8.0
dotnet sln add AnalogHwMonitor.App
dotnet add AnalogHwMonitor.App reference AnalogHwMonitor.Core
Remove-Item AnalogHwMonitor.App/Form1.cs, AnalogHwMonitor.App/Form1.Designer.cs -ErrorAction SilentlyContinue
```

Replace the whole of `AnalogHwMonitor.App/AnalogHwMonitor.App.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>AnalogHwMonitor</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AnalogHwMonitor.Core\AnalogHwMonitor.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Write the elevation manifest**

Create `AnalogHwMonitor.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AnalogHwMonitor.App" />

  <!-- LibreHardwareMonitor loads a kernel driver to read temperatures; without
       elevation most sensors are missing. -->
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>

  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /><!-- Windows 10/11 -->
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 7: Replace the template's Program.cs with the composition root**

Create `AnalogHwMonitor.App/Program.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var directory = AppContext.BaseDirectory;
        var log = new FileLog(Path.Combine(directory, "log.txt"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));

        var loaded = store.Load();
        if (loaded.Outcome != ConfigLoadOutcome.Loaded)
        {
            log.Write($"Configuration: {loaded.Outcome}.");
        }

        var config = loaded.Config;

        ISensorSource sensors;
        try
        {
            sensors = new LibreHardwareSensorSource();
            sensors.Refresh();
        }
        catch (Exception ex)
        {
            log.Write($"Cannot open the hardware monitor: {ex.Message}");
            MessageBox.Show(
                $"Cannot read hardware sensors.\n\n{ex.Message}\n\nRun the application as administrator.",
                "Analog Hardware Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var hadUnassignedChannels = config.Channels.Any(c => string.IsNullOrEmpty(c.SensorId));
        SensorDefaults.AssignSensors(config, sensors.Discover());
        if (hadUnassignedChannels)
        {
            store.Save(config);
        }

        var link = new SerialMeterLink(new SerialPortFactory(), config.ComPort, log);
        var monitor = new MonitorService(sensors, link, config, log);

        Application.Run(new TrayApplicationContext(monitor, link, store, sensors, log));
    }
}
```

- [ ] **Step 8: Write the tray context**

Create `AnalogHwMonitor.App/TrayApplicationContext.cs`:

```csharp
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
```

`SettingsForm` does not exist yet, so add this placeholder to make Task 11 build and run.
Task 12 replaces the whole file.

Create `AnalogHwMonitor.App/SettingsForm.cs`:

```csharp
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
```

- [ ] **Step 9: Build and run the whole suite**

Run: `dotnet build`
Expected: three projects build with no warnings about missing references.

Run: `dotnet test`
Expected: PASS, 66 tests (4 skipped).

- [ ] **Step 10: Verify end to end against the hardware**

With the Arduino flashed and connected, in an **elevated** PowerShell:

```powershell
dotnet run --project AnalogHwMonitor.App
```

Expected on the first run: `config.json` and `log.txt` appear in
`AnalogHwMonitor.App/bin/Debug/net8.0-windows/`, a tray icon appears with a warning
overlay, and its tooltip reads `No COM port configured.`

Then stop the app, set `"comPort": "COM3"` (your port) in that `config.json`, and run again.
Expected: the tray icon loses its warning overlay within five seconds, and all five
needles move to positions matching the current load and temperatures. Load the CPU
(for example `dotnet build` in a loop) and watch channel 0 climb.

Finally, close the app from the tray menu.
Expected: all five needles drop to zero about three seconds later.

- [ ] **Step 11: Commit**

```bash
git add AnalogHwMonitor.Core/StartupRegistration.cs AnalogHwMonitor.App AnalogHwMonitor.sln AnalogHwMonitor.Tests/StartupRegistrationTests.cs
git commit -m "feat: add tray application shell driving the meters at 1 Hz"
```

---

### Task 12: Settings window

**Files:**
- Create: `AnalogHwMonitor.App/ChannelRowControl.cs`
- Modify: `AnalogHwMonitor.App/SettingsForm.cs` (replaces the Task 11 placeholder entirely)

**Interfaces:**
- Consumes: `MonitorService`, `SerialMeterLink`, `ConfigStore`, `ISensorSource`, `SensorDescriptor`, `ChannelReading`, `PortDetector`, `StartupRegistration`, `SensorDefaults`.
- Produces: `sealed class ChannelRowControl : UserControl` with `ChannelRowControl(ChannelConfig channel, IReadOnlyList<SensorDescriptor> sensors)`, `event EventHandler<byte?>? TestPwmChanged`, `void ApplyTo(ChannelConfig channel)`, `void ShowReading(ChannelReading reading)`. `SettingsForm` keeps the constructor signature from Task 11.

This window cannot be unit tested. Its verification is the manual checklist in Step 5.

- [ ] **Step 1: Write the channel row control**

Create `AnalogHwMonitor.App/ChannelRowControl.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

/// <summary>
/// One meter's row: which sensor it shows, how it is scaled, what it currently
/// reads, and a test slider for calibration.
/// </summary>
public sealed class ChannelRowControl : UserControl
{
    private readonly ComboBox _sensor = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _min = new() { Width = 60, Minimum = -273, Maximum = 10000, DecimalPlaces = 0 };
    private readonly NumericUpDown _max = new() { Width = 60, Minimum = -273, Maximum = 10000, DecimalPlaces = 0 };
    private readonly Label _value = new() { Width = 90, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _pwm = new() { Width = 45, TextAlign = ContentAlignment.MiddleRight };
    private readonly CheckBox _test = new() { Text = "Test", Width = 55 };
    private readonly TrackBar _slider = new() { Width = 150, Minimum = 0, Maximum = 255, TickFrequency = 32, Enabled = false };
    private readonly Button _saveMin = new() { Text = "Save as min", Width = 90, Enabled = false };
    private readonly Button _saveMax = new() { Text = "Save as max", Width = 90, Enabled = false };
    private readonly Label _calibration = new() { Width = 80, TextAlign = ContentAlignment.MiddleLeft };

    private int _minPwm;
    private int _maxPwm;

    public ChannelRowControl(ChannelConfig channel, IReadOnlyList<SensorDescriptor> sensors)
    {
        _minPwm = channel.MinPwm;
        _maxPwm = channel.MaxPwm;

        _sensor.Items.Add("(none)");
        foreach (var sensor in sensors)
        {
            _sensor.Items.Add(sensor);
        }

        _sensor.DisplayMember = nameof(SensorDescriptor.Display);
        _sensor.SelectedIndex = 0;
        for (var i = 0; i < sensors.Count; i++)
        {
            if (sensors[i].Id == channel.SensorId)
            {
                _sensor.SelectedIndex = i + 1;
                break;
            }
        }

        _min.Value = (decimal)channel.Min;
        _max.Value = (decimal)channel.Max;
        UpdateCalibrationLabel();

        _test.CheckedChanged += (_, _) =>
        {
            _slider.Enabled = _test.Checked;
            _saveMin.Enabled = _test.Checked;
            _saveMax.Enabled = _test.Checked;
            TestPwmChanged?.Invoke(this, _test.Checked ? (byte)_slider.Value : null);
        };

        _slider.ValueChanged += (_, _) =>
        {
            if (_test.Checked)
            {
                TestPwmChanged?.Invoke(this, (byte)_slider.Value);
            }
        };

        _saveMin.Click += (_, _) =>
        {
            _minPwm = _slider.Value;
            UpdateCalibrationLabel();
        };

        _saveMax.Click += (_, _) =>
        {
            _maxPwm = _slider.Value;
            UpdateCalibrationLabel();
        };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };

        layout.Controls.Add(new Label { Text = $"Pin {channel.Pin}", Width = 45, TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(new Label { Text = channel.Label, Width = 80, TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(_sensor);
        layout.Controls.Add(_min);
        layout.Controls.Add(_max);
        layout.Controls.Add(_value);
        layout.Controls.Add(_pwm);
        layout.Controls.Add(_test);
        layout.Controls.Add(_slider);
        layout.Controls.Add(_saveMin);
        layout.Controls.Add(_saveMax);
        layout.Controls.Add(_calibration);

        Controls.Add(layout);
        Height = 40;
        Dock = DockStyle.Top;
    }

    /// <summary>Raised with a PWM value while Test is on, and with null when it goes off.</summary>
    public event EventHandler<byte?>? TestPwmChanged;

    public void ApplyTo(ChannelConfig channel)
    {
        channel.SensorId = _sensor.SelectedItem as SensorDescriptor is { } descriptor ? descriptor.Id : null;
        channel.Min = (double)_min.Value;
        channel.Max = (double)_max.Value;
        channel.MinPwm = _minPwm;
        channel.MaxPwm = _maxPwm;
    }

    public void ShowReading(ChannelReading reading)
    {
        _value.Text = reading.TestMode
            ? "test"
            : reading.Value is { } value ? value.ToString("0.0") : "—";
        _value.ForeColor = reading.SensorMissing ? Color.Firebrick : SystemColors.ControlText;
        _pwm.Text = reading.Pwm.ToString();
    }

    private void UpdateCalibrationLabel() => _calibration.Text = $"{_minPwm}–{_maxPwm}";
}
```

- [ ] **Step 2: Write the settings form**

Replace the whole of `AnalogHwMonitor.App/SettingsForm.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.App;

public sealed class SettingsForm : Form
{
    private readonly MonitorService _monitor;
    private readonly SerialMeterLink _link;
    private readonly ConfigStore _store;
    private readonly StartupRegistration _startup = new();
    private readonly ComboBox _ports = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _startWithWindows = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<ChannelRowControl> _rows = new();

    public SettingsForm(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors)
    {
        _monitor = monitor;
        _link = link;
        _store = store;

        Text = "Analog Hardware Monitor";
        Width = 1100;
        Height = 320;
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

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 24, WrapContents = false };
        foreach (var (text, width) in new[]
                 {
                     ("Pin", 45), ("Channel", 80), ("Sensor", 266), ("Min", 63), ("Max", 63),
                     ("Value", 93), ("PWM", 48), ("", 58), ("Calibrate", 153), ("", 186), ("Cal. range", 80),
                 })
        {
            header.Controls.Add(new Label { Text = text, Width = width, TextAlign = ContentAlignment.MiddleLeft });
        }

        var rows = new Panel { Dock = DockStyle.Fill };

        // Added in reverse because Dock = Top stacks the last-added control on top.
        for (var i = _monitor.Config.Channels.Count - 1; i >= 0; i--)
        {
            var index = i;
            var row = new ChannelRowControl(_monitor.Config.Channels[i], available);
            row.TestPwmChanged += (_, pwm) => _monitor.SetTestPwm(index, pwm);
            _rows.Insert(0, row);
            rows.Controls.Add(row);
        }

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

        Controls.Add(rows);
        Controls.Add(header);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(_status);

        RefreshPorts();
        _startWithWindows.Checked = _startup.IsEnabled();

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
    }

    private void RefreshPorts()
    {
        _ports.Items.Clear();
        foreach (var name in new SerialPortFactory().GetPortNames())
        {
            _ports.Items.Add(name);
        }

        if (_link.PortName is { } current && _ports.Items.Contains(current))
        {
            _ports.SelectedItem = current;
        }
    }

    private void Detect()
    {
        _status.Text = "Scanning ports…";
        Application.DoEvents();

        var found = PortDetector.FindMonitorPort(new SerialPortFactory(), NullLog.Instance);
        RefreshPorts();

        if (found is null)
        {
            _status.Text = "No device answered with the AHM1 banner.";
            return;
        }

        _ports.SelectedItem = found;
        _status.Text = $"Found the monitor on {found}.";
    }

    private void Save()
    {
        foreach (var (row, index) in _rows.Select((r, i) => (r, i)))
        {
            row.ApplyTo(_monitor.Config.Channels[index]);
            _monitor.SetTestPwm(index, null);
        }

        _monitor.Config.ComPort = _ports.SelectedItem as string;
        _monitor.Config.StartWithWindows = _startWithWindows.Checked;

        _link.PortName = _monitor.Config.ComPort;
        _startup.SetEnabled(_startWithWindows.Checked, Application.ExecutablePath);
        _store.Save(_monitor.Config);

        _status.Text = $"Saved to {_store.Path}";
    }

    private void OnUpdated(object? sender, IReadOnlyList<ChannelReading> readings)
    {
        if (!Visible)
        {
            return;
        }

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
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: builds with no errors.

- [ ] **Step 4: Run the whole test suite**

Run: `dotnet test`
Expected: PASS, 66 tests (4 skipped). Nothing in this task changes `Core`.

- [ ] **Step 5: Verify the window by hand**

In an **elevated** PowerShell with the Arduino connected:

```powershell
dotnet run --project AnalogHwMonitor.App
```

Work through each of these:

1. Double-click the tray icon. The window opens with five rows and live **Value** and **PWM** columns updating once a second.
2. Press **Detect**. The COM port combo lands on the Arduino's port and the status line names it.
3. Change channel 0's sensor to a different load sensor, press **Save**, and watch that needle start following the new sensor.
4. Set channel 3's **Min** to 40 and **Max** to 60, press **Save**. The CPU temperature needle now swings much further for the same temperature change.
5. Tick **Test** on channel 0. The slider comes alive; dragging it moves only that needle. The other four keep following their sensors.
6. Move the slider until the needle sits on zero, press **Save as min**; move it to full scale, press **Save as max**. The calibration range label updates.
7. Untick **Test**, press **Save**, and confirm channel 0 returns to its sensor and now rests correctly at both ends.
8. Tick **Start with Windows**, press **Save**, and confirm the value appears under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`:
   `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name AnalogHwMonitor`
9. Close the window with the X. The app stays in the tray and the needles keep moving.
10. Unplug the Arduino's USB cable. Within about three seconds every needle drops to zero and the tray icon gains a warning overlay. Plug it back in — within five seconds the needles come back on their own.
11. Reopen `config.json` and confirm your sensor choices, ranges and calibration points are all in it.

- [ ] **Step 6: Commit**

```bash
git add AnalogHwMonitor.App/ChannelRowControl.cs AnalogHwMonitor.App/SettingsForm.cs
git commit -m "feat: add settings window with live readings and meter calibration"
```

---

## Acceptance Checklist

Run this once after Task 12, with the finished device assembled:

- [ ] `dotnet test` passes, and passes again with `AHM_HARDWARE_TESTS=1` in an elevated shell.
- [ ] Launching the app without elevation shows the UAC prompt.
- [ ] Deleting `config.json` and restarting recreates it with all five channels auto-assigned.
- [ ] Corrupting `config.json` and restarting produces `config.json.bak` plus a fresh file, and the app still starts.
- [ ] Pointing a channel at a sensor id that does not exist parks that needle at zero, turns its value red, and leaves the other four working.
- [ ] All five needles track their readings under load, and rest at their calibrated zero when idle.
- [ ] Closing the app drops every needle to zero within about three seconds.
- [ ] `log.txt` contains the connect and disconnect events from the session.

---

## Addendum — Tasks 13 and 14

Added after the original twelve tasks were built and the application was run against real
hardware. Task 13 answers a problem the target machine revealed; Task 14 closes a
presentation gap the plan never covered.

### What the target machine taught us

An HP 8B7C laptop: Intel Core i7-1355U, Intel Iris Xe graphics, no discrete GPU. It runs
Memory Integrity (HVCI) and Credential Guard, enforced. That blocks the kernel driver
LibreHardwareMonitor needs, so on this machine:

- Every CPU temperature, clock and power reading is `null`. Loads still work, because
  Windows serves those from performance counters rather than model-specific registers.
  Running elevated does not help — what is blocked is the driver, not the privilege.
- No GPU temperature sensor exists at all for the integrated Iris Xe.
- GPU load does exist, but is named `D3D 3D`, which no rule in `SensorDefaults` matches.
- NVMe temperatures work fine (`/nvme/0/temperature/0`), because SMART needs no driver.

Meanwhile ACPI thermal zones, read through WMI with no driver at all, report `CPUZ_0` at
62 °C and `GFXZ_0` at 25 °C. Two of the five meters were dead for want of a data source
the operating system was willing to hand over all along.

Confirmed with all nine LibreHardwareMonitor providers enabled: the library does not
expose ACPI thermal zones. A second sensor source is genuinely needed.

---

### Task 13: ACPI thermal sensor source

**Files:**
- Create: `AnalogHwMonitor.Core/AcpiThermalSensorSource.cs`
- Create: `AnalogHwMonitor.Core/CompositeSensorSource.cs`
- Modify: `AnalogHwMonitor.Core/SensorDefaults.cs`
- Modify: `AnalogHwMonitor.Core/AnalogHwMonitor.Core.csproj` (add `System.Management`)
- Modify: `AnalogHwMonitor.App/Program.cs`
- Test: `AnalogHwMonitor.Tests/CompositeSensorSourceTests.cs`
- Test: `AnalogHwMonitor.Tests/AcpiThermalSensorSourceTests.cs`
- Test: `AnalogHwMonitor.Tests/Fakes/ThrowingSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/SensorDefaultsTests.cs` (append)

**Interfaces:**
- Consumes: `ISensorSource`, `SensorDescriptor`, `SensorKind`, `IAppLog`, `AppConfig`.
- Produces:
  - `sealed class AcpiThermalSensorSource : ISensorSource`, constructor `(IAppLog log)`,
    constant `IdPrefix = "/acpi/thermalzone/"`.
  - `sealed class CompositeSensorSource : ISensorSource`, constructor
    `(IAppLog log, params ISensorSource[] sources)`.
  - `SensorDefaults.AssignSensors(AppConfig, IReadOnlyList<SensorDescriptor>, Func<string, bool>? isReadable = null)`
    — a third, optional parameter. Existing two-argument calls keep compiling and keep
    their current behaviour.

**Why a readability predicate.** This is the crux. On the target machine
LibreHardwareMonitor reports a sensor literally called `CPU Package`, which matches the
first pattern of the CPU temperature rule — and reads `null` forever. Name matching alone
therefore picks the dead sensor over the live ACPI zone every time. Auto-detection must
prefer a candidate that currently returns a value, falling back to name order only when
nothing readable matches.

- [ ] **Step 1: Add the package**

```powershell
dotnet add AnalogHwMonitor.Core package System.Management
```

- [ ] **Step 2: Add the throwing fake and a Disposed flag**

Create `AnalogHwMonitor.Tests/Fakes/ThrowingSensorSource.cs`:

```csharp
using AnalogHwMonitor.Core;

namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>Fails at everything, to prove the composite survives a broken source.</summary>
public sealed class ThrowingSensorSource : ISensorSource
{
    public void Refresh() => throw new InvalidOperationException("refresh failed");

    public IReadOnlyList<SensorDescriptor> Discover() => throw new InvalidOperationException("discover failed");

    public float? Read(string sensorId) => throw new InvalidOperationException("read failed");

    public void Dispose()
    {
    }
}
```

Add a `public bool Disposed { get; private set; }` to `FakeSensorSource`, set by its
`Dispose()`. Change nothing else about it.

- [ ] **Step 3: Write the failing composite tests**

Create `AnalogHwMonitor.Tests/CompositeSensorSourceTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class CompositeSensorSourceTests
{
    private static FakeSensorSource SourceWith(string id, float? value, string name)
    {
        var source = new FakeSensorSource(new Dictionary<string, float?> { [id] = value });
        source.Sensors.Add(new SensorDescriptor(id, name, "Fake", SensorKind.Temperature, "°C"));
        return source;
    }

    [Fact]
    public void Discover_ConcatenatesEverySource()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        Assert.Equal(new[] { "a", "b" }, composite.Discover().Select(s => s.Id));
    }

    [Fact]
    public void Refresh_RefreshesEverySourceExactlyOnce()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        composite.Refresh();

        Assert.Equal(1, a.RefreshCount);
        Assert.Equal(1, b.RefreshCount);
    }

    [Fact]
    public void Refresh_KeepsGoingWhenOneSourceThrows()
    {
        var healthy = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource(), healthy);

        composite.Refresh();

        Assert.Equal(1, healthy.RefreshCount);
    }

    [Fact]
    public void Discover_KeepsGoingWhenOneSourceThrows()
    {
        var healthy = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource(), healthy);

        Assert.Equal(new[] { "b" }, composite.Discover().Select(s => s.Id));
    }

    [Fact]
    public void Read_FindsTheValueInWhicheverSourceHasIt()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        using var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        Assert.Equal(2f, composite.Read("b"));
    }

    [Fact]
    public void Read_ReturnsNullForAnUnknownId()
    {
        using var composite = new CompositeSensorSource(NullLog.Instance, SourceWith("a", 1, "A"));

        Assert.Null(composite.Read("nothing"));
    }

    [Fact]
    public void Read_ReturnsNullWhenASourceThrows()
    {
        using var composite = new CompositeSensorSource(NullLog.Instance, new ThrowingSensorSource());

        Assert.Null(composite.Read("anything"));
    }

    [Fact]
    public void Dispose_DisposesEverySource()
    {
        var a = SourceWith("a", 1, "A");
        var b = SourceWith("b", 2, "B");
        var composite = new CompositeSensorSource(NullLog.Instance, a, b);

        composite.Dispose();

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CompositeSensorSourceTests"`
Expected: build error, `CompositeSensorSource` does not exist.

- [ ] **Step 5: Write the composite**

Create `AnalogHwMonitor.Core/CompositeSensorSource.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Presents several sensor sources as one. A source that fails is skipped rather than
/// allowed to take the others down with it — losing the ACPI zones must not cost us the
/// CPU load, and vice versa.
/// </summary>
public sealed class CompositeSensorSource : ISensorSource
{
    private readonly IAppLog _log;
    private readonly ISensorSource[] _sources;
    private readonly bool[] _faultReported;

    public CompositeSensorSource(IAppLog log, params ISensorSource[] sources)
    {
        _log = log;
        _sources = sources;
        _faultReported = new bool[sources.Length];
    }

    public void Refresh()
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            Try(i, source =>
            {
                source.Refresh();
                return true;
            });
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover()
    {
        var all = new List<SensorDescriptor>();
        for (var i = 0; i < _sources.Length; i++)
        {
            var discovered = Try(i, source => source.Discover());
            if (discovered is not null)
            {
                all.AddRange(discovered);
            }
        }

        return all;
    }

    public float? Read(string sensorId)
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            var value = Try(i, source => source.Read(sensorId));
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (var i = 0; i < _sources.Length; i++)
        {
            Try(i, source =>
            {
                source.Dispose();
                return true;
            });
        }
    }

    /// <summary>Runs one source's operation, reporting a persistent fault only once.</summary>
    private T? Try<T>(int index, Func<ISensorSource, T> operation)
    {
        try
        {
            var result = operation(_sources[index]);
            _faultReported[index] = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_faultReported[index])
            {
                _log.Write($"Sensor source {_sources[index].GetType().Name} failed: {ex.Message}");
                _faultReported[index] = true;
            }

            return default;
        }
    }
}
```

- [ ] **Step 6: Run the composite tests**

Run: `dotnet test --filter "FullyQualifiedName~CompositeSensorSourceTests"`
Expected: PASS, 8 tests.

- [ ] **Step 7: Write the ACPI tests**

Create `AnalogHwMonitor.Tests/AcpiThermalSensorSourceTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// Reading ACPI thermal zones needs an elevated session, so the two hardware tests run
/// only when AHM_HARDWARE_TESTS=1 and report themselves as skipped otherwise. The third
/// runs everywhere on purpose: degrading to nothing is the required behaviour.
/// </summary>
public class AcpiThermalSensorSourceTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("AHM_HARDWARE_TESTS") == "1";

    [SkippableFact]
    public void Discover_FindsThermalZones()
    {
        Skip.IfNot(Enabled);

        using var source = new AcpiThermalSensorSource(NullLog.Instance);
        source.Refresh();
        var zones = source.Discover();

        Assert.NotEmpty(zones);
        Assert.All(zones, z => Assert.Equal(SensorKind.Temperature, z.Kind));
        Assert.All(zones, z => Assert.StartsWith(AcpiThermalSensorSource.IdPrefix, z.Id));
    }

    [SkippableFact]
    public void Read_ReturnsAPlausibleTemperature()
    {
        Skip.IfNot(Enabled);

        using var source = new AcpiThermalSensorSource(NullLog.Instance);
        source.Refresh();
        var zone = source.Discover().First();

        var value = source.Read(zone.Id);

        Assert.NotNull(value);
        Assert.InRange(value!.Value, -50f, 150f);
    }

    [Fact]
    public void Refresh_DegradesToNothingWhenTheQueryIsDenied()
    {
        using var source = new AcpiThermalSensorSource(NullLog.Instance);

        source.Refresh();

        Assert.Null(source.Read(AcpiThermalSensorSource.IdPrefix + "NOPE"));
    }
}
```

- [ ] **Step 8: Write the ACPI source**

Create `AnalogHwMonitor.Core/AcpiThermalSensorSource.cs`:

```csharp
using System.Management;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Reads ACPI thermal zones through WMI. No kernel driver is involved, which is the whole
/// point: where Memory Integrity blocks LibreHardwareMonitor's driver, these zones are the
/// only temperatures available. Needs elevation; without it the query is denied and this
/// source simply reports nothing rather than failing.
/// </summary>
public sealed class AcpiThermalSensorSource : ISensorSource
{
    public const string IdPrefix = "/acpi/thermalzone/";

    private readonly IAppLog _log;
    private readonly Dictionary<string, float> _values = new();
    private readonly List<SensorDescriptor> _descriptors = new();
    private bool _faultReported;

    public AcpiThermalSensorSource(IAppLog log) => _log = log;

    public void Refresh()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            _values.Clear();
            _descriptors.Clear();

            foreach (var zone in searcher.Get().Cast<ManagementBaseObject>())
            {
                using (zone)
                {
                    if (zone["InstanceName"] is not string instance || zone["CurrentTemperature"] is null)
                    {
                        continue;
                    }

                    // WMI reports tenths of a kelvin.
                    var kelvinTenths = Convert.ToDouble(zone["CurrentTemperature"]);
                    var celsius = (float)(kelvinTenths / 10.0 - 273.15);

                    var name = ShortName(instance);
                    var id = IdPrefix + name;

                    _values[id] = celsius;
                    _descriptors.Add(new SensorDescriptor(
                        id, name, "ACPI Thermal Zone", SensorKind.Temperature, "°C"));
                }
            }

            _faultReported = false;
        }
        catch (Exception ex)
        {
            if (!_faultReported)
            {
                _log.Write($"ACPI thermal zones unavailable: {ex.Message}");
                _faultReported = true;
            }

            _values.Clear();
            _descriptors.Clear();
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() => _descriptors;

    public float? Read(string sensorId) =>
        _values.TryGetValue(sensorId, out var value) ? value : null;

    public void Dispose()
    {
    }

    /// <summary>Turns "ACPI\ThermalZone\CPUZ_0" into "CPUZ_0".</summary>
    private static string ShortName(string instanceName)
    {
        var lastSeparator = instanceName.LastIndexOf('\\');
        return lastSeparator >= 0 && lastSeparator < instanceName.Length - 1
            ? instanceName[(lastSeparator + 1)..]
            : instanceName;
    }
}
```

- [ ] **Step 9: Run the ACPI tests**

Run: `dotnet test --filter "FullyQualifiedName~AcpiThermalSensorSourceTests"`
Expected: 1 passed, 2 skipped.

- [ ] **Step 10: Write the failing tests for readability-aware defaults**

Append to `AnalogHwMonitor.Tests/SensorDefaultsTests.cs`:

```csharp
    private static readonly SensorDescriptor[] HvciBlockedMachine =
    {
        new("/intelcpu/0/load/0",             "CPU Total",    "Intel Core i7-1355U", SensorKind.Load,        "%"),
        new("/intelcpu/0/temperature/12",     "CPU Package",  "Intel Core i7-1355U", SensorKind.Temperature, "°C"),
        new("/intelcpu/0/temperature/1",      "Core Average", "Intel Core i7-1355U", SensorKind.Temperature, "°C"),
        new("/gpu-intel-integrated/x/load/7", "D3D 3D",       "Intel Iris Xe",       SensorKind.Load,        "%"),
        new("/gpu-intel-integrated/x/load/8", "D3D Copy",     "Intel Iris Xe",       SensorKind.Load,        "%"),
        new("/ram/load/0",                    "Memory",       "Total Memory",        SensorKind.Load,        "%"),
        new("/acpi/thermalzone/CPUZ_0",       "CPUZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
        new("/acpi/thermalzone/GFXZ_0",       "GFXZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
        new("/acpi/thermalzone/PCHZ_0",       "PCHZ_0",       "ACPI Thermal Zone",   SensorKind.Temperature, "°C"),
    };

    /// <summary>On the blocked machine every CPU-package temperature reads null.</summary>
    private static bool ReadableOnBlockedMachine(string id) =>
        !id.StartsWith("/intelcpu/0/temperature", StringComparison.Ordinal);

    [Fact]
    public void AssignSensors_FindsIntelIntegratedGpuLoadByItsD3dName()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.Equal("/gpu-intel-integrated/x/load/7", config.Channels[1].SensorId);
    }

    [Fact]
    public void AssignSensors_PrefersAReadableAcpiZoneOverADeadCpuPackageSensor()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.Equal("/acpi/thermalzone/CPUZ_0", config.Channels[3].SensorId);
        Assert.Equal("/acpi/thermalzone/GFXZ_0", config.Channels[4].SensorId);
    }

    [Fact]
    public void AssignSensors_StillPrefersTheVendorSensorWhenItIsReadable()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, _ => true);

        Assert.Equal("/intelcpu/0/temperature/12", config.Channels[3].SensorId);
    }

    [Fact]
    public void AssignSensors_NeverPicksAnUnrelatedThermalZone()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, HvciBlockedMachine, ReadableOnBlockedMachine);

        Assert.DoesNotContain("PCHZ_0", string.Join(",", config.Channels.Select(c => c.SensorId)));
    }

    [Fact]
    public void AssignSensors_WithoutAReadabilityPredicateBehavesAsBefore()
    {
        var config = AppConfig.CreateDefault();

        SensorDefaults.AssignSensors(config, AmdMachine);

        Assert.Equal("/amdcpu/0/temperature/0", config.Channels[3].SensorId);
    }
```

- [ ] **Step 11: Extend `SensorDefaults`**

Three changes to `AnalogHwMonitor.Core/SensorDefaults.cs`.

First, a rule may now carry more than one preference hint, because the ACPI zones use
`gfx` where the vendor GPU uses `gpu`. Change `Rule`'s hint from a single string to a
string array, and change `Match` so a sensor qualifies when its identifier contains ANY
of the rule's hints. The mandatory-hint behaviour is unchanged: a hint starting with `/`
means no widening, and it stays that way for the memory rule.

Second, the rules gain the new patterns. The ACPI names come last in each temperature
rule, so a machine whose vendor sensor works still prefers it:

```csharp
    private static readonly Rule[] Rules =
    {
        new(SensorKind.Load,        new[] { "CPU Total", "CPU" },                                   new[] { "cpu" }),
        new(SensorKind.Load,        new[] { "GPU Core", "D3D 3D", "GPU" },                          new[] { "gpu" },
            Exclude: new[] { "Memory" }),
        new(SensorKind.Load,        new[] { "Memory" },                                             new[] { "/ram" }),
        new(SensorKind.Temperature, new[] { "CPU Package", "Tctl", "Core Average", "CPUZ", "CPU" }, new[] { "cpu" }),
        new(SensorKind.Temperature, new[] { "GPU Core", "GFXZ", "GPU" },                            new[] { "gpu", "gfx" },
            Exclude: new[] { "Memory" }),
    };
```

Third, `AssignSensors` takes the predicate and tries readable candidates first:

```csharp
    public static void AssignSensors(
        AppConfig config,
        IReadOnlyList<SensorDescriptor> sensors,
        Func<string, bool>? isReadable = null)
    {
        for (var i = 0; i < config.Channels.Count && i < Rules.Length; i++)
        {
            if (!string.IsNullOrEmpty(config.Channels[i].SensorId))
            {
                continue;
            }

            // A sensor that exists but never returns a value is worse than none: it parks
            // a needle at zero and looks like a working channel reading nothing.
            var readable = isReadable is null
                ? sensors
                : sensors.Where(s => isReadable(s.Id)).ToList();

            config.Channels[i].SensorId =
                (Match(readable, Rules[i]) ?? Match(sensors, Rules[i]))?.Id;
        }
    }
```

- [ ] **Step 12: Run the sensor-defaults tests**

Run: `dotnet test --filter "FullyQualifiedName~SensorDefaultsTests"`
Expected: PASS — the seven existing tests plus the five new ones.

- [ ] **Step 13: Compose both sources in the application**

In `AnalogHwMonitor.App/Program.cs`, build the sensor source as a
`CompositeSensorSource` over `LibreHardwareSensorSource` and `AcpiThermalSensorSource`,
then, after the first `Refresh()`, pass `id => sensors.Read(id) is not null` as the
readability predicate to `SensorDefaults.AssignSensors`.

Keep the existing behaviour: the friendly message box when the hardware monitor cannot be
opened at all, saving the config only when a channel had no sensor assigned, and disposal
flowing through `MonitorService`. Note the composite swallows a failing source, so the
message box now only appears if construction itself throws.

- [ ] **Step 14: Build and run the whole suite**

Run: `dotnet build` then `dotnet test`
Expected: build clean; suite green, with the ACPI hardware tests skipped.

- [ ] **Step 15: Commit**

```bash
git add AnalogHwMonitor.Core AnalogHwMonitor.App/Program.cs AnalogHwMonitor.Tests
git commit -m "feat: read ACPI thermal zones and prefer readable sensors by default"
```

- [ ] **Step 16: Owner verification (deferred)**

On the target machine, in an elevated shell: delete `config.json`, start the application,
and confirm all five channels get assigned — CPU load, `D3D 3D`, memory, `CPUZ_0`,
`GFXZ_0` — and that all five needles move.

---

### Task 14: Application icon

**Files:**
- Create: `tools/IconGenerator/IconGenerator.csproj`
- Create: `tools/IconGenerator/Program.cs`
- Create: `AnalogHwMonitor.App/appicon.ico` (generated, committed)
- Create: `AnalogHwMonitor.App/appicon-warning.ico` (generated, committed)
- Create: `AnalogHwMonitor.App/AppIcons.cs`
- Modify: `AnalogHwMonitor.App/AnalogHwMonitor.App.csproj`
- Modify: `AnalogHwMonitor.App/TrayApplicationContext.cs`
- Modify: `AnalogHwMonitor.App/SettingsForm.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing from the application; the generator is standalone.
- Produces: `static class AppIcons` in namespace `AnalogHwMonitor.App`, exposing
  `static Icon Normal` and `static Icon Warning`, each loaded once from an embedded
  resource and cached for the process lifetime.

**Why a generator rather than a downloaded file.** No licence to track, no attribution to
carry, and the shape can be regenerated at any size. Committing the generator keeps the
icon reproducible instead of an opaque binary nobody can regenerate.

**The design.** A round dial: dark face, light bezel, a 240-degree arc of scale with five
tick marks, and a needle resting at about 70 percent of scale. The warning variant is the
same dial with an amber dot in the lower right. Both must stay legible at 16 px, which
rules out text, sub-pixel outlines, and low contrast between needle and face.

- [ ] **Step 1: Create the generator project**

Create `tools/IconGenerator/IconGenerator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>icongen</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="8.0.8" />
  </ItemGroup>

</Project>
```

This project is deliberately NOT added to `AnalogHwMonitor.sln`. It runs by hand when the
icon needs regenerating and must never take part in the application's build.

- [ ] **Step 2: Write the generator**

Create `tools/IconGenerator/Program.cs`. It takes two output paths as arguments and
writes the normal and warning icons. Requirements it must satisfy:

- Render at 16, 32, 48, 64, 128 and 256 pixels, and pack all six into one `.ico`.
- Derive every dimension from the bitmap size, so the dial holds its proportions at 16 px.
- Draw with `SmoothingMode.AntiAlias` on a transparent background.
- Write the ICO container by hand: a 6-byte `ICONDIR` (`reserved=0`, `type=1`,
  `count=6`), then one 16-byte `ICONDIRENTRY` per image, then the image payloads. Store
  each image as PNG, and write `0` in the entry's width and height bytes for the 256 px
  image, as the format requires. `Icon.Save` cannot produce a multi-size file, which is
  why this is written out by hand.
- The warning variant draws the same dial, then an amber filled circle with a dark outline
  in the lower-right quadrant, sized to stay visible at 16 px.

- [ ] **Step 3: Generate both icons**

```powershell
dotnet run --project tools\IconGenerator -- AnalogHwMonitor.App\appicon.ico AnalogHwMonitor.App\appicon-warning.ico
```

- [ ] **Step 4: Verify they really are multi-size icons**

```powershell
$bytes = [System.IO.File]::ReadAllBytes("AnalogHwMonitor.App\appicon.ico")
"reserved=$([BitConverter]::ToUInt16($bytes,0)) type=$([BitConverter]::ToUInt16($bytes,2)) images=$([BitConverter]::ToUInt16($bytes,4))"
```

Expected: `reserved=0 type=1 images=6`. Repeat for the warning icon.

- [ ] **Step 5: Wire the icons into the application**

In `AnalogHwMonitor.App.csproj` set `<ApplicationIcon>appicon.ico</ApplicationIcon>` and
embed both files as resources.

Create `AnalogHwMonitor.App/AppIcons.cs` exposing `Normal` and `Warning`, each read once
from its embedded stream and cached in a static field. These are process-wide singletons;
nothing disposes them.

Replace `SystemIcons.Application` and `SystemIcons.Warning` in `TrayApplicationContext`
with `AppIcons.Normal` and `AppIcons.Warning`, and set `SettingsForm.Icon` to
`AppIcons.Normal`.

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build` then `dotnet test`
Expected: build clean, suite unchanged and green.

- [ ] **Step 7: Update the README**

Under project layout, note that `tools/IconGenerator` draws the committed icons and give
the one-line command to regenerate them.

- [ ] **Step 8: Commit**

```bash
git add tools AnalogHwMonitor.App README.md
git commit -m "feat: draw and wire the analog dial application icon"
```

- [ ] **Step 9: Owner verification (deferred)**

Start the application and confirm the dial appears in the system tray, on the settings
window, and on the executable in Explorer, and that pulling the USB cable swaps the tray
icon for the warning variant.
