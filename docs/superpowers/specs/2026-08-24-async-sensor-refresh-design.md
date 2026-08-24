# Asynchrónny refresh senzorov a odoslanie rámca — návrh

Dátum: 2026-08-24
Stav: schválený návrh, pripravený na plán implementácie
Nadväzuje na: [`2026-08-22-vu-meter-design.md`](2026-08-22-vu-meter-design.md)

## Cieľ

Vo VU režime ručičky viditeľne zadrhávajú a UI tuhne. Dôvod sú presne dve veci,
ktoré dnes bežia na UI vlákne:

| Čo | Cena | Kde je dnes |
| --- | --- | --- |
| `ISensorSource.Refresh()` | 99 ms | `MonitorService.Tick()`, WinForms timer |
| `SerialMeterLink.Send()` | blokujúci zápis, na zaseknutom porte sekundy | to isté |

Tie 99 ms sú dlhšie než 65 ms časová konštanta `VuIntegrator`, takže ručička
prechádzajúca cez refresh zastane. Presne kvôli tomu musel commit `de6d076`
posunúť refresh vo VU režime na 3 s — nie kvôli CPU, ale kvôli pohybu.

Cieľom je dostať z UI vlákna **len tie dve veci a nič viac.** Nie je to
prestavba na push model; kanálový model, kalibrácia, settings okno ani formát
rámca sa nemenia.

Odmenou je, že dôvod pre interval 3 s zaniká úplne, a s ním celá trieda
`ThrottledSensorSource`.

## Zásadné rozhodnutia

1. **Z UI vlákna odchádzajú presne dve veci** — `Refresh()` a sériový zápis.
   Stavba rámca (5 lookupov a `string.Join`, jednotky mikrosekúnd) a event
   `Updated` na UI vlákne **zostávajú**, takže `SettingsForm` ani kalibračný
   `MonitorService._testPwm` nepotrebujú žiadne marshallovanie. Je to zámer, nie
   kompromis: najmenšia zmena, ktorá symptóm odstraňuje.
2. **`Refresh()` je 1 Hz v oboch režimoch.** `AppConfig.VuMode` prestáva
   ovplyvňovať refresh a zostáva už len na profiloch kanálov a na intervale
   timera. Cena je 99 ms/s ≈ 10 % jedného jadra vo VU režime namiesto 3,3 %.
   Zaplatené za: o triedu menej kódu, jeden konstantový split menej, a zmena
   audio zariadenia (slúchadlá v/von) zaznamenaná do 1 s namiesto 3 s — dnes
   obe ručičky ležia mŕtve až tri sekundy. Ak sa tých 6,6 % ukáže ako problém,
   split sa vráti ako pár riadkov v poll slučke — ale až po meraní, nie po tipe.
3. **`ThrottledSensorSource` sa maže**, aj jeho testy. Jeho jedinou prácou bolo
   brániť `Refresh()`; tou bránou je po novom `PeriodicTimer` v poll tasku.
   Namerané hodnoty z jeho komentárov (77 ms GPU Engine counters, 13 ms WMI
   thermal zone, 7 ms audio health check, 1,6 ms celé AMD CPU cez PawnIO) sa
   **musia** preniesť do `SensorRefreshLoop`. Tá história je výsledok
   instrumentovaného buildu a nesmie sa stratiť pri mazaní súboru.
4. **Snapshot patrí dovnútra každého source, kľúčovaný `SensorId`.** Nie
   globálny store s pomenovanými poliami typu
   `HardwareState(CpuTemperature, GpuTemperature, VuLeft, VuRight)` — ten by
   zahodil päť voľne konfigurovateľných kanálov, mapovanie ľubovoľného senzora
   na ľubovoľný kanál, min/max/PWM kalibráciu aj `VuModeSwitch`.
5. **Potlačenie nezmenených rámcov sa nerobí.** Tick posiela rámec vždy. Arduino
   má `WATCHDOG_MS = 3000` a vynuluje ručičky, keď rámec nedorazí; podmienka
   „pošli len pri zmene" by na nečinnom stroji so stabilnými teplotami znamenala
   tri sekundy ticha a spadnuté ručičky, a potrebovala by keepalive. Takto ten
   problém nevznikne. Keď sa zápis reálne zasekne na viac ako 3 s, watchdog
   vynuluje ručičky — a to je správne, link je vtedy naozaj mŕtvy.
