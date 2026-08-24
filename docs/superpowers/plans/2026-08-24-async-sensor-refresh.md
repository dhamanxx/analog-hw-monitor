# Asynchrónny refresh senzorov a odoslanie rámca — implementačný plán

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dostať z UI vlákna 99 ms `ISensorSource.Refresh()` a blokujúci sériový zápis, aby ručičky vo VU režime prestali zadrhávať — bez zmeny kanálového modelu, kalibrácie, settings okna či formátu rámca.

**Architecture:** Tri vlákna namiesto jedného. Poll task volá `Refresh()` raz za sekundu; každý `ISensorSource` sa stane bezpečným na „`Refresh()` na jednom vlákne, `Read()` na druhom" tým, že si publikuje nemenný snapshot a atomicky ho prehodí. UI timer ďalej stavia rámec a vyvoláva `Updated`, ale rámec už len zaradí do `Channel<string>` s kapacitou 1; sender task ho vyprázdňuje do `SerialMeterLink`.

**Tech Stack:** .NET 10 (`net10.0-windows`), WinForms, xunit, Xunit.SkippableFact, LibreHardwareMonitorLib, NAudio, System.Management. Žiadny nový NuGet balík.

**Spec:** [`../specs/2026-08-24-async-sensor-refresh-design.md`](../specs/2026-08-24-async-sensor-refresh-design.md)

## Global Constraints

- Branch je `feature/async-sensor-refresh`, už existuje a spec je na nej commitnutý.
- **Žiadny nový NuGet balík.** Zvlášť nie `Microsoft.Extensions.TimeProvider.Testing` — rozdelenie `RefreshOnce()`/`RunAsync()` ho robí nepotrebným.
- **Arduino sketch sa nemení.** Formát rámca zostáva `V:a,b,c,d,e\n`, `FrameCodec.ChannelCount = 5`, `FrameCodec.Banner = "AHM1"`, sketch `WATCHDOG_MS = 3000`.
- **Refresh interval je `TimeSpan.FromSeconds(1)` v oboch režimoch.** `AppConfig.VuMode` už refresh neovplyvňuje.
- **Rámec sa posiela pri každom ticku**, aj keď sa nič nezmenilo. Žiadne „pošli len pri zmene" — zrazilo by to sketch watchdog.
- **Tick rate zostáva `VuIntervalMs = 40` / `SensorIntervalMs = 1000`** v `TrayApplicationContext`. Tento plán ich nemení.
- **Všetky komentáre v kóde sú po anglicky** — doc komentáre aj inline, v `Core`, `App` aj `Tests`. Rovnako commit messages. Celá existujúca kódbáza to tak má a slovenčina je v repe len v `docs/superpowers/`. Ak snippet v tomto pláne obsahuje slovenský komentár, je to chyba plánu: prelož ho, neprepisuj slovenčinu do kódu.
- Komentárový štýl repa: vysvetľuj **prečo**, nie čo, a namerané čísla nes ďalej. Testy sa píšu ako veta o chovaní (`Refresh_ReleasesTheDeviceAfterFiveSecondsWithoutAReader`), nie `Test1`.
- Hardware testy sú `[SkippableFact]` so `Skip.IfNot(Enabled)` proti `AHM_HARDWARE_TESTS == "1"`.
- Build a testy: `dotnet build AnalogHwMonitor.sln` a `dotnet test AnalogHwMonitor.sln`.

## Pasca, ktorú treba poznať pred Task 7

`TrayApplicationContext` konstruktor beží na UI vlákne, kde je nainštalovaný WinForms `SynchronizationContext`. Keby sa slučka spustila ako `_ = refreshLoop.RunAsync(_cts.Token);`, prvý `await` by sa síce vrátil hneď, ale **jeho continuation by sa zmarshallovala späť na UI vlákno** — a `Refresh()` by tam bežal ďalej. Celá zmena by bola no-op, ktorý sa nedá odhaliť inak než profilerom.

Preto sa obe slučky štartujú výhradne cez `Task.Run(...)`, ktorý ich posadí na thread pool, kde `SynchronizationContext.Current` je `null`. `.ConfigureAwait(false)` vnútri slučiek je zapísaná intencia; garanciu dáva `Task.Run`.

## File Structure

| Súbor | Zodpovednosť |
| --- | --- |
| `Core/SensorRefreshLoop.cs` | **nový** — kadencia `Refresh()`; `RefreshOnce()` je logika, `RunAsync()` je hodiny |
| `Core/QueuedMeterLink.cs` | **nový** — `IMeterLink` dekorátor; `Send()` zaradí, `RunAsync()` vyprázdňuje |
| `Core/LibreHardwareSensorSource.cs` | snapshot `SensorId → float` + list deskriptorov, atomicky prehadzovaný |
| `Core/AcpiThermalSensorSource.cs` | to isté, namiesto mutácie na mieste |
| `Core/AudioLevelSensorSource.cs` | `lock` na životný cyklus captureu; `OnSamples` mimo locku |
| `Core/SerialMeterLink.cs` | `lock` na port; `IsConnected` ako `volatile bool` mimo locku |
| `Core/MonitorService.cs` | `Tick()` už nerefreshuje |
| `Core/ThrottledSensorSource.cs` | **zmazaný** |
| `App/TrayApplicationContext.cs` | vlastní `CancellationTokenSource` a oba tasky |
| `App/Program.cs` | drôtovanie |

Testové fakes (`FakeSensorSource`, `FakeMeterLink`, `FakeSerialPort`, `FakeAudioLoopbackCapture`, `FakeTimeProvider`, `ThrowingSensorSource`, `RecordingLog`) sa **nemenia**. Tasky 3 a 4 zámerne nepridávajú testy ani hooky — dôvod je v ich vlastných sekciách.

---

### Task 1: `AcpiThermalSensorSource` publikuje snapshot namiesto mutácie na mieste

**Files:**
- Modify: `AnalogHwMonitor.Core/AcpiThermalSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/AcpiThermalSensorSourceTests.cs`

**Interfaces:**
- Consumes: nič.
- Produces: `AcpiThermalSensorSource` naďalej implementuje `ISensorSource` s nezmenenou signatúrou (`void Refresh()`, `IReadOnlyList<SensorDescriptor> Discover()`, `float? Read(string)`, `void Dispose()`). Konstruktor zostáva `AcpiThermalSensorSource(IAppLog log)`. Konstanta `IdPrefix = "/acpi/thermalzone/"` zostáva.

Dnešný `Refresh()` volá `_values.Clear()` a `_descriptors.Clear()` a napĺňa tie isté instance. `Discover()` vydáva živý `List`. Kým je všetko na UI vlákne, je to bezpečné; vo chvíli, keď `Refresh()` odíde na poll task, je `TryGetValue` nad mutovaným `Dictionary`om nedefinované chovanie.

- [ ] **Step 1: Write the failing test**

Do `AnalogHwMonitor.Tests/AcpiThermalSensorSourceTests.cs` pridaj:

```csharp
    /// <summary>
    /// Refresh() now runs on the poll task while the UI thread reads. Emptying and
    /// refilling the same collection is then undefined behaviour, so every Refresh()
    /// must publish a new instance and leave the old one alone. The test needs neither
    /// an elevated session nor a single thermal zone: instance identity is observable
    /// even when the list is empty.
    /// </summary>
    [Fact]
    public void Refresh_PublishesANewListRatherThanEmptyingTheOldOne()
    {
        using var source = new AcpiThermalSensorSource(NullLog.Instance);
        source.Refresh();
        var first = source.Discover();

        source.Refresh();

        Assert.NotSame(first, source.Discover());
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AcpiThermalSensorSourceTests.Refresh_PublishesANewListRatherThanEmptyingTheOldOne"`

