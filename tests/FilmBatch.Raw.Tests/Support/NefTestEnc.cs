// Nikon Huffman 測試編碼支援（對應 nefc.rs tests 的 Writer／encode／pack；純位元流，無 TIFF 依賴）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Tests.Support;

/// <summary>Nikon Huffman 測試編碼支援（對應 nefc.rs tests 的 Writer／encode／pack）。</summary>
internal static class NefTestEnc
{
    internal sealed class BitWriter
    {
        private readonly List<byte> _out = [];
        private ulong _acc;
        private int _n;

        public void Put(uint v, uint len)
        {
            for (var i = (int)len - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((v >> i) & 1);
                _n++;
                if (_n == 8)
                {
                    _out.Add((byte)_acc);
                    _acc = 0;
                    _n = 0;
                }
            }
        }

        public byte[] Finish()
        {
            if (_n > 0) _out.Add((byte)(_acc << (8 - _n)));
            _out.AddRange(new byte[4]);
            return [.. _out];
        }
    }

    internal static uint Category(int d)
    {
        var u = (uint)Math.Abs(d);
        uint n = 0;
        while (u > 0) { u >>= 1; n++; }
        return n;
    }

    internal static byte[] Encode(int[] values, int w, (ushort Code, byte Length)[] codes, int[,] seeds)
    {
        var wr = new BitWriter();
        var vpred = (int[,])seeds.Clone();
        for (var y = 0; y < values.Length / w; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = values[y * w + x];
                var pred = x < 2 ? vpred[y & 1, x] : values[y * w + x - 2];
                if (x < 2) vpred[y & 1, x] = v;
                var d = v - pred;
                var k = (int)Category(d);
                var (code, len) = codes[k];
                wr.Put((uint)(code >> (12 - len)), len);
                var extra = d < 0 ? d + (1 << k) - 1 : d;
                wr.Put((uint)(extra & ((1 << k) - 1)), (uint)k);
            }
        }
        return wr.Finish();
    }

    internal static int[] TestImage(int w, int h, int max, uint seed)
    {
        var s = seed;
        var outp = new int[w * h];
        for (var i = 0; i < outp.Length; i++)
        {
            var x = i % w;
            var y = i / w;
            s = s * 1_664_525u + 1_013_904_223u;
            var noise = (int)(s >> 24) % 9 - 4;
            var bas = (x * 37 + y * 11) % (max / 2) + ((x / 7 + y / 5) % 3 == 0 ? max / 3 : 0);
            outp[i] = Math.Clamp(bas + noise, 0, max);
        }
        return outp;
    }

    internal static byte[] Pack(string bits)
    {
        var list = new List<bool>();
        foreach (var c in bits)
        {
            if (char.IsWhiteSpace(c)) continue;
            list.Add(c == '1');
        }
        var outp = new List<byte>();
        for (var i = 0; i < list.Count; i += 8)
        {
            byte v = 0;
            for (var k = 0; k < 8; k++)
            {
                v <<= 1;
                var bit = i + k < list.Count ? list[i + k] : true;
                if (bit) v |= 1;
            }
            outp.Add(v);
        }
        return [.. outp];
    }
}
