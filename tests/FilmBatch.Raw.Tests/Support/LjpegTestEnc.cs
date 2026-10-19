// 測試支援：上游 ljpeg::encode 之移植（無失真 JPEG 測試編碼器；SOF3／DHT／SOS 版面與解碼器對應）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tests.Support;

/// <summary>測試用無失真 JPEG 編碼器（lightcraft <c>ljpeg::encode</c>；測試夾具專用，不在 src）。</summary>
internal static class LjpegTestEnc
{
    private sealed class BitWriter
    {
        public readonly List<byte> Out = [];
        private ulong _acc;
        private int _n;

        public void Put(uint v, int bits)
        {
            if (bits == 0) return;
            _acc = (_acc << bits) | (v & (bits >= 32 ? 0xFFFFFFFFu : ((1u << bits) - 1)));
            _n += bits;
            while (_n >= 8)
            {
                byte b = (byte)(_acc >> (_n - 8));
                Out.Add(b);
                if (b == 0xFF) Out.Add(0);
                _n -= 8;
            }
        }

        public void Flush()
        {
            if (_n > 0)
            {
                int pad = 8 - _n;
                Put((1u << pad) - 1u, pad);
            }
        }
    }

    private static byte SsssOf(int diff)
    {
        if (diff == 0) return 0;
        if (diff == 32768 || diff == -32768) return 16;
        return (byte)(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(diff)));
    }

    // T.81 Annex K.2（Figures K.1–K.3），上限 16 位元。
    private static (byte[] Counts, byte[] Values) CodeLengths(uint[] freqIn)
    {
        var freq = new long[18];
        for (var ii = 0; ii < 17; ii++) freq[ii] = freqIn[ii];
        freq[17] = 1;
        var codesize = new int[18];
        var others = new int[18];
        Array.Fill(others, -1);
        while (true)
        {
            int v1 = -1, v2 = -1;
            for (var ii = 0; ii < 18; ii++)
                if (freq[ii] > 0 && (v1 < 0 || freq[ii] <= freq[v1])) v1 = ii;
            for (var ii = 0; ii < 18; ii++)
                if (freq[ii] > 0 && ii != v1 && (v2 < 0 || freq[ii] <= freq[v2])) v2 = ii;
            if (v2 < 0) break;
            freq[v1] += freq[v2];
            freq[v2] = 0;
            int x = v1;
            codesize[x]++;
            while (others[x] >= 0) { x = others[x]; codesize[x]++; }
            others[x] = v2;
            x = v2;
            codesize[x]++;
            while (others[x] >= 0) { x = others[x]; codesize[x]++; }
        }
        var bits = new int[33];
        foreach (var cs in codesize)
            if (cs > 0) bits[Math.Min(cs, 32)]++;
        int i = 32;
        while (i > 16)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
            i--;
        }
        while (bits[i] == 0) i--;
        bits[i]--;
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
        for (var t = 0; t < counts.Length; t++)
        {
            uint code = 0;
            int k = 0;
            for (byte len = 1; len <= 16; len++)
            {
                for (var j = 0; j < counts[t][len - 1]; j++)
                {
                    if (k < values[t].Length && values[t][k] < 17)
                        table[t, values[t][k]] = (code, len);
                    code++;
                    k++;
                }
                code <<= 1;
            }
        }
        return table;
    }

    /// <summary>編碼交錯樣本（predictor 1–7；restart &gt; 0 時每 restart 個像素插 restart 標記）。</summary>
    internal static byte[] Encode(ushort[] data, int width, int height, int components, byte precision, byte predictor, int restart)
    {
        if (components is < 1 or > 4 || precision is < 2 or > 16 || predictor is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(components));
        if (data.Length != width * height * components)
            throw new ArgumentException("Data length does not match dimensions.", nameof(data));
        int rowLen = width * components;
        int init = 1 << (precision - 1);
        var diffs = new int[data.Length];
        int restartLeft = restart;
        bool firstLine = true, resetPending = true;
        var marks = new List<int>();
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
                    int idx = y * rowLen + x * components + c;
                    int p = resetPending ? init
                        : firstLine ? (x == 0 ? data[idx - rowLen] : data[idx - components])
                        : x == 0 ? data[idx - rowLen]
                        : predictor switch
                        {
                            1 => data[idx - components],
                            2 => data[idx - rowLen],
                            3 => data[idx - rowLen - components],
                            4 => data[idx - components] + data[idx - rowLen] - data[idx - rowLen - components],
                            5 => data[idx - components] + ((data[idx - rowLen] - data[idx - rowLen - components]) >> 1),
                            6 => data[idx - rowLen] + ((data[idx - components] - data[idx - rowLen - components]) >> 1),
                            _ => (data[idx - components] + data[idx - rowLen]) >> 1,
                        };
                    int d = (data[idx] - p) & 0xFFFF;
                    if (d > 32768) d -= 65536;
                    diffs[idx] = d;
                }
                resetPending = false;
            }
            firstLine = false;
        }
        var counts = new byte[components][];
        var values = new byte[components][];
        for (var c = 0; c < components; c++)
        {
            var freq = new uint[17];
            for (var k = c; k < diffs.Length; k += components) freq[SsssOf(diffs[k])]++;
            (counts[c], values[c]) = CodeLengths(freq);
        }
        var output = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC3 };
        int sofLen = 8 + 3 * components;
        output.Add((byte)(sofLen >> 8)); output.Add((byte)sofLen);
        output.Add(precision);
        output.Add((byte)(height >> 8)); output.Add((byte)height);
        output.Add((byte)(width >> 8)); output.Add((byte)width);
        output.Add((byte)components);
        for (var c = 0; c < components; c++) output.AddRange([(byte)(c + 1), (byte)0x11, (byte)0]);
        for (var c = 0; c < components; c++)
        {
            output.AddRange([0xFF, 0xC4]);
            int segLen = 2 + 17 + values[c].Length;
            output.Add((byte)(segLen >> 8)); output.Add((byte)segLen);
            output.Add((byte)c);
            output.AddRange(counts[c]);
            output.AddRange(values[c]);
        }
        if (restart > 0)
        {
            output.AddRange([0xFF, 0xDD, 0, 4]);
            output.Add((byte)(restart >> 8)); output.Add((byte)restart);
        }
        output.AddRange([0xFF, 0xDA]);
        int sosLen = 6 + 2 * components;
        output.Add((byte)(sosLen >> 8)); output.Add((byte)sosLen);
        output.Add((byte)components);
        for (var c = 0; c < components; c++) output.AddRange([(byte)(c + 1), (byte)(c << 4)]);
        output.AddRange([predictor, 0, 0]);
        var codes = CanonicalCodes(counts, values);
        var bw = new BitWriter();
        int mi = 0, rst = 0;
        for (var k = 0; k < diffs.Length; k++)
        {
            if (mi < marks.Count && marks[mi] == k)
            {
                mi++;
                bw.Flush();
                output.AddRange(bw.Out);
                output.AddRange([0xFF, (byte)(0xD0 + rst)]);
                rst = (rst + 1) & 7;
                bw = new BitWriter();
            }
            int cc = k % components;
            byte s = SsssOf(diffs[k]);
            var (code, len) = codes[cc, s];
            bw.Put(code, len);
            if (s > 0 && s < 16)
            {
                int v = diffs[k] < 0 ? diffs[k] - 1 : diffs[k];
                bw.Put((uint)v & ((1u << s) - 1u), s);
            }
        }
        bw.Flush();
        output.AddRange(bw.Out);
        output.AddRange([0xFF, 0xD9]);
        return [.. output];
    }
}
