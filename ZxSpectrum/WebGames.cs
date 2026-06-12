using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ZxSpectrum
{
    /// <summary>
    /// Hledání a stahování her z webového archivu ZXDB (api.zxinfo.dk).
    /// Soubory hostuje Spectrum Computing a archive.org (mirror World of Spectrum).
    /// </summary>
    public static class WebGames
    {
        public record Entry(string Id, string Title, string Machine, int? Year);

        const string ApiBase = "https://api.zxinfo.dk/v3";

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            // API vyžaduje identifikaci klienta (jinak hrozí blokace jako crawler)
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZxSpectrumEmulator/1.0");
            return c;
        }

        /// <summary>Vyhledá hry podle názvu – max 10 výsledků, jen dostupné ke stažení.</summary>
        public static async Task<List<Entry>> SearchAsync(string query)
        {
            string url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}"
                       + "&mode=compact&size=10&offset=0&sort=rel_desc"
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
                        && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null));
            }
            return list;
        }

        /// <summary>
        /// Vybere nejvhodnější soubor hry (snapshot před páskou), stáhne ho do
        /// <paramref name="downloadDir"/> (cache – co už tam je, se nestahuje),
        /// případný ZIP rozbalí a vrátí cestu k souboru pro nahrání.
        /// </summary>
        public static async Task<string> DownloadAsync(string id, string downloadDir)
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

        // snapshoty se spustí rovnou, pásky vyžadují LOAD "" – proto pořadí
        static readonly string[] ExtPriority = [".z80", ".sna", ".tap", ".tzx"];

        static string StripZip(string p) =>
            p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;

        static int Rank(string name) =>
            Array.IndexOf(ExtPriority, Path.GetExtension(StripZip(name)).ToLowerInvariant());

        static string PickBest(IEnumerable<string> paths) =>
            paths.Where(p => Rank(p) >= 0).OrderBy(Rank).FirstOrDefault();

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
