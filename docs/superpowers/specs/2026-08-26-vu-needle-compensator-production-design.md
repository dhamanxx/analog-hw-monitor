# Kompenzátor balistiky ručičky — poriadna implementácia

**Dátum:** 2026-08-26
**Stav:** navrhnuté, nepostavené.
**Predchodca:** [`2026-08-26-vu-needle-compensator-throwaway-design.md`](2026-08-26-vu-needle-compensator-throwaway-design.md)
— merací build, ktorý túto vec rozhodol. Kto sa k téme vracia, nech si prečíta najprv jeho
záver; tento dokument ho nezopakuje.

## Načo to je

Merací build postavil inverzný biquad na pravú VU ručičku, dnešný kód nechal na ľavej a
obe kŕmil tým istým mono signálom. Verdikt majiteľa po dvoch reláciách, druhej so
zhodnými detektormi, kde bol biquad jediným rozdielom:

> „overené je to podstatne lepšie ako ľavý meter"
> „ručička je menej rozlietaná a nekmitá ako besná"

Toto je prenesenie toho výsledku do produkcie. **Nie merge tej vetvy** — je to throwaway,
ktorý presmerúva konfiguráciu, kŕmi oba kanály mono a nesie kód, ktorý tu netreba.

Čo sa vyhralo, v číslach: presah 22,6 % → 0,9 %, náraz do dorazu −16,3 % → −0,4 %, a —
to najdôležitejšie — zrušená mechanická rezonancia ručičky na **1,57 Hz, čo je 94 BPM**,
teda 4 až 5 dB menej rozkmitu v pásme, kde hudba bije.

## Rozsah

Jeden spec, jedna vec: **kompenzátor do produkcie na obe VU ručičky.**

