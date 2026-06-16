using System;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// ZX Spectrum 48K i 128K. Paměť je rozdělená na banky (8× 16K RAM + 1 nebo
    /// 2× 16K ROM), adresní prostor tvoří 4 stránky po 16K. U 128K se stránkuje
    /// přes port 0x7FFD a je k dispozici zvukový čip AY-3-8912 (porty 0xFFFD/0xBFFD).
    /// </summary>
    public sealed class Spectrum : IBus
    {
        public bool Is128 { get; }
        public int TStatesPerFrame { get; }   // 69888 (48K) / 70908 (128K)
        public double CpuHz { get; }           // 3,5 MHz / 3,5469 MHz

        public readonly byte[][] Ram = new byte[8][];
        readonly byte[][] Rom;                 // [0] 48K, nebo [0]=editor,[1]=48K BASIC
        readonly byte[][] page = new byte[4][]; // aktuální mapování čtení (4× 16K)

        public readonly Z80 Cpu;
        public byte Border;
        public int FrameCount;

        // stránkování 128K
        int bankC000, screenBankIdx = 5, romIndex;
        bool pagingLocked;
        public byte Last7FFD { get; private set; }

        /// <summary>Matice klávesnice: 8 půlřádků, bity 0–4 = klávesy (0 = stisknuto).</summary>
        public readonly byte[] KeyRows = new byte[8];

        /// <summary>Stav Kempston joysticku (bit0 R, 1 L, 2 D, 3 U, 4 Fire; 1 = stisknuto).</summary>
        public byte Kempston;

        public TzxTape Tape;

        /// <summary>Přehrávaný RZX záznam (deterministické vstupy), nebo null.</summary>
        public RzxPlayer Rzx;

        public bool InstantTapeLoad = true;
        public bool HasStandardRom;
        public int InstantLoadCount;
        const ushort LdBytes = 0x0556;

        public readonly Beeper Beeper;
        public readonly Ay Ay;

        /// <summary>Aktivní AY čip. Na 128K vždy, na 48K volitelně (rozhraní Melodik).</summary>
        public bool EnableAy;

        long nextFrameT;

        /// <summary>Aktivní obrazová banka (5 = normální, 7 = stínová).</summary>
        public byte[] ScreenBank => Ram[screenBankIdx];

        public Spectrum(byte[] rom, bool is128)
        {
            Is128 = is128;
            TStatesPerFrame = is128 ? 70908 : 69888;
            CpuHz = is128 ? 3546900.0 : 3500000.0;

            for (int i = 0; i < 8; i++) Ram[i] = new byte[16384];

            if (is128)
            {
                if (rom == null || rom.Length != 32768)
                    throw new ArgumentException("128K ROM musí mít přesně 32768 bajtů.");
                Rom = new[] { new byte[16384], new byte[16384] };
                Array.Copy(rom, 0, Rom[0], 0, 16384);
                Array.Copy(rom, 16384, Rom[1], 0, 16384);
            }
            else
            {
                if (rom == null || rom.Length != 16384)
                    throw new ArgumentException("48K ROM musí mít přesně 16384 bajtů.");
                Rom = new[] { (byte[])rom.Clone() };
            }

            for (int i = 0; i < 8; i++) KeyRows[i] = 0xFF;

            romIndex = 0; bankC000 = 0; screenBankIdx = 5;
            pagingLocked = !is128; // 48K = stránkování trvale uzamčeno
            RemapPages();

            Beeper = new Beeper(TStatesPerFrame, 882);
            // AY má vlastní krystal ~1,7734 MHz (stejný na 128K i u rozhraní Melodik),
            // takže 48K i 128K hrají stejnou výšku.
            const double AyClock = 1773400.0;
            Ay = new Ay((AyClock / 16.0) / 44100.0);
            EnableAy = is128; // na 128K vždy, na 48K volitelně (Melodik)

            Cpu = new Z80(this);
            nextFrameT = TStatesPerFrame;
        }

        void RemapPages()
        {
            page[0] = Rom[Is128 ? romIndex : 0];
            page[1] = Ram[5];
            page[2] = Ram[2];
            page[3] = Ram[bankC000];
        }

        // ---------- IBus / paměť ----------
        public byte Read(ushort addr) => page[addr >> 14][addr & 0x3FFF];

        public void Write(ushort addr, byte value)
        {
            int slot = addr >> 14;
            if (slot == 0) return;            // stránka 0 = ROM (jen pro čtení)
            page[slot][addr & 0x3FFF] = value;
        }

        // pro snapshoty / trap (Peek umí číst i ROM, Poke do ROM nezapíše)
        public byte Peek(ushort addr) => page[addr >> 14][addr & 0x3FFF];
        public void Poke(ushort addr, byte value) => Write(addr, value);

        /// <summary>Zápis na port 0x7FFD (respektuje zámek stránkování).</summary>
        public void SetPaging(byte v)
        {
            if (!Is128) return;
            Last7FFD = v;
            if (pagingLocked) return;
            bankC000 = v & 7;
            screenBankIdx = (v & 0x08) != 0 ? 7 : 5;
            romIndex = (v & 0x10) != 0 ? 1 : 0;
            if ((v & 0x20) != 0) pagingLocked = true;
            RemapPages();
        }

        /// <summary>Nastaví stránkování ze snapshotu (ignoruje zámek).</summary>
        public void ForcePaging(byte v)
        {
            Last7FFD = v;
            if (!Is128) return;
            bankC000 = v & 7;
            screenBankIdx = (v & 0x08) != 0 ? 7 : 5;
            romIndex = (v & 0x10) != 0 ? 1 : 0;
            pagingLocked = (v & 0x20) != 0;
            RemapPages();
        }

        /// <summary>Nahraje 48K obraz paměti (0x4000–0xFFFF) do bank 5, 2 a 0.</summary>
        public void LoadRam48(byte[] ram48)
        {
            Array.Copy(ram48, 0, Ram[5], 0, 16384);
            Array.Copy(ram48, 16384, Ram[2], 0, 16384);
            Array.Copy(ram48, 32768, Ram[0], 0, 16384);
        }

        public byte In(ushort port)
        {
            // při přehrávání RZX jdou všechny vstupy ze záznamu (deterministicky)
            if (Rzx != null && Rzx.Playing) return Rzx.NextIn();

            if ((port & 1) == 0) // ULA port (sudé porty)
            {
                byte res = 0xFF;
                int high = port >> 8;
                for (int row = 0; row < 8; row++)
                    if ((high & (1 << row)) == 0)
                        res &= KeyRows[row];
                if (Tape != null && !Tape.Ear(Cpu.TStates))
                    res &= 0xBF; // bit 6 = EAR z pásky
                return res;
            }
            if (EnableAy && (port & 0xC002) == 0xC000) // 0xFFFD – čtení registru AY
                return Ay.Read();
            if ((port & 0x20) == 0)                     // 0x1F – Kempston joystick (A5=0)
                return Kempston;                        // bity aktivní v 1, horní bity 0
            return 0xFF;
        }

        public void Out(ushort port, byte value)
        {
            if ((port & 1) == 0)
            {
                Border = (byte)(value & 7);
                Beeper.Out(Cpu.TStates, value); // bit 4 = reproduktor
            }
            // stránkování jen na 128K, AY zvlášť (může být i na 48K přes Melodik)
            if (Is128 && (port & 0x8002) == 0) SetPaging(value);       // 0x7FFD
            if (EnableAy)
            {
                if ((port & 0xC002) == 0xC000) Ay.Select(value);       // 0xFFFD
                else if ((port & 0xC002) == 0x8000) Ay.Write(value);   // 0xBFFD
            }
        }

        // ---------- běh ----------
        public void RunFrame()
        {
            Beeper.OnFrameStart(Cpu.TStates);

            // ---- přehrávání RZX: jeden nahraný snímek = přerušení + N instrukcí ----
            if (Rzx != null && Rzx.Playing)
            {
                if (Rzx.NextFrame())
                {
                    Cpu.Interrupt();
                    int n = Rzx.FetchCount;
                    for (int i = 0; i < n; i++) Cpu.Step();
                }
                nextFrameT = Cpu.TStates + TStatesPerFrame;
                FrameCount++;
                return;
            }

            Cpu.Interrupt();
            while (Cpu.TStates < nextFrameT)
            {
                // Okamžité nahrávání jen na 48K. Na 128K se při zavádění přepíná
                // stránka ROM (zavaděč běží v ROM 1) a návrat z trapu by rozhodil
                // stránkování – tam se proto nahrává přes skutečné pulzy.
                if (InstantTapeLoad && HasStandardRom && !Is128
                    && Cpu.PC == LdBytes && Tape != null && Tape.AtStandardBlock)
                    TapeTrap();
                Cpu.Step();
            }
            nextFrameT += TStatesPerFrame;
            FrameCount++;
        }

        void TapeTrap()
        {
            byte[] blk = Tape.NextStandardBlock(Cpu.TStates);
            if (blk == null) return;

            ushort addr = Cpu.IX;
            int count = Cpu.DE;
            bool verify = (Cpu.F & 0x01) == 0;
            byte expected = Cpu.A;

            bool ok = blk.Length >= 1 && blk[0] == expected;
            byte parity = blk.Length >= 1 ? blk[0] : (byte)0;

            for (int k = 0; ok && k < count; k++)
            {
                int pos = 1 + k;
                if (pos >= blk.Length) { ok = false; break; }
                byte v = blk[pos];
                parity ^= v;
                ushort a = (ushort)(addr + k);
                if (verify) { if (Peek(a) != v) ok = false; }
                else Poke(a, v);
            }

            if (ok)
            {
                int csPos = 1 + count;
                if (csPos < blk.Length) { parity ^= blk[csPos]; ok = parity == 0; }
                else ok = false;
            }

            Cpu.IX = (ushort)(addr + count);
            Cpu.DE = 0;
            if (ok) { Cpu.F |= 0x01; InstantLoadCount++; } else Cpu.F &= 0xFE;
            Cpu.IFF1 = Cpu.IFF2 = true;

            ushort ret = (ushort)(Peek(Cpu.SP) | (Peek((ushort)(Cpu.SP + 1)) << 8));
            Cpu.SP += 2;
            Cpu.PC = ret;
        }

        /// <summary>FLASH atribut bliká s periodou 32 snímků (16 on / 16 off).</summary>
        public bool FlashOn => (FrameCount & 0x10) != 0;

        // ---------- snapshoty ----------
        public void LoadSnapshot(string path)
        {
            Snapshot.Load(this, path);
            nextFrameT = Cpu.TStates + TStatesPerFrame;
        }

        // ---------- páska ----------
        public void LoadTape(string path) => Tape = TzxTape.Load(path);
        public void PlayTape() => Tape?.Play(Cpu.TStates);
        public void StopTape() => Tape?.Stop();
        public bool TapePlaying => Tape != null && Tape.Playing;
        public bool TapeLoading => Tape != null && Tape.Loading;
        public void SeekTape(int block) => Tape?.SeekTo(block, Cpu.TStates);

        // ---------- klávesnice ----------
        public void SetKey(int row, int bit, bool pressed)
        {
            if (row < 0 || row > 7 || bit < 0 || bit > 4) return;
            if (pressed) KeyRows[row] &= (byte)~(1 << bit);
            else KeyRows[row] |= (byte)(1 << bit);
        }

        // ---------- Kempston joystick ----------
        public void SetJoystick(int bit, bool pressed)
        {
            if (bit < 0 || bit > 4) return;
            if (pressed) Kempston |= (byte)(1 << bit);
            else Kempston &= (byte)~(1 << bit);
        }
    }
}
