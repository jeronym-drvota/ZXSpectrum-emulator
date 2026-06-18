using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZxSpectrum
{
    /// <summary>
    /// Hledání a stahování her z webu. Dva zdroje:
    ///  • ZXDB (api.zxinfo.dk) – světový archiv, soubory na Spectrum Computing / archive.org.
    ///  • zx-spectrum.cz – český web (víc českých her a Didaktik verzí), ZIP přes s.php.
    /// Výsledky obou zdrojů se slučují do jednoho seznamu.
    /// </summary>
    public static class WebGames
    {
        public enum Source { Zxdb, ZxSpectrumCz }

        public record Entry(string Id, string Title, string Machine, int? Year, Source Source = Source.Zxdb);

        const string ApiBase = "https://api.zxinfo.dk/v3";
        const string CzBase = "https://www.zx-spectrum.cz";

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            // windows-1250 (čeština na zx-spectrum.cz) – v .NET Core nutno doregistrovat
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            // weby vyžadují identifikaci klienta (jinak hrozí blokace jako crawler)
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZxSpectrumEmulator/1.0");
            return c;
        }

        // ============================================================
        //  Vyhledávání – sloučení obou zdrojů
        // ============================================================

        /// <summary>Vyhledá hry v obou zdrojích naráz a vrátí sloučený seznam (max 10).</summary>
        public static async Task<List<Entry>> SearchAsync(string query)
        {
            var zxdb = Safe(SearchZxdbAsync(query));
            var cz = Safe(SearchCzAsync(query));
            await Task.WhenAll(zxdb, cz);

            var a = zxdb.Result; var b = cz.Result;
            // když oba zdroje selžou, vyhoď chybu (jinak ber, co je)
            if (a.Error != null && b.Error != null)
                throw new InvalidOperationException(a.Error.Message);

            return Merge(a.List, b.List, 100);
        }

        // proložení obou seznamů (aby byly oba zdroje vidět), oříznuté na max prvků
        static List<Entry> Merge(List<Entry> a, List<Entry> b, int max)
        {
            var res = new List<Entry>();
            int i = 0;
            while ((i < a.Count || i < b.Count) && res.Count < max)
            {
                if (i < a.Count) res.Add(a[i]);
                if (res.Count < max && i < b.Count) res.Add(b[i]);
                i++;
            }
            return res;
        }

        readonly record struct Result(List<Entry> List, Exception Error);

        // obal – zachytí chybu jednoho zdroje, aby nezhasil druhý
        static async Task<Result> Safe(Task<List<Entry>> task)
        {
            try { return new Result(await task, null); }
            catch (Exception ex) { return new Result(new List<Entry>(), ex); }
        }

        /// <summary>Stáhne hru podle zdroje a vrátí cestu k souboru pro nahrání.</summary>
        public static Task<string> DownloadAsync(Entry entry, string downloadDir) =>
            entry.Source == Source.ZxSpectrumCz
                ? DownloadCzAsync(entry.Id, downloadDir)
                : DownloadZxdbAsync(entry.Id, downloadDir);

        // ============================================================
        //  ZXDB (api.zxinfo.dk)
        // ============================================================

        static async Task<List<Entry>> SearchZxdbAsync(string query)
        {
            string url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}"
                       + "&mode=compact&size=60&offset=0&sort=rel_desc"
                       + "&contenttype=SOFTWARE&machinetype=ZXSPECTRUM&availability=Available";

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var list = new List<Entry>();
            foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
            {
                var src = hit.GetProperty("_source");
                list.Add(new Entry(
                    hit.GetProperty("_id").GetString(),
                    src.TryGetProperty("title", out var t) ? t.GetString() : "?",
                    src.TryGetProperty("machineType", out var m) ? m.GetString() ?? "" : "",
                    src.TryGetProperty("originalYearOfRelease", out var y)
                        && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                    Source.Zxdb));
            }
            return list;
        }

        static async Task<string> DownloadZxdbAsync(string id, string downloadDir)
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ApiBase}/games/{id}?mode=full"));
            var src = doc.RootElement.GetProperty("_source");

            var paths = new List<string>();
            if (src.TryGetProperty("releases", out var rels))
                foreach (var rel in rels.EnumerateArray())
                    if (rel.TryGetProperty("files", out var files))
                        foreach (var f in files.EnumerateArray())
                            if (f.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } s)
                                paths.Add(s);

            string pick = PickBest(paths)
                ?? throw new InvalidOperationException("hra nemá stažitelný soubor (.z80 .sna .tap .tzx)");

            Directory.CreateDirectory(downloadDir);
            string local = Path.Combine(downloadDir, Path.GetFileName(pick));
            if (!File.Exists(local))
                await File.WriteAllBytesAsync(local, await Http.GetByteArrayAsync(ToUrl(pick)));

            return local.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Unzip(local, downloadDir)
                : local;
        }

        /// <summary>Převod cesty z databáze na absolutní URL (stejně jako oficiální klienti ZXInfo).</summary>
        static string ToUrl(string path)
        {
            path = path.Replace("+", "%2B").Replace(" ", "%20");
            if (path.StartsWith("/pub/sinclair/"))
                return "https://archive.org/download/World_of_Spectrum_June_2017_Mirror/"
                     + "World%20of%20Spectrum%20June%202017%20Mirror.zip/"
                     + "World%20of%20Spectrum%20June%202017%20Mirror/sinclair/"
                     + path["/pub/sinclair/".Length..];
            return "https://spectrumcomputing.co.uk" + path;
        }

        // ============================================================
        //  zx-spectrum.cz (scraping HTML)
        // ============================================================

        // stažovací odkaz hry: …s.php?id=NÁZEV.zip  (zbytek řádku nese tooltip s formátem)
        static readonly Regex CzDownload = new(
            @"s\.php\?id=([^""&]+?)\.zip", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // viditelný název hry v odkazu na detail (…game_id=…">NÁZEV</a>)
        static readonly Regex CzTitle = new(
            @"<a\s[^>]*href=""[^""]*game_id=[^""]*""[^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex CzTip = new(@"title=""([^""]*)""", RegexOptions.IgnoreCase);
        // formáty, které umíme nahrát; disketové (TRD/DSK/SCL/TR-DOS) přeskočíme
        static readonly Regex CzSupported = new(@"\b(TAP|TZX|Z80|SNA)\b", RegexOptions.IgnoreCase);
        static readonly Regex CzUnsupported = new(@"\b(TRD|DSK|SCL|TR-?DOS)\b", RegexOptions.IgnoreCase);
        static readonly Regex CzMachine = new(@"Spectrum\s*(\d+)\s*K", RegexOptions.IgnoreCase);

        static async Task<List<Entry>> SearchCzAsync(string query)
        {
            string url = $"{CzBase}/index.php?cat1=4&cat2=6&cgam=1"
                       + $"&search_string={Uri.EscapeDataString(query)}";
            string html = await GetCzStringAsync(url);

            var titles = CzTitle.Matches(html);
            var list = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match d in CzDownload.Matches(html))
            {
                string id = d.Groups[1].Value;

                // tooltip hledáme v okně hned za odkazem (nese „… TAP verze pro Spectrum 48K")
                string win = html.Substring(d.Index, Math.Min(300, html.Length - d.Index));
                var tipM = CzTip.Match(win);
                string tip = tipM.Success ? tipM.Groups[1].Value : "";

                // známe-li formát a je to disketa, přeskoč; jinak necháme rozhodnout rozbalení
                if (tip.Length > 0 && CzUnsupported.IsMatch(tip) && !CzSupported.IsMatch(tip))
                    continue;
                if (!seen.Add(id)) continue;                        // bez duplicit

                // nejbližší předchozí název hry = řádek tohoto odkazu
                string title = null;
                foreach (Match ti in titles)
                {
                    if (ti.Index >= d.Index) break;
                    title = ti.Groups[1].Value;
                }
                title = CleanTitle(title) ?? id;

                var mm = CzMachine.Match(tip);
                string machine = mm.Success ? mm.Groups[1].Value + "K" : "";

                list.Add(new Entry(id, title, machine, null, Source.ZxSpectrumCz));
                if (list.Count >= 60) break;
            }
            return list;
        }

        // ZIP přes s.php → vybalit podporovaný soubor
        static async Task<string> DownloadCzAsync(string id, string downloadDir)
        {
            string url = $"{CzBase}/data/games/files/s.php?id={Uri.EscapeDataString(id)}.zip";
            Directory.CreateDirectory(downloadDir);
            string local = Path.Combine(downloadDir, id + ".zip");
            if (!File.Exists(local))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri($"{CzBase}/");   // s.php kontroluje referer
                using var resp = await Http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(local, await resp.Content.ReadAsByteArrayAsync());
            }
            return Unzip(local, downloadDir);
        }

        static async Task<string> GetCzStringAsync(string url)
        {
            byte[] bytes = await Http.GetByteArrayAsync(url);
            return Encoding.GetEncoding(1250).GetString(bytes); // čeština = windows-1250
        }

        static string CleanTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = System.Net.WebUtility.HtmlDecode(s).Trim();
            s = Regex.Replace(s, @"\s*\(\d+\)\s*$", "");          // useknout počet (např. „ (18)")
            return s.Length == 0 ? null : s;
        }

        // ============================================================
        //  Společné – výběr a rozbalení souboru
        // ============================================================

        // snapshoty se spustí rovnou, pásky vyžadují LOAD "" – proto pořadí
        static readonly string[] ExtPriority = [".z80", ".sna", ".tap", ".tzx"];

        static string StripZip(string p) =>
            p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;

        static int Rank(string name) =>
            Array.IndexOf(ExtPriority, Path.GetExtension(StripZip(name)).ToLowerInvariant());

        static string PickBest(IEnumerable<string> paths) =>
            paths.Where(p => Rank(p) >= 0).OrderBy(Rank).FirstOrDefault();

        /// <summary>Vybalí ze ZIPu nejvhodnější podporovaný soubor a vrátí jeho cestu.</summary>
        static string Unzip(string zipPath, string dir)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.Where(e => Rank(e.Name) >= 0).OrderBy(e => Rank(e.Name)).FirstOrDefault()
                ?? throw new InvalidOperationException("ZIP neobsahuje podporovaný soubor");
            string outPath = Path.Combine(dir, entry.Name);
            if (!File.Exists(outPath)) entry.ExtractToFile(outPath);
            return outPath;
        }
    }
}
