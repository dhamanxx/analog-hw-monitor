# VU meter na kanáloch 0 a 1 — návrh

Dátum: 2026-08-22
Stav: schválený návrh, pripravený na plán implementácie
Nadväzuje na: [`2026-08-18-analog-hw-monitor-design.md`](2026-08-18-analog-hw-monitor-design.md)

## Cieľ

Pri počúvaní hudby je CPU a GPU záťaž nezaujímavá. Zaškrtávatko **VU meter**
prepne kanály 0 a 1 (piny 3 a 5) na stereo VU meter napájaný zvukom, ktorý ide
z Windows do reproduktorov, a odškrtnutie ich vráti na CPU a GPU záťaž. Ostatné
tri meradlá (RAM, CPU teplota, GPU teplota) sa nemenia.

| Kanál | Pin | Bez VU režimu | V VU režime |
| --- | --- | --- | --- |
| 0 | 3 | CPU záťaž, 0–100 % | VU Left, -40–0 dBFS |
| 1 | 5 | GPU záťaž, 0–100 % | VU Right, -40–0 dBFS |
| 2 | 6 | Zaplnenie RAM | nezmenené |
| 3 | 9 | CPU teplota | nezmenené |
| 4 | 10 | GPU teplota | nezmenené |

## Zásadné rozhodnutia

1. **Audio vstupuje ako obyčajný senzor.** `AudioLevelSensorSource` implementuje
   existujúce `ISensorSource` a vystaví dva pseudo-senzory `/audio/0/level/0` (L)
   a `/audio/0/level/1` (R) s jednotkou `dBFS`. Pripojí sa do už existujúceho
   `CompositeSensorSource` vedľa LibreHardwareMonitor a ACPI zdroja.
   `MonitorService`, `ChannelPipeline`, `ChannelMapper`, `MeterCalibration` ani
   `FrameCodec` sa nemenia.
2. **Arduino sa nereflashuje.** Rámec zostáva `V:a,b,c,d,e`, mení sa len to, ako
   často ide po drôte. Watchdog 3 s funguje bez zmeny.
3. **Rýchlosť slučky riadi výlučne zaškrtávatko.** VU zapnuté → 40 ms (~25 Hz),
   vypnuté → 1000 ms. Žiadny iný stav rýchlosť nemení, ani porucha audia.
4. **LibreHardwareMonitor sa číta najviac raz za sekundu** bez ohľadu na rýchlosť
   slučky. Zabezpečí to dekorátor `ThrottledSensorSource`; driver I/O cez PawnIO
   je najdrahšia vec v celom ticku a nesmie zdvihnúť zaťaženie CPU.
5. **Poruchu audia hlásia ručičky, nezakrýva ju automatika.** Keď sa capture nedá
   spustiť, kanály 0 a 1 idú existujúcou cestou pre mŕtvy senzor: `null` → nula na
   ručičke, červený riadok, zápis do logu. Potichu vrátiť CPU záťaž na pin 3 by
   znamenalo, že sa človek pozerá na ručičku v domnení, že vidí hudbu, a vidí
   procesor.
6. **Za správnosť nastavení ručí užívateľ.** Nič sa v UI nezamyká; audio senzor sa
   dá priradiť ktorémukoľvek kanálu a rozsahy sa dajú nastaviť ako doteraz.

## Architektúra

```text
WASAPI loopback (default render device)
        |  DataAvailable, capture vlákno, bloky ~10-100 ms
        v
WasapiLoopbackAdapter          <- jediná trieda, ktorá vidí NAudio
        |  Span<float>, interleaved
        v
VuIntegrator x2 (L, R)         <- |x| -> jednopólový filter, tau = 65 ms
        |  Volatile.Read(double), bez lockov
        v
AudioLevelSensorSource         <- x pi/2, 20*log10, kompenzácia hlasitosti, pád pri tichu
        |  ISensorSource.Read("/audio/0/level/0") -> dBFS
        v
CompositeSensorSource ---------+--- LibreHardwareSensorSource
        |                      +--- AcpiThermalSensorSource
        v
ThrottledSensorSource          <- Refresh() najviac 1x za sekundu, Read() prechádza
        |
        v
MonitorService.Tick()          <- nezmenené
        |  "V:a,b,c,d,e" @ 25 Hz (VU on) alebo 1 Hz (VU off)
        v
Arduino UNO                    <- nezmenený sketch
```

