using System;
using NAudio.Wave;

namespace ZxSpectrum
{
    /// <summary>
    /// Přehrávání zvuku beepru přes NAudio. Emulátor po každém snímku dodá
    /// vzorky (16bit mono, 44100 Hz), které se vloží do vyrovnávací paměti
    /// a přehrávají na pozadí. Pokud se zvukové zařízení nepodaří otevřít
    /// (např. jiná platforma), objekt se tiše vypne a emulátor běží dál bez zvuku.
    /// </summary>
    public sealed class BeeperAudio : IDisposable
    {
        readonly WaveOutEvent output;
        readonly BufferedWaveProvider buffer;
        byte[] bytes = Array.Empty<byte>();

        public bool Enabled { get; private set; }

        public BeeperAudio()
        {
            try
            {
                buffer = new BufferedWaveProvider(new WaveFormat(44100, 16, 1))
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(250)
                };
                output = new WaveOutEvent { DesiredLatency = 120 };
                output.Init(buffer);
                output.Play();
                Enabled = true;
            }
            catch
            {
                Enabled = false; // bez zvukového zařízení jedeme potichu
            }
        }

        /// <summary>Vloží <paramref name="count"/> vzorků ke přehrání.</summary>
        public void Push(short[] samples, int count)
        {
            if (!Enabled) return;
            int needed = count * 2;
            if (bytes.Length < needed) bytes = new byte[needed];
            Buffer.BlockCopy(samples, 0, bytes, 0, needed);
            buffer.AddSamples(bytes, 0, needed);
        }

        public void Dispose()
        {
            try { output?.Dispose(); } catch { }
        }
    }
}
