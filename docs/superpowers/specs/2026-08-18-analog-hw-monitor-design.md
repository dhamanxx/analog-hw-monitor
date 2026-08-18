# Analógový PC hardware monitor — návrh

Dátum: 2026-08-18
Stav: schválený návrh, pripravený na plán implementácie

## Cieľ

Päť ručičkových DC voltmetrov 0–5 V zobrazuje v reálnom čase záťaž a teploty PC.
Údaje číta .NET aplikácia cez LibreHardwareMonitorLib, posiela ich po sériovom
porte do Arduina UNO a to ich prevádza na PWM výstupy budiace meradlá.

Merané veličiny a priradenie pinov:

| Kanál | Veličina | Pin Arduina |
| --- | --- | --- |
| 0 | CPU záťaž | 3 |
| 1 | GPU záťaž | 5 |
| 2 | Zaplnenie RAM | 6 |
| 3 | CPU teplota | 9 |
| 4 | GPU teplota | 10 |

Poradie kanálov je pevné a zhoduje sa s poradím hodnôt v sériovom rámci aj
s poradím položiek v `config.json`.

## Architektúra

```text
LibreHardwareMonitorLib
        |  (senzory)
        v
AnalogHwMonitor.Core          <- mapovanie, kalibrácia, slučka, config
        |  (rámec "V:a,b,c,d,e")
        v
sériový port (115200 baud)
        |
        v
Arduino UNO (sketch)          <- parsovanie, analogWrite, watchdog
        |  (PWM)
        v
5x DC voltmeter 0-5 V
```

### Zásadné rozhodnutia

1. **Všetka logika je na PC.** Arduino je hlúpy ovládač, ktorý dostáva hotové
   PWM hodnoty 0–255. Rozsahy, kalibrácia aj výber senzorov sa menia v konfigurácii
   bez reflashovania sketchu.
2. **Aplikácia beží ako tray appka s oknom nastavení.** Kalibrácia piatich ručičiek
   posuvníkmi naživo je hlavný dôvod pre GUI.
3. **Vzorkovanie 1× za sekundu, bez softvérového vyhladzovania.** Ručičkové meradlo
   má vlastnú mechanickú zotrvačnosť. Ak by pohyb pôsobil nervne, vyhladzovanie
   sa dá pridať do `ChannelMapper` bez zásahu inde.
4. **Aplikácia vyžaduje admin práva.** LibreHardwareMonitorLib načítava ring0
   ovládač; bez elevácie chýba väčšina teplotných senzorov. Rieši `app.manifest`
   s `requireAdministrator`.

## Moduly

### AnalogHwMonitor.Core (class library, net8.0-windows)

Bez akejkoľvek závislosti na GUI.

| Typ | Zodpovednosť | Závislosti |
| --- | --- | --- |
| `ISensorSource` / `LibreHardwareSensorSource` | `Discover()` vráti zoznam dostupných senzorov (id, názov, typ, jednotka); `Read(id)` vráti `float?` | LibreHardwareMonitorLib |
| `ChannelMapper` | Hodnota senzora → percento výchylky (0–100) podľa `Min`/`Max` kanála, s orezaním | žiadne, čistá funkcia |
| `MeterCalibration` | Percento → PWM 0–255 podľa `MinPwm`/`MaxPwm` meradla | žiadne, čistá funkcia |
| `IMeterLink` / `SerialMeterLink` | Otvorenie portu, overenie zariadenia, posielanie rámcov, reconnect | `System.IO.Ports` |
| `MonitorService` | Slučka 1 Hz: prečítaj 5 kanálov → mapuj → kalibruj → pošli rámec; vystavuje event so živými hodnotami | len rozhrania vyššie |
| `AppConfig` | POCO konfigurácie + načítanie/uloženie `config.json` | `System.Text.Json` |

`MonitorService` nepozná LibreHardwareMonitor ani sériový port — len dve rozhrania.
To je dôvod, prečo je jadro testovateľné bez hardvéru a prečo GUI nepotrebuje
vidieť do jeho vnútra.

### AnalogHwMonitor.App (WinForms, net8.0-windows)

Tray ikona, okno nastavení, `app.manifest` s `requireAdministrator`. Drží inštanciu
`MonitorService` a iba prekresľuje hodnoty z jeho eventu. Žiadna doménová logika.