## Moduly

### Nové v AnalogHwMonitor.Core

| Súbor | Zodpovednosť | Testy |
| --- | --- | --- |
| `VuIntegrator.cs` | Čistá balistika: `Add(ReadOnlySpan<float> block, int offset, int stride, int sampleRate)`, `Decay(TimeSpan)`, `Level`. Jeden integrátor na kanál, `offset` a `stride` z neho vyberajú jeho vzorky v interleavovanom bloku. Žiadne COM, žiadne vlákna, žiadna alokácia. | Plné |
| `IAudioLoopbackCapture.cs` | Rozhranie: `Start`, `Stop`, `SampleRate`, `ChannelCount`, `DeviceId`, `DeviceName`, `VolumeDb`, `IsMuted`, udalosť so vzorkami | — |
| `WasapiLoopbackAdapter.cs` | Implementácia nad NAudio. Analógia `SerialPortAdapter`: jediné netestované miesto. | Ručne |
| `AudioLevelSensorSource.cs` | `ISensorSource` nad capture-om: dBFS, kompenzácia hlasitosti, pád pri tichu, zdravotná kontrola a reštart | Plné, cez fake capture |
| `ThrottledSensorSource.cs` | Dekorátor obmedzujúci `Refresh()` na 1 Hz | Plné |
| `VuModeSwitch.cs` | Výmena profilov nad `AppConfig`, čistá funkcia | Plné |
| `ChannelProfile.cs` | Odkladaný profil kanála: `Channel`, `Label`, `SensorId`, `Min`, `Max` | — |

Naša trieda sa **nesmie** volať `WasapiLoopbackCapture` — tak sa volá trieda
v NAudio.

### NuGet

`NAudio.Wasapi` (MIT), nie celé `NAudio`. Obsahuje `WasapiLoopbackCapture`,
`MMDeviceEnumerator` a `AudioEndpointVolume`, ťahá si len `NAudio.Core`. Je to
čisto managed kód, takže `PublishSingleFile` so self-contained runtime funguje
ďalej bez natívnych príloh.

## Signálová cesta

### Balistika

Norma VU z roku 1942: ručička dosiahne 99 % ustálenej hodnoty za 300 ms a rovnako
pomaly padá. Pre jednopólový filter je `t(99 %) = tau * ln(100) = 4.605 * tau`,
teda **tau = 65 ms**. Nábeh a pád sú symetrické, pretože ide o lineárny filter —
jedna konštanta pokrýva oboje.

Integrátor beží **po vzorkách, nie po blokoch**: koeficient sa počíta zo
vzorkovacej frekvencie, takže výsledok nezávisí od toho, aké veľké buffery WASAPI
dodá.

```text
alpha = 1 - exp(-1 / (sampleRate * tau))        // 48 kHz, tau 65 ms -> alpha ~ 0.00032
level += (|x| - level) * alpha                  // pre každú vzorku daného kanála
```

Presnosť nad túto úroveň by bola vyhodená: pravé ručičkové meradlo si pridá vlastnú
mechanickú zotrvačnosť.

### Prevod na dBFS

Jednopólový filter nad usmernenou hodnotou dáva **priemer**, nie špičku. Priemer
usmernenej sínusovky je `2/pi = 0.6366` jej amplitúdy, takže sa hodnota pred
logaritmovaním násobí `pi/2 = 1.5708`. Sínusovka na plnú stupnicu potom číta
`0 dBFS`. Je to klasická kalibrácia VU metra: priemerne reagujúci, na sínusovke
kalibrovaný na špičku.

```text
dBFS = 20 * log10(level * pi/2)
```

Podlaha je **-100 dBFS**; `level == 0` sa na ňu klampuje namiesto `-inf`.
Prednastavený rozsah kanála je `min = -40`, `max = 0` dBFS a je editovateľný
v okne nastavení ako každý iný rozsah.

### Kompenzácia hlasitosti Windows

WASAPI loopback odoberá signál z mixu zvukového engine-u, takže hlasitosť Windows
sa v ňom typicky prejaví. Bez kompenzácie by pri hlasitosti 25 % ručičky trčali
pri nule aj pri najhlasnejšej skladbe.