Expected: FAIL — `Assert.NotSame() Failure`. Dnešný `Discover()` vracia vždy to isté `_descriptors`.

- [ ] **Step 3: Write minimal implementation**

V `AnalogHwMonitor.Core/AcpiThermalSensorSource.cs` nahraď dve mutovateľné polia jedným prehadzovaným snapshotom:

```csharp
    /// <summary>
    /// The values and descriptors from one Refresh(). One object rather than two fields
    /// on purpose: with two separate writes a reader could see the new descriptors beside
    /// the old values. A published instance is never mutated again, so the UI thread may
    /// read it while the poll task builds the next one.
    /// </summary>
    private sealed record Snapshot(
        Dictionary<string, float> Values,
        IReadOnlyList<SensorDescriptor> Descriptors);

    private readonly IAppLog _log;

    private Snapshot _snapshot =
        new(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>());

    private bool _faultReported;

    public AcpiThermalSensorSource(IAppLog log) => _log = log;

    public void Refresh()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            var values = new Dictionary<string, float>();
            var descriptors = new List<SensorDescriptor>();

            // The collection itself is a WMI/COM enumerator and must be disposed
            // alongside the objects it yields — Refresh runs once a second for the
            // life of the tray application, so leaving this to the finalizer would
            // leak one per tick.
            using var zones = searcher.Get();
            foreach (var zone in zones.Cast<ManagementBaseObject>())
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

                    values[id] = celsius;
                    descriptors.Add(new SensorDescriptor(
                        id, name, "ACPI Thermal Zone", SensorKind.Temperature, "°C"));
                }
            }

            Volatile.Write(ref _snapshot, new Snapshot(values, descriptors));
            _faultReported = false;
        }
        catch (Exception ex)
        {
            if (!_faultReported)
            {
                _log.Write($"ACPI thermal zones unavailable: {ex.Message}");
                _faultReported = true;
            }

            // Same semantics as the Clear() this replaced: after a failure nothing reads.
            // A new List rather than Array.Empty<SensorDescriptor>(), which is a cached
            // singleton — two failed refreshes would hand back the same instance, and on a
            // machine without elevation this path is the common one, not the rare one.
            Volatile.Write(
                ref _snapshot,
                new Snapshot(new Dictionary<string, float>(), new List<SensorDescriptor>()));
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
        Volatile.Read(ref _snapshot).Descriptors;

    public float? Read(string sensorId) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(sensorId, out var value) ? value : null;
```

`_faultReported` zostáva obyčajným polom: dotýka sa ho výhradne `Refresh()`, teda len poll task.

**Nezdieľaj prázdny snapshot — ani cez statickú konstantu, ani cez `Array.Empty<T>()`.** Na stroji bez elevácie zlyhá WMI dotaz pri každom `Refresh()`, takže chybová cesta je tam tá bežná. `Array.Empty<T>()` vracia cachovaný singleton, takže dva zlyhané refreshy by vrátili ten istý objekt a test zo Step 1 by padol aj nad správnou implementáciou. V `catch` teda `new List<SensorDescriptor>()`. Pri inicializácii poľa `Array.Empty` neprekáža — tú hodnotu nikto s druhým refreshom neporovnáva.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AcpiThermalSensorSourceTests"`

Expected: PASS. `Refresh_DegradesToNothingWhenTheQueryIsDenied` musí prejsť ďalej; dve `[SkippableFact]` sa preskočia bez `AHM_HARDWARE_TESTS=1`.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/AcpiThermalSensorSource.cs AnalogHwMonitor.Tests/AcpiThermalSensorSourceTests.cs
git commit -m "refactor: publish the ACPI zones as a snapshot instead of clearing in place"
```

---

### Task 2: `LibreHardwareSensorSource` publikuje snapshot a `Read()` prestane prechádzať strom

**Files:**
- Modify: `AnalogHwMonitor.Core/LibreHardwareSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs`

**Interfaces:**
- Consumes: nič.
- Produces: nezmenená signatúra `ISensorSource`; konstruktor zostáva bezparametrový `LibreHardwareSensorSource()`. Privátne `ToKind(SensorType)` a `ToUnit(SensorType)` zostávajú a používajú sa v `Refresh()`.

Dnes prechádza strom `Computer.Hardware` **na troch miestach**: `Refresh()` (`Update()`, 99 ms), `Read()` (LINQ hľadanie, 5× za tick) a `Discover()`. Navyše `sensor.Identifier.ToString()` v predikáte `Read()` alokuje string na každý senzor pri každom porovnaní. Po tomto tasku je prechod jeden — v `Refresh()`.

- [ ] **Step 1: Write the failing test**

Do `AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs` pridaj:

```csharp
    /// <summary>
    /// Discover() and Read() must read the snapshot built in Refresh(), not walk the
    /// live tree — otherwise the walk moves back to the UI thread and Refresh() on the
    /// poll task mutates it out from under it. The identity of the returned list proves
    /// this: today it produces a new List on every call, after the change it is the
    /// same object until the next Refresh().
    /// </summary>
    [SkippableFact]
    public void Discover_ReturnsTheSameSnapshotUntilTheNextRefresh()
    {
        Skip.IfNot(Enabled);

        using var source = new LibreHardwareSensorSource();
        source.Refresh();

        var first = source.Discover();

        Assert.Same(first, source.Discover());

        source.Refresh();

        Assert.NotSame(first, source.Discover());
    }
```

`Enabled` je existujúca privátna statická vlastnosť v tom súbore (`AHM_HARDWARE_TESTS == "1"`); nič nové netreba pridávať.

- [ ] **Step 2: Run test to verify it fails**

Run: `$env:AHM_HARDWARE_TESTS = "1"; dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~LibreHardwareSensorSourceTests.Discover_ReturnsTheSameSnapshotUntilTheNextRefresh"`

Expected: FAIL — `Assert.Same() Failure`. Dnešný `Discover()` končí `.ToList()`, takže každé volanie vracia nový objekt.

Bez elevácie sa test preskočí (`SKIPPED`), čo je tiež platný výsledok tohto kroku — pokračuj na Step 3 a spoliehaj sa na Step 4.

- [ ] **Step 3: Write minimal implementation**

V `AnalogHwMonitor.Core/LibreHardwareSensorSource.cs` pridaj snapshot a prepíš tri metódy. `UpdateVisitor` a `EnumerateSensors()` zostávajú bez zmeny:

