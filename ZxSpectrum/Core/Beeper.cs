using System.Collections.Generic;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// Emulace 1-bitového reproduktoru (beeper). Program přepíná bit 4 portu
    /// 0xFE; my zaznamenáváme okamžiky přepnutí (v T-stavech v rámci snímku)
    /// a na konci snímku z nich vyrobíme PCM vzorky. Pro každý vzorek se počítá
    /// podíl času, kdy byla úroveň vysoká – funguje to jako jednoduchý dolní
    /// filtr a omezuje aliasing čtvercové vlny.
    /// </summary>
    public sealed class Beeper
    {
        readonly int tStatesPerFrame;
        public int SamplesPerFrame { get; }

        const short Amplitude = 9000; // hlasitost (z 32767)

        long frameStart;
        bool level;            // aktuální úroveň reproduktoru
        bool frameStartLevel;  // úroveň na začátku snímku

        // DC blocker (horní propust) – odstraní stejnosměrnou složku, aby ticho
        // bylo opravdu ticho a nevznikalo bzučení z offsetu / podtečení bufferu.
        double dcX, dcY;
        const double R = 0.995; // mezní kmitočet ~35 Hz

        // hrany v rámci snímku: (offset v T-stavech, nová úroveň)
        readonly List<(int t, bool lvl)> edges = new();

        public Beeper(int tStatesPerFrame, int samplesPerFrame)
        {
            this.tStatesPerFrame = tStatesPerFrame;
            SamplesPerFrame = samplesPerFrame;
        }

        /// <summary>Začátek snímku – zapamatuje výchozí úroveň a vyprázdní hrany.</summary>
        public void OnFrameStart(long t)
        {
            frameStart = t;
            frameStartLevel = level;
            edges.Clear();
        }

        /// <summary>Zápis na ULA port – zachytí změnu bitu 4 (reproduktor).</summary>
        public void Out(long t, byte portValue)
        {
            bool newLevel = (portValue & 0x10) != 0;
            if (newLevel == level) return;
            level = newLevel;
            int off = (int)(t - frameStart);
            if (off < 0) off = 0;
            else if (off > tStatesPerFrame) off = tStatesPerFrame;
            edges.Add((off, newLevel));
        }

        /// <summary>Vyrobí vzorky pro právě doběhlý snímek do <paramref name="dst"/>.</summary>
        public int RenderFrame(short[] dst)
        {
            int n = SamplesPerFrame;
            int ei = 0;
            bool cur = frameStartLevel;
            long pos = 0;

            for (int s = 0; s < n; s++)
            {
                long sEnd = (long)(s + 1) * tStatesPerFrame / n;
                long high = 0, segStart = pos;

                while (ei < edges.Count && edges[ei].t < sEnd)
                {
                    int e = edges[ei].t;
                    if (cur) high += e - segStart;
                    segStart = e;
                    cur = edges[ei].lvl;
                    ei++;
                }
                if (cur) high += sEnd - segStart;

                long span = sEnd - pos;
                double frac = span > 0 ? (double)high / span : (cur ? 1.0 : 0.0);

                // unipolární vstup 0..2A, pak DC blocker -> ticho == 0, bez offsetu
                double v = frac * (2.0 * Amplitude);
                double y = v - dcX + R * dcY;
                dcX = v; dcY = y;

                int iy = (int)y;
                if (iy > 32767) iy = 32767; else if (iy < -32768) iy = -32768;
                dst[s] = (short)iy;
                pos = sEnd;
            }
            return n;
        }
    }
}
