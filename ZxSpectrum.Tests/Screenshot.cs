using System;
using System.IO;
using ZxSpectrum.Core;

namespace ZxSpectrum.Tests
{
    public static class Screenshot
    {
        // Vyrenderuje testovací ROM a uloží snímek jako PPM
        public static void Save(string path)
        {
            var s = new Spectrum(TestRom.Build());
            for (int i = 0; i < 5; i++) s.RunFrame();
            var px = new int[Ula.Width * Ula.Height];
            Ula.Render(s.Mem, s.Border, s.FlashOn, px);
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{Ula.Width} {Ula.Height}\n255\n"));
            foreach (int p in px)
            {
                w.Write((byte)((p >> 16) & 0xFF)); // R
                w.Write((byte)((p >> 8) & 0xFF));  // G
                w.Write((byte)(p & 0xFF));         // B
            }
        }
    }
}