```csharp
    /// <summary>
    /// The values and descriptors from one Refresh(). One object rather than two fields
    /// on purpose: with two separate writes a reader could see the new descriptors
    /// beside the old values. A published instance is never mutated again, so the UI
    /// thread may read it while the poll task builds the next one.
    /// </summary>
    private sealed record Snapshot(
        Dictionary<string, float> Values,
        IReadOnlyList<SensorDescriptor> Descriptors);

    private Snapshot _snapshot =
        new(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>());

    /// <summary>
    /// The only place the tree is walked. Update() and reading the values out happen in
    /// the same pass; building the snapshot lazily on the first Read() would put the walk
    /// back on the UI thread and solve nothing.
    ///
    /// Update() has hardware granularity: asking for one GPU temperature pays for every
    /// value that GPU exposes. Measured on an RTX 4070 that is 77 ms, almost all of it
    /// Windows' own GPU Engine performance counters rather than the driver. The whole AMD
    /// CPU through PawnIO is 1.6 ms by comparison. That is why this runs on the poll task,
    /// once a second.
    /// </summary>
    public void Refresh()
    {
        _computer.Accept(_visitor);

        var values = new Dictionary<string, float>();
        var descriptors = new List<SensorDescriptor>();

        foreach (var (hardware, sensor) in EnumerateSensors())
        {
            var id = sensor.Identifier.ToString();

            // A sensor with no value stays out of the dictionary, so TryGetValue returns
            // false and Read() returns null — exactly what sensor.Value returned before.
            if (sensor.Value is { } value)
            {
                values[id] = value;
            }

            descriptors.Add(new SensorDescriptor(
                id,
                sensor.Name,
                hardware.Name,
                ToKind(sensor.SensorType),
                ToUnit(sensor.SensorType)));
        }

        Volatile.Write(ref _snapshot, new Snapshot(values, descriptors));
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
        Volatile.Read(ref _snapshot).Descriptors;

    public float? Read(string sensorId) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(sensorId, out var value) ? value : null;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~LibreHardwareSensorSourceTests"`

Expected: PASS alebo SKIPPED (bez elevácie). Ak beží elevated s `AHM_HARDWARE_TESTS=1`, všetkých päť testov musí prejsť — zvlášť tie, ktoré overujú, že `Discover()` nájde senzory a `Read()` vráti hodnotu, keďže obe teraz idú cez snapshot.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS — celá suite, aby sa ukázalo, že nič iné na tomto source nezáviselo.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/LibreHardwareSensorSource.cs AnalogHwMonitor.Tests/LibreHardwareSensorSourceTests.cs
git commit -m "perf: read LibreHardwareMonitor from a snapshot instead of walking the tree per read"
```

---

### Task 3: `AudioLevelSensorSource` zamkne životný cyklus captureu

**Files:**
- Modify: `AnalogHwMonitor.Core/AudioLevelSensorSource.cs`
- Test: `AnalogHwMonitor.Tests/AudioLevelSensorLifecycleTests.cs` (bez zmeny — slúži ako regresná sieť)

**Interfaces:**
- Consumes: nič.
- Produces: nezmenená signatúra `ISensorSource` a konstruktor `AudioLevelSensorSource(IAudioLoopbackCapture capture, IAppLog log, Func<bool>? compensateVolume = null, TimeProvider? time = null)`. Konstanty `IdleTimeout`, `SilenceGap`, `MaxCompensationDb`, `StartRetryInterval` zostávajú.

Po Task 7 volá `Refresh()` poll task a `Read()` UI vlákno. Súťažia o `_started`, `_reportedError`, `_lastFailedStart`, `_lastRead`, o `_capture` aj o reset integrátorov. Najhorší prípad: poll task sa rozhodne pre `Stop()` (slúchadlá von) presne vtedy, keď je UI vlákno vnútri `TryStart()` — výsledkom je `_started == true` nad zastaveným captureom a obe ručičky ležia mŕtve.

**Tento task nepridáva žiadny test, a je to vedomé rozhodnutie.** Lock sa nedá pripnúť testom, ktorý pred jeho pridaním padá — neexistencia locku sa nedá odmerať. Ochranou proti regresii je existujúci `AudioLevelSensorLifecycleTests` (12 testov, ktoré prechádzajú `Refresh()`/`Read()` v sekvencii) a `AudioLevelSensorSourceTests`. Obe musia po zmene prejsť nezmenené. **Nepridávaj `BlockInTryStart` ani podobné hooky do `FakeAudioLoopbackCapture`** — bola to zvažovaná a zamietnutá možnosť.

- [ ] **Step 1: Establish the baseline**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AudioLevelSensor"`

Expected: PASS. Zapíš si počet testov — po Step 3 musí byť rovnaký a všetky musia prejsť.

- [ ] **Step 2: Write the implementation**

V `AnalogHwMonitor.Core/AudioLevelSensorSource.cs` pridaj pole:

```csharp
    /// <summary>
    /// Serialises the capture lifecycle. Refresh() runs on the poll task and Read() on the
    /// UI thread, so without this the health check's Stop() and a read's TryStart() could
    /// interleave and leave _started true over a stopped capture — both needles dead until
    /// something shakes them.
    ///
    /// OnSamples deliberately does NOT take this lock: it runs on the capture thread at
    /// buffer rate and must never wait on the UI thread. It communicates only through
    /// Volatile/Interlocked (_lastBufferTicks, _lastAdvanceTicks, VuIntegrator._level),
    /// and that is enough.
    /// </summary>
    private readonly object _lifecycle = new();
```

Obal telo `Refresh()` celé:

```csharp
    public void Refresh()
    {
        lock (_lifecycle)
        {
            if (!_started)
            {
                return;
            }

            if (_time.GetUtcNow() - _lastRead > IdleTimeout)
            {
                Stop();
                return;
            }

            // Headphones in, speakers out: the daily case, and the one a VU meter notices
            // immediately because both needles go dead. But the same comparison also catches
            // a capture that died under us — the adapter clears its device id on an
            // unsolicited stop, so a mismatch here means either the default device moved or
            // the capture is gone, and both want the same response. Narrowing this condition
            // to require a non-null device id would silently delete the exclusive-mode
            // recovery: it would pass every existing test and stop noticing the second case.
            var current = _capture.CurrentDefaultDeviceId;
            if (current is not null && current != _capture.DeviceId)
            {
                _log.Write($"The audio capture is no longer on the default device ({current}); restarting it.");
                Stop();   // the next Read starts it again, on the new device
            }
        }
    }
```

V `Read()` posuň lock **za** early return pre cudzie id, aby sa composite pri troch neaudio kanáloch vôbec nezamykal:

```csharp
    public float? Read(string sensorId)
    {
        var channel = sensorId switch
        {
            AudioSensorIds.Left => 0,
            AudioSensorIds.Right => 1,
            _ => -1,
        };

        // The composite asks every source for every identifier, so most calls here are
        // about somebody else's sensor. This early return sits before the lock on purpose:
        // two of the five channels are audio, so three calls a tick never take it at all.
        if (channel < 0)
        {
            return null;
        }

        lock (_lifecycle)
        {
            _lastRead = _time.GetUtcNow();

            if (!EnsureStarted())
            {
                return null;
            }

            if (_capture.IsMuted)
            {
                return (float)AudioSensorIds.FloorDbfs;
            }

            ApplySilenceDecay();

            var level = _integrators[channel].Level * AverageToPeak;

            // Returned before the compensation rather than clamped after it: the
            // compensation would otherwise lift the floor by up to the ceiling, and
            // digital silence would read -60 dBFS on a quiet system while the mute path
            // above returned -100 for the same absence of signal.
            if (level <= 0.0)
            {
                return (float)AudioSensorIds.FloorDbfs;
            }

            var dbfs = 20.0 * Math.Log10(level);

            if (_compensateVolume())
            {
                dbfs += Math.Min(-_capture.VolumeDb, MaxCompensationDb);
            }

            return (float)Math.Max(dbfs, AudioSensorIds.FloorDbfs);
        }
    }
```

A obal `Dispose()`:

```csharp
    public void Dispose()
    {
        lock (_lifecycle)
        {
            Stop();
        }

        _capture.Dispose();
    }
```