6. **Kanál je `Channel<string>` s kapacitou 1 a `DropOldest`.** Pre ručičku je
   zastaraný rámec bezcenný. Kanál nezakrýva throughput problém: rámec má ~22
   bajtov, pri 115200 baud ~1,5 ms, pri 21 Hz teda 3 % vyťaženia linky — v praxi
   nezahodí nič a je tam pre patologický prípad.
7. **`SerialMeterLink.IsConnected` sa nesmie čítať pod lockom.** Sender task
   drží lock počas `Write()`, čo je na zaseknutom porte sekundy, a UI si
   `IsConnected` číta každý tick pre tooltip. Lock na getteri by vyrobil presne
   ten symptóm, ktorý celý návrh opravuje.
8. **Asynchrónne I/O sa nezavádza.** `IMeterLink.Send` zostáva synchrónne a
   `SerialPort.Write` blokujúce. Voči zvyšku aplikácie je blokujúci zápis na
   vyhradenom tasku presne tak neblokujúci ako `WriteAsync`, a je to o triedu
   menej kódu, nulová zmena rozhrania a nula prepísaných testov.
9. **Slučky nevlastnia logiku, len kadenciu.** `SensorRefreshLoop` vystaví
   `RefreshOnce()` na testovanie a `RunAsync(ct)` je tenký `PeriodicTimer` nad
   ním. Rovnaký strih, aký má `MonitorService` v doc komentári už dnes: *„Owns no
   timer and no threads — the caller decides when a tick happens."*

## Architektúra

```text
[poll task, PeriodicTimer 1 Hz]          [WASAPI capture vlákno]
        |                                         |
   sensors.Refresh()                     VuIntegrator.Add()
   99 ms: LHM Update() + WMI +              priebežne, alfa per-sample
   audio health check                            |
        |                                         |
        +---- Volatile.Write(snapshot) ----+       |
                                          v       v
                       [UI vlákno: WinForms timer, 1 s / 40 ms]
                                 MonitorService.Tick()
                       5x Read() -> ChannelPipeline -> 5 PWM bajtov
                                          |
                            Updated (UI vlákno, bez marshallingu)
                                          |
                                          v
                        Channel<string>, kapacita 1, DropOldest
                                          |
                                          v
                        [sender task] SerialMeterLink.Send()
                                          |
                                          v
                                       Serial
```

## Vláknový model

| Vlákno | Čo tam beží | Kadencia |
| --- | --- | --- |
| poll task | `Refresh()` na celom composite | 1 Hz, oba režimy |
| WASAPI capture | `VuIntegrator.Add()` | rytmus buffrov, ~10–100 ms |
| UI (WinForms timer) | `Tick()`: `Read()`, mapovanie, `Encode()`, `Updated`, tray tooltip | 1 s / 40 ms podľa `VuMode` |
| sender task | `SerialMeterLink.Send()`, reconnect | podľa kanála |

## Prechody hardware stromu: z troch na jeden

Dnes sa strom `Computer.Hardware` prechádza na troch miestach, nie na jednom:

| Miesto | Dnes | Po zmene |
| --- | --- | --- |
| `Refresh()` → `_computer.Accept(visitor)` | `Update()` na každom hardware, 99 ms | jediný prechod, 1 Hz, poll task |
| `Read()` → `EnumerateSensors().FirstOrDefault(...)` | **prechod stromu, 5× za tick** | `TryGetValue`, O(1) |
| `Discover()` → `EnumerateSensors().Select(...)` | prechod stromu z UI eventov | vráti predpripravený list |

Bod dva je horší, než sa zdá, kvôli `CompositeSensorSource.Read()`, ktorý sa
pýta **každého** source na **každé** id: pre kanál namapovaný na ACPI zónu sa
najprv LHM prehrabe celým stromom a vráti `null`, až potom odpovie ACPI. Pre
audio kanál to isté. Niekoľko z tých piatich prechodov je teda nutne úplných.
Navyše `sensor.Identifier.ToString()` v predikáte alokuje string **na každý
senzor pri každom porovnaní** — koľko presne to je alokácií nie je zmerané a
netipuje sa, podstatné je, že refaktor tú kategóriu ruší celú.

Po zmene: **jeden prechod za sekundu na poll tasku, nula prechodov na UI
vlákne.** Dve podmienky, ktoré tú vlastnosť inak potichu zrušia:

