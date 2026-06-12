namespace ZxSpectrum.Core
{
    /// <summary>
    /// Vestavěný testovací „ROM" – ručně přeložený program v Z80, který se použije,
    /// když není k dispozici soubor 48.rom. Vyplní obrazovku svislými proužky
    /// a atributy barevným gradientem, aby bylo vidět, že emulace běží.
    /// </summary>
    public static class TestRom
    {
        public static byte[] Build()
        {
            var rom = new byte[16384];
            byte[] prog =
            {
                0xF3,             // 0000  DI
                0x31, 0x00, 0x80, // 0001  LD SP,8000h
                0x3E, 0x02,       // 0004  LD A,2        ; červený border
                0xD3, 0xFE,       // 0006  OUT (FEh),A
                0x21, 0x00, 0x40, // 0008  LD HL,4000h   ; obrazová paměť
                0x36, 0xAA,       // 000B  LD (HL),AAh   ; vzor 10101010
                0x11, 0x01, 0x40, // 000D  LD DE,4001h
                0x01, 0xFF, 0x17, // 0010  LD BC,17FFh
                0xED, 0xB0,       // 0013  LDIR          ; vyplnit pixely
                0x21, 0x00, 0x58, // 0015  LD HL,5800h   ; atributy
                0x11, 0x00, 0x03, // 0018  LD DE,0300h   ; 768 bajtů
                0x7D,             // 001B  LD A,L        ; smyčka:
                0xE6, 0x3F,       // 001C  AND 3Fh
                0x77,             // 001E  LD (HL),A
                0x23,             // 001F  INC HL
                0x1B,             // 0020  DEC DE
                0x7A,             // 0021  LD A,D
                0xB3,             // 0022  OR E
                0x20, 0xF6,       // 0023  JR NZ,-10     ; zpět na 001B
                0x18, 0xFE,       // 0025  JR $          ; nekonečná smyčka
            };
            prog.CopyTo(rom, 0);
            return rom;
        }
    }
}