`EnsureStarted()` a `Stop()` **nechaj bez vlastného locku** — volajú sa výhradne z metód, ktoré ho už držia. `lock` v C# je reentrantný, takže pridať ho aj tam by nebolo chybou, len šumom; komentárom napíš, že volajúci lock drží.

`OnSamples` a `ApplySilenceDecay` nechaj presne ako sú.

- [ ] **Step 3: Run tests to verify nothing regressed**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AudioLevelSensor"`

Expected: PASS — rovnaký počet testov ako v Step 1, všetky zelené. Zvlášť `ARoundOfStartsAndStopsLeavesNothingSubscribed`, `Refresh_RestartsWhenTheCaptureDiedUnderUs` a `Refresh_RestartsOnTheNewDeviceWhenTheDefaultOutputChanges`: všetky tri idú cez `Refresh()`/`Read()` v sekvencii, teda cez nový lock.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add AnalogHwMonitor.Core/AudioLevelSensorSource.cs
git commit -m "fix: serialise the audio capture lifecycle without touching the capture thread"
```

---

### Task 4: `SerialMeterLink` zamkne port, ale `IsConnected` nechá voľné

**Files:**
- Modify: `AnalogHwMonitor.Core/SerialMeterLink.cs`
- Test: `AnalogHwMonitor.Tests/SerialMeterLinkTests.cs` (bez zmeny — slúži ako regresná sieť)

**Interfaces:**
- Consumes: nič.
- Produces: nezmenené `IMeterLink` (`bool IsConnected`, `string? LastError`, `void Send(string)`, `void Dispose()`), plus `string? PortName { get; set; }` a `bool TryConnect()`. Konstruktor zostáva `SerialMeterLink(ISerialPortFactory factory, string? portName, IAppLog log, TimeProvider? time = null)`. Konstanty `BannerReadAttempts` a `ReconnectInterval` zostávajú.

Po Task 8 beží `Send()` na sender tasku, kým `PortName` setter beží na UI vlákne (`SettingsForm.cs:398`, `_link.PortName = _monitor.Config.ComPort;`) a volá `Disconnect()` → `_port.Dispose()`. Disponovať port spod prebiehajúceho `Write()` je chyba.

Zároveň platí obmedzenie zo spec rozhodnutia 7: **`IsConnected` sa nesmie čítať pod lockom.** Sender task ho drží počas `Write()`, čo je na zaseknutom porte sekundy, a UI si `IsConnected` číta každý tick pre tooltip — lock na getteri by vyrobil presne to tuhnutie UI, ktoré celý plán odstraňuje. To je pri implementácii najdôležitejšia vec na tomto tasku.

**Tento task nepridáva žiadny test, a je to vedomé rozhodnutie** — z rovnakého dôvodu ako Task 3. Ochranou je existujúci `SerialMeterLinkTests`, ktorý musí prejsť nezmenený. **Nepridávaj `BlockInWrite` ani podobné hooky do `FakeSerialPort`** — bola to zvažovaná a zamietnutá možnosť.

- [ ] **Step 1: Establish the baseline**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SerialMeterLinkTests"`

Expected: PASS. Zapíš si počet testov — po Step 3 musí byť rovnaký a všetky musia prejsť.

- [ ] **Step 2: Write the implementation**

V `AnalogHwMonitor.Core/SerialMeterLink.cs`:

```csharp
    /// <summary>
    /// Serialises the port. Send() runs on the sender task while the PortName setter runs
    /// on the UI thread (the settings window changes the COM port at runtime) and disposes
    /// _port — without this the port would be disposed under a write in progress.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// A copy of _port?.IsOpen kept outside the lock. The UI thread reads IsConnected every
    /// tick for the tooltip and must not wait on a stuck write — under the lock that would
    /// move the UI stall rather than remove it. Reading a disposed SerialPort would also
    /// throw ObjectDisposedException on its own.
    /// </summary>
    private volatile bool _connected;

    public bool IsConnected => _connected;
```

`PortName` setter obal celý:

```csharp
    public string? PortName
    {
        get => _portName;
        set
        {
            lock (_gate)
            {
                if (_portName == value)
                {
                    return;
                }

                _portName = value;

                // A new port is a new story: whatever was already reported about the old
                // one must not silence the first failure of this one.
                _reportedError = null;
                Disconnect();
            }
        }
    }
```

`TryConnect()` a `Send()` obal celé do `lock (_gate) { ... }`. Vo `TryConnect()` nastav `_connected = true` tam, kde sa dnes nastavuje `_port = port`, a `_connected = false` na oboch zlyhaných cestách. V `Disconnect()` nastav `_connected = false` vo `finally` vedľa `_port = null`:

```csharp
    private void Disconnect()
    {
        try
        {
            _port?.Dispose();
        }
        catch (Exception)
        {
        }
        finally
        {
            _port = null;
            _connected = false;
        }
    }
```

`Send()` používa vnútri `IsConnected`; nahraď ho priamym `_port?.IsOpen == true`, aby logika spojenia nezávisela od kópie:

```csharp
    public void Send(string frame)
    {
        lock (_gate)
        {
            if (_port?.IsOpen != true)
            {
                var now = _time.GetUtcNow();
                if (now - _lastAttempt < ReconnectInterval)
                {
                    return;
                }

                _lastAttempt = now;
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
                Report($"{PortName}: {ex.Message}");
                Disconnect();
            }
        }
    }
```

Pozor: `Report()` vnútri `Send()` čítá `PortName`, ktorého getter lock neberie — v poriadku, `lock` je reentrantný aj keby bral. `Dispose()` obal do `lock (_gate) { Disconnect(); }`.

- [ ] **Step 3: Run tests to verify nothing regressed**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SerialMeterLinkTests"`

Expected: PASS — rovnaký počet testov ako v Step 1, všetky zelené. Zvlášť testy na reconnect interval a na latchovanie chýb: obe cesty sú teraz pod lockom.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add AnalogHwMonitor.Core/SerialMeterLink.cs
git commit -m "fix: guard the serial port against a concurrent port change, keep IsConnected lock-free"
```

---

### Task 5: `SensorRefreshLoop` — nová trieda, ešte nezadrôtovaná

**Files:**
- Create: `AnalogHwMonitor.Core/SensorRefreshLoop.cs`
- Test: `AnalogHwMonitor.Tests/SensorRefreshLoopTests.cs`

**Interfaces:**
- Consumes: `ISensorSource` (`void Refresh()`), `IAppLog` (`void Write(string)`).
- Produces: `SensorRefreshLoop(ISensorSource sensors, IAppLog log)`; `static readonly TimeSpan Interval`; `void RefreshOnce()`; `Task RunAsync(CancellationToken ct)`. Task 7 volá konstruktor a `RunAsync`.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/SensorRefreshLoopTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

/// <summary>
/// Tests RefreshOnce(), not RunAsync(). RunAsync contains a PeriodicTimer, which is not
/// our code, and this repo's FakeTimeProvider overrides only GetUtcNow(), not
/// CreateTimer() — a test through RunAsync would wait on the wall clock. That is exactly
/// why the logic is separated from the cadence.
/// </summary>
public class SensorRefreshLoopTests
{
    [Fact]
    public void RefreshOnce_RefreshesTheSource()
    {
        var sensors = new FakeSensorSource(new Dictionary<string, float?>());
        var loop = new SensorRefreshLoop(sensors, NullLog.Instance);

        loop.RefreshOnce();

        Assert.Equal(1, sensors.RefreshCount);
    }

    /// <summary>
    /// An unhandled exception in a fire-and-forget task would mean the sensors quietly
    /// stop refreshing and the needles freeze forever on their last values — software that
    /// looks like it works and does not. Hence the try inside the loop, not around it.
    /// </summary>
    [Fact]
    public void RefreshOnce_SurvivesAThrowingSource()
    {
        var loop = new SensorRefreshLoop(new ThrowingSensorSource(), NullLog.Instance);

        var exception = Record.Exception(() => loop.RefreshOnce());

        Assert.Null(exception);
    }

    /// <summary>
    /// The loop runs once a second for the life of the tray application. An unlatched write
    /// would fill log.txt at about a megabyte a day and rotate the interesting history
    /// away — the same discipline SerialMeterLink and CompositeSensorSource already keep.
    /// </summary>
    [Fact]
    public void RefreshOnce_LogsAPersistentFaultOnlyOnce()
    {
        var log = new RecordingLog();
        var loop = new SensorRefreshLoop(new ThrowingSensorSource(), log);

        for (var tick = 0; tick < 5; tick++)
        {
            loop.RefreshOnce();
        }

        Assert.Single(log.Lines);
        Assert.Contains("refresh failed", log.Lines[0]);
    }
}
```

