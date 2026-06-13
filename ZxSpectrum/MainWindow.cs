using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZxSpectrum.Core;

namespace ZxSpectrum
{
    public class MainWindow : Window
    {
        Spectrum spectrum;
        bool is128;
        bool ay48; // přání mít AY (Melodik) i na 48K – platí napříč přestavbami stroje
        bool joystick = true; // šipky+Space ovládají Kempston joystick (jinak klávesnici)
        readonly WriteableBitmap bitmap;
        readonly Image image;
        readonly Border helpOverlay;
        readonly Border kbOverlay;
        readonly Border romOverlay;

        // startovní menu výběru ROM
        readonly List<(string Path, string Name, bool Is128)> romList;
        bool romMenuActive;     // dokud je menu zobrazeno, stroj stojí
        string customRomPath;   // ROM zvolená z menu (null = standardní kandidáti)

        // hledání her na webu (ZXDB / api.zxinfo.dk) – Shift+F3
        readonly Border webOverlay;
        readonly TextBlock webText;
        bool webMenuActive;
        bool webBusy;           // probíhá hledání / stahování
        string webQuery = "";
        string webStatus;       // stavová / chybová hláška
        List<WebGames.Entry> webResults;
        static readonly string DownloadDir = Path.Combine(AppContext.BaseDirectory, "downloads");

