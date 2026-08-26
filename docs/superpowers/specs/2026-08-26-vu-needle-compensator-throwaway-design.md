# Kompenzátor balistiky ručičky — throwaway merací build

**Dátum:** 2026-08-26
**Stav:** **postavené, zmerané, PRIJATÉ.** Vetva `throwaway/vu-needle-compensator`.
Výsledok a to, čo z neho platí ďalej, je na konci dokumentu — kto sa k tejto téme vráti,
nech si prečíta najprv ten záver.

## Načo to je

Predchádzajúci experiment
([`2026-08-26-vu-second-order-throwaway-design.md`](2026-08-26-vu-second-order-throwaway-design.md))
zamietol dorábanie presahu 1–1,5 % s odôvodnením, že ho mechanika meráka buď utlmí,
alebo bude príliš jemný. **Prvá polovica tej vety je nepravdivá a teraz to vieme.**

Z videa pri 240 fps je ručička zmeraná: je to lineárny systém druhého rádu so
**ζ = 0,391 a ωₙ = 11,81 rad/s** (fₙ 1,88 Hz), čiže robí **26 % presahu** a ustaľuje sa
takmer sekundu. Norma pýta 1–1,5 %. Mechanika teda presah neutlmuje — je najmenej
tlmenou časťou reťazca a robí ho asi dvadsaťnásobne viac, než sa vtedy zvažovalo dorobiť.

Zdroj konštánt sú štyri záznamy v koreni repozitára — `vu_step_response_240fps_0_35.csv`,
`_70_25`, `_0_70` a `_70_0` — prefitované päťparametrovým modelom druhého rádu
(`t0`, `y0`, `yf`, ζ, ωₙ). Použiteľné sú tri; `70_0` nie, lebo ručička bije do dorazu na
nule a tracker ju tam stráca. Tento dokument berie výsledok ako vstup.

Simulácia celého dnešného reťazca s tým plantom hovorí, že merák normu nespĺňa:
presah 19–23 % podľa úrovne, a kritérium „99 % do 300 ms" síce formálne prejde
(237–280 ms), ale falošne — ručička prechádza cez 99 % na ceste na 123 %.

Otázka teda nie je, či treba kompenzáciu. Otázka je, **či skompenzovaná ručička vyzerá
lepšie**. To nerozhodne výpočet, len dve ručičky vedľa seba — presne ako minule.

## Usporiadanie

```
                        ┌─ VuIntegrator τ=65,14 ms ─→ dBFS ─→ ChannelMapper −40…0 ─→ % ─→ pin 3  (ĽAVÝ, dnešný kód)
(L+R)/2 ─→ |x| ────────┤
                        └─ VuIntegrator τ=15 ms ─→ dBFS ─→ % (−40…0) ─→ biquad ─→ % ─→ pin 5  (PRAVÝ, nový)
```

Ľavá vetva je dnešný kód, nedotknutý. Pravá je nový reťazec a ide na **pravý merák,
kanál 1, pin 5** — ten istý fyzický kus, z ktorého sa plant meral. Kompenzátor naladený
na iný kus by bol horší než žiadny.

Obe ručičky dostanú ten istý mono signál `(L+R)/2`, takže rozdiel na cifernikoch je čisto
rozdiel balistiky. Po dobu experimentu nie je stereo.

**Stupnica sa nemení.** Obe vetvy ostávajú na lineárnej dB stupnici −40…0 dBFS. Minule sa
menila balistika aj stupnica naraz a verdikt rozhodla stupnica — o presahu sa nedozvedelo
nič. Táto chyba sa neopakuje.

## Konštanty

Všetky ako `const` na vrchu `NeedleCompensator.cs`, aby sa ktorákoľvek dala prestaviť a
rebuildnúť za pol minúty.

| konštanta | hodnota | odkiaľ |
|---|---|---|
| `PlantZeta` | 0,391 | zmerané; priemer troch čistých fitov (0,374 / 0,393 / 0,406) |
| `PlantOmegaN` | 11,81 rad/s | zmerané (11,70 / 11,85 / 11,89) |
| `TargetZeta` | 0,81 | 1,305 % presahu; odvodenie z minulého dokumentu |
| `TargetOmegaN` | 13,4221 rad/s | to isté odvodenie |
| `DetectorTauMs` | 15,0 | usmernenie pred ručičkou; priemerovanie robí kompenzátor |

### Prečo `TargetOmegaN = 13,4221` a nie 16