Netestuj hodnotu `SensorRefreshLoop.Interval`. `Assert.Equal(TimeSpan.FromSeconds(1), Interval)` len prepisuje konstantu a nič neoveruje; rozhodnutie „1 Hz v oboch režimoch" je zdokumentované v spec aj v doc komentári triedy.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SensorRefreshLoopTests"`

Expected: FAIL — kompilácia padne, `SensorRefreshLoop` neexistuje (`CS0246`).

- [ ] **Step 3: Write minimal implementation**

Create `AnalogHwMonitor.Core/SensorRefreshLoop.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Owns the cadence of <see cref="ISensorSource.Refresh"/>. Refresh() is the most
/// expensive thing in the application and it used to run on the UI thread, where 99 ms
/// stalled a VU meter needle — longer than the 65 ms time constant of
/// <see cref="VuIntegrator"/>. Hence a task of its own.
///
/// Where those 99 ms sit, so nobody has to measure it again: 77 ms is the NVIDIA GPU's
/// update, almost entirely Windows' own GPU Engine performance counters; 13 ms is the WMI
/// thermal-zone query; 7 ms is the audio health check; and the whole AMD CPU, PawnIO and
/// all, is 1.6 ms. It was 218 ms before one needless COM call was removed from the audio
/// check.
///
/// The interval is one second in both modes. This class's predecessor
/// (ThrottledSensorSource) held three seconds in VU meter mode, but not for CPU — for the
/// fact that a refresh on the UI thread stalled a needle in motion. That reason does not
/// exist here, and the extra two seconds buy something: the audio health check notices a
/// default-device change (headphones in, speakers out) within a second instead of three.
/// The price is 99 ms of work per second, about 10 % of one core continuously; almost all
/// of it is those GPU counters rather than anything this application computes.
///
/// The class owns no logic, only cadence — <see cref="RefreshOnce"/> is the part that can
/// be tested without a clock, the same cut <see cref="MonitorService"/> already makes.
/// </summary>
public sealed class SensorRefreshLoop
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ISensorSource _sensors;
    private readonly IAppLog _log;
    private string? _reportedError;

    public SensorRefreshLoop(ISensorSource sensors, IAppLog log)
    {
        _sensors = sensors;
        _log = log;
    }

    /// <summary>
    /// One refresh. <see cref="CompositeSensorSource"/> already absorbs and latches each
    /// individual source's fault, so only a failure of the composite itself reaches here —
    /// and that must not kill the loop. An unhandled exception in a fire-and-forget task
    /// means frozen values and needles that look like a working application.
    /// </summary>
    public void RefreshOnce()
    {
        try
        {
            _sensors.Refresh();
            _reportedError = null;
        }
        catch (Exception ex)
        {
            if (_reportedError != ex.Message)
            {
                _log.Write($"Sensor refresh failed: {ex.Message}");
                _reportedError = ex.Message;
            }
        }
    }

    /// <summary>
    /// Must be started through <c>Task.Run</c>. Called straight from the UI thread, the
    /// first await would marshal its continuation back onto it through the WinForms
    /// SynchronizationContext and Refresh() would keep running there — the whole change
    /// would be a no-op.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                RefreshOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // A normal shutdown. Caught here so the task reaches Completed and shutdown
            // does not have to unwrap an AggregateException.
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SensorRefreshLoopTests"`