Vedome mimo: jitter timeru a s ním presná ZOH diskretizácia — samostatná téma, viď
[Spojka na druhý spec](#spojka-na-druhý-spec) na konci.

## Kam kompenzátor patrí

```
sensor dBFS ─→ ChannelMapper ─→ % ────┬──→ NeedleCompensator ─→ % ─→ MeterCalibration ─→ PWM ─→ rámec
                                       │      (stav na kanál)
                                       └──→ ChannelReading (%, PWM) ─→ okno nastavení
```

`ChannelPipeline.Evaluate` ostáva **nedotknutý**. Jeho vlastný komentár hovorí, že tick a
okno nastavení sa o význam hodnoty nesmú rozísť, a to platí ďalej: je to naďalej jediná
definícia toho, čo daná hodnota senzora znamená. Kompenzátor **doň nepatrí** — okno
nastavení ho volá pri každom obnovení a posúvalo by tým stav filtra, čo je vedľajší účinok
na ceste, ktorá má byť čistá.

Kompenzátor pribudne v `MonitorService.Tick` ako krok navyše, medzi percentom a
`MeterCalibration.ToPwm`.

### Prečo `ChannelReading` nesie nekompenzované percento

Lebo je to jediná správna voľba, nie kompromis. Jednosmerný zisk kompenzátora je presne 1,
takže **v ustálenom stave sú obe hodnoty zhodné** — stĺpec v okne nastavení je presný
vždy, keď ručička stojí. Líšia sa len počas prechodu, kde je číslo v tabuľke nečitateľné
a kalibrácia sa podľa neho nerobí.

Kalibračný posuvník sa nemení: `SetTestPwm` obchádza `ChannelPipeline` už dnes a obíde aj
kompenzátor. Surové PWM ostane surové.

### Prečo iba VU kanály a iba vo VU režime

Nie preto, že by sa inde nedalo, ale preto, že **inde nie je čo kompenzovať**. Teplota má
tepelnú zotrvačnosť a nevyskočí na 90 °C za milisekundy; zaplnenie RAM tiež nie. Kmitanie
ručičky je odozva na skok a tie tri kanály skoky nerobia.

Jediná výnimka je CPU/GPU load, ktoré skokovo ísť vedia — tie ale sedia na kanáloch 0–1
len vtedy, keď je VU režim **vypnutý**, a vtedy beží tick 1000 ms. Proti perióde prstenca
578 ms je to hlboko pod Nyquistom: kompenzátor by tam nefungoval, len škodil.

## Čo z meracieho buildu prežije

Poriadna implementácia je **menšia** než merací build. Kontrolná relácia rozhodla, že
detektor ostáva na dnešných 65,14 ms, čím odpadá všetko, čo sa stavalo kvôli 15 ms vetve:

| z throwaway | v produkcii |
|---|---|
| `NeedleCompensator` (biquad) | **ostáva**, bez `DetectorTauMs` |
| `VuIntegrator(double tau)` | **zahodiť** — detektor je zase len `new VuIntegrator()` |
| `VuIntegrator.AddMono` | **zahodiť** — stereo sa vracia |
| pseudo-senzor `/audio/0/needle` | **zahodiť** — senzory ďalej hlásia dBFS |
| presmerovanie `config.json` | **zahodiť** — bola to ochrana experimentu |
| `LevelToDbfs` (vytiahnutá spoločná metóda) | vec vkusu — viď nižšie |

Ostáva teda jedna trieda a jeden háčik v `MonitorService.Tick`.

`LevelToDbfs` bola v meracom builde vytiahnutá preto, že ju potrebovali dve cesty. Keď
pseudo-senzor zanikne, ostane jej jediný volajúci, takže jej ponechanie **nie je zlepšenie,
je to vec vkusu** — drží pravidlo o podlahe pred kompenzáciou hlasitosti na jednom mieste
a s komentárom, ktorý ho vysvetľuje. Implementer nech sa rozhodne; obe voľby sú v poriadku,
len nech to nezdôvodňuje odstránením duplicity, ktorá už neexistuje.

## Kompenzátor

Prenos je podiel želanej a skutočnej balistiky — menovateľ je cieľ, čitateľ je nameraný
plant, ktorý sa tým vyruší a nahradí:

```
        ωt² · (s² + 2ζp·ωp·s + ωp²)
C(s) = ─────────────────────────────
        ωp² · (s² + 2ζt·ωt·s + ωt²)
```

Jednosmerný zisk presne 1, vysokofrekvenčný `ωt²/ωp²` = 1,2916 — konečný, takže sa nič
nederivuje. Diskretizácia bilineárnou transformáciou nad **nameraným uplynulým časom**
medzi tickmi, orezaným na ⟨5, 200 ms⟩. Odvodenie aj diskrétne koeficienty sú v dokumente
meracieho buildu a netreba ich robiť znova.

| konštanta | hodnota |
|---|---|
| `PlantZeta` | 0,391 |
| `PlantOmegaN` | 11,81 rad/s |
| `TargetZeta` | 0,81 |
| `TargetOmegaN` | 13,4221 rad/s |

### Konštanty ostávajú natvrdo v kóde

Nie v konfigurácii, a to je vedomé rozhodnutie. Sú namerané z videa pri 240 fps a
fitovacieho skriptu, nie odklikané v okne; konfigurácia by sľubovala nastaviteľnosť, ktorá
nie je skutočná. Oba meráky sa zhodujú do 0,8 % v ζ a 1,5 % v ωₙ, takže jedna sada pokrýva
obe.

**Postup, ak sa merák vymení** — aby sa nemuselo odvodzovať znova:

1. Natočiť skok povelu 0 → 70 % pri 240 fps, kamerou kolmo na ciferník, aspoň 1,3 s
   záznamu. Skok robiť jedným povelom, nie hudbou.
2. Vytrackovať uhol ručičky do CSV so stĺpcami `time_ms` a percento výchylky. Overiť, že
   percento je **lineárnou funkciou uhla** — mimoosová kamera to skresľuje a skreslenie
   sa prenesie do ζ.
3. Prefitovať päťparametrový model druhého rádu (`t0`, `y0`, `yf`, ζ, ωₙ). RMSE má byť
   rádovo na úrovni šumu trackera; ak je päťnásobne vyššia, záznam je zlý, nie merák.
4. Skok 70 → 0 sa na to **nehodí** — ručička bije do dorazu na nule a tracker ju tam
   stráca.

### Orezanie na 0–100 % a jeho cena

Ostáva. Pri skoku na plnú výchylku chce kompenzátor až +29 % nad rozsah a nedostane ich,
takže tam je kompenzácia oslabená. Rezervovať tú hlavu — mapovať napríklad 0 dBFS na 90 %
ciferníka — sa **nenavrhuje**: stálo by to pätinu stupnice natrvalo kvôli prechodu, ktorý
trvá dva ticky.

## Stav a reset

Dve inštancie kompenzátora, po jednej na VU kanál, vlastní ich `MonitorService`.

Resetujú sa v dvoch prípadoch:

- **pri prepnutí VU režimu** — inak by po zapnutí pokračoval na histórii spred vypnutia;
- **kým senzor vracia `null`** — nie raz pri prechode do mŕtva, ale pri každom takom ticku,
  takže stav je čistý v okamihu, keď sa kanál vráti k životu. Reset je lacný a idempotentný,
  takže to nestojí za rozlišovanie hrany.

## Testy

Tri prežijú z meracieho buildu, lebo už raz niečo dokázali: vyrušenie plantu
(kompenzátor → simulovaná ručička → cieľová balistika), jednosmerný zisk presne 1,
a orezanie kroku s resetom.

**Nový test na rezonanciu.** Všetky doterajšie testy sú skokové a ani jeden nemeria to,
čo experiment naozaj vyhralo. Nový poženie kaskádu kompenzátor + simulovaný plant
**sínusovým povelom na 1,57 Hz** a overí, že amplitúda výchylky je proti nekompenzovanej
nižšia približne o 5 dB. Bez neho môže ktokoľvek preladiť `TargetZeta` pod `1/√2 = 0,707`,
prejsť všetkými ostatnými testami a ticho vrátiť rezonanciu.

**Testy na integráciu** — tu sú tie tiché spôsoby, ako to pokaziť:

- do rámca ide **tvarované** PWM, kým `ChannelReading` hlási **netvarované**;
- v ustálenom stave sú **zhodné** — to je zároveň dôkaz jednosmerného zisku na úrovni
  celej pipeline, nie len filtra;
- kalibračný posuvník (`SetTestPwm`) kompenzátor obchádza;
- mimo VU režimu sa kompenzátor neuplatní na žiadny kanál;
- reset nastane pri prepnutí režimu aj pri `null` zo senzora.

## Spojka na druhý spec

`TargetOmegaN = 13,4221` je hodnota vybraná **kvôli jittru WinForms Timeru, nie kvôli
fyzike**. Pri presnom timeri by `ωₙ = 16` posadilo t99 na 289 ms a presah na 1,0 %, čiže
by merák splnil **obe** kritériá normy; pri jittri −8/+16 ms sa to rozpadne na 2,0 %
medián a 5,9 % v najhoršom, kým 13,4221 drží 1,5 % a 3,2 %. Kritérium 300 ms je tu preto
vedome obetované.

Príčinou nie je timer, ale **bilineárna transformácia**, ktorá predpokladá konštantný krok.
Oba systémy — cieľ aj plant — sú druhého rádu a oba majú uzavretý tvar ZOH ekvivalentu,
takže `C(z) = H_cieľ,ZOH(z) / H_plant,ZOH(z)`, prepočítané na skutočný `dt`, dá kaskádu
rovnú cieľu **presne, v okamihoch vzorkovania, pri ľubovoľnom kroku**. Je to ten istý trik,
ktorý minulému throwaway spravil simulátor ručičky imúnny voči jittru s chybou 2·10⁻¹⁶.

Keď to druhý spec urobí, voľba 13,4221 prestane platiť a `ωₙ = 16` sa otvorí. **Nech to
nájde zapísané namiesto toho, aby na to prišiel znova.**

Otvorené, čo bude musieť rozhodnúť: či presná diskretizácia stačí, alebo treba aj tesnejší
timer; a či sa oplatí merať skutočný rozptyl intervalov ticku — dnešných −8/+16 ms je
odhad z dokumentu, nie meranie.