Pri presnom timeri by `ωₜ = 16` posadilo t99 na 289 ms a presah na 1,0 %, čiže by
splnilo obe kritériá normy. Pri realistickom jittri WinForms Timeru (−8/+16 ms) to ale
nedrží — z dvanástich náhodných behov vyšiel presah 2,0 % medián a 5,9 % v najhoršom.
Kompenzátor je **inverzný** filter: musí plant vyrušiť presne, takže mu vadí, keď povel
dorazí v inom čase, než počítal. Minulý throwaway mal simulátor ručičky, ktorý bol voči
jittru imúnny, lebo integroval vlastný stav skutočným uplynulým časom; inverzný filter
takú imunitu nemá.

Konzervatívne `13,4221` dá pri tom istom jittri 1,5 % medián a 3,2 % najhorší, za cenu
t99 348 ms. Kritérium 300 ms sa teda **vedome nespĺňa**.

Skrátenie ticku na 20 ms situáciu zhoršuje (5,8 % medián), lebo rovnaký absolútny jitter
je väčší zlomok periódy. Tá cesta je zavretá.

| cieľ | jitter | t99 medián | presah medián | presah najhorší |
|---|---|---|---|---|
| ζ 0,81 / ωₜ 16 | žiadny | 290 ms | 1,0 % | 1,0 % |
| ζ 0,81 / ωₜ 16 | −8/+16 ms | 280 ms | 2,0 % | 5,9 % |
| ζ 0,81 / ωₜ 13,42 | −8/+16 ms | 348 ms | 1,5 % | 3,2 % |
| *dnes, bez kompenzácie* | −8/+16 ms | *244 ms* | *23,0 %* | *23,7 %* |

## Kompenzátor

Prenos je podiel želanej a skutočnej balistiky — menovateľ je cieľ, čitateľ je nameraný
plant, ktorý sa tým vyruší a nahradí:

```
        ωt² · (s² + 2ζp·ωp·s + ωp²)
C(s) = ─────────────────────────────
        ωp² · (s² + 2ζt·ωt·s + ωt²)
```

Jednosmerný zisk je presne 1 (dosadiť `s = 0`). Vysokofrekvenčný je `ωt²/ωp²` = 1,2916,
teda **konečný** — filter je vlastný, nič sa nederivuje a nemá ako utiecť. Cenou je, že
tick-to-tick šum úrovne zosilní o 29 %.

Diskretizácia bilineárnou transformáciou nad **nameraným uplynulým časom**, nie nad
menovitými 40 ms, takže sa koeficienty prepočítajú každý tick:

```
k  = 2/dt
B0 = k² + 2ζp·ωp·k + ωp²      A0 = k² + 2ζt·ωt·k + ωt²
B1 = −2k² + 2ωp²              A1 = −2k² + 2ωt²
B2 = k² − 2ζp·ωp·k + ωp²      A2 = k² − 2ζt·ωt·k + ωt²

y[n] = (ωt²/ωp²)·(B0·x[n] + B1·x[n−1] + B2·x[n−2])/A0 − (A1·y[n−1] + A2·y[n−2])/A0
```

Tri veci, ktoré k tomu patria a nie sú kozmetika:

- **`dt` sa oreže na ⟨5 ms, 200 ms⟩.** Bez dolnej hranice `k` vybuchne, ak by dva ticky
  prišli tesne za sebou. Horná ošetruje stall — filter je stabilný, po pár tickoch sa
  dorovná.
- **Výstup sa clampuje na 0–100 % pred PWM.** Na doznievaní chce ísť pod nulu a clamp mu
  to nedovolí, takže tam je kompenzácia oslabená. Aj tak sa ručička dorazu len dotkne
  (−0,4 %) namiesto toho, aby doň dnes bila (−16,3 %).

  > **Oprava 2026-08-26, po implementácii.** Pôvodne tu stálo „−11,5 % počas asi siedmich
  > tickov" a „−1,3 % namiesto dnešných −14,0 %". Obe čísla boli z mojej simulácie, ktorá
  > **nemodelovala `SilenceGap`** a bola navyše počítaná ešte s `ωₜ = 16` namiesto
  > finálnych 13,4221. Implementer to odmeral na skutočnom filtri: skok povelu z plnej
  > výchylky na nulu dá **−6,33 % počas jedného ticku**, nie −11 % počas siedmich, a to
  > číslo je odteraz pripnuté testom `Release_AsksForANegativeCommandTheClampMustDiscard`.
  > Prepočet s modelovanou 150 ms bránou dáva pri kroku na −12 dBFS podiel −4,4 % (čo je
  > tých −6,33 % preškálovaných zo 100 % na 70 %) a minimum ručičky −0,4 % proti dnešným
  > −16,3 %. Záver drží, čísla nie — a nemali tu byť bez merania.
- **Stav sa resetuje tam, kde sa dnes resetujú integrátory** (`EnsureStarted`, pred
  `TryStart`), inak by po znovuzapnutí capture stará história kopla ručičkou.