- Snapshot sa **musí** stavať v tom istom priechode ako `Update()`. Lazy stavba
  pri prvom `Read()` vráti prechod na UI vlákno a nevyrieši nič.
- `Program.cs:85` volá `sensors.Refresh()` pred `Discover()` a `Read()` na
  riadku 88. Ten riadok je po novom **load-bearing**: bez neho by
  `SensorDefaults.AssignSensors` videlo prázdny snapshot a nenamapovalo nič.
  Dnes to funguje skôr náhodou, po zmene to musí byť komentárom označené ako
  podmienka.

## Moduly

### Nové v `AnalogHwMonitor.Core`

**`SensorRefreshLoop`** — vlastní kadenciu `Refresh()`.

```csharp
public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

/// <summary>Jeden refresh. Verejné, aby sa dalo testovať bez hodín.</summary>
public void RefreshOnce()
{
    try { _sensors.Refresh(); }
    catch (Exception ex) { /* latchovaný log, slučka pokračuje */ }
}

public async Task RunAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(Interval);
    while (await timer.WaitForNextTickAsync(ct)) RefreshOnce();
}
```

`try` je vnútri, nie okolo slučky. Neodchytená výnimka v fire-and-forget tasku
znamená, že sourcy prestanú tichúčko refreshovať a ručičky navždy zamrznú na
posledných hodnotách — teda softvér, ktorý vyzerá funkčne a nie je. To je
najhorší možný failure mode a `TrayApplicationContext.OnTick()` ho pre svoju
cestu už dnes takto vylučuje.

**`QueuedMeterLink : IMeterLink`** — dekorátor, ktorý zo `Send()` robí „zaraď".

```csharp
private readonly Channel<string> _frames = Channel.CreateBounded<string>(
    new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,   // sender task
        SingleWriter = true,   // UI tick
    });

public bool IsConnected => _inner.IsConnected;
public string? LastError => _inner.LastError;

public void Send(string frame) => _frames.Writer.TryWrite(frame);

public async Task RunAsync(CancellationToken ct)
{
    await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
    {
        _inner.Send(frame);
    }
}
```

`Send()` po novom znamená „zaraď", čo je mierna lož v mene metódy zdedenom
z `IMeterLink`. Zaplatí sa doc komentárom, nie novým rozhraním: cena za
čestnejšie pomenovanie by bola zmenený konstruktor `MonitorService`, prepísané
`MonitorServiceTests` a nový fake — za identické chovanie. Rovnaký tvar
dekorátora, aký mal `ThrottledSensorSource`.

### Zmenené v `AnalogHwMonitor.Core`

**`LibreHardwareSensorSource`** — atomicky prehadzovaný snapshot.

```csharp
private sealed record Snapshot(
    Dictionary<string, float> Values,
    IReadOnlyList<SensorDescriptor> Descriptors);

private Snapshot _snapshot = new(new(), Array.Empty<SensorDescriptor>());

public void Refresh()
{
    _computer.Accept(_visitor);

    var values = new Dictionary<string, float>();
    var descriptors = new List<SensorDescriptor>();

    foreach (var (hardware, sensor) in EnumerateSensors())
    {
        var id = sensor.Identifier.ToString();
        if (sensor.Value is { } value) values[id] = value;
        descriptors.Add(new SensorDescriptor(
            id, sensor.Name, hardware.Name,
            ToKind(sensor.SensorType), ToUnit(sensor.SensorType)));
    }

    Volatile.Write(ref _snapshot, new Snapshot(values, descriptors));
}

public float? Read(string id) =>
    Volatile.Read(ref _snapshot).Values.TryGetValue(id, out var v) ? v : null;

public IReadOnlyList<SensorDescriptor> Discover() =>
    Volatile.Read(ref _snapshot).Descriptors;
```

**Jeden record a nie dve polia**, a to je podstatné: pri dvoch samostatných
`Volatile.Write` by čitateľ mohol vidieť nové deskriptory so starými hodnotami.
Jeden zápis jedného objektu zaručuje, že hodnoty a deskriptory sú vždy z tej
istej generácie. Publikované kolekcie sa už nikdy nemutujú, takže ich netreba
mraziť do `Frozen*`.