`AudioEndpointVolume.MasterVolumeLevel` dáva priamo útlm v dB, takže sa
**pripočítava** — žiadne delenie nulou pri stíšení na nulu:

```text
prirastok = min(-masterVolumeDb, 40)            // masterVolumeDb <= 0, prirastok >= 0
dBFS_kompenzovane = dBFS_merane + prirastok
```

Strop +40 dB je nutný: pri hlasitosti 5 % by kompenzácia pripočítala ~+26 dB
a vytiahla šum a dither na plnú stupnicu. Mute znamená podlahu.

Či loopback vôbec obsahuje master volume, závisí od zariadenia — endpointy
s hardvérovou reguláciou hlasitosti ho neobsahujú. Preto je kompenzácia
prepínateľná (`vuCompensateVolume`, default `true`): ak by sa na danej zvukovej
karte ručičky začali hýbať opačne ku knoflíku, vypne sa jedným zaškrtávatkom.

Hlasitosť sa **nečíta pollingom**. `OnVolumeNotification` sa prihlási raz
a aktualizuje jedno cachované pole, takže na tick nejde ani jedno COM volanie
(inak by ich bolo 50 za sekundu na UI vlákne).

### Pád pri tichu

Keď hudba prestane hrať, WASAPI prestane dodávať buffery úplne — nie ticho, ale
nič. Integrátor by zamrzol na poslednej hodnote a **ručička by zostala trčať
v polovici stupnice po skončení skladby.**

Preto si zdroj pamätá čas posledného bufferu (`TimeProvider`). Keď nič nepríde
dlhšie než **150 ms**, dopočíta pád tou istou 65 ms konštantou podľa uplynutého
času, takže ručička plynulo dobehne na nulu namiesto skoku alebo zamrznutia.

### Kto capture spúšťa a zastavuje

Nikto zvonku. Zdroj sa riadi tým, či ho niekto číta:

- **Prvé `Read()` audio senzora spustí capture.** `Discover()` ho nespustí nikdy —
  okno nastavení plní dropdowny a nesmie tým zabrať zvukové zariadenie. Názov
  zariadenia pre `Discover()` sa dá zistiť samotným enumerovaním, bez capture-u.
- **Keď audio senzor nikto nečítal 5 sekúnd, capture sa zastaví** a zariadenie sa
  uvolní. Vypnutie VU režimu teda capture zhasne samo tým, že kanály 0 a 1
  prestanú audio senzory čítať; nie je na to potrebná žiadna cesta z UI do zdroja.

Vďaka tomu je `AudioLevelSensorSource` naozaj len ďalší `ISensorSource` a o VU
režime nevie nič — o rýchlosti slučky rozhoduje `TrayApplicationContext`, o tom,
čo sa číta, `config.json`, a zdroj sa len prispôsobí.

`Start` na `IAudioLoopbackCapture` nevyhadzuje výnimku, ale vracia `false`
a dôvod; zdroj ho zaloguje raz a ďalej ho drží zalatchovaný rovnako ako
`SerialMeterLink.Report` a `CompositeSensorSource.Try`, aby zlyhávanie každú
sekundu nezaplnilo log. Prvé sekundy po spustení capture-u vracia `Read` stúpajúcu
hodnotu od podlahy, nie `null` — `null` znamená pokazené, nie rozbehávajúce sa.

### Prefix identifikátorov

`CompositeSensorSource` má v dokumentačnom komentári napísané, že `Read` vracia
prvú nenulovú hodnotu spomedzi zdrojov, a že je to bezpečné **len preto, že každý
zdroj používa disjunktný prefix identifikátorov**. Prefix `/audio/` je disjunktný
od `/acpi/thermalzone/` aj od všetkých prefixov LibreHardwareMonitor, takže tá
podmienka drží aj po pridaní tretieho zdroja.

### Formáty a kanály

Mix format loopbacku je takmer vždy IEEE float 32. Podporené sú aj PCM 16/24/32
ako záložné; iný formát je ohlásená porucha, nie odvysielané smeti.
Viackanálový výstup (5.1, 7.1) berie prvé dva kanály, teda front L/R. Mono
zariadenie dá L = R.

## Konfigurácia

