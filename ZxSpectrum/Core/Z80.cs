using System;

namespace ZxSpectrum.Core
{
    /// <summary>Sběrnice – propojení CPU s pamětí a I/O porty.</summary>
    public interface IBus
    {
        byte Read(ushort addr);
        void Write(ushort addr, byte value);
        byte In(ushort port);
        void Out(ushort port, byte value);
    }

    /// <summary>
    /// Emulace procesoru Zilog Z80 – kompletní dokumentovaná instrukční sada
    /// včetně prefixů CB, ED, DD a FD (IX/IY) a nejběžnějších nedokumentovaných chování.
    /// </summary>
    public sealed class Z80
    {
        // Příznakové bity registru F
        const int FC = 0x01; // Carry
        const int FN = 0x02; // Add/Subtract
        const int FP = 0x04; // Parity/Overflow
        const int F3 = 0x08; // nedok. bit 3
        const int FH = 0x10; // Half-carry
        const int F5 = 0x20; // nedok. bit 5
        const int FZ = 0x40; // Zero
        const int FS = 0x80; // Sign

        public byte A, F, B, C, D, E, H, L;
        public byte A2, F2, B2, C2, D2, E2, H2, L2; // stínové registry
        public ushort IX, IY, SP, PC;
        public byte I, R;
        public bool IFF1, IFF2;
        public int IM;
        public bool Halted;
        public long TStates;

        enum Prefix { None, IX, IY }
        Prefix prefix;

        readonly IBus bus;

        static readonly byte[] sz53 = new byte[256];
        static readonly byte[] sz53p = new byte[256];

        static Z80()
        {
            for (int i = 0; i < 256; i++)
            {
                byte v = (byte)(i & (FS | F5 | F3));
                if (i == 0) v |= FZ;
                sz53[i] = v;
                int p = i; p ^= p >> 4; p ^= p >> 2; p ^= p >> 1;
                sz53p[i] = (byte)(v | (((p & 1) == 0) ? FP : 0));
            }
        }

        public Z80(IBus bus) { this.bus = bus; Reset(); }

        public void Reset()
        {
            PC = 0; SP = 0xFFFF; A = 0xFF; F = 0xFF;
            I = 0; R = 0; IM = 0; IFF1 = IFF2 = false; Halted = false;
        }

