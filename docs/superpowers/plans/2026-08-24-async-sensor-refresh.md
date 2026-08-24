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
| `Tests/Fakes/FakeAudioLoopbackCapture.cs` | pridaný blokovací hook pre test locku |
| `Tests/Fakes/FakeSerialPort.cs` | pridaný blokovací hook pre test locku |

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
    /// Refresh() beží po novom na poll tasku, kým UI vlákno čítá. Vyprázdniť a znova
    /// naplniť tú istú kolekciu je vtedy nedefinované chovanie, takže každý Refresh()
    /// musí publikovať novú instanciu a tú starú nechať na pokoji. Test nepotrebuje
    /// elevated session ani jednu thermal zone: identita instancie je pozorovateľná
    /// aj vtedy, keď je zoznam prázdny.
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
    /// Hodnoty a deskriptory z jedného Refresh(). Jeden objekt a nie dve polia zámerne:
    /// pri dvoch samostatných zápisoch by čitateľ mohol vidieť nové deskriptory so
    /// starými hodnotami. Publikovaná instancia sa už nikdy nemutuje, takže ju smie
    /// čítať UI vlákno, kým poll task stavia ďalšiu.
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

            // Rovnaká semantika ako pôvodné Clear(): po zlyhaní nečítame nič.
            // Nová instancia, nie zdieľaná statická konstanta: na neelevovanom stroji
            // zlyhá každý Refresh(), a zdieľaná instancia by znamenala, že dva refreshy
            // za sebou vrátia to isté — čo je presne to, čo test zo Step 1 zakazuje.
            Volatile.Write(
                ref _snapshot,
                new Snapshot(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>()));
        }
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
        Volatile.Read(ref _snapshot).Descriptors;

    public float? Read(string sensorId) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(sensorId, out var value) ? value : null;