`sensor.Value` je `float?`; senzory s `null` sa do slovníka nedajú a
`TryGetValue` na ne vráti `false` → `Read()` vráti `null`. Presne dnešné
chovanie.

**`AcpiThermalSensorSource`** — to isté, menšia zmena. Už dnes stavia slovník
v `Refresh()`, len ho mutuje na mieste (`_values.Clear()` a plnenie). Nahradí sa
lokálnym novým slovníkom a rovnakým swapom. Chybová cesta (`catch` → `Clear()`)
sa stane „prehoď prázdny snapshot", čo je pôvodná semantika. `_faultReported` sa
dotýka výhradne `Refresh()`, teda len poll task, a race nemá s čím.

**`AudioLevelSensorSource`** — `lock` na životný cyklus captureu. Snapshot tu
nemá zmysel, hodnota je živá zámerne. Po novom ho ale šahajú dve vlákna:

| Metóda | Vlákno | Čo robí |
| --- | --- | --- |
| `Refresh()` | poll task | idle timeout → `Stop()`, zmena default zariadenia → `Stop()` |
| `Read()` | UI | `_lastRead`, `EnsureStarted()` → `TryStart()` |

Súťažia o `_started`, `_reportedError`, `_lastFailedStart`, `_lastRead`,
o `_capture` aj o reset integrátorov. Najhorší prípad nie je hypotetický: poll
task sa rozhodne pre `Stop()` (slúchadlá von) presne vtedy, keď je UI vlákno
vnútri `TryStart()`. Skončí to `_started == true` nad zastaveným captureom a
**obe ručičky ležia mŕtve, kým nimi niečo nezatriasa.**

Jeden `lock` okolo `Refresh`, `EnsureStarted`, `Stop` a audio vetvy `Read`. Dve
veci, ktoré **zámerne nepokrýva**:

- **`OnSamples`** beží na capture vlákne v rytme buffrov a nesmie nikdy čakať na
  UI vlákno. Dnes komunikuje výhradne cez `Volatile`/`Interlocked`
  (`_lastBufferTicks`, `_lastAdvanceTicks`, `VuIntegrator._level`), takže lock
  nepotrebuje — a nesmie ho vziať.
- **`Read()` pre cudzie id.** Early return `if (channel < 0) return null;` je
  pred akýmkoľvek dotykom stavu, takže lock ide až za neho. Composite sa audia
  pýta na všetkých päť id, ale zamkne sa len na dvoch audio → 2 nekontendované
  akvizície za tick.

`VuIntegrator` sa nemení. Jeho zdokumentovaný akceptovaný lost update medzi
`Add()` (capture vlákno) a `Decay()` (UI vlákno) platí ďalej — `Decay()` sa
volá stále len z `Read()`, teda z UI vlákna, takže nové vlákno tam nepribúda.

**`SerialMeterLink`** — `lock` a `volatile bool`. `PortName` setter beží na UI
vlákne a volá `Disconnect()` → `_port.Dispose()`, zatiaľ čo sender task môže byť
vnútri `_port.Write()`. Konkrétne call site je `SettingsForm.cs:398`
(`_link.PortName = _monitor.Config.ComPort;`) — teda nie hypotéza, ale existujúca
cesta, ktorou človek mení COM port za behu.

| Člen | Ochrana |
| --- | --- |
| `Send`, `TryConnect`, `Disconnect`, `PortName` setter | `lock` |
| `IsConnected` | `private volatile bool _connected`, píše sender task vnútri locku, UI čítá bez locku |
| `LastError` | `string?`, atomický referenčný read, bez locku — v najhoršom je tooltip o tick starší |

`IsConnected` bez locku je **požiadavka, nie optimalizácia** — viď zásadné
rozhodnutie 7.

**`MonitorService`** — `Tick()` stráca riadok `_sensors.Refresh()`. Doc komentár
musí povedať, kto refreshuje namiesto neho, inak to vyzerá ako zabudnuté volanie.

### Zmenené v `AnalogHwMonitor.App`

**`TrayApplicationContext`** — dostane `SensorRefreshLoop` a `QueuedMeterLink`,
vlastní `CancellationTokenSource` aj oba tasky. Timer, tray ikona, `OnTick()`,
`SetVuMode()` a intervaly `VuIntervalMs`/`SensorIntervalMs` zostávajú.

