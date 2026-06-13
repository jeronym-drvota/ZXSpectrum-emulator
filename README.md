# Emulátor ZX Spectrum 48K / 128K (C# + Avalonia)

Multiplatformní emulátor ZX Spectra **48K i 128K** napsaný v C# (.NET 8). Obsahuje
kompletní emulaci procesoru Z80 (včetně prefixů CB, ED, DD a FD), bankované paměti,
ULA (obraz, border, FLASH atributy), klávesnice a Kempston joysticku. Umí zvuk
(beeper + zvukový čip AY-3-8912), nahrávání pásek, snapshoty a přehrávání RZX
záznamů. GUI běží na Avalonia, takže funguje na Windows, Linuxu i macOS (zvuk je
přes NAudio prakticky jen na Windows; jinde tichý režim).

## Požadavky

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Připojení k internetu při prvním sestavení (NuGet stáhne Avalonia a NAudio)

## Spuštění

```bash
cd ZxSpectrum
dotnet run            # výchozí model 48K
dotnet run -- 128     # spustí model 128K
```

Lze předat i cestu ke snapshotu nebo pásce:

```bash
dotnet run -- /cesta/k/hre.z80
```

Bez ROM se zobrazí vestavěný testovací obraz, který ověřuje, že emulace běží.

## ROM

ROMy patří do adresáře `ZxSpectrum/roms`. Při startu se nabídnou všechny nalezené
(48K, 128K, Didaktik…). Pro 48K se hledá `48.rom` / `ZX_Spectrum_48k.rom` (16 KB),
pro 128K `ZX_Spectrum_128k.rom` (32 KB). Bez skutečné ROM se použije náhradní
testovací ROM.

ROM Spectra je chráněn autorským právem (dnes Amstrad), který už v roce 1999
veřejně povolil jeho šíření spolu s emulátory. Najdeš ho např. v archivu
World of Spectrum nebo v balíčcích jiných emulátorů.

## Podporované formáty

- **Snapshoty:** `.sna`, `.z80` (verze 1/2/3, 48K i 128K) – načtení i uložení.
- **Pásky:** `.tap`, `.tzx` – nahrávání přes reálné pulzy, na 48K i okamžité
  nahrávání přes ROM trap (LD-BYTES na 0x0556).
- **Záznamy:** `.rzx` – deterministické přehrávání (snapshot + vstupy po snímcích;
  zatím jen s vloženým `.z80`).

Soubor lze otevřít klávesou **F3**, přetažením do okna, nebo argumentem na příkazové
řádce. Klávesou **Shift+F3** lze hru vyhledat a stáhnout přímo z webu (ZXDB).

## Ovládání

| Klávesa | Funkce |
|---|---|
| **Tab** | přepnutí šipky+Space mezi joystickem a klávesnicí |
| šipky + Space | Kempston joystick (Space = fire) |
| **F1** | nápověda (mapa kláves emulátoru) |
| **F2** | model 48K / 128K (reset) |
| **F3** | otevřít soubor (.sna .z80 .tzx .tap .rzx) |
| **Shift+F3** | hledat a spustit hru z webu (ZXDB) |
| **F4** | AY zvuk na 48K (rozhraní Melodik) zap/vyp |
| **F5** | zvuk zap/vyp |
| **F6** | páska: přehrát / zastavit |
| **F7** | maximální rychlost (neomezeně) |
| **F8** | rychlé (pulzní) nahrávání |
| **F9** | okamžité nahrávání (ROM trap, jen 48K) |
| **F10** | reset stroje |
| **F11** | grafická mapa kláves ZX Spectra |
| **F12** | fullscreen (též Alt+Enter, Esc = ven) |
| **Insert** | vložit POKE (cheat: `adresa,hodnota`) |
| **Home** | uložit snapshot (nový soubor do `Dokumenty\snapshots`) |
| **End** | nahrát snapshot (výběr ze seznamu šipkami) |
| **PrintScreen** | uložit obrazovku jako „nahrávací" animovaný GIF |
| **Num + / − / ∗** | rychleji / pomaleji / 100 % |

### Klávesnice