```

`_faultReported` zostáva obyčajným polom: dotýka sa ho výhradne `Refresh()`, teda len poll task.

**Nezdieľaj prázdny snapshot cez statickú konstantu.** Na stroji bez elevácie zlyhá WMI dotaz pri každom `Refresh()`, takže chybová cesta je tam tá bežná — a zdieľaná instancia by znamenala, že dva refreshy za sebou vrátia ten istý objekt. Test zo Step 1 by padol aj nad správnou implementáciou.

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
    /// Discover() a Read() musia čítať snapshot postavený v Refresh(), nie prechádzať
    /// živý strom — inak sa prechod vráti na UI vlákno a Refresh() na poll tasku ho
    /// mutuje pod rukami. Identita vráteného zoznamu to dokazuje: dnes vzniká nový
    /// List na každé volanie, po zmene je to ten istý objekt až do ďalšieho Refresh().
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
    /// Hodnoty a deskriptory z jedného Refresh(). Jeden objekt a nie dve polia zámerne:
    /// pri dvoch samostatných zápisoch by čitateľ mohol vidieť nové deskriptory so
    /// starými hodnotami. Publikovaná instancia sa už nemutuje, takže ju smie čítať UI
    /// vlákno, kým poll task stavia ďalšiu.
    /// </summary>
    private sealed record Snapshot(
        Dictionary<string, float> Values,
        IReadOnlyList<SensorDescriptor> Descriptors);

    private Snapshot _snapshot =
        new(new Dictionary<string, float>(), Array.Empty<SensorDescriptor>());

    /// <summary>
    /// Jediné miesto, kde sa prechádza strom. Update() aj odčítanie hodnôt sa robia
    /// v tom istom priechode; stavať snapshot lazy pri prvom Read() by prechod vrátilo
    /// na UI vlákno a nevyriešilo nič.
    ///
    /// Update() má hardware granularitu: pýtať sa na jednu GPU teplotu platí za každú
    /// hodnotu, ktorú tá GPU vystavuje. Namerané na RTX 4070 je to 77 ms, takmer celé
    /// GPU Engine performance counters Windowsu, nie driver. Celý AMD CPU cez PawnIO
    /// je oproti tomu 1,6 ms. Preto je toto na poll tasku a raz za sekundu.
    /// </summary>
    public void Refresh()
    {
        _computer.Accept(_visitor);

        var values = new Dictionary<string, float>();
        var descriptors = new List<SensorDescriptor>();

        foreach (var (hardware, sensor) in EnumerateSensors())
        {
            var id = sensor.Identifier.ToString();

            // Senzor bez hodnoty sa do slovníka nedá, takže TryGetValue vráti false
            // a Read() null — presne to, čo vracal predchádzajúci sensor.Value.
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
- Modify: `AnalogHwMonitor.Tests/Fakes/FakeAudioLoopbackCapture.cs`
- Test: `AnalogHwMonitor.Tests/AudioLevelSensorLifecycleTests.cs`

**Interfaces:**
- Consumes: nič.
- Produces: nezmenená signatúra `ISensorSource` a konstruktor `AudioLevelSensorSource(IAudioLoopbackCapture capture, IAppLog log, Func<bool>? compensateVolume = null, TimeProvider? time = null)`. Konstanty `IdleTimeout`, `SilenceGap`, `MaxCompensationDb`, `StartRetryInterval` zostávajú.
- Produces pre testy: `FakeAudioLoopbackCapture.BlockInTryStart` typu `ManualResetEventSlim?` — keď je nastavený, `TryStart` naň počká, než uspeje.

Po Task 7 volá `Refresh()` poll task a `Read()` UI vlákno. Súťažia o `_started`, `_reportedError`, `_lastFailedStart`, `_lastRead`, o `_capture` aj o reset integrátorov. Najhorší prípad: poll task sa rozhodne pre `Stop()` (slúchadlá von) presne vtedy, keď je UI vlákno vnútri `TryStart()` — výsledkom je `_started == true` nad zastaveným captureom a obe ručičky ležia mŕtve.

- [ ] **Step 1: Write the failing test**

Najprv pridaj dva hooky do `AnalogHwMonitor.Tests/Fakes/FakeAudioLoopbackCapture.cs`:

```csharp
    /// <summary>
    /// Keď je nastavený, TryStart naň počká — na samom konci, keď je handler už
    /// zaregistrovaný a Format nastavený. Dovolí testu držať životný cyklus otvorený a
    /// z iného vlákna dokázať, že capture vlákno naň nečaká.
    /// </summary>
    public ManualResetEventSlim? BlockInTryStart { get; set; }

    /// <summary>Nastaví sa tesne pred čakaním, aby test vedel, že sme naozaj vnútri.</summary>
    public ManualResetEventSlim? EnteredTryStart { get; set; }
```

a **na konec** `TryStart`, za `error = null;` a **pred** `return true;`:

```csharp
        EnteredTryStart?.Set();
        BlockInTryStart?.Wait();