        // snapshoty: ukládání do Dokumenty\snapshots (vždy nový soubor), výběrové nahrávání
        static readonly string SnapshotDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "snapshots");
        // „nahrávací" GIF aktuální obrazovky
        static readonly string ScreenshotDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "screenshots");
        string currentGameName = "Spectrum";   // základ názvu ukládaného snapshotu

        // overlay výběru snapshotu k nahrání (End)
        readonly Border snapOverlay;
        readonly TextBlock snapText;
        bool snapMenuActive;
        List<string> snapFiles = new();
        int snapIndex;

        // vkládání POKE (cheaty) – klávesa Insert
        readonly Border pokeOverlay;
        readonly TextBlock pokeText;
        bool pokeMenuActive;
        string pokeInput = "";
        string pokeStatus;                                  // potvrzení / chyba poslední operace
        readonly List<(ushort addr, byte val)> appliedPokes = new(); // historie pro výpis

        // automatické vkládání kláves (autostart pásky: LOAD "" / Tape Loader)
        // položka: keys = stisknout na 'frames' snímků; keys == null = jen čekat
        readonly Queue<((int row, int bit)[] keys, int frames)> autoSeq = new();
        (int row, int bit)[] autoHeld; // právě držené klávesy
        int autoTimer;                 // zbývající snímky držení / čekání
        bool autoTapeAfter;            // po dokončení fronty spustit pásku
        readonly int[] pixels = new int[Ula.Width * Ula.Height];
        readonly DispatcherTimer timer;

        const string HelpText =
            "MAPA KLÁVES  (F1 zavře)\n" +
            "\n" +
            "  Tab        joystick  /  klávesnice\n" +
            "  šipky+Space Kempston joystick (Space = fire)\n" +
            "\n" +
            "  F2         model 48K / 128K\n" +
            "  F3         otevřít soubor (.sna .z80 .tzx .tap .rzx)\n" +
            "  Shift+F3   hledat a spustit hru z webu (ZXDB)\n" +
            "  F4         AY zvuk na 48K (Melodik)\n" +
            "  F5         zvuk zap / vyp\n" +
            "  F6         páska: přehrát / zastavit\n" +
            "  F7         max rychlost\n" +
            "  F8         rychlé nahrávání\n" +
            "  F9         okamžité nahrávání\n" +
            "  F10        reset\n" +
            "  F11        mapa kláves ZX Spectrum\n" +
            "  F12        fullscreen  (též Alt+Enter, Esc = ven)\n" +
            "\n" +
            "  Insert     vložit POKE (cheat: adresa,hodnota)\n" +
            "  Home       uložit snapshot (Dokumenty\\snapshots)\n" +
            "  End        nahrát snapshot (výběr šipkami)\n" +
            "  PrintScreen uložit obrazovku jako nahrávací GIF\n" +
            "  Pause      pozastavit / spustit emulátor\n" +
            "\n" +
            "  Num +  /  Num -    rychleji / pomaleji\n" +
            "  Num *              rychlost 100 %";

        /// <summary>Zrychlit běh, dokud hraje páska (rychlé nahrávání).</summary>
        bool fastTape = true;

        readonly BeeperAudio audio = new();
        readonly short[] audioBuf = new short[882];
        bool muted;

        // časování podle reálných hodin – nezávisle na (ne)přesnosti časovače
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTime;       // čas posledního snímku (s)
        double tStateBudget;   // kolik T-stavů zbývá odběhnout
        double speed = 1.0;    // násobič rychlosti emulace
        bool unlimited;        // běh na maximum (bez limitu reálného času)
        bool paused;           // emulátor pozastaven (stroj stojí, obraz se drží)

        public MainWindow(string[] args)
        {
            // model: výchozí 48K, "128" v argumentech přepne na 128K
            BuildMachine(args.Any(a => a == "128" || a == "-128" || a == "/128"));

            Width = Ula.Width * 2 + 16;
            Height = Ula.Height * 2 + 40;
            Background = Brushes.Black;

            bitmap = new WriteableBitmap(
                new PixelSize(Ula.Width, Ula.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

            helpOverlay = MakeOverlay(HelpText);
            kbOverlay = MakeKeyboardOverlay();
            romList = ScanRoms();
            romOverlay = MakeRomOverlay();
            webText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 14
            };
            webOverlay = MakeOverlayBox(webText);
            pokeText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 14
            };
            pokeOverlay = MakeOverlayBox(pokeText);
            snapText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 14
            };
            snapOverlay = MakeOverlayBox(snapText);

            Content = new Panel { Children = { image, helpOverlay, kbOverlay, romOverlay, webOverlay, pokeOverlay, snapOverlay } };
            Focusable = true;

            timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(20) // 50 Hz
            };
            timer.Tick += (_, _) => Frame();
            lastTime = clock.Elapsed.TotalSeconds;
            timer.Start();

            // přetažení souboru snapshotu do okna
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            // snapshot zadaný na příkazové řádce (má přednost před menu ROM)
            var snap = FindSnapshotArg(args);
            if (snap != null) LoadSnapshotFile(snap);
            else if (romList.Count > 1)
            {
                romOverlay.IsVisible = true;
                romMenuActive = true;
            }
        }

        // ---------- sestavení stroje ----------
        /// <summary>(Znovu)vytvoří emulovaný stroj v daném modelu a zachová pásku.</summary>
        void BuildMachine(bool want128)
        {
            var oldTape = spectrum?.Tape;

            bool testRom = true;
            byte[] rom = null;

            // ROM zvolená ze startovního menu – platí, jen pokud sedí model
            if (customRomPath != null)
            {
                try
                {
                    var data = File.ReadAllBytes(customRomPath);
                    bool rom128 = data.Length == 32768;
                    if ((data.Length == 16384 || rom128) && rom128 == want128)
                    {
                        rom = data;
                        testRom = false;
                    }
                    else customRomPath = null; // jiný model → zpět na standardní ROM
                }
                catch (IOException) { customRomPath = null; }
            }

            rom ??= want128
                ? LoadRom(Rom128Candidates, 32768, out testRom)
                : LoadRom(Rom48Candidates, 16384, out testRom);

            if (rom == null && want128) // 128K ROM se nenašla → spadneme na 48K
            {
                want128 = false;
                rom = LoadRom(Rom48Candidates, 16384, out testRom);
            }
            if (rom == null) { rom = TestRom.Build(); testRom = true; }

            is128 = want128;
            spectrum = new Spectrum(rom, is128);
            spectrum.HasStandardRom = !testRom;
            spectrum.Tape = oldTape;
            if (!is128) spectrum.EnableAy = ay48; // AY na 48K dle volby (128K vždy)

            // srovnání časování
            lastTime = clock.Elapsed.TotalSeconds;
            tStateBudget = 0;

            AutoClear(); // reset ruší rozjetý autostart pásky
            UpdateTitle();
        }

        void UpdateTitle()
        {
            string model = is128 ? "128K" : "48K";
            string rom = customRomPath != null ? $" [{Path.GetFileName(customRomPath)}]" : "";
            Title = spectrum.HasStandardRom
                ? $"ZX Spectrum {model}{rom}"
                : $"ZX Spectrum {model} – testovací obraz (ROM nenalezena)";
        }

        // ---------- nahrávané soubory ----------
        static readonly string[] SnapshotExts = [".sna", ".z80"];
        static readonly string[] TapeExts = [".tzx", ".tap"];

        static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
        static bool IsSnapshot(string path) => SnapshotExts.Contains(Ext(path));
        static bool IsTape(string path) => TapeExts.Contains(Ext(path));
        static bool IsRzx(string path) => Ext(path) == ".rzx";
        static bool IsLoadable(string path) => IsSnapshot(path) || IsTape(path) || IsRzx(path);

        static string FindSnapshotArg(string[] args) =>
            args.FirstOrDefault(a => IsLoadable(a) && File.Exists(a));

        void LoadSnapshotFile(string path, bool updateGameName = true)
        {
            if (romMenuActive) SelectRom(-1); // přetažení souboru zavře menu ROM
            AutoClear(); // ruční načtení ruší rozjetý autostart pásky
            // u nahrání ze složky snapshotů necháme název hry, aby se nezřetězil s časem
            if (updateGameName) currentGameName = Path.GetFileNameWithoutExtension(path);

            try
            {
                if (IsRzx(path)) { LoadRzx(path); return; }

                if (IsTape(path))
                {
                    spectrum.Rzx = null;
                    spectrum.LoadTape(path);
                    Title = is128
                        ? $"ZX Spectrum 128K – páska {Path.GetFileName(path)} "
                          + "(vyber Tape Loader + Enter, pak spusť pásku F6; zrychli F8)"
                        : $"ZX Spectrum 48K – páska {Path.GetFileName(path)} "
                          + "(napiš LOAD \"\"; turbo loadery spustíš páskou přes F6)";
                }
                else
                {
                    // snapshot může vyžadovat jiný model – v tom případě stroj přestavíme
                    bool want128 = Snapshot.DetectModel128(path);
                    if (want128 != is128) BuildMachine(want128);
                    spectrum.Rzx = null;
                    spectrum.LoadSnapshot(path);
                    Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – {Path.GetFileName(path)}";
                }
            }
            catch (Exception ex)
            {
                Title = $"ZX Spectrum – chyba načtení: {ex.Message}";
            }
        } 

        // ---------- ukládání / nahrávání snapshotů ----------
        /// <summary>Uloží stav jako nový .z80 do Dokumenty\snapshots (název hry + datum/čas).</summary>
        void SaveSnapshot()
        {
            try
            {
                Directory.CreateDirectory(SnapshotDir);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string file = $"{SanitizeFileName(currentGameName)}_{stamp}.z80";
                Snapshot.SaveZ80(spectrum, Path.Combine(SnapshotDir, file));
                Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – uloženo: {file}";
            }
            catch (Exception ex)
            {
                Title = $"ZX Spectrum – chyba uložení: {ex.Message}";
            }
        }

        /// <summary>Z názvu hry vyrobí platný název souboru (nahradí zakázané znaky).</summary>
        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Spectrum";
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Trim();
            return name.Length == 0 ? "Spectrum" : name;
        }

        /// <summary>Uloží aktuální obrazovku jako animovaný GIF imitující nahrávání z pásky.</summary>
        void SaveLoadingGif()
        {
            try
            {
                Directory.CreateDirectory(ScreenshotDir);
                // zkopírovat obsah obrazové banky (pixely + atributy), ať generujeme z konzistentního stavu
                var snap = new byte[6912];
                Array.Copy(spectrum.ScreenBank, snap, 6912);
                byte border = spectrum.Border;

                string file = $"{SanitizeFileName(currentGameName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.gif";
                string full = Path.Combine(ScreenshotDir, file);
                Title = $"ZX Spectrum – generuji GIF {file}…";

                // generování běží na pozadí (neblokuje smyčku snímků)
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        LoadingGif.Save(snap, border, full);
                        Dispatcher.UIThread.Post(() =>
                            Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – uložen GIF: {file}");
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => Title = $"ZX Spectrum – chyba GIF: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Title = $"ZX Spectrum – chyba GIF: {ex.Message}";
            }
        }

        void OpenSnapLoad()
        {
            helpOverlay.IsVisible = kbOverlay.IsVisible = false;
            ClearKeys();
            try
            {
                snapFiles = Directory.Exists(SnapshotDir)
                    ? Directory.GetFiles(SnapshotDir)
                        .Where(f =>
                        {
                            var e = Path.GetExtension(f).ToLowerInvariant();
                            return e == ".z80" || e == ".sna";
                        })
                        .OrderByDescending(File.GetLastWriteTime)
                        .ToList()
                    : new List<string>();
            }
            catch { snapFiles = new List<string>(); }

            snapIndex = 0;
            snapMenuActive = true;
            snapOverlay.IsVisible = true;
            UpdateSnapOverlay();
        }

        void CloseSnapLoad()
        {
            snapMenuActive = false;
            snapOverlay.IsVisible = false;
        }

        void UpdateSnapOverlay()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("NAHRÁT SNAPSHOT   (↑↓ výběr, Enter nahraje, Esc zavře)\n");
            sb.Append($"  aktuální hra: {currentGameName}\n\n");

            if (snapFiles.Count == 0)
            {
                sb.Append("  Žádné snapshoty. Ulož klávesou Home do:\n");
                sb.Append($"  {SnapshotDir}");
                snapText.Text = sb.ToString();
                return;
            }

            // posuvné okno se zvýrazněnou položkou
            const int window = 14;
            int from = Math.Max(0, Math.Min(snapIndex - window / 2, snapFiles.Count - window));
            int to = Math.Min(snapFiles.Count, from + window);
            if (from > 0) sb.Append("    ⋮\n");
            for (int i = from; i < to; i++)
            {
                string name = Path.GetFileName(snapFiles[i]);
                if (name.Length > 50) name = name[..49] + "…";
                string when = File.GetLastWriteTime(snapFiles[i]).ToString("yyyy-MM-dd HH:mm");
                sb.Append(i == snapIndex ? "  ► " : "    ");
                sb.Append($"{name,-51} {when}\n");
            }
            if (to < snapFiles.Count) sb.Append("    ⋮");
            snapText.Text = sb.ToString();
        }

        void LoadSelectedSnap()
        {
            if (snapFiles.Count == 0) { CloseSnapLoad(); return; }
            string path = snapFiles[snapIndex];
            CloseSnapLoad();
            LoadSnapshotFile(path, updateGameName: false); // ponech název aktuální hry
            // srovnat časování po skoku stavu
            lastTime = clock.Elapsed.TotalSeconds;
            tStateBudget = 0;
        }

        void LoadRzx(string path)
        {
            var player = RzxPlayer.Load(path);
            if (player.SnapshotExt != "z80")
                throw new NotSupportedException(
                    $"RZX se snapshotem .{player.SnapshotExt} (umím jen vložené .z80).");

            bool want128 = Snapshot.IsZ80_128(player.Snapshot);
            if (want128 != is128) BuildMachine(want128);

            Snapshot.LoadZ80(spectrum, player.Snapshot);
            spectrum.Rzx = player;
            player.Play();

            // srovnání časování
            lastTime = clock.Elapsed.TotalSeconds;
            tStateBudget = 0;

            Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – přehrávám RZX "
                  + $"({player.FrameCount} snímků)";
        }

        void ShowSpeed() =>
            Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – rychlost: {speed * 100:0} %";

        void ToggleModel()
        {
            customRomPath = null; // zpět na standardní ROM cílového modelu
            BuildMachine(!is128); // přepne 48K/128K a stroj resetuje
        }

        void ToggleFullscreen() =>
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal : WindowState.FullScreen;

        static Border MakeOverlayBox(Control child) => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = child
        };

        static Border MakeOverlay(string text) => MakeOverlayBox(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 14
        });

        // ---------- grafická mapa klávesnice ZX Spectra ----------
        static readonly IBrush KeyBg = new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x2c));
        static readonly IBrush KeyWhite = Brushes.White;
        static readonly IBrush KeyDim = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
        static readonly IBrush KeyRed = new SolidColorBrush(Color.FromRgb(0xF0, 0x40, 0x40));
        static readonly IBrush KeyGreen = new SolidColorBrush(Color.FromRgb(0x40, 0xC8, 0x55));

        static TextBlock Lbl(string t, double size, IBrush col, FontWeight w = FontWeight.Normal) => new()
        {
            Text = t, FontSize = size, Foreground = col, FontWeight = w,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };

        // Klávesa jako na Spectru:
        //  – velké písmeno vlevo, hlavní příkaz (bíle) hned vpravo od něj
        //  – zelený E-mode příkaz nad hlavním příkazem, symbol-shift (červeně) vpravo nahoře
        //  – červený E-mode příkaz dole
        static Control SpKey(string letter, string sym, string kw, string eg = "", string er = "")
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                RowDefinitions = new RowDefinitions("Auto,*,Auto")
            };

            if (!string.IsNullOrEmpty(eg)) // zelený E-mode (nad příkazem)
            {
                var g = Lbl(eg, 8, KeyGreen);
                g.HorizontalAlignment = HorizontalAlignment.Left;
                g.Margin = new Thickness(6, 2, 0, 0);
                Grid.SetRow(g, 0); Grid.SetColumn(g, 0); Grid.SetColumnSpan(g, 2);
                grid.Children.Add(g);
            }
            if (!string.IsNullOrEmpty(sym)) // symbol-shift vpravo nahoře (větší)
            {
                var s = Lbl(sym, 11, KeyRed);
                s.HorizontalAlignment = HorizontalAlignment.Right;
                s.Margin = new Thickness(0, 2, 5, 0);
                Grid.SetRow(s, 0); Grid.SetColumn(s, 1);
                grid.Children.Add(s);
            }

            var big = Lbl(letter, letter.Length > 1 ? 12 : 22, KeyWhite, FontWeight.Bold);
            big.HorizontalAlignment = HorizontalAlignment.Left;
            big.VerticalAlignment = VerticalAlignment.Center;
            big.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetRow(big, 1); Grid.SetColumn(big, 0);
            grid.Children.Add(big);

            if (!string.IsNullOrEmpty(kw)) // hlavní příkaz vpravo od písmena (větší)
            {
                var k = Lbl(kw, 9, KeyWhite);
                k.HorizontalAlignment = HorizontalAlignment.Left;
                k.VerticalAlignment = VerticalAlignment.Center;
                k.Margin = new Thickness(5, 0, 3, 0);
                Grid.SetRow(k, 1); Grid.SetColumn(k, 1);
                grid.Children.Add(k);
            }

            if (!string.IsNullOrEmpty(er)) // červený E-mode (dole)
            {
                var r = Lbl(er, 8, KeyRed);
                r.HorizontalAlignment = HorizontalAlignment.Left;
                r.Margin = new Thickness(6, 0, 0, 2);
                Grid.SetRow(r, 2); Grid.SetColumn(r, 0); Grid.SetColumnSpan(r, 2);
                grid.Children.Add(r);
            }

            return new Border
            {
                Width = 96, Height = 70, Margin = new Thickness(3),
                CornerRadius = new CornerRadius(7), Background = KeyBg, Child = grid
            };
        }

        // modifikátor / široká klávesa s prostým popiskem
        static Control SpWide(string label, double w)
        {
            var t = Lbl(label, 11, KeyWhite, FontWeight.Bold);
            t.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                Width = w, Height = 66, Margin = new Thickness(3),
                CornerRadius = new CornerRadius(7), Background = KeyBg, Child = t
            };
        }

        static StackPanel SpRow(params Control[] keys)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var k in keys) row.Children.Add(k);
            return row;
        }

        static Border MakeKeyboardOverlay()
        {
            var kb = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            kb.Children.Add(new TextBlock
            {
                Text = "KLÁVESNICE ZX SPECTRUM   (F11 zavře)",
                Foreground = KeyWhite, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 14, Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            kb.Children.Add(new TextBlock
            {
                Text = "písmeno + příkaz · zeleně/červeně E-mode (Extend) · vpravo nahoře symbol shift (Ctrl)",
                Foreground = KeyDim, FontSize = 10, Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // číselná řada: dole funkce Caps Shiftu, vpravo nahoře symbol-shift
            kb.Children.Add(SpRow(
                SpKey("1", "!", "EDIT"), SpKey("2", "@", "CAPS LOCK"), SpKey("3", "#", "TRUE VID"),
                SpKey("4", "$", "INV VID"), SpKey("5", "%", "←"), SpKey("6", "&", "↓"),
                SpKey("7", "'", "↑"), SpKey("8", "(", "→"), SpKey("9", ")", "GRAPHICS"),
                SpKey("0", "_", "DELETE")));

            kb.Children.Add(SpRow(
                SpKey("Q", "<=", "PLOT", "SIN", "ASN"), SpKey("W", "<>", "DRAW", "COS", "ACS"),
                SpKey("E", ">=", "REM", "TAN", "ATN"), SpKey("R", "<", "RUN", "INT", "VERIFY"),
                SpKey("T", ">", "RANDOMIZE", "RND", "MERGE"), SpKey("Y", "AND", "RETURN", "STR$", "["),
                SpKey("U", "OR", "IF", "CHR$", "]"), SpKey("I", "AT", "INPUT", "CODE", "IN"),
                SpKey("O", ";", "POKE", "PEEK", "OUT"), SpKey("P", "\"", "PRINT", "TAB", "©")));

            kb.Children.Add(SpRow(
                SpKey("A", "STOP", "NEW", "READ", "~"), SpKey("S", "NOT", "SAVE", "RESTORE", "|"),
                SpKey("D", "STEP", "DIM", "DATA", "\\"), SpKey("F", "TO", "FOR", "SGN", "{"),
                SpKey("G", "THEN", "GOTO", "ABS", "}"), SpKey("H", "↑", "GOSUB", "SQR", "CIRCLE"),
                SpKey("J", "-", "LOAD", "VAL", "VAL$"), SpKey("K", "+", "LIST", "LEN", "SCREEN$"),
                SpKey("L", "=", "LET", "USR", "ATTR"), SpWide("ENTER", 96)));

            kb.Children.Add(SpRow(
                SpWide("CAPS\nSHIFT", 78), SpKey("Z", ":", "COPY", "LN", "BEEP"),
                SpKey("X", "£", "CLEAR", "EXP", "INK"), SpKey("C", "?", "CONT", "LPRINT", "PAPER"),
                SpKey("V", "/", "CLS", "LLIST", "FLASH"), SpKey("B", "*", "BORDER", "BIN", "BRIGHT"),
                SpKey("N", ",", "NEXT", "INKEY$", "OVER"), SpKey("M", ".", "PAUSE", "PI", "INVERSE"),
                SpWide("SYMBOL\nSHIFT", 78), SpWide("BREAK / SPACE", 150)));

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 0, 0, 0)),
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = kb }
            };
        }

        void ToggleTape()
        {
            string model = is128 ? "128K" : "48K";
            if (spectrum.Tape == null)
            {
                Title = $"ZX Spectrum {model} – žádná páska (otevři ji klávesou F4)";
                return;
            }
            if (spectrum.TapePlaying)
            {
                spectrum.StopTape();
                Title = $"ZX Spectrum {model} – páska zastavena";
            }
            else
            {
                spectrum.PlayTape();
                Title = $"ZX Spectrum {model} – páska se přehrává…";
            }
        }

        async void OpenSnapshotDialog()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Otevřít snapshot nebo pásku",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("ZX Spectrum soubory")
                    {
                        Patterns = ["*.sna", "*.z80", "*.tzx", "*.tap", "*.rzx"]
                    },
                    FilePickerFileTypes.All
                ]
            });

            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path)) LoadSnapshotFile(path);
            }
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetFiles();
            if (files == null) return;
            foreach (var f in files)
            {
                var path = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path) && IsLoadable(path))
                {
                    LoadSnapshotFile(path);
                    break;
                }
            }
        }

        void Frame()
        {
            if (romMenuActive) // menu výběru ROM – stroj stojí, jen překreslujeme
            {
                lastTime = clock.Elapsed.TotalSeconds;
                Ula.Render(spectrum.ScreenBank, spectrum.Border, spectrum.FlashOn, pixels);
                using (var fb = bitmap.Lock())
                    Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
                image.InvalidateVisual();
                return;
            }

            if (paused) // pozastaveno – stroj neběží, jen držíme obraz
            {
                lastTime = clock.Elapsed.TotalSeconds;
                tStateBudget = 0;
                Ula.Render(spectrum.ScreenBank, spectrum.Border, spectrum.FlashOn, pixels);
                using (var fb = bitmap.Lock())
                    Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
                image.InvalidateVisual();
                return;
            }

            bool turbo = unlimited || (fastTape && (spectrum.TapePlaying || AutoActive));

            if (turbo)
            {
                // Maximální rychlost: proženeme co nejvíc snímků za daný rozpočet
                // času a vykreslíme až nakonec. UI zůstává responzivní (~50 Hz).
                // Zvuk se zde neprodukuje (zabránění zahlcení bufferu).
                var sw = Stopwatch.StartNew();
                int frames = 0;
                do
                {
                    RunMachineFrame();
                    frames++;
                } while (frames < 4000 && sw.ElapsedMilliseconds < 16
                         && (unlimited || spectrum.TapePlaying || AutoActive));

                // po zrychlení nedoháníme „dluh" reálného času
                lastTime = clock.Elapsed.TotalSeconds;
                tStateBudget = 0;
            }
            else
            {
                // Doženeme reálný uplynulý čas (× násobič rychlosti).
                double now = clock.Elapsed.TotalSeconds;
                double dt = now - lastTime;
                lastTime = now;
                tStateBudget += dt * spectrum.CpuHz * speed;

                // strop proti „spirále smrti", když stroj nestíhá
                int tpf = spectrum.TStatesPerFrame;
                double max = tpf * 8;
                if (tStateBudget > max) tStateBudget = max;

                int frames = 0;
                while (tStateBudget >= tpf && frames < 8)
                {
                    RunMachineFrame();
                    tStateBudget -= tpf;
                    frames++;
                    // zvuk jen při reálné rychlosti (jinak by se buffer rozsynchr.)
                    if (!muted && speed == 1.0)
                    {
                        int n = spectrum.Beeper.RenderFrame(audioBuf);
                        if (spectrum.EnableAy) spectrum.Ay.RenderFrameAdd(audioBuf, n);
                        audio.Push(audioBuf, n);
                    }
                }
            }

            Ula.Render(spectrum.ScreenBank, spectrum.Border, spectrum.FlashOn, pixels);
            using (var fb = bitmap.Lock())
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            image.InvalidateVisual();
        }

        // ---------- ROM ----------
        static readonly string[] Rom48Candidates =
            ["48.rom", "roms/48.rom", "roms/ZX_Spectrum_48k.rom"];
        static readonly string[] Rom128Candidates =
            ["128.rom", "roms/128.rom", "roms/ZX_Spectrum_128k.rom"];

        /// <summary>Najde ROM zadané délky v obvyklých cestách; vrací null, když není.</summary>
        static byte[] LoadRom(string[] names, int expectedLen, out bool testRom)
        {
            testRom = true;
            foreach (var name in names)
            {
                foreach (var path in new[] { name, Path.Combine(AppContext.BaseDirectory, name) })
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            var data = File.ReadAllBytes(path);
                            if (data.Length == expectedLen) { testRom = false; return data; }
                        }
                    }
                    catch (IOException) { }
                }
            }
            return null;
        }

        // ---------- startovní menu výběru ROM ----------
        /// <summary>Najde dostupné ROM (16K = 48K stroj, 32K = 128K stroj), max 10 pro volbu 0–9.</summary>
        static List<(string Path, string Name, bool Is128)> ScanRoms()
        {
            var found = new List<(string, string, bool)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseDir in new[] { ".", AppContext.BaseDirectory })
            foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "roms") })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.rom")
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var name = Path.GetFileName(f);
                        if (!seen.Add(name)) continue; // stejný název ve více cestách → první vyhrává
                        long len = new FileInfo(f).Length;
                        if (len == 16384) found.Add((f, name, false));
                        else if (len == 32768) found.Add((f, name, true));
                    }
                }
                catch (IOException) { }
            }
            return found.Take(10).ToList();
        }

        Border MakeRomOverlay()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("VÝBĚR ROM   (0–9 vybere, Esc = výchozí)\n\n");
            for (int i = 0; i < romList.Count; i++)
                sb.Append($"  {i}   {romList[i].Name,-34}{(romList[i].Is128 ? "128K" : "48K")}\n");
            return MakeOverlay(sb.ToString().TrimEnd('\n'));
        }

        /// <summary>Zavře menu; index mimo seznam (např. -1 při Esc) ponechá výchozí stroj.</summary>
        void SelectRom(int index)
        {
            romMenuActive = false;
            romOverlay.IsVisible = false;
            if (index >= 0 && index < romList.Count)
            {
                customRomPath = romList[index].Path;
                BuildMachine(romList[index].Is128);
            }
            // čas strávený v menu se nedohání
            lastTime = clock.Elapsed.TotalSeconds;
            tStateBudget = 0;
        }

        // ---------- autostart pásky (vkládání kláves) ----------
        /// <summary>Běží autostart (čekání na boot / psaní příkazu / start pásky)?</summary>
        bool AutoActive => autoHeld != null || autoTimer > 0 || autoSeq.Count > 0 || autoTapeAfter;

        void AutoClear()
        {
            autoSeq.Clear();
            autoHeld = null;
            autoTimer = 0;
            autoTapeAfter = false;
        }

        /// <summary>Jeden emulovaný snímek: krok fronty vkládaných kláves + běh stroje.</summary>
        void RunMachineFrame()
        {
            AutoKeysStep();
            spectrum.RunFrame();
        }

        void AutoKeysStep()
        {
            if (autoHeld != null || autoTimer > 0)
            {
                if (--autoTimer <= 0 && autoHeld != null)
                {
                    foreach (var (r, b) in autoHeld) spectrum.SetKey(r, b, false);
                    autoHeld = null;
                    autoTimer = 4; // mezera mezi stisky
                }
                return;
            }
            if (autoSeq.Count == 0)
            {
                if (autoTapeAfter)
                {
                    autoTapeAfter = false;
                    spectrum.PlayTape();
                }
                return;
            }
            var (keys, frames) = autoSeq.Dequeue();
            autoTimer = frames;
            if (keys != null)
            {
                autoHeld = keys;
                foreach (var (r, b) in keys) spectrum.SetKey(r, b, true);
            }
        }

        /// <summary>Resetuje stroj, nahraje pásku a sám ji spustí (LOAD "" / Tape Loader).</summary>
        void AutoRunTape(string path)
        {
            BuildMachine(is128); // čistý start (BuildMachine volá AutoClear)
            currentGameName = Path.GetFileNameWithoutExtension(path);
            spectrum.Rzx = null;
            spectrum.LoadTape(path);

            if (is128)
            {
                autoSeq.Enqueue((null, 160));               // čekání na boot menu
                autoSeq.Enqueue((new[] { (6, 0) }, 4));     // Enter → Tape Loader
            }
            else
            {
                autoSeq.Enqueue((null, 150));               // čekání na editor BASICu
                autoSeq.Enqueue((new[] { (6, 3) }, 4));     // J → LOAD
                autoSeq.Enqueue((new[] { (7, 1), (5, 0) }, 4)); // Sym Shift + P → "
                autoSeq.Enqueue((new[] { (7, 1), (5, 0) }, 4)); // Sym Shift + P → "
                autoSeq.Enqueue((new[] { (6, 0) }, 4));     // Enter
            }
            autoTapeAfter = true; // po napsání příkazu spustit pásku

            Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – {Path.GetFileName(path)} (autostart…)";
        }

        // ---------- hledání her na webu (Shift+F3) ----------
        void OpenWebSearch()
        {
            helpOverlay.IsVisible = kbOverlay.IsVisible = false;
            ClearKeys(); // pustit držené klávesy (Shift z Shift+F3)
            webMenuActive = true;
            webOverlay.IsVisible = true;
            UpdateWebOverlay();
        }

        void CloseWebSearch()
        {
            webMenuActive = false;
            webOverlay.IsVisible = false;
            webStatus = null;
        }

        /// <summary>Uvolní všechny klávesy i joystick emulovaného stroje.</summary>
        void ClearKeys()
        {
            for (int r = 0; r < 8; r++) spectrum.KeyRows[r] = 0xFF;
            spectrum.Kempston = 0;
        }

        void UpdateWebOverlay()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("HLEDÁNÍ HER NA WEBU – ZXDB   (Esc zavře)\n\n");
            sb.Append($"  Název: {webQuery}_\n");

            if (webBusy)
            {
                sb.Append($"\n  {webStatus}");
            }
            else if (webResults is { Count: > 0 })
            {
                sb.Append("\n  Spusť klávesou 0–9   (Backspace = nové hledání)\n\n");
                for (int i = 0; i < webResults.Count; i++)
                {
                    var r = webResults[i];
                    string title = r.Title.Length > 34 ? r.Title[..33] + "…" : r.Title;
                    string machine = r.Machine.Replace("ZX-Spectrum ", "");
                    sb.Append($"  {i}   {title,-36}{r.Year?.ToString() ?? "    "}  {machine}\n");
                }
                if (webStatus != null) sb.Append($"\n  {webStatus}");
            }
            else
            {
                sb.Append(webResults != null
                    ? "\n  Nic nenalezeno – uprav název a stiskni Enter."
                    : "\n  Napiš název hry a stiskni Enter.");
                if (webStatus != null) sb.Append($"\n  {webStatus}");
            }
            webText.Text = sb.ToString();
        }

        async void SearchWeb()
        {
            webBusy = true;
            webStatus = "Hledám…";
            UpdateWebOverlay();
            try
            {
                webResults = await WebGames.SearchAsync(webQuery.Trim());
                webStatus = null;
            }
            catch (Exception ex)
            {
                webResults = null;
                webStatus = "Chyba hledání: " + ex.Message;
            }
            webBusy = false;
            if (webMenuActive) UpdateWebOverlay();
        }

        async void RunWebGame(int index)
        {
            var entry = webResults[index];
            webBusy = true;
            webStatus = $"Stahuji {entry.Title}…";
            UpdateWebOverlay();
            try
            {
                string path = await WebGames.DownloadAsync(entry.Id, DownloadDir);
                webBusy = false;
                if (!webMenuActive) return; // uživatel mezitím zavřel Escapem
                CloseWebSearch();
                if (IsTape(path)) AutoRunTape(path); // pásku rovnou spustit (bez LOAD "")
                else LoadSnapshotFile(path);
                currentGameName = entry.Title; // pěkný název hry pro snapshoty
            }
            catch (Exception ex)
            {
                webBusy = false;
                webStatus = "Chyba stažení: " + ex.Message;
                if (webMenuActive) UpdateWebOverlay();
            }
        }

        // ---------- vkládání POKE (cheaty) – Insert ----------
        void OpenPoke()
        {
            helpOverlay.IsVisible = kbOverlay.IsVisible = false;
            ClearKeys();           // pustit držené klávesy
            pokeMenuActive = true;
            pokeOverlay.IsVisible = true;
            UpdatePokeOverlay();
        }

        void ClosePoke()
        {
            pokeMenuActive = false;
            pokeOverlay.IsVisible = false;
            pokeStatus = null;
        }

        void UpdatePokeOverlay()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("VLOŽIT POKE   (Esc zavře)\n\n");
            sb.Append("  Zadej: adresa,hodnota   (např. 23693,71)\n");
            sb.Append("  Enter = aplikovat, Backspace = mazat\n\n");
            sb.Append($"  POKE {pokeInput}_\n");

            if (appliedPokes.Count > 0)
            {
                sb.Append("\n  Použité:\n");
                int from = Math.Max(0, appliedPokes.Count - 8);
                for (int i = from; i < appliedPokes.Count; i++)
                    sb.Append($"    {appliedPokes[i].addr},{appliedPokes[i].val}\n");
            }
            if (pokeStatus != null) sb.Append($"\n  {pokeStatus}");
            pokeText.Text = sb.ToString();
        }

        /// <summary>Naparsuje "adresa,hodnota" (i s mezerou) a zapíše do paměti.</summary>
        void ApplyPoke()
        {
            string s = pokeInput.Trim();
            if (s.Length == 0) return;

            // oddělovač čárka, mezera nebo středník
            var parts = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out int addr)
                || !int.TryParse(parts[1], out int val))
            {
                pokeStatus = "Chybný formát – zadej adresa,hodnota.";
                UpdatePokeOverlay();
                return;
            }
            if (addr < 0 || addr > 65535 || val < 0 || val > 255)
            {
                pokeStatus = "Mimo rozsah (adresa 0–65535, hodnota 0–255).";
                UpdatePokeOverlay();
                return;
            }
            if (addr < 16384)
            {
                pokeStatus = "Adresa je v ROM (0–16383) – zápis ignorován.";
                UpdatePokeOverlay();
                return;
            }

            spectrum.Poke((ushort)addr, (byte)val);
            appliedPokes.Add(((ushort)addr, (byte)val));
            pokeStatus = $"POKE {addr},{val} – hotovo.";
            pokeInput = "";
            UpdatePokeOverlay();
        }

        // ---------- Kempston joystick ----------
        // bit0 vpravo, 1 vlevo, 2 dolů, 3 nahoru, 4 fire (aktivní v 1)
        static readonly Dictionary<Key, int> KempstonMap = new()
        {
            [Key.Right] = 0, [Key.Left] = 1, [Key.Down] = 2, [Key.Up] = 3, [Key.Space] = 4,
        };

        // ---------- klávesnice ----------
        // (řádek matice, bit) – řádky odpovídají adresním linkám A8–A15
        static readonly Dictionary<Key, (int row, int bit)[]> KeyMap = new()
        {
            // půlřádek 0 (0xFEFE): Caps Shift, Z, X, C, V
            [Key.LeftShift] = [(0, 0)],
            [Key.RightShift] = [(0, 0)],
            [Key.Z] = [(0, 1)], [Key.X] = [(0, 2)],
            [Key.C] = [(0, 3)], [Key.V] = [(0, 4)],
            // půlřádek 1 (0xFDFE): A, S, D, F, G
            [Key.A] = [(1, 0)], [Key.S] = [(1, 1)],
            [Key.D] = [(1, 2)], [Key.F] = [(1, 3)], [Key.G] = [(1, 4)],
            // půlřádek 2 (0xFBFE): Q, W, E, R, T
            [Key.Q] = [(2, 0)], [Key.W] = [(2, 1)],
            [Key.E] = [(2, 2)], [Key.R] = [(2, 3)], [Key.T] = [(2, 4)],
            // půlřádek 3 (0xF7FE): 1–5
            [Key.D1] = [(3, 0)], [Key.D2] = [(3, 1)],
            [Key.D3] = [(3, 2)], [Key.D4] = [(3, 3)], [Key.D5] = [(3, 4)],
            // půlřádek 4 (0xEFFE): 0, 9, 8, 7, 6
            [Key.D0] = [(4, 0)], [Key.D9] = [(4, 1)],
            [Key.D8] = [(4, 2)], [Key.D7] = [(4, 3)], [Key.D6] = [(4, 4)],
            // půlřádek 5 (0xDFFE): P, O, I, U, Y
            [Key.P] = [(5, 0)], [Key.O] = [(5, 1)],
            [Key.I] = [(5, 2)], [Key.U] = [(5, 3)], [Key.Y] = [(5, 4)],
            // půlřádek 6 (0xBFFE): Enter, L, K, J, H
            [Key.Enter] = [(6, 0)],
            [Key.L] = [(6, 1)], [Key.K] = [(6, 2)],
            [Key.J] = [(6, 3)], [Key.H] = [(6, 4)],
            // půlřádek 7 (0x7FFE): Space, Symbol Shift, M, N, B
            [Key.Space] = [(7, 0)],
            [Key.LeftCtrl] = [(7, 1)],
            [Key.RightCtrl] = [(7, 1)],
            [Key.M] = [(7, 2)], [Key.N] = [(7, 3)], [Key.B] = [(7, 4)],
            // pohodlné kombinace
            [Key.Back] = [(0, 0), (4, 0)],   // Caps Shift + 0  (DELETE)
            [Key.Left] = [(0, 0), (3, 4)],   // Caps Shift + 5
            [Key.Down] = [(0, 0), (4, 4)],   // Caps Shift + 6
            [Key.Up] = [(0, 0), (4, 3)],     // Caps Shift + 7
            [Key.Right] = [(0, 0), (4, 2)],  // Caps Shift + 8
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (romMenuActive) // startovní menu ROM má přednost a polyká vše
            {
                int idx = -1;
                if (e.Key >= Key.D0 && e.Key <= Key.D9) idx = e.Key - Key.D0;
                else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) idx = e.Key - Key.NumPad0;
                if (idx >= 0 && idx < romList.Count) SelectRom(idx);
                else if (e.Key == Key.Escape) SelectRom(-1); // výchozí stroj
                e.Handled = true;
                return;
            }
            if (webMenuActive) // hledání her na webu
            {
                // Pozor: e.Handled smí dostat jen konkrétní klávesy – zpracovaný
                // KeyDown by potlačil TextInput a nešlo by psát název hry.
                // Ostatní klávesy projdou, ale return zabrání jejich vstupu do Spectra.
                if (e.Key == Key.Escape)
                {
                    CloseWebSearch();
                    e.Handled = true;
                }
                else if (!webBusy && webResults is { Count: > 0 })
                {
                    int idx = -1;
                    if (e.Key >= Key.D0 && e.Key <= Key.D9) idx = e.Key - Key.D0;
                    else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) idx = e.Key - Key.NumPad0;

                    if (idx >= 0 && idx < webResults.Count)
                    {
                        RunWebGame(idx);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Back) // zpět na zadání názvu
                    {
                        webResults = null;
                        webStatus = null;
                        UpdateWebOverlay();
                        e.Handled = true;
                    }
                }
                else if (!webBusy && e.Key == Key.Enter && webQuery.Trim().Length > 0)
                {
                    SearchWeb();
                    e.Handled = true;
                }
                else if (!webBusy && e.Key == Key.Back && webQuery.Length > 0)
                {
                    webQuery = webQuery[..^1];
                    UpdateWebOverlay();
                    e.Handled = true;
                }
                return;
            }
            if (pokeMenuActive) // vkládání POKE
            {
                // jen konkrétní klávesy nastaví Handled (jinak by se potlačil TextInput)
                if (e.Key == Key.Escape)
                {
                    ClosePoke();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    ApplyPoke();
                    e.Handled = true;
                }
                else if (e.Key == Key.Back && pokeInput.Length > 0)
                {
                    pokeInput = pokeInput[..^1];
                    UpdatePokeOverlay();
                    e.Handled = true;
                }
                return;
            }
            if (snapMenuActive) // výběr snapshotu k nahrání
            {
                if (e.Key == Key.Escape) CloseSnapLoad();
                else if (e.Key == Key.Up) { if (snapIndex > 0) snapIndex--; UpdateSnapOverlay(); }
                else if (e.Key == Key.Down) { if (snapIndex < snapFiles.Count - 1) snapIndex++; UpdateSnapOverlay(); }
                else if (e.Key == Key.Enter) LoadSelectedSnap();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Insert) // vložit POKE (cheat)
            {
                OpenPoke();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Home) // uložit snapshot (nový soubor)
            {
                SaveSnapshot();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.End) // nahrát snapshot (výběr šipkami)
            {
                OpenSnapLoad();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.PrintScreen) // GIF – akce až na KeyUp (PrintScreen na Windows často nedá KeyDown)
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Pause) // pozastavit / spustit emulátor
            {
                paused = !paused;
                if (paused) ClearKeys(); // pustit držené klávesy, ať se „nezaseknou"
                Title = paused
                    ? $"ZX Spectrum {(is128 ? "128K" : "48K")} – POZASTAVENO (Pause spustí)"
                    : $"ZX Spectrum {(is128 ? "128K" : "48K")} – běží";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F1) // nápověda – ovládání emulátoru
            {
                helpOverlay.IsVisible = !helpOverlay.IsVisible;
                kbOverlay.IsVisible = false;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11) // mapa kláves ZX Spectrum
            {
                kbOverlay.IsVisible = !kbOverlay.IsVisible;
                helpOverlay.IsVisible = false;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F12) // fullscreen
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F2) // přepnout model 48K / 128K (resetuje stroj)
            {
                ToggleModel();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F3) // otevřít soubor; Shift+F3 = hledat hru na webu
            {
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0) OpenWebSearch();
                else OpenSnapshotDialog();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F4) // AY (Melodik) na 48K – zapnout/vypnout
            {
                ay48 = !ay48;
                if (is128)
                {
                    Title = "ZX Spectrum 128K – AY je vždy zapnutý (volba platí pro 48K)";
                }
                else
                {
                    spectrum.EnableAy = ay48;
                    Title = ay48
                        ? "ZX Spectrum 48K – AY zvuk (Melodik) ZAPNUT"
                        : "ZX Spectrum 48K – AY zvuk VYPNUT";
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5) // ztlumit / obnovit zvuk
            {
                muted = !muted;
                Title = muted
                    ? "ZX Spectrum 48K – zvuk ZTLUMEN"
                    : (audio.Enabled
                        ? "ZX Spectrum 48K – zvuk zapnut"
                        : "ZX Spectrum 48K – zvukové zařízení nedostupné");
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F6) // páska: přehrát / zastavit
            {
                ToggleTape();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F8) // přepnout rychlé (pulzní) nahrávání
            {
                fastTape = !fastTape;
                Title = fastTape
                    ? "ZX Spectrum 48K – rychlé nahrávání ZAPNUTO"
                    : "ZX Spectrum 48K – rychlé nahrávání VYPNUTO (reálný čas)";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F9) // přepnout okamžité nahrávání (ROM trap)
            {
                spectrum.InstantTapeLoad = !spectrum.InstantTapeLoad;
                Title = spectrum.InstantTapeLoad
                    ? "ZX Spectrum 48K – okamžité nahrávání ZAPNUTO (standardní bloky)"
                    : "ZX Spectrum 48K – okamžité nahrávání VYPNUTO";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F7) // přepnout neomezenou rychlost
            {
                unlimited = !unlimited;
                Title = unlimited
                    ? "ZX Spectrum – rychlost: MAX (neomezeně)"
                    : "ZX Spectrum – rychlost: 100 %";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F10) // reset stroje
            {
                BuildMachine(is128);
                Title = $"ZX Spectrum {(is128 ? "128K" : "48K")} – reset";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0) // Alt+Enter: fullscreen
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) // Esc: ven z fullscreenu
            {
                WindowState = WindowState.Normal;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Add)      // num + : rychleji
            {
                unlimited = false;
                speed = Math.Min(8.0, speed * 1.25);
                ShowSpeed();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Subtract) // num - : pomaleji
            {
                unlimited = false;
                speed = Math.Max(0.1, speed / 1.25);
                ShowSpeed();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Multiply) // num * : rychlost 100 %
            {
                speed = 1.0; unlimited = false;
                ShowSpeed();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Tab) // přepnout šipky+Space mezi joystickem a klávesnicí
            {
                joystick = !joystick;
                spectrum.Kempston = 0; // zahoď případně „držené" směry
                Title = joystick
                    ? "ZX Spectrum – Kempston joystick (šipky + Space = fire)"
                    : "ZX Spectrum – šipky = kurzor, Space = mezerník";
                e.Handled = true;
                return;
            }
            // Kempston: šipky + Space řídí joystick (a nejdou do klávesnice)
            if (joystick && KempstonMap.TryGetValue(e.Key, out int jbit))
            {
                spectrum.SetJoystick(jbit, true);
                e.Handled = true;
                return;
            }
            if (KeyMap.TryGetValue(e.Key, out var keys))
            {
                foreach (var (row, bit) in keys) spectrum.SetKey(row, bit, true);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.PrintScreen) // uložit obrazovku jako „nahrávací" GIF
            {
                SaveLoadingGif();
                e.Handled = true;
                return;
            }
            if (joystick && KempstonMap.TryGetValue(e.Key, out int jbit))
            {
                spectrum.SetJoystick(jbit, false);
                e.Handled = true;
                return;
            }
            if (KeyMap.TryGetValue(e.Key, out var keys))
            {
                foreach (var (row, bit) in keys) spectrum.SetKey(row, bit, false);
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            // psaní názvu hry v overlay hledání (dokud nejsou výsledky)
            if (webMenuActive && !webBusy && (webResults == null || webResults.Count == 0)
                && e.Text != null)
            {
                foreach (char c in e.Text)
                    if (!char.IsControl(c) && webQuery.Length < 40) webQuery += c;
                UpdateWebOverlay();
                e.Handled = true;
                return;
            }
            // psaní POKE (povoleny jen číslice, čárka, mezera a středník)
            if (pokeMenuActive && e.Text != null)
            {
                foreach (char c in e.Text)
                    if ((char.IsDigit(c) || c == ',' || c == ' ' || c == ';') && pokeInput.Length < 16)
                        pokeInput += c;
                UpdatePokeOverlay();
                e.Handled = true;
                return;
            }
            base.OnTextInput(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            timer.Stop();
            audio.Dispose();
            base.OnClosed(e);
        }
    }
}
