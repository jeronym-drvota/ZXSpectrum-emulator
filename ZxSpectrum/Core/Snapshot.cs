using System;
using System.IO;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// Načítání snapshotů ZX Spectra ve formátech .SNA a .Z80 (48K i 128K).
    /// Obnoví registry procesoru, obsah RAM, stránkování (128K) a barvu okraje.
    /// </summary>
    public static class Snapshot
    {
        /// <summary>Zjistí, zda snapshot vyžaduje model 128K (jinak 48K).</summary>
        public static bool DetectModel128(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var d = File.ReadAllBytes(path);
            if (ext == ".sna") return d.Length > 49179;        // 48K SNA = 49179 B
            if (ext == ".z80") return IsZ80_128(d);
            return false;
        }

        /// <summary>Rozpozná z bajtů .z80 snapshotu, zda jde o model 128K.</summary>
        public static bool IsZ80_128(byte[] d)
        {
            if (d.Length < 36) return false;
            ushort pc = (ushort)(d[6] | (d[7] << 8));
            if (pc != 0) return false;                         // verze 1 = vždy 48K
            int extraLen = d[30] | (d[31] << 8);
            int hw = d[34];
            return extraLen == 23 ? hw >= 3 : hw >= 4;
        }

        public static void Load(Spectrum spec, string path)
        {
            var data = File.ReadAllBytes(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".sna": LoadSna(spec, data); break;
                case ".z80": LoadZ80(spec, data); break;
                default:
                    throw new NotSupportedException(
                        "Nepodporovaný formát snapshotu (čekám .sna nebo .z80).");
            }
        }

        // =================== SNA ===================
        public static void LoadSna(Spectrum spec, byte[] d)
        {
            if (d.Length < 27 + 49152)
                throw new InvalidDataException("Neplatný .SNA soubor.");

            var c = spec.Cpu;
            c.I = d[0];
            c.L2 = d[1];  c.H2 = d[2];
            c.E2 = d[3];  c.D2 = d[4];
            c.C2 = d[5];  c.B2 = d[6];
            c.F2 = d[7];  c.A2 = d[8];
            c.L  = d[9];  c.H  = d[10];
            c.E  = d[11]; c.D  = d[12];
            c.C  = d[13]; c.B  = d[14];
            c.IY = (ushort)(d[15] | (d[16] << 8));
            c.IX = (ushort)(d[17] | (d[18] << 8));
            c.IFF2 = (d[19] & 0x04) != 0;
            c.IFF1 = c.IFF2;
            c.R  = d[20];
            c.F  = d[21]; c.A = d[22];
            c.SP = (ushort)(d[23] | (d[24] << 8));
            c.IM = d[25] & 0x03;
            spec.Border = (byte)(d[26] & 7);
            c.Halted = false;

            bool is128 = d.Length > 49179 && spec.Is128;

            if (!is128)
            {
                // 48K: 49152 B RAM, PC se vyzvedne ze zásobníku
                var ram = new byte[49152];
                Array.Copy(d, 27, ram, 0, 49152);
                spec.LoadRam48(ram);
                c.PC = (ushort)(spec.Peek(c.SP) | (spec.Peek((ushort)(c.SP + 1)) << 8));
                c.SP += 2;
                return;
            }

            // 128K: bank5, bank2, právě stránkovaná banka, pak PC/port, pak zbytek bank
            int p = 27;
            Array.Copy(d, p, spec.Ram[5], 0, 16384); p += 16384;
            Array.Copy(d, p, spec.Ram[2], 0, 16384); p += 16384;
            int pagedBankOffset = p; p += 16384;

            ushort pc = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
            byte port = d[p]; p += 1;
            p += 1; // TR-DOS příznak (ignorujeme)

            int pagedBank = port & 7;
            Array.Copy(d, pagedBankOffset, spec.Ram[pagedBank], 0, 16384);

            for (int b = 0; b < 8 && p + 16384 <= d.Length; b++)
            {
                if (b == 5 || b == 2 || b == pagedBank) continue;
                Array.Copy(d, p, spec.Ram[b], 0, 16384); p += 16384;
            }

            c.PC = pc;
            spec.ForcePaging(port);
        }

        // =================== Z80 (v1 / v2 / v3) ===================
        public static void LoadZ80(Spectrum spec, byte[] d)
        {
            if (d.Length < 30)
                throw new InvalidDataException("Neplatný .Z80 soubor.");

            var c = spec.Cpu;
            c.A = d[0]; c.F = d[1];
            c.C = d[2]; c.B = d[3];
            c.L = d[4]; c.H = d[5];
            ushort pc = (ushort)(d[6] | (d[7] << 8));
            c.SP = (ushort)(d[8] | (d[9] << 8));
            c.I = d[10];

            byte b12 = d[12];
            if (b12 == 0xFF) b12 = 1;
            c.R = (byte)((d[11] & 0x7F) | ((b12 & 1) << 7));
            spec.Border = (byte)((b12 >> 1) & 7);
            bool compressed = (b12 & 0x20) != 0;

            c.E  = d[13]; c.D  = d[14];
            c.C2 = d[15]; c.B2 = d[16];
            c.E2 = d[17]; c.D2 = d[18];
            c.L2 = d[19]; c.H2 = d[20];
            c.A2 = d[21]; c.F2 = d[22];
            c.IY = (ushort)(d[23] | (d[24] << 8));
            c.IX = (ushort)(d[25] | (d[26] << 8));
            c.IFF1 = d[27] != 0;
            c.IFF2 = d[28] != 0;
            c.IM = d[29] & 0x03;
            c.Halted = false;

            if (pc != 0)
            {
                // ---- Verze 1: jeden blok 48K RAM od 0x4000 ----
                c.PC = pc;
                var ram = new byte[49152];
                if (compressed) Decompress(d, 30, d.Length - 30, ram, 0, 49152);
                else Array.Copy(d, 30, ram, 0, Math.Min(49152, d.Length - 30));
                spec.LoadRam48(ram);
                return;
            }

            // ---- Verze 2/3 ----
            int extraLen = d[30] | (d[31] << 8);
            c.PC = (ushort)(d[32] | (d[33] << 8));
            int hw = d[34];
            bool is128 = spec.Is128;

            if (is128)
            {
                spec.ForcePaging(d[35]);          // poslední zápis na 0x7FFD
                // AY registry (verze 3 a delší rozšířená hlavička)
                if (extraLen >= 23 && 39 + 16 <= d.Length)
                {
                    for (int i = 0; i < 16; i++) { spec.Ay.Select(i); spec.Ay.Write(d[39 + i]); }
                    spec.Ay.Select(d[38]);
                }
            }

            int p = 32 + extraLen;
            while (p + 3 <= d.Length)
            {
                int len = d[p] | (d[p + 1] << 8);
                int page = d[p + 2];
                p += 3;

                int bank = BankForPage(page, is128);
                byte[] dst = bank >= 0 ? spec.Ram[bank] : null;

                if (len == 0xFFFF) // nekomprimováno, 16384 B
                {
                    if (dst != null && p + 16384 <= d.Length) Array.Copy(d, p, dst, 0, 16384);
                    p += 16384;
                }
                else
                {
                    if (dst != null) Decompress(d, p, len, dst, 0, 16384);
                    p += len;
                }
            }
        }

        // mapování čísla stránky Z80 na fyzickou RAM banku
        static int BankForPage(int page, bool is128)
        {
            if (is128) return (page >= 3 && page <= 10) ? page - 3 : -1;
            return page switch { 8 => 5, 4 => 2, 5 => 0, _ => -1 };
        }

        static void Decompress(byte[] src, int srcStart, int srcLen,
                               byte[] dst, int dstStart, int dstMax)
        {
            int s = srcStart;
            int sEnd = Math.Min(srcStart + srcLen, src.Length);
            int o = dstStart;
            int oEnd = dstStart + dstMax;

            while (s < sEnd && o < oEnd)
            {
                byte b = src[s];
                if (b == 0xED && s + 1 < sEnd && src[s + 1] == 0xED)
                {
                    int count = src[s + 2];
                    byte val = src[s + 3];
                    s += 4;
                    for (int i = 0; i < count && o < oEnd; i++) dst[o++] = val;
                }
                else { dst[o++] = b; s++; }
            }
        }
    }
}