### AnalogHwMonitor.Tests (xUnit)

Testuje `Core` proti fake implementáciám `ISensorSource` a `IMeterLink`.

### arduino/analog_hw_monitor/analog_hw_monitor.ino

Jeden súbor, rádovo 80 riadkov.

## Sériový protokol

Rýchlosť 115200 baud, ASCII, ukončenie riadku znakom LF.

### PC → Arduino, 1× za sekundu

```text
V:128,200,64,30,255
```

Päť celých čísel 0–255 v poradí kanálov podľa tabuľky vyššie. Textový formát je
zvolený zámerne: pri ladení stačí Serial Monitor v Arduino IDE. Pri piatich
číslach za sekundu je réžia zanedbateľná.

### Arduino → PC

Pri štarte vypíše `AHM1`. Slúži na overenie, že na porte je naozaj monitor,
a na funkciu tlačidla „Detekovať", ktoré prejde dostupné porty a nájde ten správny.
Inak Arduino nekomunikuje smerom k PC.

**Arduino UNO sa pri otvorení sériového portu resetuje.** Aplikácia preto po otvorení
portu čaká 2 sekundy na banner a až potom začne posielať rámce.

## Sketch Arduina

Tri zodpovednosti, nič viac:

1. **Parsovanie.** Číta znaky do bufferu po koniec riadku, rozdelí na päť čísel.
   Nevalidný riadok zahodí (nesprávny počet hodnôt, hodnota mimo 0–255,
   neočakávaný prefix).
2. **Výstup.** Pri platnom rámci `analogWrite()` na päť pinov a zapamätanie `millis()`.
3. **Watchdog.** Ak od posledného platného rámca ubehlo viac ako 3000 ms, zapíše na
   všetky piny 0. Mŕtve spojenie je tak na prvý pohľad odlíšiteľné od nečinného PC.

Kalibrácia ani rozsahy v sketchi nie sú.

### Hardvérové poznámky

- Piny 5 a 6 bežia na Timer0 (~980 Hz), piny 3, 9 a 10 na ~490 Hz. Pre ručičkové
  meradlo je frekvencia nepodstatná, ale prescaler Timer0 sa nesmie meniť —
  rozbilo by to `millis()`, a teda watchdog.
- Meradlo si PWM odtlmí zotrvačnosťou a spravidla ide pripojiť priamo. Ak by
  ručička viditeľne vibrovala, rieši to RC článok (napr. 1 kΩ + 10 µF) na výstupe.
  Ide o vec na doladenie pri stavaní, nie o predpoklad návrhu.

## Konfigurácia

Súbor `config.json` vedľa .exe (prenosná zložka, nie `%AppData%`). Pole `channels`
má presne päť položiek, ich poradie určuje kanál a pin.

```json
{
  "comPort": "COM3",
  "startWithWindows": true,
  "channels": [
    {
      "pin": 3,
      "label": "CPU Load",
      "sensorId": "/amdcpu/0/load/0",
      "min": 0,
      "max": 100,
      "minPwm": 0,
      "maxPwm": 255
    }
  ]
}
```

- `sensorId` — identifikátor senzora z LibreHardwareMonitoru.
- `min` / `max` — v jednotkách senzora (% alebo °C). Určujú, čo znamená nulová
  a plná výchylka.
- `minPwm` / `maxPwm` — dvojbodová kalibrácia konkrétneho meradla.
- `startWithWindows` — pri zapnutí sa zapíše hodnota do
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pri vypnutí sa odstráni.

Hraničné prípady konfigurácie sú definované takto:

- `min == max` → kanál posiela vždy 0 %; nedelí sa nulou.
- `min > max` → mapovanie je obrátené (vyššia hodnota senzora dá nižšiu výchylku).
  Je to legitímne nastavenie, nie chyba.
- `minPwm > maxPwm` → rovnako legitímne, obráti smer pohybu ručičky.
- Hodnoty PWM sa po výpočte orezávajú na 0–255 a zaokrúhľujú na najbližšie celé číslo.

