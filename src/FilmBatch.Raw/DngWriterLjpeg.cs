// 移植自 LightCraft crates/raw/src/ljpeg.rs（編碼側：ITU-T T.81 process 14 lossless JPEG，附錄 H）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw;

/// <summary>生產用 lossless JPEG（LJ92）編碼器：DNG 磚塊用（lightcraft <c>ljpeg::encode</c>）。</summary>
internal static class DngWriterLjpeg
{
    private sealed class BitWriter
    {
        public readonly List<byte> Out = [];
        private ulong _acc;
        private int _n;

        public void Put(uint v, int bits)
        {
            if (bits == 0) return;
            _acc = (_acc << bits) | (v & ((bits >= 32 ? uint.MaxValue : (1u << bits) - 1u)));
            _n += bits;
            while (_n >= 8)
            {
                var b = (byte)(_acc >> (_n - 8));
                Out.Add(b);
                if (b == 0xff) Out.Add(0);
                _n -= 8;
            }
        }

        public void Flush()
        {
            if (_n > 0) Put((1u << (8 - _n)) - 1u, 8 - _n);
        }
    }

    private static byte SsssOf(int diff)
    {
        if (diff == 0) return 0;
        if (diff == 32768 || diff == -32768) return 16;
        return (byte)(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(diff)));
    }

    /// <summary>T.81 附錄 K.2（圖 K.1–K.3）的封包 Huffman：頻率 → 碼長上限 16。</summary>
    private static (byte[] Counts, byte[] Values) CodeLengths(uint[] freqIn)
    {
        var freq = new long[18];
        for (var i = 0; i < 17; i++) freq[i] = freqIn[i];
        freq[17] = 1; // 保留符號，避免全 1 碼
        var codesize = new int[18];
        var others = new int[18];
        Array.Fill(others, -1);
        while (true)
        {
            var v1 = -1;
            for (var i = 0; i < 18; i++)
                if (freq[i] > 0 && (v1 < 0 || freq[i] <= freq[v1])) v1 = i;
            var v2 = -1;
            for (var i = 0; i < 18; i++)
                if (freq[i] > 0 && i != v1 && (v2 < 0 || freq[i] <= freq[v2])) v2 = i;
            if (v2 < 0) break;
            var (a, b) = (v1, v2);
            freq[a] += freq[b];
            freq[b] = 0;
            var x = a;
            codesize[x]++;
            while (others[x] >= 0) { x = others[x]; codesize[x]++; }
            others[x] = b;
            x = b;
            codesize[x]++;
            while (others[x] >= 0) { x = others[x]; codesize[x]++; }
        }
        var bits = new int[33];
        foreach (var cs in codesize)
            if (cs > 0) bits[Math.Min(cs, 32)]++;
        var size = 32;
        while (size > 16)
        {
            while (bits[size] > 0)
            {
                var j = size - 2;
                while (bits[j] == 0) j--;
                bits[size] -= 2;
                bits[size - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
            size--;
        }
        while (bits[size] == 0) size--;
        bits[size]--;
        var counts = new byte[16];
        for (var l = 1; l <= 16; l++) counts[l - 1] = (byte)bits[l];
        var values = new List<byte>();
        for (var l = 1; l <= 32; l++)
            for (var s = 0; s < 17; s++)
                if (codesize[s] == l) values.Add((byte)s);
        return (counts, [.. values]);
    }

    private static (uint Code, byte Len)[,] CanonicalCodes(byte[][] counts, byte[][] values)
    {
        var table = new (uint, byte)[counts.Length, 17];
        for (var c = 0; c < counts.Length; c++)
        {
            uint code = 0;
            var k = 0;
            for (var len = 1; len <= 16; len++)
            {
                for (var j = 0; j < counts[c][len - 1]; j++)
                {
                    if (k < values[c].Length && values[c][k] < 17)
                        table[c, values[c][k]] = (code, (byte)len);
                    code++;
                    k++;
                }
                code <<= 1;
            }
        }
        return table;
    }

    /// <summary>
    /// 編碼交錯樣本（predictor 1–7；<paramref name="restart"/> &gt; 0 時每該像素數插 restart 標記）。
    /// </summary>
    internal static byte[] Encode(ushort[] data, int width, int height, int components, byte precision, byte predictor, int restart)
    {
        if (components is < 1 or > 4 || precision is < 2 or > 16 || predictor is < 1 or > 7)
            throw RawException.Corrupt("lossless JPEG: bad encode parameters");
        if ((long)data.Length != (long)width * height * components)
            throw RawException.Corrupt("lossless JPEG: data length mismatch");
        int init = 1 << (precision - 1);
        int rowLen = width * components;
        var diffs = new int[data.Length];
        var marks = new List<int>();
        int restartLeft = restart;
        bool firstLine = true, resetPending = true;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (restart > 0)
                {
                    if (restartLeft == 0)
                    {
                        marks.Add((y * width + x) * components);
                        restartLeft = restart;
                        firstLine = true;
                        resetPending = true;
                    }
                    restartLeft--;
                }
                for (var c = 0; c < components; c++)
                {
                    var idx = y * rowLen + x * components + c;
                    int p;
                    if (resetPending) p = init;
                    else if (firstLine) p = x == 0 ? data[idx - rowLen] : data[idx - components];
                    else if (x == 0) p = data[idx - rowLen];
                    else
                    {
                        int ra = data[idx - components], rb = data[idx - rowLen], rc = data[idx - rowLen - components];
                        p = predictor switch
                        {
                            1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            _ => (ra + rb) >> 1,
                        };
                    }
                    var d = (data[idx] - p) & 0xffff;
                    if (d > 32768) d -= 65536;
                    diffs[idx] = d;
                }
                resetPending = false;
            }
            firstLine = false;
        }
        var tables = new (byte[] Counts, byte[] Values)[components];
        for (var c = 0; c < components; c++)
        {
            var freq = new uint[17];
            for (var k = c; k < diffs.Length; k += components) freq[SsssOf(diffs[k])]++;
            tables[c] = CodeLengths(freq);
        }
        var outp = new List<byte> { 0xff, 0xd8, 0xff, 0xc3 };
        var sofLen = (ushort)(8 + 3 * components);
        outp.Add((byte)(sofLen >> 8)); outp.Add((byte)sofLen);
        outp.Add(precision);
        outp.Add((byte)(height >> 8)); outp.Add((byte)height);
        outp.Add((byte)(width >> 8)); outp.Add((byte)width);
        outp.Add((byte)components);
        for (var c = 0; c < components; c++) { outp.Add((byte)(c + 1)); outp.Add(0x11); outp.Add(0); }
        for (var c = 0; c < components; c++)
        {
            outp.Add(0xff); outp.Add(0xc4);
            var dhtLen = (ushort)(2 + 17 + tables[c].Values.Length);
            outp.Add((byte)(dhtLen >> 8)); outp.Add((byte)dhtLen);
            outp.Add((byte)c);
            outp.AddRange(tables[c].Counts);
            outp.AddRange(tables[c].Values);
        }
        if (restart > 0)
        {
            outp.AddRange([0xff, 0xdd, 0, 4, (byte)(restart >> 8), (byte)restart]);
        }
        outp.AddRange([0xff, 0xda]);
        var sosLen = (ushort)(6 + 2 * components);
        outp.Add((byte)(sosLen >> 8)); outp.Add((byte)sosLen);
        outp.Add((byte)components);
        for (var c = 0; c < components; c++) { outp.Add((byte)(c + 1)); outp.Add((byte)(c << 4)); }
        outp.AddRange([predictor, 0, 0]);
        var counts = new byte[components][];
        var values = new byte[components][];
        for (var c = 0; c < components; c++) { counts[c] = tables[c].Counts; values[c] = tables[c].Values; }
        var codes = CanonicalCodes(counts, values);
        var bw = new BitWriter { };
        bw.Out.AddRange(outp);
        var markSet = new HashSet<int>(marks);
        byte rst = 0;
        for (var k = 0; k < diffs.Length; k++)
        {
            if (markSet.Contains(k))
            {
                bw.Flush();
                bw.Out.AddRange([0xff, (byte)(0xd0 + rst)]);
                rst = (byte)((rst + 1) & 7);
            }
            var cc = k % components;
            var s = SsssOf(diffs[k]);
            var (code, len) = codes[cc, s];
            bw.Put(code, len);
            if (s > 0 && s < 16)
            {
                var v = diffs[k] < 0 ? diffs[k] - 1 : diffs[k];
                bw.Put((uint)(v & ((1 << s) - 1)), s);
            }
        }
        bw.Flush();
        bw.Out.AddRange([0xff, 0xd9]);
        return [.. bw.Out];
    }
}