```

Poradie je celý zmysel toho hooku. Keby čakanie bolo na začiatku `TryStart`, `_onSamples` by bol ešte `null`, `Deliver()` by bol no-op a test by prešiel aj vtedy, keby `OnSamples` lock bral — teda by nedokazoval nič.

Potom do `AnalogHwMonitor.Tests/AudioLevelSensorLifecycleTests.cs` pridaj:

```csharp
    /// <summary>
    /// OnSamples beží na WASAPI capture vlákne v rytme buffrov a nesmie nikdy čakať na
    /// UI vlákno. Test drží životný cyklus otvorený vnútri TryStart() na jednom vlákne
    /// a z druhého doručí buffer: ten musí prejsť. Keby OnSamples bral ten istý lock,
    /// audio vlákno by sa zablokovalo na dobu, ktorú WASAPI netoleruje.
    /// </summary>
    [Fact]
    public void DeliveringSamplesDoesNotWaitForTheLifecycleLock()
    {
        var capture = new FakeAudioLoopbackCapture();
        var time = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var source = new AudioLevelSensorSource(capture, NullLog.Instance, () => false, time);

        // Prvý start prejde bez blokovania; až ten druhý uvízne.
        source.Read(AudioSensorIds.Left);

        capture.CurrentDefaultDeviceId = "device-2";
        source.Refresh();                       // všimne si zmenu zariadenia a zastaví capture

        capture.EnteredTryStart = entered;
        capture.BlockInTryStart = gate;

        var blocked = Task.Run(() => source.Read(AudioSensorIds.Left));

        // Bez tohto čakania by test mohol doručiť buffer skôr, než Read() vôbec lock
        // vezme, a prešiel by aj nad implementáciou, ktorá OnSamples zamyká.
        Assert.True(
            entered.Wait(TimeSpan.FromSeconds(5)),
            "the second start never reached TryStart");

        // Handler je teraz zaregistrovaný a životný cyklus zamknutý. Buffer z
        // „capture vlákna" musí prejsť.
        var delivered = Task.Run(() => capture.Deliver(new float[256]));

        Assert.True(
            delivered.Wait(TimeSpan.FromSeconds(2)),
            "OnSamples waited on the lifecycle lock; the capture thread must never block on it.");

        gate.Set();
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AudioLevelSensorLifecycleTests.DeliveringSamplesDoesNotWaitForTheLifecycleLock"`

Expected: PASS už teraz — dnes žiadny lock neexistuje, takže sa naň nedá čakať. Test je regresná pasca pre Step 3: po pridaní locku musí prejsť ďalej. Zaznamenaj, že prešiel, a pokračuj; ak po Step 3 padne, lock je na `OnSamples`, čo je chyba.

- [ ] **Step 3: Write minimal implementation**

V `AnalogHwMonitor.Core/AudioLevelSensorSource.cs` pridaj pole:

```csharp
    /// <summary>
    /// Serializuje životný cyklus captureu. Refresh() beží na poll tasku a Read() na UI
    /// vlákne, takže inak by sa Stop() z health checku a TryStart() z čítania mohli
    /// preložiť a nechať _started == true nad zastaveným captureom — teda obe ručičky
    /// mŕtve, kým nimi niečo nezatriasa.
    ///
    /// OnSamples tento lock zámerne NEBERIE: beží na capture vlákne v rytme buffrov a
    /// nesmie nikdy čakať na UI vlákno. Komunikuje výhradne cez Volatile/Interlocked
    /// (_lastBufferTicks, _lastAdvanceTicks, VuIntegrator._level), a to stačí.
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
        // about somebody else's sensor. Tento early return je pred lockom zámerne:
        // z piatich kanálov sú audio dva, takže tri volania za tick sa nezamknú vôbec.
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~AudioLevelSensor"`

Expected: PASS — všetkých 12 testov v `AudioLevelSensorLifecycleTests` plus `AudioLevelSensorSourceTests`. Zvlášť `ARoundOfStartsAndStopsLeavesNothingSubscribed` a `Refresh_RestartsWhenTheCaptureDiedUnderUs`: obe idú cez `Refresh()`/`Read()` v sekvencii, teda cez nový lock.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/AudioLevelSensorSource.cs AnalogHwMonitor.Tests/Fakes/FakeAudioLoopbackCapture.cs AnalogHwMonitor.Tests/AudioLevelSensorLifecycleTests.cs
git commit -m "fix: serialise the audio capture lifecycle without touching the capture thread"
```

---

### Task 4: `SerialMeterLink` zamkne port, ale `IsConnected` nechá voľné

**Files:**
- Modify: `AnalogHwMonitor.Core/SerialMeterLink.cs`
- Modify: `AnalogHwMonitor.Tests/Fakes/FakeSerialPort.cs`
- Test: `AnalogHwMonitor.Tests/SerialMeterLinkTests.cs`

**Interfaces:**
- Consumes: nič.
- Produces: nezmenené `IMeterLink` (`bool IsConnected`, `string? LastError`, `void Send(string)`, `void Dispose()`), plus `string? PortName { get; set; }` a `bool TryConnect()`. Konstruktor zostáva `SerialMeterLink(ISerialPortFactory factory, string? portName, IAppLog log, TimeProvider? time = null)`. Konstanty `BannerReadAttempts` a `ReconnectInterval` zostávajú.
- Produces pre testy: `FakeSerialPort.BlockInWrite` typu `ManualResetEventSlim?`.

Po Task 8 beží `Send()` na sender tasku, kým `PortName` setter beží na UI vlákne (`SettingsForm.cs:398`, `_link.PortName = _monitor.Config.ComPort;`) a volá `Disconnect()` → `_port.Dispose()`. Disponovať port spod prebiehajúceho `Write()` je chyba.

Zároveň platí obmedzenie zo spec rozhodnutia 7: **`IsConnected` sa nesmie čítať pod lockom.** Sender task ho drží počas `Write()`, čo je na zaseknutom porte sekundy, a UI si `IsConnected` číta každý tick pre tooltip — lock na getteri by vyrobil presne to tuhnutie UI, ktoré celý plán odstraňuje.

- [ ] **Step 1: Write the failing test**

Najprv pridaj hook do `AnalogHwMonitor.Tests/Fakes/FakeSerialPort.cs`, do `Write` hneď za kontrolu `ThrowOnWrite`:

```csharp
    /// <summary>Keď je nastavený, Write naň počká — zaseknutý port, ktorý drží zámok.</summary>
    public ManualResetEventSlim? BlockInWrite { get; set; }

    /// <summary>Nastaví sa tesne pred čakaním, aby test vedel, že zámok je naozaj držaný.</summary>
    public ManualResetEventSlim? EnteredWrite { get; set; }
```

```csharp
    public void Write(string text)
    {
        if (ThrowOnWrite is not null)
        {
            IsOpen = false;
            throw ThrowOnWrite;
        }

        EnteredWrite?.Set();
        BlockInWrite?.Wait();
        Written.Add(text);
    }
```

Potom do `AnalogHwMonitor.Tests/SerialMeterLinkTests.cs`:

```csharp
    /// <summary>
    /// Zaseknutý zápis drží zámok portu sekundy. IsConnected číta UI vlákno každý tick
    /// pre tooltip, takže sa naň nesmie čakať — inak by presun zápisu na vlastný task
    /// tuhnutie UI len presunul, nie odstránil.
    /// </summary>
    [Fact]
    public void IsConnected_DoesNotWaitForAWriteInProgress()
    {
        var port = new FakeSerialPort(FrameCodec.Banner);
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var factory = new FakeSerialPortFactory();
        factory.AddPort("COM7", () => port);
        using var link = new SerialMeterLink(factory, "COM7", NullLog.Instance);

        // Prvý Send() sa spojí a zapíše; až druhý uvízne.
        link.Send("V:0,0,0,0,0\n");
        Assert.True(link.IsConnected);

        port.EnteredWrite = entered;
        port.BlockInWrite = gate;
        var writing = Task.Run(() => link.Send("V:1,1,1,1,1\n"));

        // Bez tohto čakania by probe mohol prebehnúť skôr, než Send() zámok vôbec vezme,
        // a test by prešiel aj nad implementáciou, ktorá IsConnected zamyká.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the write never started");

        var probe = Task.Run(() => link.IsConnected);

        Assert.True(
            probe.Wait(TimeSpan.FromSeconds(2)),
            "IsConnected blocked on the port lock; the UI tick reads it every tick.");

        gate.Set();
        Assert.True(writing.Wait(TimeSpan.FromSeconds(5)));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SerialMeterLinkTests.IsConnected_DoesNotWaitForAWriteInProgress"`

Expected: PASS už teraz — dnes lock neexistuje. Rovnako ako v Task 3 je to regresná pasca pre Step 3: musí prejsť aj po pridaní locku. Ak po Step 3 padne, `IsConnected` sa dostal pod lock.

- [ ] **Step 3: Write minimal implementation**

V `AnalogHwMonitor.Core/SerialMeterLink.cs`:

```csharp
    /// <summary>
    /// Serializuje port. Send() beží na sender tasku, kým PortName setter beží na UI
    /// vlákne (SettingsForm mení COM port za behu) a disponuje _port — bez tohto by sa
    /// port disponoval spod prebiehajúceho Write().
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Kópia _port?.IsOpen mimo locku. UI vlákno číta IsConnected každý tick pre
    /// tooltip a nesmie na zaseknutom zápise čakať — pod lockom by to znamenalo
    /// presunúť tuhnutie UI, nie ho odstrániť. Čítanie disponovaného SerialPortu by
    /// navyše samo hodilo ObjectDisposedException.
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SerialMeterLinkTests"`

Expected: PASS — celý existujúci súbor plus nový test. Zvlášť testy na reconnect interval a na latchovanie chýb: obe cesty sú teraz pod lockom.

Run: `dotnet test AnalogHwMonitor.sln`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AnalogHwMonitor.Core/SerialMeterLink.cs AnalogHwMonitor.Tests/Fakes/FakeSerialPort.cs AnalogHwMonitor.Tests/SerialMeterLinkTests.cs
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
/// Testuje sa RefreshOnce(), nie RunAsync(). Obsahom RunAsync je PeriodicTimer, teda
/// nie náš kód, a FakeTimeProvider v tomto repe prepisuje len GetUtcNow(), nie
/// CreateTimer() — test cez RunAsync by čakal na reálne hodiny. Presne kvôli tomu je
/// logika oddelená od kadencie.
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
    /// Neodchytená výnimka v fire-and-forget tasku by znamenala, že senzory prestanú
    /// tichúčko refreshovať a ručičky navždy zamrznú na posledných hodnotách — teda
    /// softvér, ktorý vyzerá funkčne a nie je. Preto try vnútri slučky, nie okolo nej.
    /// </summary>
    [Fact]
    public void RefreshOnce_SurvivesAThrowingSource()
    {
        var loop = new SensorRefreshLoop(new ThrowingSensorSource(), NullLog.Instance);

        var exception = Record.Exception(() => loop.RefreshOnce());

        Assert.Null(exception);
    }

    /// <summary>
    /// Slučka beží raz za sekundu po celý život tray aplikácie. Nelatchovaný zápis by
    /// naplnil log.txt megabajtom za deň a odrotoval z neho celú zaujímavú históriu —
    /// tá istá disciplína, akú už majú SerialMeterLink a CompositeSensorSource.
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

    [Fact]
    public void Interval_IsOneSecondRegardlessOfMode()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SensorRefreshLoop.Interval);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AnalogHwMonitor.sln --filter "FullyQualifiedName~SensorRefreshLoopTests"`

Expected: FAIL — kompilácia padne, `SensorRefreshLoop` neexistuje (`CS0246`).

- [ ] **Step 3: Write minimal implementation**

Create `AnalogHwMonitor.Core/SensorRefreshLoop.cs`:

```csharp
namespace AnalogHwMonitor.Core;

/// <summary>
/// Vlastní kadenciu <see cref="ISensorSource.Refresh"/>. Refresh() je najdrahšia vec
/// v celej aplikácii a bežal na UI vlákne, kde 99 ms zastavilo ručičku VU metra —
/// dlhšie, než je 65 ms časová konštanta <see cref="VuIntegrator"/>. Preto má vlastný
/// task.
///
/// Kde tých 99 ms sedí, aby to nikto nemusel merať znova: 77 ms je update NVIDIA GPU,
/// takmer celé GPU Engine performance counters Windowsu; 13 ms je WMI dotaz na thermal
/// zones; 7 ms je audio health check; a celé AMD CPU, PawnIO a všetko, je 1,6 ms.
/// Pred odstránením jedného zbytočného COM volania z audio checku to bolo 218 ms.
///
/// Interval je jedna sekunda v oboch režimoch. Predchodca tejto triedy
/// (ThrottledSensorSource) mal vo VU režime tri sekundy, ale nie kvôli CPU — kvôli tomu,
/// že refresh na UI vlákne zastavil ručičku v pohybe. Tento dôvod tu zaniká, a jedna
/// sekunda navyše znamená, že audio health check si všimne zmenu default zariadenia
/// (slúchadlá v/von) do sekundy namiesto troch. Cena je 99 ms práce za sekundu, teda
/// asi 10 % jedného jadra nepretržite; takmer celé to sú tie GPU counters, nie niečo,
/// čo táto aplikácia počíta.
///
/// Trieda nevlastní logiku, len kadenciu — <see cref="RefreshOnce"/> je to, čo sa dá
/// otestovať bez hodín, rovnaký strih, aký má <see cref="MonitorService"/>.
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
    /// Jeden refresh. <see cref="CompositeSensorSource"/> už pohlcuje a latchuje poruchu
    /// každého jednotlivého source, takže sem sa dostane len zlyhanie composite samotného
    /// — ale to nesmie zabiť slučku. Neodchytená výnimka v fire-and-forget tasku znamená
    /// zamrznuté hodnoty a ručičky, ktoré vyzerajú ako funkčná aplikácia.
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
    /// Musí sa štartovať cez <c>Task.Run</c>. Volaný priamo z UI vlákna by prvý await
    /// zmarshalloval continuation späť naň cez WinForms SynchronizationContext a
    /// Refresh() by na UI vlákne bežal ďalej — celá zmena by bola no-op.
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
            // Normálne vypnutie. Odchytené tu, aby task dobehol do stavu Completed a
            // vypínanie nemuselo rozbaľovať AggregateException.
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
    /// <summary>Signalizuje prvý rámec cez TaskCompletionSource, takže test nemusí
    /// polovať ani čítať List cez hranicu vlákien.</summary>
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
    /// Prvý zápis uvízne, kým ho test nepustí — zaseknutý port. Oba príchody
    /// signalizuje cez TaskCompletionSource, takže test nikdy nečítá _frames, kým doň
    /// pumpa ešte môže zapisovať.
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

        /// <summary>Zapisuje výhradne pumpa. Čítaj až po dobehnutí jej tasku.</summary>
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
    /// Kapacita 1 a DropOldest: pre ručičku je zastaraný rámec bezcenný, takže kým je
    /// zápis zaseknutý, medziľahlé rámce sa zahodia a pošle sa len najnovší.
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

        // Pumpa teraz visí vnútri Send("frame-1"). Kanál drží jeden rámec, takže
        // frame-2 a frame-3 vypadnú a prežije len frame-4.
        link.Send("frame-2");
        link.Send("frame-3");
        link.Send("frame-4");

        gate.Set();
        await inner.SecondArrived.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        // Až tu — pumpa dobehla, takže _frames už nikto nemutuje.
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
/// Odpojí odoslanie rámca od volajúceho. <see cref="Send"/> po novom znamená „zaraď" —
/// mierna lož v mene zdedenom z <see cref="IMeterLink"/>, ktorá je zaplatená týmto
/// komentárom. Čestnejšie meno by stálo zmenený konstruktor MonitorService, prepísané
/// jeho testy a nový fake, a to všetko za identické chovanie.
///
/// Dôvod je, že <see cref="SerialMeterLink.Send"/> je blokujúci zápis a bežal na UI
/// vlákne. Na zaseknutom prevodníku to nie sú mikrosekundy, ale sekundy.
///
/// Kanál má kapacitu 1 a <see cref="BoundedChannelFullMode.DropOldest"/>: pre ručičku
/// je zastaraný rámec bezcenný, takže keď je zápis pomalý, medziľahlé rámce sa zahodia
/// a odošle sa len najnovší. Nezakrýva to problém s priepustnosťou — rámec má ~22
/// bajtov, pri 115200 baud ~1,5 ms, teda pri 21 Hz asi 3 % vyťaženia linky. Kapacita 1
/// je tam pre patologický prípad, nie pre bežný režim.
///
/// Asynchrónne I/O sa nezavádza zámerne. Voči zvyšku aplikácie je blokujúci Write na
/// vyhradenom tasku presne tak neblokujúci ako WriteAsync, a je to o triedu menej kódu.
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
    /// Zaradí rámec a vráti sa. Nikdy neblokuje a nikdy nehodí: <c>DropOldest</c> robí
    /// zo <c>TryWrite</c> vždy úspech, a po <see cref="Dispose"/> je návratové
    /// <c>false</c> len „vypíname sa", nie chyba.
    /// </summary>
    public void Send(string frame) => _frames.Writer.TryWrite(frame);

    /// <summary>
    /// Musí sa štartovať cez <c>Task.Run</c>. Volaný priamo z UI vlákna by continuation
    /// zmarshalloval späť naň cez WinForms SynchronizationContext a zápis by na UI
    /// vlákne zostal — celá zmena by bola no-op.
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
            // Normálne vypnutie.
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

Expected: PASS — 6 testov.

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
    /// Refresh() vlastní SensorRefreshLoop na vlastnom tasku. Keby ho Tick() volal tiež,
    /// vo VU režime by 99 ms práce bežalo 21× za sekundu na UI vlákne.
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
/// Nerefreshuje. Hardware obnovuje <see cref="SensorRefreshLoop"/> na vlastnom tasku raz
/// za sekundu, pretože Refresh() je 99 ms a na UI vlákne zastavil ručičku VU metra.
/// Tick() teda čítá to, čo tam posledný refresh nechal — čo je pre teploty a záťaž
/// presne v poriadku, a audio úroveň je aj tak živá, keďže sa počíta na capture vlákne.
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
        // Task.Run a nie priame zavolanie: konstruktor beží na UI vlákne, kde je
        // nainštalovaný WinForms SynchronizationContext, a ten by continuation po prvom
        // await zmarshalloval späť naň — Refresh() by na UI vlákne bežal ďalej a celá
        // zmena by bola no-op, ktorý sa nedá odhaliť inak než profilerom.
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

            // Cancellation nepreruší už rozbehnutý blokujúci zápis ani prebiehajúci
            // 99 ms Refresh(), takže tu je strop. Na zaseknutom porte to znamená až dve
            // sekundy blokovaného UI vlákna pri exite — lepšie než trhať port a driver
            // spod prebiehajúcej operácie.
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
        // source that dies later costs its own readings and nothing else. Refresh()
        // už nevolá tick — vlastní ho SensorRefreshLoop na vlastnom tasku raz za
        // sekundu, v oboch režimoch. Preto tu nie je žiadny throttle a preto VuMode
        // interval refreshu neovplyvňuje.
        ISensorSource sensors = new CompositeSensorSource(log, sources.ToArray());

        // Load-bearing, nie zvyk: AssignSensors nižšie číta snapshot, ktorý stavia
        // práve tento Refresh(). Bez neho by videlo prázdny snapshot a nenamapovalo nič.
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

        // MonitorService píše do frontu, nie na port. Tick beží na UI vlákne a
        // SerialPort.Write je na zaseknutom prevodníku otázka sekúnd, nie mikrosekúnd.
        // Tray a SettingsForm držia ďalej SerialMeterLink: potrebujú PortName a
        // IsConnected, ktoré na fronte nemajú čo robiť.
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

**Poznámka k Task 3 a 4 Step 2** — v oboch tých taskoch nový test **prechádza už pred implementáciou**, pretože pripína neprítomnosť locku na nesprávnom mieste, nie prítomnosť chovania. Nie je to TDD v obvyklom smere a je to zámerné: lock sa nedá otestovať tak, že by pred jeho pridaním test padal. Skutočným testom v týchto dvoch taskoch je, že **celá existujúca suite prejde ďalej** (Step 4) a že tieto dva testy neprestanú prechádzať.
