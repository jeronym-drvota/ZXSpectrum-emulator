# ZX Spectrum Emulator — kontext projektu

Emulátor ZX Spectra **48K i 128K** v jazyce **C# / .NET 8** s UI ve frameworku **Avalonia 11**.
Komentáře v kódu i UI jsou **česky**. Uživatel preferuje stručné, věcné odpovědi (česky).

## Sestavení
- Projekt: `ZxSpectrum/ZxSpectrum.csproj` (net8.0, WinExe, RootNamespace `ZxSpectrum`).
- Závislosti (NuGet): Avalonia 11.1.3 (+ Desktop, Themes.Fluent), **NAudio 2.2.1** (zvuk, prakticky Windows-only — na jiných OS spadne do tichého režimu).
- Build dělá uživatel ve Visual Studiu. **Pozn.: v dosavadních sezeních nešlo kompilovat v sandboxu** (chybí .NET SDK, omezená síť), takže logika se ověřovala porty do Pythonu + revizí. Build vždy potvrzuje uživatel.

## Struktura
```
ZxSpectrum/
  Program.cs            – Avalonia bootstrap (App, MainWindow)
  MainWindow.cs         – okno, smyčka snímků, časování, vstup (klávesnice/joystick), UI overlaye
  BeeperAudio.cs        – NAudio výstup (WaveOutEvent + BufferedWaveProvider)
  Core/
    Z80.cs              – CPU Z80 (kompletní instrukční sada vč. CB/ED/DD/FD), IBus, T-stavy
    Spectrum.cs         – stroj: bankovaná paměť, porty (IBus), běh snímku, trap, snapshoty, páska, joystick
    Ula.cs              – render obrazu z 16K obrazové banky do BGRA framebufferu
    Beeper.cs           – 1-bit reproduktor: záznam přepnutí bitu 4, render PCM vzorků + DC blocker
    Ay.cs               – AY-3-8912 (3 tóny, šum, obálka), render vzorků
    Snapshot.cs         – načítání i ukládání .sna a .z80 (48K i 128K), detekce modelu
    LoadingGif.cs       – uložení obrazovky jako animovaný „nahrávací" GIF (vlastní GIF89a/LZW)
    TzxTape.cs          – páska .tzx/.tap: bloky → pulzy + syrová data, přehrávač EAR
    RzxPlayer.cs        – přehrávání RZX záznamů (snapshot + vstupy po snímcích)
    TestRom.cs          – náhradní testovací ROM, když chybí skutečná
  roms/                 – 48.rom / ZX_Spectrum_48k.rom, ZX_Spectrum_128k.rom (32K), Didaktik…
```

## Architektura — klíčové body

### Paměť (Spectrum.cs)
- `byte[][] Ram` = 8 bank po 16K; `Rom` = 1 ROM (48K) nebo 2 ROM (128K editor + 48K BASIC).
- Adresní prostor = 4 stránky po 16K přes `page[4]` (mapování čtení). `Read`/`Write` přes `page[addr>>14]`. Stránka 0 = ROM (zápis blokován).
- 128K stránkování portem **0x7FFD** (`SetPaging`): bity 0-2 banka na 0xC000, bit3 obrazovka (5/7), bit4 ROM, bit5 zámek. 48K = zámek trvale.
- `ScreenBank` = aktivní obrazová banka (5 normální / 7 stínová), používá ji ULA.
- 48K mapuje slot1=Ram[5], slot2=Ram[2], slot3=Ram[0]; `LoadRam48` tomu odpovídá.

### Porty (Spectrum.In / Out)
- ULA port = sudé porty (`port&1==0`): IN = klávesnice (horní bajt vybírá půlřádky) + bit6 EAR z pásky; OUT = border (bity 0-2) + beeper (bit4).
- **0x7FFD** paging (jen 128K). **0xFFFD** AY select/read, **0xBFFD** AY write — řízeno `EnableAy` (128K vždy; 48K volitelně = Melodik). AY takt fixně **1773400 Hz**.
- **0x1F** Kempston joystick (`(port&0x20)==0`), bity aktivní v 1: bit0 R,1 L,2 D,3 U,4 fire.
- Při přehrávání RZX vrací `In` nahrané hodnoty (`Rzx.NextIn()`).

