# VU balistika druhého rádu — throwaway merací build

**Dátum:** 2026-08-26
**Stav:** **postavené, zmerané, ZAMIETNUTÉ.** Vetva `throwaway/vu-second-order` bola
zmazaná. Výsledok a to, čo z neho platí ďalej, je na konci dokumentu — kto sa k tejto
téme vráti, nech si prečíta najprv ten záver.

## Načo to je

Popis, ktorý sme kontrolovali — „300 ms to 99 percent of a step, symmetric attack and
decay, 1 to 1.5 dB overshoot" — sedí na dnešný kód v prvých dvoch klauzulách a v tretej
nie. `VuIntegrator` je jednopólový filter, a ten presah spraviť nevie: je monotónny.
Slovo „overshoot" sa v repozitári nevyskytuje ani raz.

Otvorená otázka teda nie je *ako* presah dorobiť, ale **či ho na tomto hardvéri vôbec
vidno**. Fyzický merák má vlastnú mechanickú zotrvačnosť; ak je sám systémom druhého
rádu, výsledný reťazec je štvrtého rádu a presah sa buď zmaže, alebo zdvojí. Lacné
panelové meráky bývajú silne tlmené. Toto nerozhodne výpočet, len dve ručičky vedľa seba.

Build nemá nič dokázať. Má ukázať, aby sa dalo povedať „nemeníme nič".

## Usporiadanie

Obe ručičky dostanú ten istý mono signál `(L+R)/2`, takže rozdiel na cifernikoch je
čisto rozdiel balistiky a stupnice, nie obsahu kanálov. Po dobu experimentu nie je stereo.

```
                     ┌─ VuIntegrator τ=65 ms ─→ dBFS ─→ lineárne −40..0 ─→ %  ─→ pin 3 (L)
(L+R)/2 ─→ rectify ──┤
                     └─ detektor τ=15 ms ─→ výchylka ─→ ručička 2. rádu ─→ %  ─→ pin 5 (R)
```

Ľavá vetva je dnešný kód, nedotknutý. Pravá je nový reťazec.

## Konštanty

Všetky ako `const` na vrchu jedného súboru, aby sa ktorákoľvek dala prestaviť a
rebuildnúť za pol minúty.

| konštanta | hodnota | odkiaľ |
|---|---|---|
| `DetectorTauMs` | 15,0 | len usmernenie a potlačenie ripple; „elektrická" časť pred ručičkou |
| `Zeta` | 0,81 | `exp(−πζ/√(1−ζ²))` = 1,305 % presahu — v pásme normy 1–1,5 % |
| `OmegaN` | 13,4221 rad/s | 99 % skoku pri 300 ms; vrchol presahu pri 399 ms; `f_n` = 2,136 Hz |
| `ZeroVuRefDbfs` | −12,0 | úroveň, ktorá sadne na 0 VU |
| `ZeroVuDeflection` | 71,0 % | poloha 0 VU na oblúku, ako na skutočnom VU |

`Zeta` a `OmegaN` sú overené numericky, nie odvodené na papieri: analytická step response
pri `ζ = 0,81` prechádza 99 % pri `ωₙt = 4,0266`, čo pri 300 ms dáva `ωₙ = 13,4221`.

### Prečo `ZeroVuRefDbfs = −12`

Výchylka je úmerná napätiu, takže dvojica referencie a polohy 0 VU určuje celú stupnicu.
Pri `−12` vyjde doraz cifernika (100 %) na `−9,03 dBFS`, čo je `+3,0 VU` nad referenciou —
červená zóna skutočného meráka. `VuModeSwitch` si poznamenáva, že hlasný moderný materiál
integruje okolo −12 dBFS, čiže taký materiál sedí na 0 VU a hlasnejšie pasáže lížu doraz.

Nižšia referencia experiment kazí: pri `−18` pegne cifernik od −15 dBFS vyššie a ručička
visí na doraze celý čas. `−14` je druhá rozumná voľba, ak sa pri tichšom materiáli ukáže
výchylky primálo.

| dBFS | ref −12 | ref −14 | ref −18 | dnes |
|---:|---:|---:|---:|---:|
| −12 | 71,0 % | 89,4 % | doraz | 70,0 % |
| −18 | 35,6 % | 44,8 % | 71,0 % | 55,0 % |
| −30 | 8,9 % | 11,3 % | 17,8 % | 25,0 % |
| −40 | 2,8 % | 3,6 % | 5,6 % | 0,0 % |

