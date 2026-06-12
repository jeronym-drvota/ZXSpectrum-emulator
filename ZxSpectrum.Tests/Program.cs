using System;
using ZxSpectrum.Core;

namespace ZxSpectrum.Tests
{
    static class Program
    {
        static int failures;

        static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) failures++;
        }

        /// <summary>Vytvoří stroj, nahraje program do RAM na 0x8000 a spustí ho do HALT/limity.</summary>
        static Spectrum RunRam(byte[] prog, int maxSteps = 100000)
        {
            var s = new Spectrum(TestRom.Build());
            prog.CopyTo(s.Mem, 0x8000);
            s.Cpu.PC = 0x8000;
            s.Cpu.SP = 0x7FFE;
            int steps = 0;
            while (!s.Cpu.Halted && steps++ < maxSteps) s.Cpu.Step();
            return s;
        }

        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--screenshot")
            {
                Screenshot.Save(args[1]);
                Console.WriteLine($"Snímek uložen: {args[1]}");
                return;
            }

            // ---- 1. Boot vestavěného testovacího ROM (DI, OUT, LDIR, JR NZ smyčka) ----
            {
                var s = new Spectrum(TestRom.Build());
                for (int i = 0; i < 5; i++) s.RunFrame();
                bool pixelsOk = true;
                for (int a = 0x4000; a < 0x5800; a++) if (s.Mem[a] != 0xAA) { pixelsOk = false; break; }
                bool attrsOk = true;
                for (int a = 0x5800; a < 0x5B00; a++) if (s.Mem[a] != (a & 0x3F)) { attrsOk = false; break; }
                Check(pixelsOk, "test ROM: pixely vyplněny vzorem 0xAA (LDIR)");
                Check(attrsOk, "test ROM: atributy = gradient (AND/OR/JR NZ smyčka)");
                Check(s.Border == 2, "test ROM: border nastaven přes OUT (0xFE)");
                Check(s.Cpu.PC == 0x0025, "test ROM: CPU skončil ve smyčce JR $");
            }

            // ---- 2. ALU a příznaky ----
            {
                // LD A,0x0F; ADD A,0x01; HALT
                var s = RunRam(new byte[] { 0x3E, 0x0F, 0xC6, 0x01, 0x76 });
                Check(s.Cpu.A == 0x10 && (s.Cpu.F & 0x10) != 0 && (s.Cpu.F & 0x01) == 0,
                    "ADD A,n: výsledek a half-carry");
            }
            {
                // LD A,0x80; ADD A,0x80 -> 0x00, C=1, PV=1 (přetečení), Z=1
                var s = RunRam(new byte[] { 0x3E, 0x80, 0xC6, 0x80, 0x76 });
                Check(s.Cpu.A == 0 && (s.Cpu.F & 0x01) != 0 && (s.Cpu.F & 0x04) != 0 && (s.Cpu.F & 0x40) != 0,
                    "ADD A,n: carry + overflow + zero");
            }
            {
                // LD A,0x10; SUB 0x20 -> 0xF0, C=1, S=1, N=1
                var s = RunRam(new byte[] { 0x3E, 0x10, 0xD6, 0x20, 0x76 });
                Check(s.Cpu.A == 0xF0 && (s.Cpu.F & 0x01) != 0 && (s.Cpu.F & 0x80) != 0 && (s.Cpu.F & 0x02) != 0,
                    "SUB n: borrow + sign + N");
            }
            {
                // SCF; LD A,0x00; ADC A,0x00 -> A=1 (přenos z carry)
                var s = RunRam(new byte[] { 0x37, 0x3E, 0x00, 0xCE, 0x00, 0x76 });
                Check(s.Cpu.A == 1, "ADC A,n s carry");
            }
            {
                // DAA: LD A,0x15; ADD A,0x27; DAA -> 0x42 (BCD 15+27)
                var s = RunRam(new byte[] { 0x3E, 0x15, 0xC6, 0x27, 0x27, 0x76 });
                Check(s.Cpu.A == 0x42, "DAA po sčítání BCD");
            }

            // ---- 3. Skoky, DJNZ, zásobník ----
            {
                // LD B,5; LD A,0; smyčka: ADD A,3; DJNZ -> A=15
                var s = RunRam(new byte[] { 0x06, 0x05, 0x3E, 0x00, 0xC6, 0x03, 0x10, 0xFC, 0x76 });
                Check(s.Cpu.A == 15 && s.Cpu.B == 0, "DJNZ smyčka");
            }
            {
                // CALL podprogramu, který nastaví A=0x77 a RET; pak HALT
                // 8000: CD 06 80  CALL 8006
                // 8003: 76        HALT
                // 8004: 00 00
                // 8006: 3E 77     LD A,77
                // 8008: C9        RET
                var s = RunRam(new byte[] { 0xCD, 0x06, 0x80, 0x76, 0x00, 0x00, 0x3E, 0x77, 0xC9 });
                Check(s.Cpu.A == 0x77 && s.Cpu.PC == 0x8004 && s.Cpu.SP == 0x7FFE,
                    "CALL/RET + zásobník");
            }
            {
                // LD BC,0x1234; PUSH BC; POP DE
                var s = RunRam(new byte[] { 0x01, 0x34, 0x12, 0xC5, 0xD1, 0x76 });
                Check(s.Cpu.DE == 0x1234, "PUSH/POP");
            }
            {
                // LD HL,0x9000; LD DE,0x8000; ADD HL,DE -> 0x1000 + carry
                var s = RunRam(new byte[] { 0x21, 0x00, 0x90, 0x11, 0x00, 0x80, 0x19, 0x76 });
                Check(s.Cpu.HL == 0x1000 && (s.Cpu.F & 0x01) != 0, "ADD HL,DE s přenosem");
            }
            {
                // EX (SP),HL: PUSH BC(=0xBEEF), LD HL,0x1234, EX (SP),HL, POP DE
                var s = RunRam(new byte[] { 0x01, 0xEF, 0xBE, 0xC5, 0x21, 0x34, 0x12, 0xE3, 0xD1, 0x76 });
                Check(s.Cpu.HL == 0xBEEF && s.Cpu.DE == 0x1234, "EX (SP),HL");
            }

            // ---- 4. CB prefix ----
            {
                // LD A,0x01; CB 07 (RLC A) -> 0x02; CB C7 (SET 0,A) -> 0x03; CB 47 (BIT 0,A) -> Z=0
                var s = RunRam(new byte[] { 0x3E, 0x01, 0xCB, 0x07, 0xCB, 0xC7, 0xCB, 0x47, 0x76 });
                Check(s.Cpu.A == 0x03 && (s.Cpu.F & 0x40) == 0, "CB: RLC/SET/BIT");
            }
            {
                // SRL: LD A,0x81; CB 3F -> 0x40, C=1
                var s = RunRam(new byte[] { 0x3E, 0x81, 0xCB, 0x3F, 0x76 });
                Check(s.Cpu.A == 0x40 && (s.Cpu.F & 0x01) != 0, "CB: SRL A");
            }

            // ---- 5. DD/FD prefix (IX/IY) ----
            {
                // LD IX,0x9000; LD (IX+5),0x42; LD A,(IX+5); INC (IX+5)
                var s = RunRam(new byte[]
                {
                    0xDD, 0x21, 0x00, 0x90,       // LD IX,9000
                    0xDD, 0x36, 0x05, 0x42,       // LD (IX+5),42
                    0xDD, 0x7E, 0x05,             // LD A,(IX+5)
                    0xDD, 0x34, 0x05,             // INC (IX+5)
                    0x76
                });
                Check(s.Cpu.A == 0x42 && s.Mem[0x9005] == 0x43 && s.Cpu.IX == 0x9000,
                    "DD: LD (IX+d),n / LD A,(IX+d) / INC (IX+d)");
            }
            {
                // DD CB: LD IX,0x9000; LD (IX+2),1; SET 7,(IX+2)
                var s = RunRam(new byte[]
                {
                    0xDD, 0x21, 0x00, 0x90,
                    0xDD, 0x36, 0x02, 0x01,
                    0xDD, 0xCB, 0x02, 0xFE,       // SET 7,(IX+2)
                    0x76
                });
                Check(s.Mem[0x9002] == 0x81, "DD CB: SET 7,(IX+d)");
            }

            // ---- 6. ED prefix ----
            {
                // LD HL,0x5000; LD DE,0x3000; OR A (vynuluje C); SBC HL,DE -> 0x2000
                var s = RunRam(new byte[] { 0x21, 0x00, 0x50, 0x11, 0x00, 0x30, 0xB7, 0xED, 0x52, 0x76 });
                Check(s.Cpu.HL == 0x2000, "ED: SBC HL,DE");
            }
            {
                // NEG: LD A,1; ED 44 -> 0xFF
                var s = RunRam(new byte[] { 0x3E, 0x01, 0xED, 0x44, 0x76 });
                Check(s.Cpu.A == 0xFF && (s.Cpu.F & 0x01) != 0, "ED: NEG");
            }
            {
                // LDDR: zkopírovat 4 bajty pozpátku z 0x9003 do 0x9103
                var s = new Spectrum(TestRom.Build());
                byte[] prog =
                {
                    0x21, 0x03, 0x90,             // LD HL,9003
                    0x11, 0x03, 0x91,             // LD DE,9103
                    0x01, 0x04, 0x00,             // LD BC,4
                    0xED, 0xB8,                   // LDDR
                    0x76
                };
                prog.CopyTo(s.Mem, 0x8000);
                s.Mem[0x9000] = 1; s.Mem[0x9001] = 2; s.Mem[0x9002] = 3; s.Mem[0x9003] = 4;
                s.Cpu.PC = 0x8000; s.Cpu.SP = 0x7FFE;
                int n = 0; while (!s.Cpu.Halted && n++ < 1000) s.Cpu.Step();
                Check(s.Mem[0x9100] == 1 && s.Mem[0x9103] == 4 && s.Cpu.BC == 0,
                    "ED: LDDR");
            }
            {
                // CPIR: najít 0x42 v bloku
                var s = new Spectrum(TestRom.Build());
                byte[] prog =
                {
                    0x3E, 0x42,                   // LD A,42
                    0x21, 0x00, 0x90,             // LD HL,9000
                    0x01, 0x10, 0x00,             // LD BC,16
                    0xED, 0xB1,                   // CPIR
                    0x76
                };
                prog.CopyTo(s.Mem, 0x8000);
                s.Mem[0x9005] = 0x42;
                s.Cpu.PC = 0x8000; s.Cpu.SP = 0x7FFE;
                int n = 0; while (!s.Cpu.Halted && n++ < 1000) s.Cpu.Step();
                Check(s.Cpu.HL == 0x9006 && (s.Cpu.F & 0x40) != 0, "ED: CPIR našel hodnotu");
            }

            // ---- 7. Přerušení (IM 1) ----
            {
                var s = new Spectrum(TestRom.Build());
                byte[] prog = { 0xED, 0x56, 0xFB, 0x76 }; // IM 1; EI; HALT
                prog.CopyTo(s.Mem, 0x8000);
                s.Cpu.PC = 0x8000; s.Cpu.SP = 0x7FFE;
                int n = 0; while (!s.Cpu.Halted && n++ < 100) s.Cpu.Step();
                s.Cpu.Interrupt();
                Check(s.Cpu.PC == 0x0038 && !s.Cpu.Halted && s.Cpu.SP == 0x7FFC && !s.Cpu.IFF1,
                    "IM1 přerušení: skok na 0x38, probuzení z HALT");
            }

            // ---- 8. Port klávesnice ----
            {
                var s = new Spectrum(TestRom.Build());
                s.SetKey(3, 0, true); // klávesa "1" (půlřádek 0xF7, bit 0)
                byte v = s.In(0xF7FE);
                byte v2 = s.In(0xFEFE); // jiný půlřádek – klávesa se nesmí projevit
                Check((v & 0x01) == 0 && (v2 & 0x1F) == 0x1F, "klávesnice: matice půlřádků");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "Vše OK ✔" : $"{failures} testů selhalo ✘");
            Environment.Exit(failures == 0 ? 0 : 1);
        }
    }
}
