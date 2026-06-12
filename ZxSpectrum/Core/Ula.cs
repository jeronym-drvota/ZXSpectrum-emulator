namespace ZxSpectrum.Core
{
    /// <summary>
    /// Vykreslení obrazu ULA: 256×192 pixelů + atributy 32×24 (INK/PAPER/BRIGHT/FLASH)
    /// a barevný border kolem. Výstup je framebuffer 320×240 ve formátu BGRA.
    /// </summary>
    public static class Ula
    {
        public const int Width = 320, Height = 240;
        public const int BorderX = 32, BorderY = 24;

        // 8 základních + 8 jasných barev (BGRA, alfa FF)
        static readonly uint[] Palette =
        {
            0xFF000000, 0xFF0000D7, 0xFFD70000, 0xFFD700D7,
            0xFF00D700, 0xFF00D7D7, 0xFFD7D700, 0xFFD7D7D7,
            0xFF000000, 0xFF0000FF, 0xFFFF0000, 0xFFFF00FF,
            0xFF00FF00, 0xFF00FFFF, 0xFFFFFF00, 0xFFFFFFFF,
        };

        /// <summary><paramref name="screen"/> je 16K obrazová banka (5 nebo 7);
        /// pixely jsou na offsetu 0x0000–0x17FF, atributy 0x1800–0x1AFF.</summary>
        public static void Render(byte[] screen, byte border, bool flashOn, int[] pixels)
        {
            int bcol = unchecked((int)Palette[border & 7]);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bcol;

            for (int y = 0; y < 192; y++)
            {
                // Specifické prokládané adresování obrazové paměti Spectra (v rámci banky)
                int addr = ((y & 0xC0) << 5) | ((y & 0x07) << 8) | ((y & 0x38) << 2);
                int attrRow = 0x1800 + (y >> 3) * 32;
                int dst = (y + BorderY) * Width + BorderX;

                for (int col = 0; col < 32; col++)
                {
                    byte bits = screen[addr + col];
                    byte attr = screen[attrRow + col];
                    int ink = attr & 7;
                    int paper = (attr >> 3) & 7;
                    if ((attr & 0x40) != 0) { ink += 8; paper += 8; }
                    if ((attr & 0x80) != 0 && flashOn) (ink, paper) = (paper, ink);

                    int pi = unchecked((int)Palette[ink]);
                    int pp = unchecked((int)Palette[paper]);
                    int o = dst + col * 8;
                    for (int b = 0; b < 8; b++)
                        pixels[o + b] = ((bits >> (7 - b)) & 1) != 0 ? pi : pp;
                }
            }
        }
    }
}