## Detektor a mapovanie na výchylku

Detektor je jednopólový filter nad usmerneným signálom, rovnaký tvar ako dnešný
`VuIntegrator.Add`, len s `τ = 15 ms` namiesto 65 ms. Priemerovanie na 300 ms tu už
nerobí — to je odteraz úloha ručičky. Koeficient sa počíta zo vzorkovacej frekvencie
per-sample, aby výsledok nezávisel od veľkosti bloku, ktorý WASAPI podal.

```
level     = detektor.Level · (π/2)          // average-to-peak, plná sínusovka = 1,0
level    ·= 10^(min(−VolumeDb, 40)/20)      // kompenzácia hlasitosti, ako dnes
reference = 10^(ZeroVuRefDbfs/20)
target    = clamp(ZeroVuDeflection · level / reference, 0, 100)
```

Kompenzácia hlasitosti tu zostáva zapnutá zámerne: bez nej by referencia −12 dBFS
opisovala polohu posuvníka hlasitosti, nie úroveň materiálu, a cifernik by sa hýbal pri
každom stlačení klávesy hlasitosti.

Orezanie na 100 % je pred ručičkou, nie za ňou — ručička sa má rozbiehať k dorazu, nie
prenášať do modelu cieľ, ktorý je mimo oblúka.

## Model ručičky

`x'' + 2ζωₙx' + ωₙ²x = ωₙ²·target`, kde `x` je výchylka v percentách a `target` je to,
čo hovorí detektor.

Cieľová výchylka je počas ticku konštantná, čo je presne **zero-order hold**, a ten má pre
systém druhého rádu uzavreté riešenie. Cez chybu `e = x − target`:

```
σ  = ζ·ωₙ                    ω_d = ωₙ·√(1−ζ²)
c1 = exp(−σ·dt)·cos(ω_d·dt)  c2  = exp(−σ·dt)·sin(ω_d·dt)

e₁ = c1·e₀ + c2·(v₀ + σ·e₀)/ω_d
v₁ = c1·v₀ − c2·(ωₙ²·e₀ + σ·v₀)/ω_d
x₁ = target + e₁
```

Žiadny substepping. Uzavretý tvar je exaktný pri ľubovoľnom `dt` — chyba proti analytickej
step response je 2·10⁻¹⁶ aj pri kroku 1 s — a preto **imúnny voči jittru WinForms Timeru**:
pri simulovanom rozptyle −8/+16 ms okolo 40 ms vyšiel presah 1,303 % oproti analytickým
1,305 %.

Numerická integrácia bola zamietnutá po zmeraní, nie od oka. Semi-implicitný Euler po 1 ms
dáva 1,216 % presahu (o 7 % menej), po 5 ms 0,879 % (už pod pásmom normy) a pri holom
40 ms kroku presah zmizne úplne. Uzavretý tvar je aj lacnejší: tri transcendentné funkcie
za tick namiesto štyridsiatich substepov.

Krok sa robí o **nameraný uplynulý čas**, nie o menovitých 40 ms.

## Zapojenie do pipeline

Nový pseudo-senzor `/audio/0/needle` vracia rovno **výchylku v percentách**. Kanál 1
dostane natvrdo `Min = 0`, `Max = 100`, čím sa `ChannelMapper` stane priechodzím a nový
reťazec zostane celý vnútri audio zdroja.

Nedotknuté: `ChannelPipeline`, `ChannelMapper`, `MeterCalibration`, `FrameCodec`, sketch,
a `MinPwm`/`MaxPwm` z konfigurácie — to je kalibrácia fyzického meráka, ktorá s balistikou
nemá čo robiť.

Prepis konfigurácie oboch VU kanálov sa robí pri štarte nad **klonom** načítanej
konfigurácie, takže sa neukladá a throwaway build nemá ako poškodiť `config.json` —
nastavovacie okno a `ConfigStore` držia ďalej originál. Pri štarte sa do logu zapíše, že ide o merací build.

## Čo sa uvidí skôr než presah

Nová stupnica posadí ručičku úplne inde a vizuálne to bude dominovať. Ak sa nový cifernik
nepáči, presah je vedľajší a odpoveď na „mení sa vôbec niečo" je nie — to je legitímny
a užitočný výsledok experimentu, nie jeho zlyhanie.

## Návrat

`git checkout main` a rebuild. `main` sa nedotýka.


---

# Výsledok