Pribudnú tri veci: `vuMode`, `vuCompensateVolume` a odkladacie miesto
`stashedChannels`.

```json
{
  "comPort": "COM3",
  "startWithWindows": true,
  "vuMode": true,
  "vuCompensateVolume": true,
  "channels": [
    { "pin": 3,  "label": "VU Left",  "sensorId": "/audio/0/level/0", "min": -40, "max": 0,   "minPwm": 4, "maxPwm": 249 },
    { "pin": 5,  "label": "VU Right", "sensorId": "/audio/0/level/1", "min": -40, "max": 0,   "minPwm": 2, "maxPwm": 251 },
    { "pin": 6,  "label": "Memory",   "sensorId": "/ram/load/0",      "min": 0,   "max": 100, "minPwm": 0, "maxPwm": 255 },
    { "pin": 9,  "label": "CPU Temp", "sensorId": "/amdcpu/0/temperature/2",     "min": 30, "max": 90, "minPwm": 0, "maxPwm": 255 },
    { "pin": 10, "label": "GPU Temp", "sensorId": "/gpu-nvidia/0/temperature/0", "min": 30, "max": 90, "minPwm": 0, "maxPwm": 255 }
  ],
  "stashedChannels": [
    { "channel": 0, "label": "CPU Load", "sensorId": "/amdcpu/0/load/0",     "min": 0, "max": 100 },
    { "channel": 1, "label": "GPU Load", "sensorId": "/gpu-nvidia/0/load/0", "min": 0, "max": 100 }
  ]
}
```

| Pole | Význam |
| --- | --- |
| `vuMode` | Ktorá strana je práve živá. Riadi aj rýchlosť slučky. |
| `vuCompensateVolume` | Odpočítať útlm hlasitosti Windows z meranej úrovne |
| `stashedChannels` | Signálová polovica profilu, ktorý práve nie je v hre |

### Dva profily na kanál, prepínač ich vymieňa

Kanál 0 potrebuje v VU režime iný senzor, iný rozsah **aj** iný názov, ale
kalibráciu meradla (`minPwm` / `maxPwm`) si musí ponechať — tá patrí fyzickej
ručičke, nie signálu. Preto `stashedChannels` obsahuje len štyri polia plus kľúč:
piny a kalibračné body sa prepnutím nikdy nemenia, takže v odkladacom mieste
nemajú čo robiť. Úzky tvar odkladaného profilu je vlastnosť, nie opomenutie: tvar
sám hovorí, čo sa vymieňa.