### Časování
- `TStatesPerFrame`: 48K=69888, 128K=70908. `CpuHz`: 3 500 000 / 3 546 900.
- MainWindow.Frame() dohání **reálný čas** přes Stopwatch (ne fixní 1 snímek/tik) → správná rychlost i při jitteru časovače. Násobič `speed`, `unlimited` (max).

### Nahrávání pásky
- TzxTape rozbalí bloky (0x10,0x11,0x12,0x13,0x14,0x20, smyčky 0x24/0x25, metadata) na seznam **bloků**, každý má pulzy + (u standardních) syrové bajty.
- **Instant load** = trap na adrese **0x0556** (LD-BYTES) vloží blok přímo do paměti. **Jen 48K!** (na 128K trap rozbíjel stránkování ROM → vypnuto, 128K jede přes pulzy).
- Pulzní nahrávání čte EAR (bit6) v reálném čase; `fastTape` zrychlí.

### Snapshoty / RZX
- Snapshot.cs: 48K i 128K (.sna podle délky, .z80 v1/v2/v3 podle hlavičky). `DetectModel128` / `IsZ80_128` → MainWindow podle toho přestaví stroj.
- RzxPlayer: parsuje RZX (zlib bloky 0x30 snapshot, 0x80 vstupy), přehrává deterministicky (N instrukcí/snímek + nahrané IN). Umí jen vložený **.z80**.

## Ovládání (klávesy) — definováno v MainWindow.OnKeyDown
- **Tab** – joystick (šipky+Space) / klávesnice
- **F1** – nápověda (overlay s ovládáním)
- **F2** – model 48K / 128K (reset stroje)
- **F3** – otevřít soubor (.sna .z80 .tzx .tap .rzx)
- **F4** – AY zvuk na 48K (Melodik) zap/vyp
- **F5** – zvuk zap/vyp
- **F6** – páska přehrát/zastavit
- **F7** – max rychlost (unlimited)
- **F8** – rychlé (pulzní) nahrávání
- **F9** – okamžité nahrávání (ROM trap, jen 48K)
- **F10** – reset stroje
- **F11** – grafická mapa kláves ZX Spectra (overlay)
- **F12** – fullscreen (též **Alt+Enter**, **Esc** ven)
- **Insert** – vložit POKE (cheat: adresa,hodnota; overlay)
- **Home** – uložit snapshot (.z80) do Dokumenty\snapshots (název hry + datum/čas)
- **End** – nahrát snapshot (výběrový overlay, šipky + Enter)
- **PrintScreen** – uložit obrazovku jako „nahrávací" animovaný GIF do Dokumenty\screenshots (akce na KeyUp)
- **Num + / − / *** – rychleji / pomaleji / 100 %
- Kempston: **šipky + Space (fire)**

### Grafická klávesnice (F11, MainWindow.MakeKeyboardOverlay)
Každá klávesa: velké písmeno vlevo, hlavní příkaz (bíle) vpravo od něj, zelený E-mode příkaz nad příkazem, červený E-mode dole, symbol-shift (červeně) vpravo nahoře.
Pozn.: L=LET, K=LIST (správně dle reálného Spectra). Pár E-mode tokenů horní řady (ASN/ACS/ATN, VERIFY, MERGE) doplněno dle standardu — případně doladit.

## Konvence / preference
- Odpovídat **česky**, stručně a věcně, bez zbytečné omáčky.
- Komentáře v kódu česky.
- Algoritmickou logiku ověřovat (Python porty / testy), build nechat na uživateli.
- Nepoužité členy mazat (čistota), klávesy držet bez konfliktů.

## Možná další práce (zatím neuděláno)
- GIF: volitelná komprese bloků (teď nekomprimované 0xFFFF u .z80; GIF má vlastní LZW).
- **Nahrávání** RZX (jen přehrávání hotové).
- SZX snapshoty (RZX i samostatně).
- ULA contention, floating bus, Issue 2/3 rozdíl EAR — neemulováno.
- Per-scanline render / multicolor efekty (teď render po snímku z banky).
- Reálný USB gamepad pro joystick.
- Zelené E-mode tokeny mají u pár kláves nejistotu — ověřit proti reálné klávesnici.