Build sa postavil celý (päť taskov, šesť commitov), prešiel 205 testami a bežal na
skutočnom hardvéri s oboma meráčmi vedľa seba. Verdikt majiteľa po pozeraní na hudbu:

> „ľavý VU meter je lepší"

Ľavý je dnešný kód: jednopólový filter s τ = 65 ms a lineárna dB stupnica −40…0.
Vetva bola zmazaná, `main` sa nikdy nedotkla.

## Čo sa teda nemení

- **Presah 1–1,5 % sa nedorába.** Buď ho mechanika meráka utlmí, alebo je príliš jemný
  na to, aby na cifernku niečo pridal. Toto nerozhodol výpočet a ani nemohol — presne
  preto sa ten build staval.
- **Výchylková stupnica sa nezavádza.** Fyzikálne vernejšia stupnica (výchylka úmerná
  napätiu, 0 VU na 71 % oblúka) čítala na tomto cifernku horšie než lineárna v dB.
- `VuIntegrator`, `ChannelMapper` a rozsah −40…0 dBFS zostávajú ako sú.

## Čo z toho platí ďalej

**Popis, ktorý celú analýzu spustil, je v tretej klauzule nepravdivý.** Znel „300 ms to
99 percent of a step, symmetric attack and decay, 1 to 1.5 dB overshoot". Prvé dve
klauzuly na dnešný kód sedia presne. Tretia je zle dvakrát:

1. ANSI C16.5 / IEC 60268-17 definuje presah **1–1,5 % výchylky**, nie decibelu. V dB
   priestore to číslo nemá význam bez toho, aby sa povedalo, na akej úrovni skok nastal.
2. `VuIntegrator` **žiadny presah nerobí a robiť nemôže** — jednopólový filter je
   monotónny. Slovo „overshoot" sa v repozitári nevyskytuje ani raz, a po tomto
   experimente sa vyskytovať ani nemá.

Ak sa niekde v poznámkach alebo dokumentácii tá veta o presahu ešte objaví, patrí
opraviť alebo zmazať.

## Dva fakty o codebase, ktoré experiment odhalil

Oba sa našli až pri implementácii, oba platia bez ohľadu na VU balistiku, a oba boli
overené v zdroji.

**Kalibrácia `π/2` predpokladá sínusovku, takže jednosmerný signál číta +3,92 dBFS.**
Detektor je jednopólový filter nad `|x|`, čiže konverguje k strednej hodnote `|x|`.
Pre konštantný signál 1,0 je to 1,0; po škálovaní `AverageToPeak = π/2` vyjde 1,5708 a
`20·log10(1,5708) = +3,922 dBFS`. Pre plnú sínusovku je stredná hodnota `2/π`, po
škálovaní presne 1,0, teda 0 dBFS. **Test, ktorý postaví DC buffer a čaká 0 dBFS, je
nesplniteľný** — táto chyba bola v pláne a implementer ju správne odmietol prepísať.

**`config.json` sa ukladá cez `monitor.Config`, nie cez samostatnú referenciu.**
`TrayApplicationContext` originálny `AppConfig` vôbec nedostane — `Program.cs` mu podáva
len `monitor` — a všade číta `_monitor.Config`. Ukladajú dve cesty:
`TrayApplicationContext.cs:121` pri prepnutí VU mode a `SettingsForm.cs:425` pri Save.
Dôsledok pre kohokoľvek, kto by opäť skúšal podstrčiť `MonitorService` upravenú
konfiguráciu: **klon `AppConfig` pred uložením nechráni.** Uloží sa ten klon. Ak to
niekedy bude treba, správne riešenie je dať `TrayApplicationContext` iný `ConfigStore`
s vlastným súborom, nie klonovať konfiguráciu.

## Čo si nechať, ak sa téma otvorí znova

Odvodenie konštánt vyššie je overené numericky a netreba ho robiť znova: `ζ = 0,81` dáva
presah 1,305 %, `ωₙ = 13,4221 rad/s` posadí 99 % na 300 ms a vrchol na 399 ms. Uzavretý
tvar zero-order hold je exaktný pri ľubovoľnom kroku (chyba 2·10⁻¹⁶ aj pri 1 s) a je
jediný použiteľný spôsob, ako ten presah pri 40 ms ticku vôbec uvidieť — semi-implicitný
Euler po 1 ms ho okreše na 1,216 %, po 5 ms na 0,879 % a pri holom 40 ms kroku zmizne
úplne. Tabuľka polôh cifernika pre referencie −12 / −14 / −18 dBFS je vyššie.