Kľúčom je **`channel`, index kanála**, nie `pin`. `Pin` je v `ChannelConfig`
zadokumentovaný ako informatívny („the frame is positional") a na PC strane od
neho nezávisí nič; kľúčovať ním obnovu profilu by povýšilo informatívne pole na
nosné, kde preklep rozbije prepínač. Index je naopak identita, ktorú systém už
používa všade: pozícia v rámci, poradie v `channels`, `ChannelReading.Index`,
indexovanie test režimu. Obnova je preto priame indexovanie, nikdy nie hľadanie,
a nemôže byť dvojznačná.

`VuModeSwitch` je jedna symetrická výmena:

- **Prvé zapnutie** (odkladacie miesto prázdne): do stashu ide CPU/GPU Load, do
  kanálov 0 a 1 idú VU defaulty (`VU Left` / `VU Right`, `-40`–`0` dBFS).
- **Vypnutie**: výmena. Kanály dostanú CPU/GPU Load späť, stash si vezme VU
  profily *vrátane* toho, čo na nich užívateľ doladil.
- **Každé ďalšie prepnutie**: tá istá výmena. Obe strany si nesú svoje nastavenia.

### Pokazený stash nesmie stáť kalibráciu

`ConfigStore.IsValid` dnes zhodí celý config na defaulty, keď nemá presne päť
kanálov. Pri kanáloch je to správne, ale odkladacie miesto je vedľajšia vec.
Ak `stashedChannels` po ručnej editácii nemá presne 0 alebo 2 položky, alebo
`channel` chýba, je duplicitný či mimo rozsahu, berie sa **celý stash ako
prázdny** a dopočíta sa z defaultov. Zvyšok configu, hlavne dvakrát päť
kalibračných bodov, prežije nedotknutý.

Ak je `vuMode: true` a stash je prázdny, kanály 0 a 1 už držia VU profily
a odkladacie miesto sa stratilo. Vypnutie ich vráti na `AppConfig.CreateDefault()`
profily bez `sensorId`, ktoré `SensorDefaults` pri ďalšom spustení doplní samo.

### SensorKind

`SensorKind` dostane člena `Audio`. Pravidlá v `SensorDefaults` matchujú len
`Load` a `Temperature`, takže samotné pridanie enum člena stačí na to, aby
automatika pri prvom spustení nikdy nepriradila audio senzor na CPU kanál.

`SensorDescriptor` pre audio: `Hardware` je skutočný názov výstupného zariadenia
(napr. `Realtek Audio`), takže priamo v dropdowne je vidieť, čo sa meria. `Id`
zostáva stabilné (`/audio/0/level/0`) bez ohľadu na zariadenie, takže prehodenie
slúchadiel nerozbije config.

## Slučka a jej rýchlosť

`TrayApplicationContext` prepne `_timer.Interval` na `40` pri zapnutom VU režime
a na `1000` pri vypnutom. Zrýchlenie odhalí tri veci, ktoré 1 Hz ticho držalo.

### Reconnect musí byť časový, nie tickový

`SerialMeterLink.ReconnectEveryTicks = 5` počíta ticky a komentár pri ňom to
priznáva: „At 1 Hz this is the 5 s from the spec." Pri 25 Hz by z piatich sekúnd
bolo 200 ms — pri odpojenom Arduine dvadsaťkrát častejšie otváranie portu, každý
pokus s čítaním bannera a 500 ms timeoutmi.

Konštanta sa mení na `ReconnectInterval = 5 s` merané cez `TimeProvider` z .NET 8
(`TimeProvider.System` v produkcii, päťriadkový fake v testoch — žiadny nový
NuGet). Zámer bol vždy takýto, len ho nebolo treba vysloviť. Test
`Send_RetriesTheConnectionOnlyEveryFifthTick` sa prepíše na posun fake hodín.

### LibreHardwareMonitor zostáva na 1 Hz

`ThrottledSensorSource` obalí celý `CompositeSensorSource` a pustí `Refresh()`
najviac raz za sekundu; `Read()` a `Discover()` prechádzajú nedotknuté. Teploty
a záťaž sa teda naďalej merajú raz za sekundu, audio úroveň je živá pri každom
ticku (počíta ju capture vlákno, nie `Refresh`), a `MonitorService` o ničom z toho
nevie. Zdravotná kontrola audio zariadenia sa vezie na tom istom 1 Hz `Refresh()`,
takže prehodenie zo slúchadiel na reproduktory sa zachytí do sekundy bez COM
notifikačného klienta.

### UI sa nesmie prekresľovať 25x za sekundu

`SettingsForm.OnUpdated` sa obmedzí na ~5 Hz (200 ms). Číslo v dB sa hýbe plynulo
dosť na to, aby bolo vidieť, čo sa meria, a dropdowny so slidermi nemajú dôvod
prekresľovať sa rýchlejšie. Tray ikona a tooltip sa priraďujú len pri zmene.

### Vedome nezmenené

`System.Windows.Forms.Timer` má granularitu ~15,6 ms, takže interval 40 ms
fyzicky vychádza na ~47 ms, teda ~21 Hz namiesto 25. Pri 300 ms integrácii
a ručičke s mechanickou zotrvačnosťou to nie je vidieť, a čokoľvek presnejšie
(multimediálny timer, vlastné vlákno) by zabilo vlastnosť, na ktorej stojí celá
jednoduchosť tejto aplikácie: *timer beží na UI vlákne, takže sa nikde nič
nemarshalluje.*

`SerialPort.WriteTimeout` je 1000 ms a zápis ide z UI vlákna. Zablokovaný zápis
môže zamrznúť okno na sekundu — to je pravda už dnes pri 1 Hz, pri 25 Hz je len
25x viac príležitostí. Arduino svoj buffer stíha vyprázdňovať (22 bajtov na rámec,
550 B/s na 11,5 kB/s linke), takže to zostáva nezmenené. Keby to raz začalo
štípať, riešením je odosielacie vlákno, a to je iná téma než VU meter.

## Okno nastavení a tray

- **Tray menu**: zaškrtávateľná položka `VU meter` nad `Settings…`. Jeden klik
  počas hudby, bez otvárania okna.
- **Okno nastavení**: zaškrtávatko `VU meter mode` a pod ním
  `Compensate Windows volume`.
- **Riadky kanálov 0 a 1**: nemenia sa vôbec. Dropdown ukáže
  `Realtek Audio · Level L`, jednotka `dBFS` príde zo `SensorDescriptor.Unit`,
  takže stĺpec Value píše `-14.2 dBFS` a kalibračný riadok
  `-14.2 dBFS -> 64.5 % -> PWM 165`. Test režim aj kalibrácia posuvníkmi fungujú
  zadarmo.
- Prepnutie z tray menu pri otvorenom okne nastavení si vyžiada jeho obnovu, inak
  by v ňom zostali staré riadky.

Nič sa nezamyká: audio senzor sa dá priradiť ktorémukoľvek kanálu. Aplikácia je
permisívna už dnes (obrátené hranice sú povolená vlastnosť, nie chyba).

## Chybové stavy

Žiadny z nich nesmie zhodiť aplikáciu, a žiadny sa nesmie zakryť automatikou.

| Situácia | Správanie |
| --- | --- |
| Žiadne výstupné zariadenie | `Read` vráti `null` → kanály 0 a 1 na nulu, červené riadky, jedna riadka v logu. Kontrola na 1 Hz skúša znova. Tray odznak sa **nemení**: ten podľa README znamená „port sa nedá otvoriť" a tento význam si ponecháva. |
| Endpoint zabraný v exclusive mode | To isté. Po uvoľnení sa capture obnoví sám. |
| Neznámy mix format | To isté, s formátom v logu. Radšej ohlásená porucha než odvysielané smeti. |
| Hudba prestala hrať | Buffery prestanú prichádzať; po 150 ms sa dopočíta pád a ručičky plynulo dobehnú na nulu. Nie je to porucha. |
| Prehodené výstupné zariadenie | Zdravotná kontrola to zistí do sekundy a spustí Stop → Dispose → Start. |
| Mute alebo hlasitosť na nule | Podlaha stupnice, nie delenie nulou. |
| `stashedChannels` poškodené ručnou editáciou | Berie sa ako prázdne a dopĺňa sa z defaultov. Kalibrácia meradiel zostáva. |

## Pamäť a disciplína pri audiu

Pri WASAPI sa dá tečúca pamäť vyrobiť šiestimi rôznymi spôsobmi. Každý z nich má
v návrhu opatrenie.

1. **Neodhlásené handlery.** `DataAvailable` a `RecordingStopped` sa musia odhlásiť
   *pred* zahodením capture-u. Inak každé prepnutie VU pridá ďalší handler a ten
   istý buffer sa spracuje N-krát — nie je to len leak, ručička začne po desiatom
   prepnutí ukazovať nezmysly.
2. **`StopRecording()` je v NAudio asynchrónne.** `RecordingStopped` príde neskôr
   na capture vlákne. Zahodiť COM objekt hneď po `StopRecording()` znamená race
   a uniknutý `IAudioClient`. Správne poradie: Stop → počkať na `RecordingStopped`
   s timeoutom → Dispose.
3. **Alokácia v `DataAvailable`.** `WaveInEventArgs.Buffer` si NAudio recykluje.
   Kopírovať ho do nového poľa pri každom callbacku je 25–100 alokácií po
   desiatkach kB za sekundu — GC churn, ktorý sa v Task Manageri tvári presne ako
   leak. Buffer sa číta na mieste cez `MemoryMarshal.Cast<byte, float>`, nulová
   alokácia. Stav integrátora je O(1), nič sa nikam nehromadí.
4. **COM objekty zo zdravotnej kontroly.** `GetDefaultAudioEndpoint()` vracia nový
   `MMDevice` pri každom volaní. Kontrola raz za sekundu porovná `ID` s aktuálnym
   a objekt hneď zlikviduje; `MMDeviceEnumerator` aj aktívny `MMDevice` sa držia
   jeden, nie nový každú sekundu.
5. **Hlasitosť cez notifikáciu, nie pollingom.** `OnVolumeNotification` sa prihlási
   raz a pri Dispose sa odhlási — je to ten istý druh leaku ako bod 1.
6. **Reštart pri zmene zariadenia.** Prehodenie slúchadiel spustí ten istý
   Stop → Dispose → Start cyklus ako prepínanie VU, takže ho kryje ten istý test.

## Testovanie

Testy rozšíria existujúci `AnalogHwMonitor.Tests`. Fake capture ide do
`Tests/Fakes/` k `FakeSensorSource` a spol., fake `TimeProvider` tiež.
**Žiadny z testov nepotrebuje zvukový hardvér.**

- `VuIntegrator` — nábeh na 99 % za 300 ms, symetrický pád, nezávislosť výsledku
  od veľkosti bufferu, deinterleaving L/R, mono a 5.1 vstup.
- `AudioLevelSensorSource` — sínusovka na plnú stupnicu číta 0 dBFS (kalibrácia
  `pi/2`), ticho číta podlahu `-100`, klampovanie, pád pri tichu podľa uplynutého
  času, `null` pri nespustiteľnom capture, `null` pri neznámom formáte.
- Kompenzácia hlasitosti — pripočítanie útlmu, mute → podlaha, strop +40 dB,
  vypnutá kompenzácia nemení nič.
- Životný cyklus — `Discover()` nespustí capture, prvé `Read()` ho spustí, päť
  sekúnd bez čítania ho zastaví, sto cyklov Start/Stop nenechá prihlásený ani
  jeden handler a počet Dispose sa rovná počtu Start.
- `ThrottledSensorSource` — `Refresh()` prejde raz za sekundu aj pri 25 volaniach,
  prvé volanie prejde vždy, `Read()` a `Discover()` prechádzajú vždy.
- `VuModeSwitch` — prvé zapnutie, vypnutie, opakované prepínanie so zachovaním
  doladených rozsahov, nedotknuté `minPwm`/`maxPwm`, pokazený a prázdny stash.
- `SerialMeterLink` — reconnect po 5 sekundách merané fake hodinami, nezávisle od
  počtu volaní `Send`.

Netestuje sa automaticky WinForms, reálny sériový port ani `WasapiLoopbackAdapter`.
Ten sa overuje ručne: pustiť hudbu, sledovať stĺpec Value a ručičky, prehodiť
výstupné zariadenie, stíšiť na nulu, mute, zastaviť prehrávanie.

## Zmeny v existujúcom kóde

| Súbor | Zmena |
| --- | --- |
| `AppConfig.cs` | `VuMode`, `VuCompensateVolume`, `StashedChannels` |
| `SensorDescriptor.cs` | `SensorKind.Audio` |
| `ConfigStore.cs` | Sanitizácia `stashedChannels` bez zhodenia zvyšku configu |
| `SerialMeterLink.cs` | Tickový reconnect → časový cez `TimeProvider` |
| `Program.cs` | Vloženie `AudioLevelSensorSource` do kompozície a obalenie `ThrottledSensorSource` |
| `TrayApplicationContext.cs` | Položka `VU meter` v menu, prepínanie intervalu, priradenie ikony len pri zmene |
| `SettingsForm.cs` | Dve zaškrtávatka, obmedzenie `OnUpdated` na 5 Hz, obnova pri prepnutí z trayu |
| `SerialMeterLinkTests.cs` | Prepis reconnect testu na fake hodiny |

Nemení sa: `MonitorService`, `ChannelPipeline`, `ChannelMapper`,
`MeterCalibration`, `FrameCodec`, `ChannelReading`, `ChannelRowControl`,
`CompositeSensorSource`, `SensorDefaults`, sketch Arduina.

## Vedome vynechané (YAGNI)

Žiadny peak-hold, žiadny výber konkrétneho zariadenia (vždy default render),
žiadna prepínateľná balistika (PPM), žiadna analýza spektra, žiadne per-aplikačné
metrovanie, žiadny VU na ostatných troch kanáloch ako prednastavená možnosť.

Najpravdepodobnejším budúcim doplnkom je prepínateľná balistika PPM — sú to dve
časové konštanty vo `VuIntegrator` a jedno pole v configu, bez zásahu do
ostatných modulov.
