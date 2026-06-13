namespace ZxSpectrum.Core
{
    /// <summary>
    /// Zvukový čip AY-3-8912 (ZX Spectrum 128K). Tři kanály obdélníkového tónu,
    /// generátor šumu a obálka. Registry se zapisují přes port 0xBFFD (výběr
    /// registru přes 0xFFFD). Vzorky se generují po snímku a přimíchávají k beepru.
    ///
    /// Vnitřně běží na taktu AY/16 (rozlišení tónových čítačů); pro každý zvukový
    /// vzorek se odběhne ~2,5 taktu a výstup se zprůměruje (jednoduchý antialias).
    /// </summary>
    public sealed class Ay
    {
        readonly double ticksPerSample;
        readonly int[] reg = new int[16];
        int sel;

        // čítače tónů / šumu / obálky a jejich fáze
        int ca, cb, cc, cn, ce;
        int phA, phB, phC;
        int rng = 1;       // 17bitový posuvný registr šumu
        int noiseBit;

        // obálka
        int envStep;
        bool envHold, envAttack;
        int envVol;

        double tickAcc;
        int lastMix;

        // DC blocker
        double dcX, dcY;
        const double R = 0.995;

        // logaritmická hlasitostní tabulka (16 úrovní), škálovaná na ~4700
        static readonly int[] Amp =
        {
            0, 64, 96, 137, 199, 290, 398, 643,
            795, 1244, 1658, 2115, 2681, 3230, 3987, 4700
        };

        public Ay(double ticksPerSample) => this.ticksPerSample = ticksPerSample;

        public void Reset()
        {
            for (int i = 0; i < 16; i++) reg[i] = 0;
            sel = 0; rng = 1; envStep = 0; envHold = false; envVol = 0;
        }

        public void Select(int value) => sel = value & 0x0F;
        public byte Read() => (byte)reg[sel];

        /// <summary>Aktuálně vybraný registr (pro uložení snapshotu).</summary>
        public int Selected => sel;
        /// <summary>Přečte hodnotu registru bez změny výběru (pro uložení snapshotu).</summary>
        public byte ReadReg(int i) => (byte)reg[i & 0x0F];

        public void Write(int value)
        {
            reg[sel] = value & 0xFF;
            if (sel == 13) // zápis tvaru obálky ji restartuje
            {
                int shape = reg[13] & 0x0F;
                envAttack = (shape & 4) != 0;
                envStep = 0;
                envHold = false;
                int ep = ((reg[12] << 8) | reg[11]);
                ce = (ep <= 0 ? 1 : ep) * 16;
                UpdateEnvVol();
            }
        }

        void UpdateEnvVol() => envVol = envHold ? envVol
                                                : (envAttack ? envStep : 15 - envStep);

        void ClockEnv()
        {
            if (envHold) return;
            envStep++;
            if (envStep > 15)
            {
                envStep = 0;
                int shape = reg[13] & 0x0F;
                if ((shape & 8) == 0) { envHold = true; envVol = 0; return; }
                if ((shape & 2) != 0) envAttack = !envAttack;       // alternate
                if ((shape & 1) != 0) { envHold = true; envVol = envAttack ? 15 : 0; return; } // hold
            }
            UpdateEnvVol();
        }

        void StepTick()
        {
            int pa = ((reg[1] & 0x0F) << 8) | reg[0];
            int pb = ((reg[3] & 0x0F) << 8) | reg[2];
            int pc = ((reg[5] & 0x0F) << 8) | reg[4];
            int pn = reg[6] & 0x1F;
            int ep = (reg[12] << 8) | reg[11];

            if (--ca <= 0) { ca = pa <= 0 ? 1 : pa; phA ^= 1; }
            if (--cb <= 0) { cb = pb <= 0 ? 1 : pb; phB ^= 1; }
            if (--cc <= 0) { cc = pc <= 0 ? 1 : pc; phC ^= 1; }
            if (--cn <= 0)
            {
                cn = pn <= 0 ? 1 : pn;
                int bit = (rng ^ (rng >> 3)) & 1;
                rng = (rng >> 1) | (bit << 16);
                noiseBit = rng & 1;
            }
            if (--ce <= 0) { ce = (ep <= 0 ? 1 : ep) * 16; ClockEnv(); }
        }

        int ChanLevel(int ch, int tone)
        {
            int r7 = reg[7];
            int toneDis = (r7 >> ch) & 1;
            int noiseDis = (r7 >> (ch + 3)) & 1;
            int active = (tone | toneDis) & (noiseBit | noiseDis);
            if (active == 0) return 0;
            int v = reg[8 + ch];
            int amp = (v & 0x10) != 0 ? envVol : (v & 0x0F);
            return Amp[amp];
        }

        int Mix() => ChanLevel(0, phA) + ChanLevel(1, phB) + ChanLevel(2, phC);

        /// <summary>Vygeneruje <paramref name="n"/> vzorků a PŘIČTE je do dst.</summary>
        public void RenderFrameAdd(short[] dst, int n)
        {
            for (int s = 0; s < n; s++)
            {
                tickAcc += ticksPerSample;
                int ticks = (int)tickAcc;
                tickAcc -= ticks;

                long sum = 0;
                for (int t = 0; t < ticks; t++) { StepTick(); sum += Mix(); }
                int outv = ticks > 0 ? (int)(sum / ticks) : lastMix;
                lastMix = outv;

                double y = outv - dcX + R * dcY; // DC blocker
                dcX = outv; dcY = y;

                int iy = dst[s] + (int)y;
                if (iy > 32767) iy = 32767; else if (iy < -32768) iy = -32768;
                dst[s] = (short)iy;
            }
        }
    }
}
