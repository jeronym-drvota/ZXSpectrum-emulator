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
            public string Desc;    // lidský popis bloku (pro prohlížeč pásky)
        }

        readonly List<Block> blocks = new();

        // stav pulzního přehrávače
        int bi;          // index aktuálního bloku
        int pi;          // index pulzu v rámci bloku
        long tapeT;      // vlastní hodiny pásky (posouvají se jen při nahrávání)
        long endT;       // konec aktuálního pulzu v hodinách pásky
        bool level;      // aktuální úroveň (false = nízká)
        long lastEarT;   // T-stav procesoru při posledním čtení EAR
        int tightRun;    // počet po sobě jdoucích „těsných" čtení EAR
        public bool Playing { get; private set; }

        /// <summary>
        /// True, jen když páska právě skutečně dodává data (konzumují se pulzy).
        /// Přepne na false, jakmile zavaděč přestane těsně číst EAR (běží hra,
        /// pauza mezi bloky). Slouží k zapnutí „turbo" rychlosti jen po dobu
        /// nahrávání – jinak by hra po nahrání běžela mnohonásobně zrychleně,
        /// protože páska zůstává „Playing" kvůli pokračování dalším levelem.
        /// </summary>
        public bool Loading { get; private set; }

        // Páska má vlastní hodiny (tapeT), které se posouvají JEN když zavaděč
        // skutečně nahrává. Nahrávání poznáme podle souvislé série těsných čtení
        // EAR: zavaděč čte EAR v těsné smyčce mnohokrát za sebou (mezery desítky
        // až stovky T-stavů), kdežto hra ULA port čte jen v krátké dávce (sken
        // klávesnice = pár půlřádků) a pak dlouho nic (~snímek, 70000 T).
        //
        // Mez IdleGap = největší mezera, která se ještě počítá jako „těsné" čtení.
        // MinTightRun = kolik těsných čtení po sobě musí přijít, než to uznáme za
        // nahrávání (víc než sken klávesnice), aby dávka kláves při hraní pásku
        // nerozjela. Bez toho by se páska během hraní přetočila za blok dalšího
        // levelu (a ten by se nenahrál) a/nebo by hra po nahrání běžela zrychleně,
        // protože páska zůstává „Playing" kvůli pokračování dalším levelem.
        const long IdleGap = 10000;
        const int MinTightRun = 32;

        /// <summary>True, pokud další blok ke čtení je standardní (lze instant-load).</summary>
        public bool AtStandardBlock => bi < blocks.Count && blocks[bi].Data != null;

        // ---------- prohlížeč bloků ----------
        /// <summary>Počet bloků na pásce.</summary>
        public int BlockCount => blocks.Count;

        /// <summary>Index bloku, kde je teď ukazatel pásky (== BlockCount po dohrání).</summary>
        public int CurrentBlock => Math.Min(bi, blocks.Count);

        /// <summary>Lidský popis bloku pro výpis.</summary>
        public string BlockDesc(int i) =>
            i >= 0 && i < blocks.Count ? (blocks[i].Desc ?? "Blok") : "";

        /// <summary>Přesune ukazatel pásky na zvolený blok (přehrávání i instant-load).</summary>
        public void SeekTo(int block, long nowT)
        {
            if (blocks.Count == 0) return;
            if (block < 0) block = 0;
            if (block >= blocks.Count) block = blocks.Count - 1;
            ArmFrom(block, nowT);   // nastaví bi/pi/endT i hodiny pásky
        }

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

        public void Stop() { Playing = false; Loading = false; }

        void ArmFrom(int block, long nowT)
        {
            bi = block;
            pi = 0;
            level = false;
            tapeT = 0;
            while (bi < blocks.Count && blocks[bi].Pulses.Length == 0) bi++;
            if (bi < blocks.Count) endT = blocks[bi].Pulses[0];
            lastEarT = nowT;
            tightRun = 0;
            Loading = false; // teprve souvislé nahrávání to potvrdí
        }

        /// <summary>Úroveň EAR (true = vysoká). Po dohrání i při pozastavení zůstává držená.</summary>
        public bool Ear(long nowT)
        {
            if (!Playing) return true;

            // Posun vlastních hodin pásky jen při souvislém těsném čtení (= nahrávání).
            // Krátká dávka (sken klávesnice za hraní) ani dlouhá pauza páskou nehnou.
            long gap = nowT - lastEarT;
            lastEarT = nowT;
            tightRun = (gap >= 0 && gap <= IdleGap) ? tightRun + 1 : 0;

            if (tightRun < MinTightRun) { Loading = false; return level; }
            Loading = true;

            tapeT += gap;
            while (tapeT >= endT)
            {
                pi++;
                if (pi >= blocks[bi].Pulses.Length)
                {
                    bi++; pi = 0;
                    while (bi < blocks.Count && blocks[bi].Pulses.Length == 0) bi++;
                    if (bi >= blocks.Count) { Playing = false; Loading = false; return true; }
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
        string curDesc;

        void Flush()
        {
            if (cur != null)
            {
                blocks.Add(new Block { Pulses = cur.ToArray(), Data = curData, Desc = curDesc });
                cur = null; curData = null; curDesc = null;
            }
        }

        void NewBlock(byte[] data, string desc) { Flush(); cur = new List<int>(); curData = data; curDesc = desc; }

        void EnsureBlock() { if (cur == null) { cur = new List<int>(); curData = null; curDesc = null; } }

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
            NewBlock(raw, StdDesc(raw));
            int pilot = d[start] < 128 ? PilotHeader : PilotData;
            AddMany(pilot, PilotPulse);
            Add(Sync1); Add(Sync2);
            AddData(d, start, len, Bit0, Bit1, 8);
            AddPause(pauseMs);
        }

        // popis standardního bloku: z hlavičky vytáhne typ a název, jinak „data"
        static string StdDesc(byte[] raw)
        {
            if (raw.Length >= 19 && raw[0] == 0x00) // hlavička
            {
                string name = "";
                for (int k = 2; k < 12 && k < raw.Length; k++)
                    name += raw[k] >= 32 && raw[k] < 127 ? (char)raw[k] : ' ';
                name = name.Trim();
                string typ = raw[1] switch
                {
                    0 => "program",
                    1 => "pole čísel",
                    2 => "pole znaků",
                    3 => "kód",
                    _ => "hlavička"
                };
                return name.Length > 0 ? $"Hlavička – {typ} „{name}\"" : $"Hlavička – {typ}";
            }
            int payload = raw.Length >= 2 ? raw.Length - 2 : raw.Length; // bez flagu a kontrolního součtu
            return $"Data ({payload} B)";
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
                        NewBlock(null, $"Turbo data ({len} B)");
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
                        NewBlock(null, "Čistý tón"); AddMany(num, pulseLen);
                        break;
                    }
                    case 0x13: // sekvence pulzů
                    {
                        if (i + 1 > d.Length) return;
                        int num = d[i++];
                        if (i + num * 2 > d.Length) return;
                        NewBlock(null, "Sekvence pulzů");
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
                        NewBlock(null, $"Čistá data ({len} B)");
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