Defaulty pri prvom spustení: záťaže a pamäť `0–100`, teploty `30–90`,
kalibrácia `0–255`. Senzory sa priradia auto-detekciou (CPU Total load,
CPU package temperature, GPU core load, GPU core temperature, Memory load);
používateľ ich môže kedykoľvek prepísať.

## Okno nastavení

Jedna obrazovka. Hore výber COM portu s tlačidlom „Detekovať". Pod tým tabuľka
piatich riadkov:

| Pin | Popis | Senzor (rozbaľovací zoznam) | Min | Max | Hodnota | PWM |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | CPU Load | CPU Total | 0 | 100 | 34 % | 87 |

Rozbaľovací zoznam obsahuje všetky senzory nájdené cez `Discover()`, takže kanál
sa dá prepnúť aj na úplne inú veličinu. Stĺpce *Hodnota* a *PWM* sú živé,
obnovujú sa 1× za sekundu z eventu `MonitorService` — naraz je teda vidieť,
čo sa číta aj čo sa posiela.

### Kalibračný režim

Prepínač „Test" pri riadku odpojí kanál od senzora a nahradí ho posuvníkom
posielajúcim surové PWM. Postup: ručička presne na nulu → „Ulož ako min",
ručička na doraz stupnice → „Ulož ako max". Kým je Test na danom kanáli aktívny,
normálne posielanie na tento kanál stojí; ostatné kanály bežia ďalej.

Rámec sa aj v testovacom režime posiela vždy celý, 1× za sekundu — kanály v Teste
nesú hodnotu posuvníka, ostatné hodnotu zo senzora. Arduino teda nepozná pojem
testovacieho režimu a jeho watchdog sa správa rovnako ako inak.

## Chybové stavy

Žiadny z nich nesmie zhodiť aplikáciu.

| Situácia | Správanie |
| --- | --- |
| Port sa nedá otvoriť alebo zmizol | Tray ikona dostane výstražný prekryv, tooltip uvedie dôvod, pokus o otvorenie sa opakuje každých 5 s. Arduino po 3 s samo stiahne ručičky na nulu. |
| `sensorId` už neexistuje (napr. vymenená GPU) | Kanál posiela 0 a v tabuľke svieti červeno; ostatné štyri bežia ďalej. |
| Zariadenie na porte nevráti banner `AHM1` | Port sa zavrie a považuje sa za nesprávny; opakuje sa ako pri neúspešnom otvorení. |
| `config.json` chýba alebo je poškodený | Premenuje sa na `config.json.bak`, vytvorí sa nový s defaultmi, udalosť sa zapíše do logu. |

`log.txt` vedľa .exe, jednoduchý append. Po prekročení 1 MB sa premenuje na
`log.old.txt` (prípadný predchádzajúci sa prepíše) a začne sa nový. Pri procese
bežiacom celý deň na pozadí je to jediný spôsob, ako spätne zistiť, čo sa stalo.

## Testovanie

Automatizované testy pokrývajú `Core` proti fake `ISensorSource` a fake `IMeterLink`,
teda bez PC senzorov aj bez Arduina:

- `ChannelMapper` — hodnota pod `min`, nad `max`, v strede rozsahu, degenerovaný
  prípad `min == max`.
- `MeterCalibration` — 0 % → `minPwm`, 100 % → `maxPwm`, zaokrúhľovanie, prevrátené
  hranice (`minPwm > maxPwm`).
- Formát rámca — presná podoba reťazca vrátane poradia kanálov.
- `AppConfig` — načítanie platného súboru, chýbajúceho súboru, poškodeného súboru
  (vrátane vzniku `.bak`).
- `MonitorService` — chýbajúci senzor dá 0, všetkých päť kanálov ide v správnom poradí.

Netestuje sa automaticky WinForms ani reálny sériový port. Sketch sa overuje ručne
cez Serial Monitor (rámce sa dajú napísať ručne), watchdog zavretím aplikácie.

## Vedome vynechané (YAGNI)

História a grafy, podpora viacerých zariadení naraz, generátor ciferníkov,
auto-update, nelineárne mapovacie krivky, softvérové vyhladzovanie.

Vyhladzovanie a nelineárna krivka sú najpravdepodobnejšie budúce doplnky. Obe sú
prírastkom v `ChannelMapper` a nevyžadujú zmenu žiadneho iného modulu.