        // 16bitové páry
        public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
        public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
        public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }
        public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)value; } }

        // HL / IX / IY podle aktivního prefixu
        ushort HLp
        {
            get => prefix == Prefix.IX ? IX : prefix == Prefix.IY ? IY : HL;
            set { if (prefix == Prefix.IX) IX = value; else if (prefix == Prefix.IY) IY = value; else HL = value; }
        }

        byte Hp
        {
            get => prefix == Prefix.IX ? (byte)(IX >> 8) : prefix == Prefix.IY ? (byte)(IY >> 8) : H;
            set
            {
                if (prefix == Prefix.IX) IX = (ushort)((value << 8) | (IX & 0xFF));
                else if (prefix == Prefix.IY) IY = (ushort)((value << 8) | (IY & 0xFF));
                else H = value;
            }
        }

        byte Lp
        {
            get => prefix == Prefix.IX ? (byte)IX : prefix == Prefix.IY ? (byte)IY : L;
            set
            {
                if (prefix == Prefix.IX) IX = (ushort)((IX & 0xFF00) | value);
                else if (prefix == Prefix.IY) IY = (ushort)((IY & 0xFF00) | value);
                else L = value;
            }
        }

        byte Fetch() { byte v = bus.Read(PC); PC++; return v; }
        ushort Fetch16() { byte lo = Fetch(); byte hi = Fetch(); return (ushort)(lo | (hi << 8)); }
        ushort Read16(ushort a) => (ushort)(bus.Read(a) | (bus.Read((ushort)(a + 1)) << 8));
        void Write16(ushort a, ushort v) { bus.Write(a, (byte)v); bus.Write((ushort)(a + 1), (byte)(v >> 8)); }
        void Push(ushort v) { SP -= 2; Write16(SP, v); }
        ushort Pop() { ushort v = Read16(SP); SP += 2; return v; }
        void IncR() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));

        /// <summary>Adresa paměťového operandu: (HL) nebo (IX+d)/(IY+d) – u prefixu načte displacement.</summary>
        ushort MemAddr()
        {
            if (prefix == Prefix.None) return HL;
            sbyte d = (sbyte)Fetch();
            return (ushort)((prefix == Prefix.IX ? IX : IY) + d);
        }

        // Registry podle indexu 0..7 (6 = (HL), řeší se zvlášť). „Plain" varianty ignorují prefix.
        byte GetReg(int i) => i switch
        {
            0 => B, 1 => C, 2 => D, 3 => E, 4 => Hp, 5 => Lp, 7 => A,
            _ => throw new InvalidOperationException()
        };
        void SetReg(int i, byte v)
        {
            switch (i)
            {
                case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break;
                case 4: Hp = v; break; case 5: Lp = v; break; case 7: A = v; break;
            }
        }
        byte GetRegPlain(int i) => i switch
        {
            0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L, 7 => A,
            _ => throw new InvalidOperationException()
        };
        void SetRegPlain(int i, byte v)
        {
            switch (i)
            {
                case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break;
                case 4: H = v; break; case 5: L = v; break; case 7: A = v; break;
            }
        }

        ushort GetRP(int i) => i switch { 0 => BC, 1 => DE, 2 => HLp, 3 => SP, _ => 0 };
        void SetRP(int i, ushort v)
        {
            switch (i) { case 0: BC = v; break; case 1: DE = v; break; case 2: HLp = v; break; case 3: SP = v; break; }
        }

        bool Cond(int y) => y switch
        {
            0 => (F & FZ) == 0, 1 => (F & FZ) != 0,
            2 => (F & FC) == 0, 3 => (F & FC) != 0,
            4 => (F & FP) == 0, 5 => (F & FP) != 0,
            6 => (F & FS) == 0, 7 => (F & FS) != 0,
            _ => false
        };

        // ---------- ALU ----------
        void Add8(byte v)
        {
            int r = A + v;
            F = (byte)(sz53[r & 0xFF] | (r > 0xFF ? FC : 0)
                | (((A ^ v ^ r) & 0x10) != 0 ? FH : 0)
                | ((((A ^ ~v) & (A ^ r)) & 0x80) != 0 ? FP : 0));
            A = (byte)r;
        }
        void Adc8(byte v)
        {
            int c = F & FC; int r = A + v + c;
            F = (byte)(sz53[r & 0xFF] | (r > 0xFF ? FC : 0)
                | (((A ^ v ^ r) & 0x10) != 0 ? FH : 0)
                | ((((A ^ ~v) & (A ^ r)) & 0x80) != 0 ? FP : 0));
            A = (byte)r;
        }
        void Sub8(byte v)
        {
            int r = A - v;
            F = (byte)(sz53[r & 0xFF] | FN | (r < 0 ? FC : 0)
                | (((A ^ v ^ r) & 0x10) != 0 ? FH : 0)
                | ((((A ^ v) & (A ^ r)) & 0x80) != 0 ? FP : 0));
            A = (byte)r;
        }
        void Sbc8(byte v)
        {
            int c = F & FC; int r = A - v - c;
            F = (byte)(sz53[r & 0xFF] | FN | (r < 0 ? FC : 0)
                | (((A ^ v ^ r) & 0x10) != 0 ? FH : 0)
                | ((((A ^ v) & (A ^ r)) & 0x80) != 0 ? FP : 0));
            A = (byte)r;
        }
        void And8(byte v) { A &= v; F = (byte)(sz53p[A] | FH); }
        void Xor8(byte v) { A ^= v; F = sz53p[A]; }
        void Or8(byte v) { A |= v; F = sz53p[A]; }
        void Cp8(byte v)
        {
            int r = A - v;
            F = (byte)((sz53[r & 0xFF] & ~(F3 | F5)) | (v & (F3 | F5)) | FN
                | (r < 0 ? FC : 0)
                | (((A ^ v ^ r) & 0x10) != 0 ? FH : 0)
                | ((((A ^ v) & (A ^ r)) & 0x80) != 0 ? FP : 0));
        }
        void Alu(int y, byte v)
        {
            switch (y)
            {
                case 0: Add8(v); break; case 1: Adc8(v); break;
                case 2: Sub8(v); break; case 3: Sbc8(v); break;
                case 4: And8(v); break; case 5: Xor8(v); break;
                case 6: Or8(v); break; case 7: Cp8(v); break;
            }
        }

        byte Inc8(byte v)
        {
            byte r = (byte)(v + 1);
            F = (byte)((F & FC) | sz53[r] | (((v & 0x0F) == 0x0F) ? FH : 0) | (r == 0x80 ? FP : 0));
            return r;
        }
        byte Dec8(byte v)
        {
            byte r = (byte)(v - 1);
            F = (byte)((F & FC) | FN | sz53[r] | (((v & 0x0F) == 0) ? FH : 0) | (r == 0x7F ? FP : 0));
            return r;
        }

        ushort Add16(ushort a, ushort b)
        {
            int r = a + b;
            F = (byte)((F & (FS | FZ | FP)) | (((a ^ b ^ r) >> 8) & FH) | (r > 0xFFFF ? FC : 0) | ((r >> 8) & (F3 | F5)));
            return (ushort)r;
        }
        void AdcHL(ushort b)
        {
            int c = F & FC; int hl = HL; int r = hl + b + c; ushort res = (ushort)r;
            F = (byte)(((res >> 8) & (FS | F3 | F5)) | (res == 0 ? FZ : 0) | (r > 0xFFFF ? FC : 0)
                | (((hl ^ b ^ r) >> 8) & FH)
                | ((((hl ^ ~b) & (hl ^ r)) & 0x8000) != 0 ? FP : 0));
            HL = res;
        }
        void SbcHL(ushort b)
        {
            int c = F & FC; int hl = HL; int r = hl - b - c; ushort res = (ushort)r;
            F = (byte)(FN | ((res >> 8) & (FS | F3 | F5)) | (res == 0 ? FZ : 0) | (r < 0 ? FC : 0)
                | (((hl ^ b ^ r) >> 8) & FH)
                | ((((hl ^ b) & (hl ^ r)) & 0x8000) != 0 ? FP : 0));
            HL = res;
        }

        // ---------- rotace a posuvy (CB) ----------
        byte Rlc(byte v) { byte r = (byte)((v << 1) | (v >> 7)); F = (byte)(sz53p[r] | ((v >> 7) & FC)); return r; }
        byte Rrc(byte v) { byte r = (byte)((v >> 1) | (v << 7)); F = (byte)(sz53p[r] | (v & FC)); return r; }
        byte Rl(byte v) { byte r = (byte)((v << 1) | (F & FC)); F = (byte)(sz53p[r] | ((v >> 7) & FC)); return r; }
        byte Rr(byte v) { byte r = (byte)((v >> 1) | ((F & FC) << 7)); F = (byte)(sz53p[r] | (v & FC)); return r; }
        byte Sla(byte v) { byte r = (byte)(v << 1); F = (byte)(sz53p[r] | ((v >> 7) & FC)); return r; }
        byte Sra(byte v) { byte r = (byte)((v >> 1) | (v & 0x80)); F = (byte)(sz53p[r] | (v & FC)); return r; }
        byte Sll(byte v) { byte r = (byte)((v << 1) | 1); F = (byte)(sz53p[r] | ((v >> 7) & FC)); return r; }
        byte Srl(byte v) { byte r = (byte)(v >> 1); F = (byte)(sz53p[r] | (v & FC)); return r; }
        byte Rot(int y, byte v) => y switch
        {
            0 => Rlc(v), 1 => Rrc(v), 2 => Rl(v), 3 => Rr(v),
            4 => Sla(v), 5 => Sra(v), 6 => Sll(v), 7 => Srl(v), _ => v
        };

        void BitTest(int y, byte v)
        {
            byte m = (byte)(v & (1 << y));
            F = (byte)((F & FC) | FH | (m == 0 ? (FZ | FP) : 0) | (m & FS) | (v & (F3 | F5)));
        }

        void Daa()
        {
            int corr = 0;
            bool carry = (F & FC) != 0;
            if ((F & FH) != 0 || (A & 0x0F) > 9) corr |= 0x06;
            if (carry || A > 0x99) { corr |= 0x60; carry = true; }
            byte before = A;
            if ((F & FN) != 0)
            {
                A = (byte)(A - corr);
                bool h = (F & FH) != 0 && (before & 0x0F) < 6;
                F = (byte)(FN | (carry ? FC : 0) | (h ? FH : 0) | sz53p[A]);
            }
            else
            {
                bool h = (before & 0x0F) > 9;
                A = (byte)(A + corr);
                F = (byte)((carry ? FC : 0) | (h ? FH : 0) | sz53p[A]);
            }
        }

        // ---------- přerušení ----------
        /// <summary>Maskovatelné přerušení (na Spectru přichází 50× za sekundu z ULA).</summary>
        public void Interrupt()
        {
            if (!IFF1) { if (Halted) { /* DI+HALT = stroj stojí */ } return; }
            if (Halted) Halted = false;
            IFF1 = IFF2 = false;
            IncR();
            Push(PC);
            if (IM == 2)
            {
                ushort vec = (ushort)((I << 8) | 0xFF);
                PC = Read16(vec);
                TStates += 19;
            }
            else
            {
                PC = 0x0038;
                TStates += 13;
            }
        }

        /// <summary>Vykoná jednu instrukci a vrátí počet spotřebovaných T-stavů.</summary>
        public int Step()
        {
            long t0 = TStates;
            if (Halted) { TStates += 4; IncR(); return 4; }
            prefix = Prefix.None;
            int op = Fetch(); IncR();
            while (op == 0xDD || op == 0xFD)
            {
                prefix = op == 0xDD ? Prefix.IX : Prefix.IY;
                TStates += 4;
                op = Fetch(); IncR();
            }
            if (op == 0xCB) ExecCB();
            else if (op == 0xED) ExecED();
            else ExecMain(op);
            return (int)(TStates - t0);
        }

        // ---------- hlavní dekodér ----------
        void ExecMain(int op)
        {
            int x = op >> 6, y = (op >> 3) & 7, z = op & 7;

            // x=1: LD r,r' a HALT
            if (x == 1)
            {
                if (op == 0x76) { Halted = true; TStates += 4; return; }
                if (z == 6)
                {
                    ushort a = MemAddr();
                    SetRegPlain(y, bus.Read(a));
                    TStates += prefix == Prefix.None ? 7 : 19;
                }
                else if (y == 6)
                {
                    ushort a = MemAddr();
                    bus.Write(a, GetRegPlain(z));
                    TStates += prefix == Prefix.None ? 7 : 19;
                }
                else
                {
                    SetReg(y, GetReg(z));
                    TStates += 4;
                }
                return;
            }

            // x=2: ALU A, r
            if (x == 2)
            {
                if (z == 6)
                {
                    ushort a = MemAddr();
                    Alu(y, bus.Read(a));
                    TStates += prefix == Prefix.None ? 7 : 19;
                }
                else
                {
                    Alu(y, GetReg(z));
                    TStates += 4;
                }
                return;
            }

            // x=0
            if (x == 0)
            {
                switch (z)
                {
                    case 0:
                        switch (y)
                        {
                            case 0: TStates += 4; break; // NOP
                            case 1: { byte t = A; A = A2; A2 = t; t = F; F = F2; F2 = t; TStates += 4; } break; // EX AF,AF'
                            case 2: // DJNZ d
                                {
                                    sbyte d = (sbyte)Fetch();
                                    B--;
                                    if (B != 0) { PC = (ushort)(PC + d); TStates += 13; }
                                    else TStates += 8;
                                }
                                break;
                            case 3: { sbyte d = (sbyte)Fetch(); PC = (ushort)(PC + d); TStates += 12; } break; // JR d
                            default: // JR cc,d
                                {
                                    sbyte d = (sbyte)Fetch();
                                    if (Cond(y - 4)) { PC = (ushort)(PC + d); TStates += 12; }
                                    else TStates += 7;
                                }
                                break;
                        }
                        break;
                    case 1:
                        if ((y & 1) == 0) { SetRP(y >> 1, Fetch16()); TStates += 10; } // LD rp,nn
                        else { HLp = Add16(HLp, GetRP(y >> 1)); TStates += 11; }       // ADD HL,rp
                        break;
                    case 2:
                        switch (y)
                        {
                            case 0: bus.Write(BC, A); TStates += 7; break;                  // LD (BC),A
                            case 1: A = bus.Read(BC); TStates += 7; break;                  // LD A,(BC)
                            case 2: bus.Write(DE, A); TStates += 7; break;                  // LD (DE),A
                            case 3: A = bus.Read(DE); TStates += 7; break;                  // LD A,(DE)
                            case 4: Write16(Fetch16(), HLp); TStates += 16; break;          // LD (nn),HL
                            case 5: HLp = Read16(Fetch16()); TStates += 16; break;          // LD HL,(nn)
                            case 6: bus.Write(Fetch16(), A); TStates += 13; break;          // LD (nn),A
                            case 7: A = bus.Read(Fetch16()); TStates += 13; break;          // LD A,(nn)
                        }
                        break;
                    case 3:
                        if ((y & 1) == 0) SetRP(y >> 1, (ushort)(GetRP(y >> 1) + 1)); // INC rp
                        else SetRP(y >> 1, (ushort)(GetRP(y >> 1) - 1));              // DEC rp
                        TStates += 6;
                        break;
                    case 4: // INC r
                        if (y == 6)
                        {
                            ushort a = MemAddr();
                            bus.Write(a, Inc8(bus.Read(a)));
                            TStates += prefix == Prefix.None ? 11 : 23;
                        }
                        else { SetReg(y, Inc8(GetReg(y))); TStates += 4; }
                        break;
                    case 5: // DEC r
                        if (y == 6)
                        {
                            ushort a = MemAddr();
                            bus.Write(a, Dec8(bus.Read(a)));
                            TStates += prefix == Prefix.None ? 11 : 23;
                        }
                        else { SetReg(y, Dec8(GetReg(y))); TStates += 4; }
                        break;
                    case 6: // LD r,n
                        if (y == 6)
                        {
                            ushort a = MemAddr();      // u DD/FD nejdřív displacement…
                            byte n = Fetch();          // …pak teprve immediate
                            bus.Write(a, n);
                            TStates += prefix == Prefix.None ? 10 : 19;
                        }
                        else { SetReg(y, Fetch()); TStates += 7; }
                        break;
                    case 7:
                        switch (y)
                        {
                            case 0: { byte c = (byte)(A >> 7); A = (byte)((A << 1) | c); F = (byte)((F & (FS | FZ | FP)) | (A & (F3 | F5)) | c); TStates += 4; } break;            // RLCA
                            case 1: { byte c = (byte)(A & 1); A = (byte)((A >> 1) | (c << 7)); F = (byte)((F & (FS | FZ | FP)) | (A & (F3 | F5)) | c); TStates += 4; } break;     // RRCA
                            case 2: { byte c = (byte)(A >> 7); A = (byte)((A << 1) | (F & FC)); F = (byte)((F & (FS | FZ | FP)) | (A & (F3 | F5)) | c); TStates += 4; } break;    // RLA
                            case 3: { byte c = (byte)(A & 1); A = (byte)((A >> 1) | ((F & FC) << 7)); F = (byte)((F & (FS | FZ | FP)) | (A & (F3 | F5)) | c); TStates += 4; } break; // RRA
                            case 4: Daa(); TStates += 4; break;                                                                                                                    // DAA
                            case 5: A ^= 0xFF; F = (byte)((F & (FS | FZ | FP | FC)) | FH | FN | (A & (F3 | F5))); TStates += 4; break;                                            // CPL
                            case 6: F = (byte)((F & (FS | FZ | FP)) | FC | (A & (F3 | F5))); TStates += 4; break;                                                                  // SCF
                            case 7: { int c = F & FC; F = (byte)((F & (FS | FZ | FP)) | (c != 0 ? FH : FC) | (A & (F3 | F5))); TStates += 4; } break;                              // CCF
                        }
                        break;
                }
                return;
            }

            // x=3
            switch (z)
            {
                case 0: // RET cc
                    if (Cond(y)) { PC = Pop(); TStates += 11; } else TStates += 5;
                    break;
                case 1:
                    if ((y & 1) == 0) // POP rp2
                    {
                        ushort v = Pop();
                        switch (y >> 1) { case 0: BC = v; break; case 1: DE = v; break; case 2: HLp = v; break; case 3: AF = v; break; }
                        TStates += 10;
                    }
                    else
                    {
                        switch (y >> 1)
                        {
                            case 0: PC = Pop(); TStates += 10; break;                       // RET
                            case 1: // EXX
                                {
                                    (B, B2) = (B2, B); (C, C2) = (C2, C);
                                    (D, D2) = (D2, D); (E, E2) = (E2, E);
                                    (H, H2) = (H2, H); (L, L2) = (L2, L);
                                    TStates += 4;
                                }
                                break;
                            case 2: PC = HLp; TStates += 4; break;                          // JP (HL)
                            case 3: SP = HLp; TStates += 6; break;                          // LD SP,HL
                        }
                    }
                    break;
                case 2: // JP cc,nn
                    {
                        ushort nn = Fetch16();
                        if (Cond(y)) PC = nn;
                        TStates += 10;
                    }
                    break;
                case 3:
                    switch (y)
                    {
                        case 0: PC = Fetch16(); TStates += 10; break;                       // JP nn
                        // y=1: CB prefix – řeší se ve Step()
                        case 2: { byte n = Fetch(); bus.Out((ushort)((A << 8) | n), A); TStates += 11; } break;  // OUT (n),A
                        case 3: { byte n = Fetch(); A = bus.In((ushort)((A << 8) | n)); TStates += 11; } break;  // IN A,(n)
                        case 4: // EX (SP),HL
                            {
                                ushort tmp = Read16(SP);
                                Write16(SP, HLp);
                                HLp = tmp;
                                TStates += prefix == Prefix.None ? 19 : 23;
                            }
                            break;
                        case 5: { ushort t = DE; DE = HL; HL = t; TStates += 4; } break;    // EX DE,HL (vždy skutečné HL!)
                        case 6: IFF1 = IFF2 = false; TStates += 4; break;                   // DI
                        case 7: IFF1 = IFF2 = true; TStates += 4; break;                    // EI
                    }
                    break;
                case 4: // CALL cc,nn
                    {
                        ushort nn = Fetch16();
                        if (Cond(y)) { Push(PC); PC = nn; TStates += 17; }
                        else TStates += 10;
                    }
                    break;
                case 5:
                    if ((y & 1) == 0) // PUSH rp2
                    {
                        ushort v = (y >> 1) switch { 0 => BC, 1 => DE, 2 => HLp, _ => AF };
                        Push(v);
                        TStates += 11;
                    }
                    else if (y == 1) { ushort nn = Fetch16(); Push(PC); PC = nn; TStates += 17; } // CALL nn
                    break;
                case 6: Alu(y, Fetch()); TStates += 7; break;                               // ALU A,n
                case 7: Push(PC); PC = (ushort)(op & 0x38); TStates += 11; break;           // RST p
            }
        }

        // ---------- CB prefix ----------
        void ExecCB()
        {
            if (prefix != Prefix.None)
            {
                // DD CB d op / FD CB d op
                sbyte d = (sbyte)Fetch();
                ushort addr = (ushort)((prefix == Prefix.IX ? IX : IY) + d);
                int op = Fetch();
                int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
                byte v = bus.Read(addr);
                if (x == 1) { BitTest(y, v); TStates += 20; return; }
                byte r = x == 0 ? Rot(y, v)
                       : x == 2 ? (byte)(v & ~(1 << y))
                       : (byte)(v | (1 << y));
                bus.Write(addr, r);
                if (z != 6) SetRegPlain(z, r); // nedokumentované: výsledek i do registru
                TStates += 23;
            }
            else
            {
                int op = Fetch(); IncR();
                int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
                if (z == 6)
                {
                    byte v = bus.Read(HL);
                    if (x == 1) { BitTest(y, v); TStates += 12; return; }
                    byte r = x == 0 ? Rot(y, v)
                           : x == 2 ? (byte)(v & ~(1 << y))
                           : (byte)(v | (1 << y));
                    bus.Write(HL, r);
                    TStates += 15;
                }
                else
                {
                    byte v = GetRegPlain(z);
                    if (x == 1) { BitTest(y, v); TStates += 8; return; }
                    byte r = x == 0 ? Rot(y, v)
                           : x == 2 ? (byte)(v & ~(1 << y))
                           : (byte)(v | (1 << y));
                    SetRegPlain(z, r);
                    TStates += 8;
                }
            }
        }

        // ---------- ED prefix ----------
        void ExecED()
        {
            int op = Fetch(); IncR();
            int x = op >> 6, y = (op >> 3) & 7, z = op & 7;

            if (x == 1)
            {
                switch (z)
                {
                    case 0: // IN r,(C)
                        {
                            byte v = bus.In(BC);
                            if (y != 6) SetRegPlain(y, v);
                            F = (byte)((F & FC) | sz53p[v]);
                            TStates += 12;
                        }
                        break;
                    case 1: // OUT (C),r
                        bus.Out(BC, y == 6 ? (byte)0 : GetRegPlain(y));
                        TStates += 12;
                        break;
                    case 2: // SBC/ADC HL,rp
                        {
                            ushort v = y switch { 0 or 1 => BC, 2 or 3 => DE, 4 or 5 => HL, _ => SP };
                            if ((y & 1) == 0) SbcHL(v); else AdcHL(v);
                            TStates += 15;
                        }
                        break;
                    case 3: // LD (nn),rp / LD rp,(nn)
                        {
                            ushort nn = Fetch16();
                            int rp = y >> 1;
                            if ((y & 1) == 0)
                            {
                                ushort v = rp switch { 0 => BC, 1 => DE, 2 => HL, _ => SP };
                                Write16(nn, v);
                            }
                            else
                            {
                                ushort v = Read16(nn);
                                switch (rp) { case 0: BC = v; break; case 1: DE = v; break; case 2: HL = v; break; case 3: SP = v; break; }
                            }
                            TStates += 20;
                        }
                        break;
                    case 4: // NEG
                        {
                            byte a = A; A = 0; Sub8(a);
                            TStates += 8;
                        }
                        break;
                    case 5: // RETN / RETI
                        PC = Pop();
                        IFF1 = IFF2;
                        TStates += 14;
                        break;
                    case 6: // IM x
                        IM = y switch { 0 or 1 or 4 or 5 => 0, 2 or 6 => 1, _ => 2 };
                        TStates += 8;
                        break;
                    case 7:
                        switch (y)
                        {
                            case 0: I = A; TStates += 9; break;                                              // LD I,A
                            case 1: R = A; TStates += 9; break;                                              // LD R,A
                            case 2: A = I; F = (byte)((F & FC) | sz53[A] | (IFF2 ? FP : 0)); TStates += 9; break;  // LD A,I
                            case 3: A = R; F = (byte)((F & FC) | sz53[A] | (IFF2 ? FP : 0)); TStates += 9; break;  // LD A,R
                            case 4: // RRD
                                {
                                    byte m = bus.Read(HL);
                                    byte nm = (byte)((A << 4) | (m >> 4));
                                    A = (byte)((A & 0xF0) | (m & 0x0F));
                                    bus.Write(HL, nm);
                                    F = (byte)((F & FC) | sz53p[A]);
                                    TStates += 18;
                                }
                                break;
                            case 5: // RLD
                                {
                                    byte m = bus.Read(HL);
                                    byte nm = (byte)((m << 4) | (A & 0x0F));
                                    A = (byte)((A & 0xF0) | (m >> 4));
                                    bus.Write(HL, nm);
                                    F = (byte)((F & FC) | sz53p[A]);
                                    TStates += 18;
                                }
                                break;
                            default: TStates += 8; break; // NOP
                        }
                        break;
                }
                return;
            }

            if (x == 2 && z <= 3 && y >= 4)
            {
                bool inc = (y & 1) == 0;     // LDI/LDD: y=4 inc, y=5 dec
                bool rep = y >= 6;
                switch (z)
                {
                    case 0: // LDI/LDD/LDIR/LDDR
                        {
                            byte v = bus.Read(HL);
                            bus.Write(DE, v);
                            HL = (ushort)(HL + (inc ? 1 : -1));
                            DE = (ushort)(DE + (inc ? 1 : -1));
                            BC--;
                            int n = v + A;
                            F = (byte)((F & (FS | FZ | FC)) | (BC != 0 ? FP : 0)
                                | ((n & 0x02) != 0 ? F5 : 0) | ((n & 0x08) != 0 ? F3 : 0));
                            if (rep && BC != 0) { PC -= 2; TStates += 21; }
                            else TStates += 16;
                        }
                        break;
                    case 1: // CPI/CPD/CPIR/CPDR
                        {
                            byte v = bus.Read(HL);
                            int r = A - v;
                            bool h = ((A ^ v ^ r) & 0x10) != 0;
                            HL = (ushort)(HL + (inc ? 1 : -1));
                            BC--;
                            int n = r - (h ? 1 : 0);
                            F = (byte)((F & FC) | FN | (sz53[r & 0xFF] & (FS | FZ)) | (h ? FH : 0)
                                | (BC != 0 ? FP : 0)
                                | ((n & 0x02) != 0 ? F5 : 0) | ((n & 0x08) != 0 ? F3 : 0));
                            if (rep && BC != 0 && (F & FZ) == 0) { PC -= 2; TStates += 21; }
                            else TStates += 16;
                        }
                        break;
                    case 2: // INI/IND/INIR/INDR
                        {
                            byte v = bus.In(BC);
                            bus.Write(HL, v);
                            HL = (ushort)(HL + (inc ? 1 : -1));
                            B--;
                            F = (byte)(sz53[B] | FN);
                            if (rep && B != 0) { PC -= 2; TStates += 21; }
                            else TStates += 16;
                        }
                        break;
                    case 3: // OUTI/OUTD/OTIR/OTDR
                        {
                            byte v = bus.Read(HL);
                            B--;
                            bus.Out(BC, v);
                            HL = (ushort)(HL + (inc ? 1 : -1));
                            F = (byte)(sz53[B] | FN);
                            if (rep && B != 0) { PC -= 2; TStates += 21; }
                            else TStates += 16;
                        }
                        break;
                }
                return;
            }

            TStates += 8; // neznámé ED = NOP
        }
    }
}
