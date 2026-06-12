using System;
using System.Collections.Generic;
using System.IO;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// Páska ve formátu TZX (i TAP). Rozbalí se na posloupnost bloků; každý blok
    /// drží své pulzy (délky v T-stavech) a u standardních bloků navíc syrové
    /// bajty (flag … data … kontrolní součet), aby je šlo nahrát „okamžitě"
    /// odchycením ROM rutiny LD-BYTES místo emulace pulzů.
    ///
    /// Přehrávač podle počtu T-stavů procesoru vrací úroveň vstupu EAR (bit 6
    /// portu 0xFE), kterou čtou turbo / vlastní zavaděče. Předpokládá 3,5 MHz.
    /// </summary>
    public sealed class TzxTape
    {
        // standardní časování ROM zavaděče (T-stavy)
        const int PilotPulse = 2168;
        const int PilotHeader = 8063;
        const int PilotData = 3223;
        const int Sync1 = 667;
        const int Sync2 = 735;
        const int Bit0 = 855;
        const int Bit1 = 1710;
        const int TPerMs = 3500;

        sealed class Block
        {
            public int[] Pulses;   // rozbalené pulzy bloku
            public byte[] Data;    // syrové bajty (flag..checksum), nebo null u nestandardních
        }

        readonly List<Block> blocks = new();

        // stav pulzního přehrávače
        int bi;          // index aktuálního bloku
        int pi;          // index pulzu v rámci bloku
        long endT;       // absolutní T-stav, kdy končí aktuální pulz
        bool level;      // aktuální úroveň (false = nízká)
        public bool Playing { get; private set; }

        /// <summary>True, pokud další blok ke čtení je standardní (lze instant-load).</summary>
        public bool AtStandardBlock => bi < blocks.Count && blocks[bi].Data != null;

        public int PulseCount
        {
            get { int n = 0; foreach (var b in blocks) n += b.Pulses.Length; return n; }
        }

        // ---------- načtení ----------
        public static TzxTape Load(string path)
        {
            var data = File.ReadAllBytes(path);
            var tape = new TzxTape();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tap") tape.ParseTap(data);
            else tape.ParseTzx(data);
            tape.Flush();
            if (tape.PulseCount == 0)
                throw new InvalidDataException("Páska neobsahuje žádná přehratelná data.");
            return tape;
        }

        // ---------- pulzní přehrávání ----------
        public void Play(long nowT)
        {
            if (blocks.Count == 0) return;
            ArmFrom(bi, nowT);
            Playing = bi < blocks.Count;
        }

        public void Stop() => Playing = false;

        void ArmFrom(int block, long nowT)
        {
            bi = block;
            pi = 0;
            level = false;
            while (bi < blocks.Count && blocks[bi].Pulses.Length == 0) bi++;
            if (bi < blocks.Count) endT = nowT + blocks[bi].Pulses[0];
        }

        /// <summary>Úroveň EAR (true = vysoká). Po dohrání zůstává vysoká.</summary>
        public bool Ear(long nowT)
        {
            if (!Playing) return true;
            while (nowT >= endT)
            {
                pi++;
                if (pi >= blocks[bi].Pulses.Length)
                {
                    bi++; pi = 0;
                    while (bi < blocks.Count && blocks[bi].Pulses.Length == 0) bi++;
                    if (bi >= blocks.Count) { Playing = false; return true; }
                }
                level = !level;
                endT += blocks[bi].Pulses[pi];
            }
            return level;
        }

        /// <summary>
        /// Vrátí syrové bajty dalšího standardního bloku a posune se na další blok
        /// (pro instant-load). Vrací null, pokud další blok není standardní.
        /// </summary>
        public byte[] NextStandardBlock(long nowT)
        {
            if (bi >= blocks.Count || blocks[bi].Data == null) return null;
            var data = blocks[bi].Data;
            int next = bi + 1;
            if (Playing) ArmFrom(next, nowT); // sesouhlasit pulzy na další blok
            else bi = next;
            return data;
        }

        // ---------- sestavování bloků ----------
        List<int> cur;       // pulzy právě stavěného bloku
        byte[] curData;

        void Flush()
        {
            if (cur != null)
            {
                blocks.Add(new Block { Pulses = cur.ToArray(), Data = curData });
                cur = null; curData = null;
            }
        }

        void NewBlock(byte[] data) { Flush(); cur = new List<int>(); curData = data; }

        void EnsureBlock() { if (cur == null) { cur = new List<int>(); curData = null; } }

        void Add(int len) { EnsureBlock(); cur.Add(len); }
        void AddMany(int count, int len) { EnsureBlock(); for (int i = 0; i < count; i++) cur.Add(len); }
        void AddPause(int ms) { if (ms > 0) Add(ms * TPerMs); }

        void AddData(byte[] d, int start, int len, int zero, int one, int usedBitsLast)
        {
            EnsureBlock();
            for (int n = 0; n < len; n++)
            {
                byte b = d[start + n];
                int bits = (n == len - 1) ? usedBitsLast : 8;
                for (int bit = 7; bit >= 8 - bits; bit--)
                {
                    int pl = ((b >> bit) & 1) != 0 ? one : zero;
                    cur.Add(pl); cur.Add(pl);
                }
            }
        }

        // standardní blok – nový blok se syrovými bajty + rozbalené pulzy
        void AddStandard(byte[] d, int start, int len, int pauseMs)
        {
            var raw = new byte[len];
            Array.Copy(d, start, raw, 0, len);
            NewBlock(raw);
            int pilot = d[start] < 128 ? PilotHeader : PilotData;
            AddMany(pilot, PilotPulse);
            Add(Sync1); Add(Sync2);
            AddData(d, start, len, Bit0, Bit1, 8);
            AddPause(pauseMs);
        }

        // ---------- TAP ----------
        void ParseTap(byte[] d)
        {
            int i = 0;
            while (i + 2 <= d.Length)
            {
                int len = d[i] | (d[i + 1] << 8);
                i += 2;
                if (len == 0 || i + len > d.Length) break;
                AddStandard(d, i, len, 1000);
                i += len;
            }
        }

        // ---------- TZX ----------
        void ParseTzx(byte[] d)
        {
            if (d.Length < 10 || d[0] != 'Z' || d[1] != 'X' || d[2] != 'T')
                throw new InvalidDataException("Neplatná hlavička TZX.");

            int i = 10;
            int loopStart = -1, loopCount = 0;
            long guard = 0;
            const int MaxPulses = 60_000_000;

            while (i < d.Length && PulseCount < MaxPulses && guard++ < 5_000_000)
            {
                int id = d[i++];
                switch (id)
                {
                    case 0x10: // standardní rychlost
                    {
                        if (i + 4 > d.Length) return;
                        int pause = U16(d, i), len = U16(d, i + 2);
                        i += 4;
                        if (i + len > d.Length) return;
                        AddStandard(d, i, len, pause);
                        i += len;
                        break;
                    }
                    case 0x11: // turbo (nestandardní)
                    {
                        if (i + 18 > d.Length) return;
                        int pp = U16(d, i), s1 = U16(d, i + 2), s2 = U16(d, i + 4);
                        int zero = U16(d, i + 6), one = U16(d, i + 8), pl = U16(d, i + 10);
                        int ub = d[i + 12], pause = U16(d, i + 13), len = U24(d, i + 15);
                        i += 18;
                        if (i + len > d.Length) return;
                        NewBlock(null);
                        AddMany(pl, pp); Add(s1); Add(s2);
                        AddData(d, i, len, zero, one, ub == 0 ? 8 : ub);
                        AddPause(pause);
                        i += len;
                        break;
                    }
                    case 0x12: // čistý tón
                    {
                        if (i + 4 > d.Length) return;
                        int pulseLen = U16(d, i), num = U16(d, i + 2);
                        i += 4;
                        NewBlock(null); AddMany(num, pulseLen);
                        break;
                    }
                    case 0x13: // sekvence pulzů
                    {
                        if (i + 1 > d.Length) return;
                        int num = d[i++];
                        if (i + num * 2 > d.Length) return;
                        NewBlock(null);
                        for (int k = 0; k < num; k++) Add(U16(d, i + k * 2));
                        i += num * 2;
                        break;
                    }
                    case 0x14: // čistá data (nestandardní)
                    {
                        if (i + 10 > d.Length) return;
                        int zero = U16(d, i), one = U16(d, i + 2);
                        int ub = d[i + 4], pause = U16(d, i + 5), len = U24(d, i + 7);
                        i += 10;
                        if (i + len > d.Length) return;
                        NewBlock(null);
                        AddData(d, i, len, zero, one, ub == 0 ? 8 : ub);
                        AddPause(pause);
                        i += len;
                        break;
                    }
                    case 0x15: // přímý záznam – přeskočit
                    {
                        if (i + 8 > d.Length) return;
                        i += 8 + U24(d, i + 5);
                        break;
                    }
                    case 0x20: AddPause(U16(d, i)); i += 2; break;                 // pauza (k aktuálnímu bloku)
                    case 0x21: i += 1 + (i < d.Length ? d[i] : 0); break;          // group start
                    case 0x22: break;                                              // group end
                    case 0x23: i += 2; break;                                      // jump (ignorujeme)
                    case 0x24: if (i + 2 > d.Length) return; loopCount = U16(d, i); i += 2; loopStart = i; break;
                    case 0x25: if (loopCount > 1 && loopStart >= 0) { loopCount--; i = loopStart; } break;
                    case 0x26: { if (i + 2 > d.Length) return; i += 2 + U16(d, i) * 2; break; }
                    case 0x27: break;
                    case 0x28: { if (i + 2 > d.Length) return; i += 2 + U16(d, i); break; }
                    case 0x2A: i += 4; break;
                    case 0x2B: i += 5; break;
                    case 0x30: i += 1 + (i < d.Length ? d[i] : 0); break;
                    case 0x31: if (i + 2 > d.Length) return; i += 2 + d[i + 1]; break;
                    case 0x32: if (i + 2 > d.Length) return; i += 2 + U16(d, i); break;
                    case 0x33: if (i + 1 > d.Length) return; i += 1 + d[i] * 3; break;
                    case 0x35: if (i + 20 > d.Length) return; i += 20 + (int)U32(d, i + 16); break;
                    case 0x5A: i += 9; break;
                    case 0x18:
                    case 0x19: if (i + 4 > d.Length) return; i += 4 + (int)U32(d, i); break;
                    default: return; // neznámý blok bez známé délky
                }
            }
        }

        static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        static int U24(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);
        static uint U32(byte[] d, int o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    }
}