| PC klávesa | Spectrum |
|---|---|
| A–Z, 0–9, Enter, mezerník | odpovídající klávesy |
| Shift | Caps Shift |
| Ctrl | Symbol Shift (interpunkce, operátory) |
| Backspace | Caps Shift + 0 (DELETE) |
| Šipky (v režimu klávesnice) | Caps Shift + 5/6/7/8 |

Tip: ve Spectrum BASICu se příkazy píší jednou klávesou – např. `P` napíše rovnou
`PRINT`. Zkus `P`, pak `Ctrl+P` (uvozovky), `ahoj`, `Ctrl+P`, `Enter`.

### POKE (cheaty)

Klávesa **Insert** otevře okno pro vložení POKE. Napiš `adresa,hodnota`
(např. `23693,71`), potvrď **Enter**. Oddělovač může být čárka, mezera i středník.
Zápis respektuje aktuální stránkování (na 128K do právě namapované banky), adresy
v ROM (pod 16384) se ignorují.

### Snapshoty (uložení / nahrání)

**Home** uloží aktuální stav jako **nový** soubor `.z80` do složky
`Dokumenty\snapshots`; název je odvozen z aktuální hry a doplněn o datum a čas
(např. `Manic Miner_2026-06-12_14-30-05.z80`). **End** otevře seznam uložených
snapshotů – šipkami `↑`/`↓` vybereš, **Enter** nahraje (v případě potřeby se sám
přepne model 48K ↔ 128K), **Esc** zavře.

### „Nahrávací" GIF

**PrintScreen** uloží aktuální obrazovku jako animovaný GIF do `Dokumenty\screenshots`,
který vypadá jako nahrávání z pásky: blikající červeno-azurové pruhy v borderu a
obraz se odkrývá v autentickém prokládaném pořadí (nejdřív monochromaticky pixely
v pořadí adres obrazové paměti, pak doskáčou barvy z atributů). Generování má
vlastní GIF89a/LZW enkodér (bez externích závislostí) a běží na pozadí.

## Struktura projektu

```
ZxSpectrum/
├── Core/
│   ├── Z80.cs        – jádro CPU: kompletní instrukční sada (CB/ED/DD/FD),
│   │                   příznaky, přerušení IM 0/1/2, R registr, T-stavy
│   ├── Spectrum.cs   – stroj: bankovaná paměť, porty (IBus), běh snímku,
│   │                   stránkování 128K, snapshoty, páska, joystick
│   ├── Ula.cs        – render obrazu z obrazové banky do BGRA framebufferu
│   ├── Beeper.cs     – 1-bit reproduktor (bit 4 portu 0xFE) → PCM
│   ├── Ay.cs         – zvukový čip AY-3-8912 (3 tóny, šum, obálka)
│   ├── Snapshot.cs   – načítání i ukládání .sna / .z80 (48K i 128K)
│   ├── TzxTape.cs    – páska .tzx/.tap: bloky → pulzy + syrová data
│   ├── RzxPlayer.cs  – přehrávání RZX záznamů
│   └── TestRom.cs    – náhradní testovací ROM, když chybí skutečná
├── BeeperAudio.cs    – výstup zvuku přes NAudio
├── WebGames.cs       – hledání a stahování her z webu (ZXDB)
├── Program.cs        – vstupní bod a Avalonia App
├── MainWindow.cs     – okno, smyčka snímků, časování, vstup, UI overlaye
├── roms/             – soubory ROM (48K, 128K, Didaktik…)
└── ZxSpectrum.csproj

ZxSpectrum.Tests/     – konzolové testy jádra
```

Jádro v `Core/` nemá závislosti na GUI – jde znovu použít v jiném frontendu
(konzole, web, hra…).

## Testy

```bash
cd ZxSpectrum.Tests
dotnet run                          # testy jádra Z80
dotnet run -- --screenshot out.ppm  # uloží snímek testovacího obrazu
```

## Co (zatím) chybí

Náměty na rozšíření:

- **Nahrávání** RZX (zatím jen přehrávání) a SZX snapshoty.
- **Přesné časování ULA** – memory contention, floating bus, rozdíl Issue 2/3.
- **Per-scanline render** – multicolor a další efekty (teď render po snímku).
- **Reálný USB gamepad** pro joystick.