Expected: PASS — 4 testy.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/SensorRefreshLoop.cs AnalogHwMonitor.Tests/SensorRefreshLoopTests.cs
git commit -m "feat: add the sensor refresh loop that will own the 1 Hz cadence"
```

---

### Task 6: `QueuedMeterLink` — nová trieda, ešte nezadrôtovaná

**Files:**
- Create: `AnalogHwMonitor.Core/QueuedMeterLink.cs`
- Test: `AnalogHwMonitor.Tests/QueuedMeterLinkTests.cs`

**Interfaces:**
- Consumes: `IMeterLink` (`bool IsConnected`, `string? LastError`, `void Send(string)`, `void Dispose()`).
- Produces: `QueuedMeterLink(IMeterLink inner)` implementujúci `IMeterLink`, plus `Task RunAsync(CancellationToken ct)`. Task 8 ho odovzdá do `MonitorService` ako `IMeterLink` a do `TrayApplicationContext` kvôli `RunAsync`.

- [ ] **Step 1: Write the failing test**

Create `AnalogHwMonitor.Tests/QueuedMeterLinkTests.cs`:

```csharp
using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class QueuedMeterLinkTests
{
    /// <summary>Signals the first frame through a TaskCompletionSource, so the test
    /// never has to poll or read a List across a thread boundary.</summary>
    private sealed class SignallingMeterLink : IMeterLink
    {
        private readonly TaskCompletionSource<string> _first = new();

        public Task<string> First => _first.Task;

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame) => _first.TrySetResult(frame);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The first write hangs until the test releases it — a jammed port. Both arrivals
    /// are signalled through a TaskCompletionSource, so the test never reads _frames
    /// while the pump could still be writing into it.
    /// </summary>
    private sealed class BlockingMeterLink : IMeterLink
    {
        private readonly ManualResetEventSlim _gate;
        private readonly TaskCompletionSource _firstArrived = new();
        private readonly TaskCompletionSource _secondArrived = new();
        private readonly List<string> _frames = new();
        private bool _blocked;

        public BlockingMeterLink(ManualResetEventSlim gate) => _gate = gate;

        public Task FirstArrived => _firstArrived.Task;

        public Task SecondArrived => _secondArrived.Task;

        /// <summary>Written exclusively by the pump. Read only after its task has finished.</summary>
        public IReadOnlyList<string> Frames => _frames;

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame)
        {
            _frames.Add(frame);

            if (!_blocked)
            {
                _blocked = true;
                _firstArrived.TrySetResult();
                _gate.Wait();
                return;
            }

            _secondArrived.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Send_ReachesTheInnerLink()
    {
        var inner = new SignallingMeterLink();
        using var link = new QueuedMeterLink(inner);
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() => link.RunAsync(cts.Token));

        link.Send("V:1,2,3,4,5\n");

        Assert.Equal("V:1,2,3,4,5\n", await inner.First.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Capacity 1 and DropOldest: a stale frame is worthless to a needle, so while the
    /// write is stuck the intermediate frames are dropped and only the newest is sent.
    /// </summary>
    [Fact]
    public async Task Send_KeepsOnlyTheNewestFrameWhileTheLinkIsBusy()
    {
        using var gate = new ManualResetEventSlim(false);
        var inner = new BlockingMeterLink(gate);
        using var link = new QueuedMeterLink(inner);
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() => link.RunAsync(cts.Token));

        link.Send("frame-1");
        await inner.FirstArrived.WaitAsync(TimeSpan.FromSeconds(5));

        // The pump is now hanging inside Send("frame-1"). The channel holds one frame,
        // so frame-2 and frame-3 fall out and only frame-4 survives.
        link.Send("frame-2");
        link.Send("frame-3");
        link.Send("frame-4");

        gate.Set();
        await inner.SecondArrived.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        // Only here — the pump has finished, so nobody mutates _frames any more.
        Assert.Equal(new[] { "frame-1", "frame-4" }, inner.Frames);
    }

    [Fact]
    public void Send_DoesNotThrowAfterDispose()
    {
        var link = new QueuedMeterLink(new FakeMeterLink());
        link.Dispose();

        Assert.Null(Record.Exception(() => link.Send("V:0,0,0,0,0\n")));
    }

    [Fact]
    public void StatusDelegatesToTheInnerLink()
    {
        var inner = new FakeMeterLink { IsConnected = false, LastError = "COM7: denied" };
        using var link = new QueuedMeterLink(inner);

        Assert.False(link.IsConnected);
        Assert.Equal("COM7: denied", link.LastError);
    }

    [Fact]
    public void Dispose_DisposesTheInnerLink()
    {
        var inner = new RecordingDisposeLink();
        var link = new QueuedMeterLink(inner);

        link.Dispose();

        Assert.True(inner.Disposed);
    }

    private sealed class RecordingDisposeLink : IMeterLink
    {
        public bool Disposed { get; private set; }

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~QueuedMeterLinkTests"`

Expected: FAIL — kompilácia padne, `QueuedMeterLink` neexistuje (`CS0246`).

- [ ] **Step 3: Write minimal implementation**

Create `AnalogHwMonitor.Core/QueuedMeterLink.cs`:

```csharp
using System.Threading.Channels;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Decouples sending a frame from the caller. <see cref="Send"/> now means "enqueue" — a
/// mild lie in a name inherited from <see cref="IMeterLink"/>, and this comment is what
/// pays for it. An honester name would cost a changed MonitorService constructor, its
/// rewritten tests and a new fake, all for identical behaviour.
///
/// The reason is that <see cref="SerialMeterLink.Send"/> is a blocking write and it used
/// to run on the UI thread. On a wedged adapter that is not microseconds but seconds.
///
/// The channel holds one frame with <see cref="BoundedChannelFullMode.DropOldest"/>: a
/// stale frame is worthless to a needle, so while the write is slow the intermediate
/// frames are dropped and only the newest is sent. This does not paper over a throughput
/// problem — a frame is ~22 bytes, ~1.5 ms at 115200 baud, so about 3 % of the link at
/// 21 Hz. The capacity of one is there for the pathological case, not the normal one.
///
/// Async I/O is deliberately not introduced. To the rest of the application a blocking
/// Write on a dedicated task is exactly as non-blocking as WriteAsync, and it is a class
/// less code.
/// </summary>
public sealed class QueuedMeterLink : IMeterLink
{
    private readonly IMeterLink _inner;

    private readonly Channel<string> _frames = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // sender task
            SingleWriter = true,   // UI tick
        });

    public QueuedMeterLink(IMeterLink inner) => _inner = inner;

    public bool IsConnected => _inner.IsConnected;

    public string? LastError => _inner.LastError;

    /// <summary>
    /// Enqueues a frame and returns. Never blocks and never throws: <c>DropOldest</c> makes
    /// <c>TryWrite</c> always succeed, and after <see cref="Dispose"/> a <c>false</c> return
    /// only means "we are shutting down", not a failure.
    /// </summary>
    public void Send(string frame) => _frames.Writer.TryWrite(frame);

    /// <summary>
    /// Must be started through <c>Task.Run</c>. Called straight from the UI thread it would
    /// marshal its continuations back onto it through the WinForms SynchronizationContext
    /// and the write would stay on the UI thread — the whole change would be a no-op.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _frames.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                _inner.Send(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // A normal shutdown.
        }
    }

    public void Dispose()
    {
        _frames.Writer.TryComplete();
        _inner.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~QueuedMeterLinkTests"`

Expected: PASS — 5 testov.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/QueuedMeterLink.cs AnalogHwMonitor.Tests/QueuedMeterLinkTests.cs
git commit -m "feat: add the queued meter link that will own the serial write"
```

---

### Task 7: Prepnutie refreshu na poll task a zmazanie `ThrottledSensorSource`

**Files:**
- Modify: `AnalogHwMonitor.Core/MonitorService.cs`
- Delete: `AnalogHwMonitor.Core/ThrottledSensorSource.cs`
- Delete: `AnalogHwMonitor.Tests/ThrottledSensorSourceTests.cs`
- Modify: `AnalogHwMonitor.App/TrayApplicationContext.cs`
- Modify: `AnalogHwMonitor.App/Program.cs`
- Test: `AnalogHwMonitor.Tests/MonitorServiceTests.cs`

**Interfaces:**
- Consumes: `SensorRefreshLoop(ISensorSource, IAppLog)` a `SensorRefreshLoop.RunAsync(CancellationToken)` z Task 5.
- Produces: `TrayApplicationContext(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors, IAppLog log, SensorRefreshLoop refreshLoop)`. Task 8 pridá siedmy parameter.

Tento task musí byť atomický. Odstrániť `Refresh()` z `Tick()` bez pridania slučky by znamenalo, že senzory sa neaktualizujú vôbec; zmazať `ThrottledSensorSource` bez odstránenia `Refresh()` z `Tick()` by znamenalo 99 ms refresh 21× za sekundu na UI vlákne, teda úplné zamrznutie.

- [ ] **Step 1: Write the failing test**

V `AnalogHwMonitor.Tests/MonitorServiceTests.cs` nahraď `Tick_RefreshesTheHardwareExactlyOnce` za:

```csharp
    /// <summary>
    /// Refresh() is owned by SensorRefreshLoop on its own task. If Tick() called it too,
    /// in VU mode 99 ms of work would run 21 times a second on the UI thread.
    /// </summary>
    [Fact]
    public void Tick_DoesNotRefreshTheHardware()
    {
        var sensors = SensorsAt(10, 10, 10, 40, 40);
        using var service = new MonitorService(sensors, new FakeMeterLink(), ConfigWithSensors(), NullLog.Instance);

        service.Tick();

        Assert.Equal(0, sensors.RefreshCount);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~MonitorServiceTests.Tick_DoesNotRefreshTheHardware"`

Expected: FAIL — `Assert.Equal() Failure: Expected 0, Actual 1`.

- [ ] **Step 3: Write minimal implementation**

**3a.** V `AnalogHwMonitor.Core/MonitorService.cs` zmaž riadok `_sensors.Refresh();` zo začiatku `Tick()` a uprav doc komentár triedy:

```csharp
/// <summary>
/// One tick of the whole system: turn five readings into five PWM bytes and push one
/// frame down the link. Owns no timer and no threads — the caller decides when a tick
/// happens.
///
/// Does not refresh. Hardware is refreshed by <see cref="SensorRefreshLoop"/> on its own
/// task once a second, because Refresh() is 99 ms and stalled the VU meter needle on the
/// UI thread. Tick() therefore reads whatever the last refresh left there — which is
/// exactly fine for temperatures and load, and the audio level is live anyway, since it
/// is computed on the capture thread.
/// </summary>
```

**3b.** Zmaž `AnalogHwMonitor.Core/ThrottledSensorSource.cs` a `AnalogHwMonitor.Tests/ThrottledSensorSourceTests.cs`.

```bash
git rm AnalogHwMonitor.Core/ThrottledSensorSource.cs AnalogHwMonitor.Tests/ThrottledSensorSourceTests.cs
```

Namerané čísla z jeho komentárov už sú v `SensorRefreshLoop` z Task 5 — over to a nič nedopisuj, ak tam sú.

**3c.** V `AnalogHwMonitor.App/TrayApplicationContext.cs` pridaj šiesty parameter a vlastníctvo tasku. K existujúcim poliam pridaj:

```csharp
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;
```

Do signatúry konstruktora pridaj `SensorRefreshLoop refreshLoop` ako posledný parameter a na koniec tela konstruktora, **pred** `_log.Write($"Started on ...")`:

```csharp
        // Task.Run rather than a direct call: the constructor runs on the UI thread,
        // where the WinForms SynchronizationContext is installed, and it would marshal
        // the continuation after the first await back onto it — Refresh() would keep
        // running on the UI thread and the whole change would be a no-op that only a
        // profiler could catch.
        _pollTask = Task.Run(() => refreshLoop.RunAsync(_cts.Token));
```

Prepíš `Dispose`:

```csharp
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();

            _cts.Cancel();

            // Cancellation does not interrupt a blocking write already in flight, nor
            // an in-progress 99 ms Refresh(), hence the cap here. On a jammed port that
            // means up to two seconds of blocked UI thread on exit — better than
            // ripping the port and driver out from under an operation in progress.
            Task.WaitAll(new[] { _pollTask }, TimeSpan.FromSeconds(2));
            _cts.Dispose();

            _icon.Visible = false;
            _icon.Dispose();
            _monitor.Dispose();
            _log.Write("Stopped.");
        }

        base.Dispose(disposing);
    }
```

**3d.** V `AnalogHwMonitor.App/Program.cs` nahraď reťaz so `ThrottledSensorSource`:

```csharp
        // From here on the composite absorbs and latches every source fault, so a
        // source that dies later costs its own readings and nothing else. Refresh() is
        // no longer called by the tick — it is owned by SensorRefreshLoop on its own
        // task once a second, in both modes. That is why there is no throttle here and
        // why VuMode no longer affects the refresh interval.
        ISensorSource sensors = new CompositeSensorSource(log, sources.ToArray());

        // Load-bearing, not habit: AssignSensors below reads the snapshot that this very
        // Refresh() builds. Without it, it would see an empty snapshot and map nothing.
        sensors.Refresh();
```

a na konci pridaj slučku a odovzdaj ju:

```csharp
        var link = new SerialMeterLink(new SerialPortFactory(), config.ComPort, log);
        var monitor = new MonitorService(sensors, link, config, log);
        var refreshLoop = new SensorRefreshLoop(sensors, log);

        Application.Run(new TrayApplicationContext(monitor, link, store, sensors, log, refreshLoop));
```

- [ ] **Step 4: Run tests and build to verify they pass**

Run: `dotnet build AnalogHwMonitor.sln`

Expected: PASS, 0 warnings. Ak sa objaví `CS0246` na `ThrottledSensorSource`, zostala niekde referencia — nájdi ju cez `grep -rn ThrottledSensorSource --include=*.cs .`

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS. Počet testov klesne o 10 (zmazaný `ThrottledSensorSourceTests`).

- [ ] **Step 5: Manual verification**

Spusť aplikáciu elevated a over:

```bash
dotnet run --project AnalogHwMonitor.App
```

1. Ručičky sa hýbu, tray tooltip ukazuje COM port alebo dôvod odpojenia.
2. Zaškrtni **VU meter** a pusti hudbu — ručičky 0 a 1 sledujú zvuk **bez viditeľného zadrhnutia raz za sekundu**. Toto je celý účel plánu; ak zadrhnutie zostalo, `Task.Run` chýba alebo `Refresh()` zostal v `Tick()`.
3. Otvor Settings, prepni COM port a vráť ho — nesmie to spadnúť ani zatuhnúť.
4. Prepoj slúchadlá/reproduktory počas VU režimu — ručičky sa musia oživiť do sekundy.
5. Exit z tray menu — proces musí skončiť do dvoch sekúnd a `log.txt` musí končiť `Stopped.`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "perf: refresh the sensors on their own task at 1 Hz instead of on the UI thread"
```

---

### Task 8: Prepnutie sériového zápisu na sender task

**Files:**
- Modify: `AnalogHwMonitor.App/TrayApplicationContext.cs`
- Modify: `AnalogHwMonitor.App/Program.cs`

**Interfaces:**
- Consumes: `QueuedMeterLink(IMeterLink)` a `QueuedMeterLink.RunAsync(CancellationToken)` z Task 6; `TrayApplicationContext` zo Task 7.
- Produces: `TrayApplicationContext(MonitorService monitor, SerialMeterLink link, ConfigStore store, ISensorSource sensors, IAppLog log, SensorRefreshLoop refreshLoop, QueuedMeterLink sendLoop)`.

`SettingsForm` sa **nemení**. Na linku volá výhradne `PortName` (get aj set) a `IsConnected`, teda členy, ktoré zostávajú na `SerialMeterLink`; obal dostane len `MonitorService`. `TrayApplicationContext` drží `SerialMeterLink` ďalej pre tooltip a `QueuedMeterLink` navyše len preto, aby mala čo spustiť.

- [ ] **Step 1: Wire the queued link in Program.cs**

V `AnalogHwMonitor.App/Program.cs` nahraď posledné riadky:

```csharp
        var link = new SerialMeterLink(new SerialPortFactory(), config.ComPort, log);

        // MonitorService writes into the queue, not to the port. Tick runs on the UI
        // thread, and SerialPort.Write on a jammed adapter is a matter of seconds, not
        // microseconds. The tray and SettingsForm keep holding SerialMeterLink: they
        // need PortName and IsConnected, which have no business being on the queue.
        var sendLoop = new QueuedMeterLink(link);
        var monitor = new MonitorService(sensors, sendLoop, config, log);
        var refreshLoop = new SensorRefreshLoop(sensors, log);

        Application.Run(
            new TrayApplicationContext(monitor, link, store, sensors, log, refreshLoop, sendLoop));
```

- [ ] **Step 2: Wire the sender task in TrayApplicationContext**

Pridaj pole:

```csharp
    private readonly Task _sendTask;
```

Do signatúry konstruktora pridaj `QueuedMeterLink sendLoop` ako posledný parameter, a vedľa `_pollTask`:

```csharp
        _pollTask = Task.Run(() => refreshLoop.RunAsync(_cts.Token));
        _sendTask = Task.Run(() => sendLoop.RunAsync(_cts.Token));
```

V `Dispose` rozšír čakanie na oba tasky:

```csharp
            Task.WaitAll(new[] { _pollTask, _sendTask }, TimeSpan.FromSeconds(2));
```

`_monitor.Dispose()` zostáva až za tým čakaním — disponuje `QueuedMeterLink`, ktorý disponuje `SerialMeterLink`, a to sa nesmie stať, kým sender task ešte môže zapisovať.

- [ ] **Step 3: Build and run the tests**

Run: `dotnet build AnalogHwMonitor.sln`

Expected: PASS, 0 warnings.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS — celá suite.

- [ ] **Step 4: Manual verification**

```bash
dotnet run --project AnalogHwMonitor.App
```

1. Ručičky sa hýbu, tooltip ukazuje port. `log.txt` obsahuje `Connected to COM…`.
2. **Odpoj USB kábel počas VU režimu.** UI nesmie zatuhnúť ani na chvíľu; ručičky spadnú na nulu do troch sekúnd (sketch watchdog) a tooltip prejde na varovanie. `log.txt` dostane **jeden** riadok o chybe, nie jeden za tick.
3. Zapoj kábel späť — do piatich sekúnd (`ReconnectInterval`) sa spojenie obnoví a ručičky sa oživia.
4. Prepni COM port v Settings počas VU režimu — nesmie to spadnúť.
5. Exit — proces skončí do dvoch sekúnd, `log.txt` končí `Stopped.`

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "perf: write to the serial port from its own task instead of the UI thread"
```

---

### Task 9: README a záverečné overenie

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: hotový stav po Task 8.
- Produces: nič.

- [ ] **Step 1: Find what the README claims about the loop and the refresh rate**

Run: `grep -n "Throttled\|1 Hz\|25 Hz\|refresh\|interval\|VU" README.md`

Prečítaj nájdené sekcie. README opisuje slučku a rýchlosť čítania; po tomto pláne je tam minimálne jedno tvrdenie, ktoré už neplatí — `ThrottledSensorSource` neexistuje a interval nie je 1 s / 3 s podľa režimu.

- [ ] **Step 2: Update the README**

Uprav nájdené sekcie tak, aby povedali:

- Refresh senzorov je **1 Hz v oboch režimoch**, na vlastnom tasku. `VuMode` ho neovplyvňuje.
- Rámce sa stavajú na UI vlákne v rytme 1 s / 40 ms podľa `VuMode` a odosielajú sa z vlastného sender tasku.
- Aplikácia beží na troch vláknach plus WASAPI capture vlákno; tabuľka vláknového modelu zo spec sekcie „Vláknový model" sa dá prevziať.
- Cena refreshu je asi 10 % jedného jadra nepretržite, z toho väčšina sú GPU Engine performance counters Windowsu.

Nepridávaj nič, čo README dnes nesľubuje — je to súbor pre užívateľa, nie druhá kópia specu.

- [ ] **Step 3: Full verification**

Run: `dotnet build AnalogHwMonitor.sln`

Expected: PASS, 0 warnings.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS. Zapíš si počet testov.

Run: `grep -rn "ThrottledSensorSource" --include=*.cs --include=*.md . | grep -v /obj/ | grep -v docs/superpowers`

Expected: žiadny výstup. Zmienky v `docs/superpowers/` sú historické a majú tam zostať.

Run: `$env:AHM_HARDWARE_TESTS = "1"; dotnet test AnalogHwMonitor.sln` (v elevated shelli)

Expected: PASS vrátane hardware testov. Ak nie je elevovaný shell k dispozícii, zapíš, že tento krok neprebehol — netvrď, že prešiel.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: describe the three-thread model and the flat 1 Hz sensor refresh"
```

---

## Self-Review

**Spec coverage** — každá sekcia specu má task:

| Spec | Task |
| --- | --- |
| Rozhodnutie 1 (dve veci z UI vlákna) | 7, 8 |
| Rozhodnutie 2 (1 Hz v oboch režimoch) | 5, 7 |
| Rozhodnutie 3 (zmazať `ThrottledSensorSource`, prenesené merania) | 5 (komentáre), 7 (mazanie) |
| Rozhodnutie 4 (snapshot per source, kľúčovaný `SensorId`) | 1, 2 |
| Rozhodnutie 5 (žiadne potlačenie nezmenených rámcov) | negatívne — `Tick()` posiela vždy, žiadny task to nemení; overené v Task 8 Step 4 bodom 2 |
| Rozhodnutie 6 (`Channel`, kapacita 1, `DropOldest`) | 6 |
| Rozhodnutie 7 (`IsConnected` mimo locku) | 4 |
| Rozhodnutie 8 (žiadne async I/O) | 6 |
| Rozhodnutie 9 (slučky nevlastnia logiku) | 5 |
| Prechody stromu 3 → 1 | 2 |
| Load-bearing `Program.cs:85` | 7 Step 3d |
| `AudioLevelSensorSource` lock, `OnSamples` mimo | 3 |
| `SerialMeterLink` lock | 4 |
| `MonitorService` bez `Refresh()` | 7 |
| Shutdown s dvojsekundovým stropom | 7, 8 |
| `CompositeSensorSource` nedotknutý | žiadny task ho nemení — zámer |
| `SettingsForm` nedotknutý | Task 8 to explicitne konštatuje |
| Chybové stavy (tabuľka) | 5 (slučka prežije), 8 Step 4 (odpojený kábel) |
| Testovanie | 1, 2, 3, 4, 5, 6, 7 |
| README | 9 |

**Placeholder scan** — žiadne „TBD", „implement later" ani „similar to Task N"; každý krok nesie skutočný kód alebo konkrétny príkaz. Jediné miesto, ktoré hovorí „nájdi a uprav" namiesto hotového textu, je Task 9 Step 1–2 (README). Je to vedomé: obsah README nepoznám bez prečítania a hádať jeho formulácie by bolo horšie než povedať, čo v ňom musí po zmene platiť.

**Type consistency** — `SensorRefreshLoop(ISensorSource, IAppLog)`, `RefreshOnce()`, `RunAsync(CancellationToken)`, `Interval` sú rovnaké v Task 5, 7 aj 9. `QueuedMeterLink(IMeterLink)` a `RunAsync(CancellationToken)` rovnaké v Task 6 a 8. `TrayApplicationContext` má po Task 7 šesť parametrov a po Task 8 sedem, v oboch taskoch vymenované v poradí. `Snapshot(Dictionary<string, float>, IReadOnlyList<SensorDescriptor>)` je rovnaký record v Task 1 a 2, oba privátne vo svojej triede, teda bez kolízie.

**Poznámka k Task 3 a 4** — ani jeden nepridáva test, čo je pri TDD pláne výnimka a preto je vysvetlená v oboch taskoch aj tu. Lock sa nedá pripnúť testom, ktorý pred jeho pridaním padá: neexistencia locku sa nedá odmerať. Zvažovaná bola alternatíva — testy s blokovacím hookom vo fake, ktoré by pripínali, že lock **nie je** na `OnSamples` a na `IsConnected` — a bola zamietnutá, pretože by prechádzali pred aj po implementácii. Ochranou v oboch taskoch je preto existujúca suite: `AudioLevelSensorLifecycleTests` a `AudioLevelSensorSourceTests` pre Task 3, `SerialMeterLinkTests` pre Task 4. Musia prejsť nezmenené, s rovnakým počtom testov ako pred zmenou.

**Poznámka k testovaniu konstánt** — plán nikde netestuje hodnotu policy konstanty proti sebe samej. Zmazaný `ThrottledSensorSourceTests` to robil (`CurrentInterval_FollowsTheMode`), ale tam to malo zmysel: overoval **prepínanie** medzi dvoma intervalmi podľa režimu, čo je chovanie. `SensorRefreshLoop` má jeden interval a žiadne prepínanie, takže by z toho zostala tautológia.