## Zapojenie do pipeline

Nový pseudo-senzor `/audio/0/needle` vracia rovno **výchylku v percentách**. Kanál 1
dostane `Min = 0`, `Max = 100`, čím sa `ChannelMapper` stane priechodzím a nový reťazec
ostane celý vnútri audio zdroja — rovnaký trik ako minule.

Tri zásahy do existujúcich súborov:

- `VuIntegrator` dostane voliteľné τ v konštruktore, default = dnešná konštanta. Bez toho
  sa 15 ms detektor postaviť nedá, lebo τ je dnes `static readonly`.
- `VuIntegrator` dostane `AddMono(block, channelCount, sampleRate)` — priemer cez kanály
  rámca, bez alokácie na capture vlákne. Dnešné `Add` ostáva nedotknuté. Musí zvládnuť aj
  mono endpoint (`ChannelCount == 1`).
- `OnSamples` kŕmi tri detektory mono namiesto dvoch po kanáloch. Cena je asi 144 tisíc
  násobení za sekundu pri 48 kHz, čiže nič, a capture vlákno ďalej nealokuje.

Nedotknuté: `ChannelPipeline`, `ChannelMapper`, `MeterCalibration`, `FrameCodec`, sketch,
a `MinPwm`/`MaxPwm` z konfigurácie — to je kalibrácia fyzického meráka, ktorá s balistikou
nesúvisí.

## Ochrana konfigurácie

`config.json` sa chráni **na zdroji, nie klonovaním**. `Program.cs:14` dostane cestu
`config.throwaway.json`, a ak ten súbor neexistuje, skopíruje sa doň dnešný `config.json`,
aby si build priniesol kalibráciu meráka.

Tým sú obe ukladacie cesty presmerované naraz — tray VU toggle
(`TrayApplicationContext.cs:121`) aj Save v nastaveniach (`SettingsForm.cs:425`) — lebo
obe idú cez ten istý `ConfigStore`. Klon `AppConfig` by to nespravil; prečo, vysvetľuje
záver minulého dokumentu. Do logu ide pri štarte riadok, že ide o merací build.

## Testy

- `VuIntegrator` — `AddMono` naozaj priemeruje kanály rámca; vlastné τ dosiahne 1−1/e
  presne pri τ; **default τ ostal nezmenený** (poistka, že ľavá vetva sa nehla).
- `NeedleCompensator` — jednosmerný zisk presne 1; koeficienty pri `dt = 40 ms` proti
  ručne dopočítanej referencii; `dt` sa oreže na oboch koncoch; `Reset` vyčistí stav.
- Ten test, na ktorom záleží: **skok priamo do kompenzátora** (bez detektora), jeho výstup
  prehnaný **simulovaným plantom** (ζ 0,391 / ωₙ 11,81, uzavretý tvar zero-order hold),
  musí dať cieľovú odozvu — presah 1,3 % a 99 % pri 300 ms, v tolerancii diskretizácie.
  To overí, že kompenzátor plant naozaj vyrušil. Tých 348 ms z tabuľky vyššie je niečo
  iné: tam je pred kompenzátorom ešte detektor a tick s jittrom.
- `AudioLevelSensorSource` — `/audio/0/needle` vracia percentá a nie dBFS, neznáme id
  ďalej vracia `null`, a čítanie ihly neruší `Left`.

## Čo budú oči vidieť

Na skoku na −12 dBFS špička **85,8 % → 70,6 %** (presah 22,6 % → 0,9 %), čiže odraz
zmizne, a na doznievaní ručička prestane biť do nuly. To sú tie dve viditeľné veci.

Aj tieto dve čísla sú prepočítané na finálne `ωₜ = 13,4221`; pôvodne tu stálo
85,6 → 70,3 z behu s `ωₜ = 16`.

Ak sa pravý merák aj tak nebude páčiť, je to legitímny a užitočný výsledok experimentu,
nie jeho zlyhanie.

## Čo tento build povedať nemôže

1. **Menia sa dve premenné** — biquad aj detektor 65,14 → 15 ms. Ak bude pravý horší,
   nebude jasné, ktorá za to môže. Vedomé rozhodnutie majiteľa; `DetectorTauMs` je
   konštanta na vrchu súboru, takže rozpliesť sa to dá jedným riadkom a rebuildom.
