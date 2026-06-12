# Emulátor ZX Spectrum 48K (C# + Avalonia)

Multiplatformní emulátor ZX Spectrum 48K napsaný v C#. Obsahuje kompletní emulaci
procesoru Z80 (včetně prefixů CB, ED, DD a FD), 48K paměti, ULA (obraz, border,
FLASH atributy) a klávesnice. GUI běží na Avalonia, takže funguje na Windows,
Linuxu i macOS.

## Požadavky

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Připojení k internetu při prvním sestavení (NuGet stáhne Avalonia)

## Spuštění

```bash
cd ZxSpectrum
dotnet run
```

Bez souboru ROM se zobrazí vestavěný testovací obraz (barevné proužky), který
ověřuje, že emulace běží.

## ROM

Pro skutečný zážitek (BASIC, © 1982 Sinclair Research Ltd) potřebuješ originální
16KB ROM Spectra 48K. Ulož ho jako **`48.rom`** do adresáře `ZxSpectrum`
(vedle `.csproj`), nebo cestu k němu předej jako argument:

```bash
dotnet run -- /cesta/k/48.rom
```

ROM je chráněn autorským právem společnosti Amstrad, která ale už v roce 1999
veřejně povolila jeho šíření spolu s emulátory. Najdeš ho např. v archivu
World of Spectrum nebo v balíčcích jiných emulátorů (soubor mívá přesně
16384 bajtů).

## Klávesnice

| PC klávesa | Spectrum |
|---|---|
| A–Z, 0–9, Enter, mezerník | odpovídající klávesy |
| Shift | Caps Shift |
| Ctrl | Symbol Shift (interpunkce, operátory) |
| Backspace | Caps Shift + 0 (DELETE) |
| Šipky | Caps Shift + 5/6/7/8 |

Tip: ve Spectrum BASICu se příkazy píší jednou klávesou – např. `P` napíše
rovnou `PRINT`. Zkus `P`, pak `Ctrl+P` (uvozovky), `ahoj`, `Ctrl+P`, `Enter`.

## Struktura projektu

```
ZxSpectrum/
├── Core/
│   ├── Z80.cs        – jádro CPU: kompletní dokumentovaná instrukční sada,
│   │                   příznaky, přerušení IM 0/1/2, R registr, T-stavy
│   ├── Spectrum.cs   – stroj: paměťová mapa, ULA port 0xFE, matice klávesnice,
│   │                   běh snímku (69 888 T-stavů, 50Hz přerušení)
│   ├── Ula.cs        – převod obrazové paměti + atributů na framebuffer 320×240
│   └── TestRom.cs    – vestavěný testovací program v Z80
├── Program.cs        – vstupní bod a Avalonia App
├── MainWindow.cs     – okno, WriteableBitmap, časovač 50 Hz, mapování kláves
└── ZxSpectrum.csproj

ZxSpectrum.Tests/     – konzolové testy jádra (spusť `dotnet run`)
```

Jádro v `Core/` nemá žádné závislosti na GUI – jde znovu použít v jiném
frontendu (konzole, web, hra…).

## Testy

```bash
cd ZxSpectrum.Tests
dotnet run                          # 24 testů jádra Z80
dotnet run -- --screenshot out.ppm  # uloží snímek testovacího obrazu
```

## Co (zatím) chybí

Náměty na rozšíření, kdyby ses chtěl vyřádit:

- **Zvuk** – beeper je bit 4 portu 0xFE; stačí vzorkovat jeho změny.
- **Nahrávání programů** – formát `.z80`/`.sna` (snapshot paměti a registrů)
  je nejjednodušší cesta, jak spouštět hry. Formát `.tap` vyžaduje emulaci
  nahrávání z pásky (bit 6 portu 0xFE).
- **Přesné časování ULA** – memory contention a floating bus pro hry,
  které na nich závisí.
- **Kempston joystick** – port 0x1F, mapování na šipky.
