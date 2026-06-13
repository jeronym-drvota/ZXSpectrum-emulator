using System;
using System.Collections.Generic;
using System.IO;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// Uloží aktuální obrazovku jako animovaný GIF, který vypadá jako nahrávání
    /// z pásky na ZX Spectru: blikající červeno-azurové pruhy v borderu a obraz
    /// odkrývaný v autentickém prokládaném pořadí (nejdřív monochromaticky pixely
    /// v pořadí adres obrazové paměti, pak doskáčou barvy z atributů).
    ///
    /// Obsahuje vlastní enkodér GIF89a + LZW (žádná externí závislost).
    /// </summary>
    public static class LoadingGif
    {
        const int W = 320, H = 240;     // základní rozlišení (border 32×24 + obraz 256×192)
        const int BX = 32, BY = 24;
        const int Red = 2, Cyan = 5;

        // ZX paleta jako RGB (shodná s Ula.Palette, jen BGRA → RGB)
        static readonly byte[][] Pal =
        {
            new byte[]{0x00,0x00,0x00}, new byte[]{0x00,0x00,0xD7}, new byte[]{0xD7,0x00,0x00}, new byte[]{0xD7,0x00,0xD7},
            new byte[]{0x00,0xD7,0x00}, new byte[]{0x00,0xD7,0xD7}, new byte[]{0xD7,0xD7,0x00}, new byte[]{0xD7,0xD7,0xD7},
            new byte[]{0x00,0x00,0x00}, new byte[]{0x00,0x00,0xFF}, new byte[]{0xFF,0x00,0x00}, new byte[]{0xFF,0x00,0xFF},
            new byte[]{0x00,0xFF,0x00}, new byte[]{0x00,0xFF,0xFF}, new byte[]{0xFF,0xFF,0x00}, new byte[]{0xFF,0xFF,0xFF},
        };

        /// <summary>
        /// Vygeneruje a uloží GIF. <paramref name="screen"/> je 16K obrazová banka
        /// (pixely 0x0000–0x17FF, atributy 0x1800–0x1AFF), <paramref name="border"/>
        /// je barva borderu pro finální snímek, <paramref name="scale"/> zvětšení.
        /// </summary>
        public static void Save(byte[] screen, byte border, string path, int scale = 2)
        {
            var frames = new List<byte[]>();
            var delays = new List<int>();
            int sw = W * scale, sh = H * scale;
            int phase = 0;

            // 1) pilot tón – tlusté scrollující pruhy, obraz černý
            for (int i = 0; i < 12; i++)
            {
                frames.Add(Scale(BuildFrame(screen, 0, 0, phase, 0, border), scale));
                delays.Add(4); phase += 3;
            }
            // 2) nahrávání pixelů (6144 B) v pořadí adres → prokládaný efekt
            const int pf = 48;
            for (int i = 1; i <= pf; i++)
            {
                int n = 6144 * i / pf;
                frames.Add(Scale(BuildFrame(screen, n, 0, phase, 1, border), scale));
                delays.Add(3); phase++;
            }
            // 3) nahrávání atributů (768 B) → barvy doskáčou odshora
            const int af = 18;
            for (int i = 1; i <= af; i++)
            {
                int m = 768 * i / af;
                frames.Add(Scale(BuildFrame(screen, 6144, m, phase, 1, border), scale));
                delays.Add(3); phase++;
            }
            // 4) finále – hotový barevný obraz, plný border, delší podržení
            frames.Add(Scale(BuildFrame(screen, 6144, 768, phase, 2, border), scale));
            delays.Add(220);

            WriteGif(path, frames, sw, sh, delays);
        }

        // ---------- sestavení jednoho snímku (v základním rozlišení) ----------
        // n = kolik pixelových bajtů je „nahráno", m = kolik atributů, mode 0/1/2 = pilot/data/finále
        static byte[] BuildFrame(byte[] screen, int n, int m, int phase, int mode, byte border)
        {
            var fr = new byte[W * H];
            for (int py = 0; py < H; py++)
            {
                bool inY = py >= BY && py < BY + 192;
                int sy = py - BY;
                int baseAddr = inY ? (((sy & 0xC0) << 5) | ((sy & 0x07) << 8) | ((sy & 0x38) << 2)) : 0;
                int rowOff = py * W;
                byte bidx = BorderIndex(py, phase, mode, border);

                for (int px = 0; px < W; px++)
                {
                    byte idx;
                    if (inY && px >= BX && px < BX + 256)
                    {
                        int sx = px - BX;
                        int col = sx >> 3;
                        int off = baseAddr + col;
                        if (off >= n) idx = 0;                 // ještě nenahráno → černá
                        else
                        {
                            byte bits = screen[off];
                            bool on = ((bits >> (7 - (sx & 7))) & 1) != 0;
                            int ab = (sy >> 3) * 32 + col;
                            int ink, paper;
                            if (ab < m)                        // atribut už nahrán → skutečné barvy
                            {
                                byte a = screen[0x1800 + ab];
                                ink = a & 7; paper = (a >> 3) & 7;
                                if ((a & 0x40) != 0) { ink += 8; paper += 8; }
                            }
                            else { ink = 15; paper = 0; }      // zatím monochromaticky (bílá na černé)
                            idx = (byte)(on ? ink : paper);
                        }
                    }
                    else idx = bidx;                           // border

                    fr[rowOff + px] = idx;
                }
            }
            return fr;
        }

        static byte BorderIndex(int py, int phase, int mode, byte border)
        {
            if (mode == 0)   // pilot: tlusté scrollující pruhy
                return (byte)(((((py + phase) >> 3) & 1) == 0) ? Red : Cyan);
            if (mode == 1)   // data: jemný rychle blikající šum
            {
                uint h;
                unchecked { h = ((uint)py * 2246822519u) ^ ((uint)phase * 3266489917u); }
                return (byte)((h & 0x100) != 0 ? Red : Cyan);
            }
            return (byte)(border & 7); // finále: plný border
        }

        // nejbližší soused – zvětšení snímku
        static byte[] Scale(byte[] fr, int s)
        {
            if (s == 1) return fr;
            int nw = W * s, nh = H * s;
            var outp = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int srow = (y / s) * W;
                int drow = y * nw;
                for (int x = 0; x < nw; x++) outp[drow + x] = fr[srow + x / s];
            }
            return outp;
        }

        // ---------- zápis GIF89a ----------
        static void WriteGif(string path, List<byte[]> frames, int w, int h, List<int> delays)
        {
            const int minCode = 4; // 16 barev
            var o = new List<byte>(64 * 1024);
            o.AddRange(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
            Le16(o, w); Le16(o, h);
            o.Add(0xF0 | 3); o.Add(0); o.Add(0);         // globální tabulka barev, velikost 3 → 16
            for (int i = 0; i < 16; i++) { o.Add(Pal[i][0]); o.Add(Pal[i][1]); o.Add(Pal[i][2]); }
            // NETSCAPE2.0 – nekonečná smyčka
            o.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
            o.AddRange(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            o.AddRange(new byte[] { 0x03, 0x01, 0, 0, 0x00 });

            for (int f = 0; f < frames.Count; f++)
            {
                o.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x04 }); // GCE, disposal = 1
                Le16(o, delays[f]); o.Add(0); o.Add(0);
                o.Add(0x2C); Le16(o, 0); Le16(o, 0); Le16(o, w); Le16(o, h); o.Add(0);
                o.Add(minCode);
                Lzw(o, frames[f], minCode);
            }
            o.Add(0x3B);
            File.WriteAllBytes(path, o.ToArray());
        }

        static void Le16(List<byte> o, int v) { o.Add((byte)(v & 0xFF)); o.Add((byte)((v >> 8) & 0xFF)); }

        // ---------- LZW (varianta GIF; změna délky kódu při nextc == 2^size + 1) ----------
        static void Lzw(List<byte> outp, byte[] idx, int minCode)
        {
            int clear = 1 << minCode, eoi = clear + 1, size = minCode + 1;
            var bw = new BitWriter(outp);
            var table = new Dictionary<int, int>();
            bw.Write(clear, size);
            int next = eoi + 1;
            int w = idx[0];
            for (int i = 1; i < idx.Length; i++)
            {
                int k = idx[i];
                int key = (w << 8) | k;
                if (table.TryGetValue(key, out int c)) w = c;
                else
                {
                    bw.Write(w, size);
                    table[key] = next++;
                    if (next == (1 << size) + 1)
                    {
                        if (size < 12) size++;
                        else { bw.Write(clear, size); table.Clear(); size = minCode + 1; next = eoi + 1; }
                    }
                    w = k;
                }
            }
            bw.Write(w, size);
            bw.Write(eoi, size);
            bw.Finish();
        }

        /// <summary>Zápis kódů LSB-first do GIF sub-bloků (max 255 bajtů na blok).</summary>
        sealed class BitWriter
        {
            readonly List<byte> outp;
            readonly byte[] block = new byte[255];
            int blockLen, buffer, bits;

            public BitWriter(List<byte> outp) => this.outp = outp;

            public void Write(int code, int size)
            {
                buffer |= code << bits;
                bits += size;
                while (bits >= 8)
                {
                    block[blockLen++] = (byte)(buffer & 0xFF);
                    buffer >>= 8; bits -= 8;
                    if (blockLen == 255) FlushBlock();
                }
            }

            void FlushBlock()
            {
                if (blockLen == 0) return;
                outp.Add((byte)blockLen);
                for (int i = 0; i < blockLen; i++) outp.Add(block[i]);
                blockLen = 0;
            }

            public void Finish()
            {
                if (bits > 0)
                {
                    block[blockLen++] = (byte)(buffer & 0xFF);
                    buffer = 0; bits = 0;
                    if (blockLen == 255) FlushBlock();
                }
                FlushBlock();
                outp.Add(0); // terminátor dat snímku
            }
        }
    }
}