2. ~~**Plant je zmeraný len na pravom meráku.**~~ **Vyriešené 2026-08-26 po napísaní
   tohto dokumentu.** Ľavý merák bol domeraný tým istým postupom,
   `vu_left_0_to_70_frame_time_angle_value_240fps.csv`: **ζ = 0,377, ωₙ = 11,53 rad/s**,
   presah 27,8 %, RMSE 0,65 % proti šumu 0,10 %. Proti pravému (0,374 / 11,70) je to
   rozdiel 0,8 % v ζ a 1,5 % v ωₙ, teda v rámci merateľnosti ten istý kus. „Ľavý vs
   pravý" je odteraz čistý súboj balistík a kompenzátor naladený na 0,391 / 11,81 sedí
   na oba.

   Druhý ľavý záznam, `vu_left_0_to_35_...csv`, je nepoužiteľný — tracker prvých 110 ms
   preskakuje medzi dvoma vetvami (21 % za 8 ms, čo je desaťnásobok fyzikálneho stropu)
   a stratil celý nábeh. Na túto otázku ho netreba: amplitúdovú závislosť už zavreli tri
   body na pravom meráku.
3. **Nedemonštruje zhodu s normou.** Kritérium 300 ms sa mieva zámerne (348 ms) a jitter
   timeru drží presah na 1,5 % medián, 3,2 % v najhoršom.
4. **Na doznievaní do ticha je kompenzácia orezaná** clampom na nule.

## Návrat

`git checkout main`, rebuild, zmazať `config.throwaway.json`. `main` sa nedotkne.

---

# Výsledok

Build sa postavil celý (päť taskov, sedem commitov), prešiel 198 testami a bežal na
skutočnom hardvéri s oboma meráčmi vedľa seba. Verdikt majiteľa:

> „jednoznačne pravý to znamená kompenzovaný meter je lepší menaj prekmitov a je to
> podstatne presnejšie a na 0 to nenaraža na doraz"

Pravý je nový reťazec: detektor τ = 15 ms, inverzný biquad ζ 0,81 / ωₙ 13,4221, tá istá
stupnica −40…0 dBFS ako vľavo. **Toto je opačný verdikt než pri predchádzajúcom
experimente**, kde vyhral dnešný kód — a je to konzistentné: vtedy sa dorábal presah
1,3 % k meráku, ktorý ich už 26 % robil sám, teraz sa tých 26 % odoberá.

## Čo z toho platí bez výhrad

Dve z troch pozorovaní sú čistá balistika a **nie sú ničím zamútené**:

- **„menej prekmitov"** — presah ide 22,6 % → 0,9 %, a robí to výhradne biquad.
- **„na 0 to nenaráža na doraz"** — ručička sa dorazu dotkne na −0,4 % namiesto toho, aby
  doň dnes tlačila 16,3 %. Tiež výhradne biquad.

Obe sú presne to, čo simulácia predpovedala, a obe sú dôvodom, prečo build vznikol.

## Čo z toho zatiaľ pripísať nemožno

**„podstatne presnejšie"** je jediné pozorovanie, ktoré padá do tieňa confoundu z
limitácie č. 1. Skrátený detektor priemeruje v dB doméne, kým dnešný v amplitúdovej, a
Jensenova nerovnosť z toho robí systematický posun: pravý číta na dynamickom materiáli
nižšie — odmerané −2,6 % na náhodných 60 ms zhlukoch, −6,2 % pri striedaní −6/−26 dBFS,
−20,3 % pri striedaní 0/−40 dBFS, a presne 0,0 % na ustálenom tóne. Pri `DetectorTauMs`
prepnutom na 65,14 ms je ten posun nula vo všetkých štyroch prípadoch, čiže ho robí celý
detektor a nie biquad, ktorého jednosmerný zisk je presne 1.

„Presnejšie" teda môže znamenať dve rôzne veci a z jednej relácie sa nerozlíšia: buď že
ručička sedí bližšie k tomu, čo hudba naozaj robí, alebo že sa oku páči, keď merák číta
o pár percent nižšie. **Rozhodne to jedna kontrolná relácia** s `DetectorTauMs = 65,14`,
kde je biquad jediným rozdielom — jedna konštanta a rebuild.

## Ďalší krok

Toto **nie je merge**. Vetva je throwaway a `Program.cs` v nej presmerúva konfiguráciu do
`config.throwaway.json`, `VuIntegrator` má mŕtvu preťaženú metódu a oba VU kanály dostávajú
mono. Víťazstvo kompenzátora znamená nový spec pre poriadnu implementáciu, a ten by mal
vyriešiť aspoň:

- **v ktorej doméne sa priemeruje** — kontrolná relácia vyššie je jeho vstup;
- **stereo** — mono fold bol len kvôli férovosti porovnania;
- **jitter timeru**, ktorý je dnes limitom presnosti kompenzátora, nie matematika;
- **druhý merák** — ľavý je zmeraný (ζ 0,377 / ωₙ 11,53) a od pravého sa líši o 0,8 %,
  takže jedna sada konštánt stačí na oba, ale kompenzátor musí bežať na oboch kanáloch.
