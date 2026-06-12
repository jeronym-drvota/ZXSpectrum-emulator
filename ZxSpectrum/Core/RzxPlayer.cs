using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZxSpectrum.Core
{
    /// <summary>
    /// Přehrávání záznamů ve formátu RZX. RZX neukládá stav v jednom okamžiku,
    /// ale počáteční snapshot + posloupnost veškerého vstupu (hodnot načtených
    /// instrukcemi IN) snímek po snímku. Přehrávání je deterministické: procesor
    /// běží normálně, ale místo skutečných portů dostává nahrané hodnoty a každý
    /// snímek vykoná přesný počet instrukcí, pak přijde přerušení.
    /// </summary>
    public sealed class RzxPlayer
    {
        public byte[] Snapshot { get; private set; }
        public string SnapshotExt { get; private set; }

        struct Frame { public int Fetch; public byte[] In; }
        readonly List<Frame> frames = new();

        int frameIdx;
        int inIdx;
        byte[] curIn = Array.Empty<byte>();

        public bool Playing { get; private set; }
        public int FetchCount { get; private set; }
        public int FrameCount => frames.Count;

        public static RzxPlayer Load(string path)
        {
            var d = File.ReadAllBytes(path);
            var p = new RzxPlayer();
            p.Parse(d);
            if (p.Snapshot == null)
                throw new InvalidDataException("RZX neobsahuje vložený snapshot.");
            if (p.frames.Count == 0)
                throw new InvalidDataException("RZX neobsahuje žádné snímky.");
            return p;
        }

        // ---------- přehrávání ----------
        public void Play() { frameIdx = -1; Playing = frames.Count > 0; }
        public void Stop() => Playing = false;

        /// <summary>Posun na další snímek; nastaví počet instrukcí a vstupní data.</summary>
        public bool NextFrame()
        {
            frameIdx++;
            if (frameIdx >= frames.Count) { Playing = false; return false; }
            var f = frames[frameIdx];
            FetchCount = f.Fetch;
            curIn = f.In;
            inIdx = 0;
            return true;
        }

        /// <summary>Další nahraná hodnota IN (po vyčerpání vrací 0xFF).</summary>
        public byte NextIn() => inIdx < curIn.Length ? curIn[inIdx++] : (byte)0xFF;

        // ---------- parsování ----------
        void Parse(byte[] d)
        {
            if (d.Length < 10 || d[0] != 'R' || d[1] != 'Z' || d[2] != 'X' || d[3] != '!')
                throw new InvalidDataException("Neplatná hlavička RZX.");

            int i = 10;
            byte[] prevIn = Array.Empty<byte>();
            while (i + 5 <= d.Length)
            {
                int id = d[i];
                long len = U32(d, i + 1);
                if (len < 5 || i + len > d.Length) break;
                int body = i + 5;
                int blockEnd = (int)(i + len);

                switch (id)
                {
                    case 0x30: ParseSnapshot(d, body, blockEnd); break;       // snapshot
                    case 0x80: ParseInput(d, body, blockEnd, ref prevIn); break; // záznam vstupu
                    // 0x10 (creator), 0x20 (security signature) atd. – přeskočíme
                }
                i = blockEnd;
            }
        }

        void ParseSnapshot(byte[] d, int s, int end)
        {
            uint flags = (uint)U32(d, s);
            bool external = (flags & 1) != 0;
            bool compressed = (flags & 2) != 0;
            string ext = Encoding.ASCII.GetString(d, s + 4, 4).TrimEnd('\0', ' ').ToLowerInvariant();
            int dataStart = s + 12; // flags(4) + ext(4) + nekompr. délka(4)
            int dataLen = end - dataStart;

            if (external)
                throw new NotSupportedException("RZX s externím (neuloženým) snapshotem není podporován.");

            Snapshot = compressed ? Inflate(d, dataStart, dataLen) : Slice(d, dataStart, dataLen);
            SnapshotExt = ext;
        }

        void ParseInput(byte[] d, int s, int end, ref byte[] prevIn)
        {
            int numFrames = (int)U32(d, s);
            // s+4: rezervováno (1 B), s+5: T-stavy (4 B)
            uint flags = (uint)U32(d, s + 9);
            bool isProtected = (flags & 1) != 0;
            bool compressed = (flags & 2) != 0;
            if (isProtected)
                throw new NotSupportedException("Chráněný (šifrovaný) RZX není podporován.");

            int dataStart = s + 13;
            int dataLen = end - dataStart;
            byte[] fr = compressed ? Inflate(d, dataStart, dataLen) : Slice(d, dataStart, dataLen);

            int p = 0;
            for (int f = 0; f < numFrames && p + 4 <= fr.Length; f++)
            {
                int fetch = fr[p] | (fr[p + 1] << 8); p += 2;
                int inCount = fr[p] | (fr[p + 1] << 8); p += 2;

                byte[] ins;
                if (inCount == 0xFFFF)        // stejný vstup jako předchozí snímek
                {
                    ins = prevIn;
                }
                else
                {
                    int take = Math.Min(inCount, fr.Length - p);
                    ins = new byte[take];
                    Array.Copy(fr, p, ins, 0, take);
                    p += inCount;
                }
                prevIn = ins;
                frames.Add(new Frame { Fetch = fetch, In = ins });
            }
        }

        // ---------- pomocné ----------
        static byte[] Inflate(byte[] d, int start, int len)
        {
            using var ms = new MemoryStream(d, start, len);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            return outMs.ToArray();
        }

        static byte[] Slice(byte[] d, int start, int len)
        {
            var r = new byte[Math.Max(0, len)];
            Array.Copy(d, start, r, 0, r.Length);
            return r;
        }

        static long U32(byte[] d, int o) =>
            d[o] | ((long)d[o + 1] << 8) | ((long)d[o + 2] << 16) | ((long)d[o + 3] << 24);
    }
}