```csharp
_timer.Stop();                    // prestanú pribúdať rámce
_cts.Cancel();                    // oba loopy vypadnú
Task.WaitAll([_pollTask, _sendTask], TimeSpan.FromSeconds(2));
_monitor.Dispose();               // až teraz; nič už nepíše
```

Ten timeout robí reálnu prácu: cancellation **nepreruší** už rozbehnutý
blokujúci `SerialPort.Write`. Na zaseknutom porte teda pri exite zablokuje UI
vlákno až na 2 s. Prijaté — je to lepšie než trhať `SerialPort` spod
prebiehajúceho zápisu.

**`Program.cs`** — drôtovanie. `ThrottledSensorSource` zmizne z reťaze,
`SerialMeterLink` sa obalí do `QueuedMeterLink`, obe slučky sa skonštruujú a
odovzdajú do tray. `sensors.Refresh()` na riadku 85 zostáva a dostane komentár.

### Vedome nedotknuté

`CompositeSensorSource` **netreba** diriť, a je to šťastná náhoda, ktorú treba
zdokumentovať, aby ju niekto neskôr „nevyčistil": jeho `_lastFault` je
`string?[,]` indexovaný per source **aj per operation**, takže `Refresh` z poll
tasku píše do slotu `[i, Refresh]`, a `Read` s `Discover` z UI vlákna do
`[i, Read]` a `[i, Discover]`. Vždy rôzne prvky, referenčné zápisy sú atomické,
invariant naprieč slotmi neexistuje. Ten druhý index pritom vznikol z úplne
iného dôvodu (striedajúce sa hlášky, `c7fc720`).

`SettingsForm` **netreba** zmeniť. Na linku volá výhradne `PortName` (get aj
set) a `IsConnected`, teda členy, ktoré zostávajú na `SerialMeterLink`; obal
`QueuedMeterLink` dostane len `MonitorService`. `TrayApplicationContext` drží
`SerialMeterLink` ďalej pre tooltip a dostane `QueuedMeterLink` navyše len preto,
aby mala čo spustiť.

Nedotknuté ďalej: `VuIntegrator`, `FrameCodec`, `ChannelPipeline`,
`ChannelMapper`, `MeterCalibration`, `VuModeSwitch`, `ConfigStore`, `AppConfig`,
`FileLog`, `WasapiLoopbackAdapter`, `PortDetector`, `StartupRegistration`,
`SensorDefaults`, `AppIcons`, `ChannelRowControl`, a **Arduino sketch**.

## Chybové stavy

| Porucha | Chovanie |
| --- | --- |
| Jeden source hodí v `Refresh()` | `CompositeSensorSource` ho pohltí a latchne, ostatné sourcy idú ďalej (nezmenené) |
| Celý composite hodí v `Refresh()` | `RefreshOnce()` odchytí, latchne do logu, slučka pokračuje |
| Poll task zomrie | nesmie sa stať; ak áno, hodnoty zamrznú — preto je `try` vnútri slučky |
| Sender task zomrie | rámce prestanú chodiť, Arduino watchdog vynuluje ručičky do 3 s → viditeľné |
| Sériový zápis blokuje | kanál zahadzuje staré rámce, UI beží; po 3 s ticha watchdog vynuluje |
| Zmena COM portu počas zápisu | `lock` v `SerialMeterLink` serializuje setter proti `Send()` |
| Zmena audio zariadenia | `Refresh()` na poll tasku si všimne do 1 s, `Stop()` pod lockom, ďalší `Read()` naštartuje na novom |

## Testovanie

Nové:

- **`SensorRefreshLoopTests`** — `RefreshOnce()` volá `Refresh()` raz;
  `ThrowingSensorSource` (už v repo) neprebije slučku a zaloguje raz, nie
  opakovane. `RunAsync` sa netestuje: jeho obsahom je `PeriodicTimer`, čo nie je
  náš kód, a existujúci `FakeTimeProvider` prepisuje len `GetUtcNow()`, nie
  `CreateTimer()` — test by čakal na reálne hodiny. Preto to rozdelenie.
- **`QueuedMeterLinkTests`** — rámce dorazia do `FakeMeterLink`; pri
  nevyprázdňovanom kanáli prežije len najnovší rámec; `Send()` po `Dispose()`
  nehodí; `IsConnected` a `LastError` delegujú.
- **Nemennosť snapshotu** namiesto race testov. Race test je flaky z princípu;
  skutočný invariant je, že publikovaný snapshot sa už nemutuje:

  ```csharp
  var before = source.Discover();
  var count = before.Count;
  source.Refresh();
  Assert.Equal(count, before.Count);   // starý list nám nikto nevyprázdnil
  ```

  Tento test **dnes na `AcpiThermalSensorSource` padá** a po zmene prejde.
  Pripína bug, nie plánovač vlákien. To isté pre `LibreHardwareSensorSource`
  (`SkippableFact`, ako to robia existujúce testy toho source).
- **`AudioLevelSensorSource`** — `Refresh()` rozhodne o `Stop()`, následný
  `Read()` naštartuje znova; overené cez start/stop counts na
  `FakeAudioLoopbackCapture`. V sekvencii, teda deterministicky.

Mazané: celý `ThrottledSensorSourceTests`; a
`MonitorServiceTests.Tick_RefreshesTheHardwareExactlyOnce`, ktorý sa stáva
obsolete — nahradí ho `Tick_DoesNotRefreshTheHardware`. Zvyšok
`MonitorServiceTests` a `FakeMeterLink` prežívajú nedotknuté.

## Zmeny v existujúcom kóde

| Súbor | Zmena |
| --- | --- |
| `Core/SensorRefreshLoop.cs` | **nový** |
| `Core/QueuedMeterLink.cs` | **nový** |
| `Core/ThrottledSensorSource.cs` | **zmazaný**, komentáre s meraniami presunuté |
| `Core/LibreHardwareSensorSource.cs` | snapshot swap, `Read()` na O(1) |
| `Core/AcpiThermalSensorSource.cs` | snapshot swap namiesto mutácie na mieste |
| `Core/AudioLevelSensorSource.cs` | `lock` na lifecycle, nie na `OnSamples` |
| `Core/SerialMeterLink.cs` | `lock` + `volatile bool _connected` |
| `Core/MonitorService.cs` | `Tick()` stráca `_sensors.Refresh()`, doc komentár |
| `App/TrayApplicationContext.cs` | dva parametre navyše, `CancellationTokenSource`, dva tasky, shutdown |
| `App/Program.cs` | drôtovanie, komentár k load-bearing `Refresh()` |
| `Tests/ThrottledSensorSourceTests.cs` | **zmazaný** |
| `Tests/MonitorServiceTests.cs` | jeden test nahradený |
| `Tests/SensorRefreshLoopTests.cs`, `Tests/QueuedMeterLinkTests.cs` | **nové** |
| `Tests/AcpiThermalSensorSourceTests.cs`, `LibreHardwareSensorSourceTests.cs`, `AudioLevelSensorLifecycleTests.cs` | pridané testy |
| `README.md` | sekcia o vláknovom modeli a intervale |

## Vedome vynechané (YAGNI)

- **`HardwareStateStore` s CAS a `StoreChanged`.** Riešil by koordináciu
  producentov, ktorú tento návrh nemá: `Refresh()` a `Read()` sú stále pull,
  len z iného vlákna. Bez pomenovaných polí (rozhodnutie 4) by z neho zostal
  slovník so zámkom, teda presne to, čo už každý source má sám.
- **Push model s `ArduinoSender` čakajúcim na signál.** Tick riadi WinForms
  timer, ktorý musí bežať aj tak kvôli `Updated` a tray tooltipu. Druhý
  mechanizmus na to isté je len ďalší stav.
- **`SemaphoreSlim` ako signál.** Počítadlo rastie s každou zmenou od
  producentov; správna semantika je „posledná hodnota, staršie zahoď", čo je
  presne `Channel` s kapacitou 1 a `DropOldest`.
- **`WriteAsync` nad `SerialPort.BaseStream`.** Viď rozhodnutie 8.
- **`Microsoft.Extensions.TimeProvider.Testing`.** Rozdelenie
  `RefreshOnce()`/`RunAsync()` robí balík nepotrebným.
- **Prepis VU cesty na 40 ms polling loop.** `VuIntegrator` dnes počíta úroveň
  priebežne na capture vlákne s alfou per-sample, teda presnejšie a nezávisle od
  veľkosti WASAPI buffra. Vzorkovanie každých 40 ms by bolo horšie.
- **Konfigurovateľný interval refreshu.** Jedna konstanta v
  `SensorRefreshLoop`. Keď bude treba, zmení sa tam.
