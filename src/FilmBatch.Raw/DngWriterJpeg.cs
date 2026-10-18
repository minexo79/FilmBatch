// Baseline DCT JPEG 編碼器（有損 DNG 磚塊用）：行為對應 jpeg-encoder 0.7.1
//（MIT OR Apache-2.0；Copyright (c) Mathieu Champlon 2020）的預設非最佳化路徑——
// Annex K 預設 Huffman 表、Annex K 量化表（品質縮放）、4:4:4 交錯單一掃描、無 restart。
// FDCT 為 Loeffler 整數演算法（IJG 衍生；jpeg-encoder 的 fdct.rs 移植自 mozjpeg／Independent JPEG Group）。
// 解碼相容性由 FilmBatch.Raw.Codecs.Jpeg8（同儲存庫 baseline 解碼器）驗證。
// 注意：發布成品須在第三方聲明提及 jpeg-encoder 與 Independent JPEG Group（port 行為所致）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw;

/// <summary>8-bit baseline JPEG（4:4:4）編碼器（有損 DNG 磚塊專用）。輸入為逐列 RGB 位元組。</summary>
internal static class DngWriterJpeg
{
    /// <summary>Zig-zag 順序（T.81 圖 A.6）。</summary>
    private static readonly byte[] Zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20,
        13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59,
        52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly ushort[] AnnexKLuma =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69,
        56, 14, 17, 22, 29, 51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81,
        104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly ushort[] AnnexKChroma =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99,
        99, 47, 66, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] LumaDcLengths = [0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] LumaDcValues = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
    private static readonly byte[] ChromaDcLengths = [0x00, 0x03, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] ChromaDcValues = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
    private static readonly byte[] LumaAcLengths =
    [
        0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D,
    ];
    private static readonly byte[] LumaAcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];
    private static readonly byte[] ChromaAcLengths =
    [
        0x00, 0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77,
    ];
    private static readonly byte[] ChromaAcValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int Fix029 = 2446, Fix039 = 3196, Fix054 = 4433, Fix076 = 6270, Fix089 = 7373;
    private const int Fix117 = 9633, Fix150 = 12299, Fix184 = 15137, Fix196 = 16069, Fix205 = 16819;
    private const int Fix256 = 20995, Fix307 = 25172;

    private static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;

    /// <summary>Loeffler 整數 FDCT（結果整體放大 8 倍；輸入為減 128 後的 i16）。</summary>
    private static void Fdct(short[] d)
    {
        var tmp = new int[64];
        for (var y = 0; y < 8; y++)
        {
            var o = y * 8;
            var t0 = d[o + 0] + d[o + 7]; var t7 = d[o + 0] - d[o + 7];
            var t1 = d[o + 1] + d[o + 6]; var t6 = d[o + 1] - d[o + 6];
            var t2 = d[o + 2] + d[o + 5]; var t5 = d[o + 2] - d[o + 5];
            var t3 = d[o + 3] + d[o + 4]; var t4 = d[o + 3] - d[o + 4];
            var t10 = t0 + t3; var t13 = t0 - t3; var t11 = t1 + t2; var t12 = t1 - t2;
            tmp[o + 0] = (t10 + t11) << Pass1Bits;
            tmp[o + 4] = (t10 - t11) << Pass1Bits;
            var z1 = (t12 + t13) * Fix054;
            tmp[o + 2] = Descale(z1 + t13 * Fix076, ConstBits - Pass1Bits);
            tmp[o + 6] = Descale(z1 + t12 * -Fix184, ConstBits - Pass1Bits);
            z1 = t4 + t7; var z2 = t5 + t6; var z3 = t4 + t6; var z4 = t5 + t7;
            var z5 = (z3 + z4) * Fix117;
            var u4 = t4 * Fix029; var u5 = t5 * Fix205; var u6 = t6 * Fix307; var u7 = t7 * Fix150;
            z1 *= -Fix089; z2 *= -Fix256; z3 *= -Fix196; z4 *= -Fix039;
            z3 += z5; z4 += z5;
            tmp[o + 7] = Descale(u4 + z1 + z3, ConstBits - Pass1Bits);
            tmp[o + 5] = Descale(u5 + z2 + z4, ConstBits - Pass1Bits);
            tmp[o + 3] = Descale(u6 + z2 + z3, ConstBits - Pass1Bits);
            tmp[o + 1] = Descale(u7 + z1 + z4, ConstBits - Pass1Bits);
        }
        var outp = new int[64];
        for (var x = 0; x < 8; x++)
        {
            var t0 = tmp[0 + x] + tmp[56 + x]; var t7 = tmp[0 + x] - tmp[56 + x];
            var t1 = tmp[8 + x] + tmp[48 + x]; var t6 = tmp[8 + x] - tmp[48 + x];
            var t2 = tmp[16 + x] + tmp[40 + x]; var t5 = tmp[16 + x] - tmp[40 + x];
            var t3 = tmp[24 + x] + tmp[32 + x]; var t4 = tmp[24 + x] - tmp[32 + x];
            var t10 = t0 + t3; var t13 = t0 - t3; var t11 = t1 + t2; var t12 = t1 - t2;
            outp[0 + x] = Descale(t10 + t11, Pass1Bits);
            outp[32 + x] = Descale(t10 - t11, Pass1Bits);
            var z1 = (t12 + t13) * Fix054;
            outp[16 + x] = Descale(z1 + t13 * Fix076, ConstBits + Pass1Bits);
            outp[48 + x] = Descale(z1 + t12 * -Fix184, ConstBits + Pass1Bits);
            z1 = t4 + t7; var z2 = t5 + t6; var z3 = t4 + t6; var z4 = t5 + t7;
            var z5 = (z3 + z4) * Fix117;
            var u4 = t4 * Fix029; var u5 = t5 * Fix205; var u6 = t6 * Fix307; var u7 = t7 * Fix150;
            z1 *= -Fix089; z2 *= -Fix256; z3 *= -Fix196; z4 *= -Fix039;
            z3 += z5; z4 += z5;
            outp[56 + x] = Descale(u4 + z1 + z3, ConstBits + Pass1Bits);
            outp[40 + x] = Descale(u5 + z2 + z4, ConstBits + Pass1Bits);
            outp[24 + x] = Descale(u6 + z2 + z3, ConstBits + Pass1Bits);
            outp[8 + x] = Descale(u7 + z1 + z4, ConstBits + Pass1Bits);
        }
        for (var i = 0; i < 64; i++) d[i] = (short)outp[i];
    }

    private sealed class QuantTable
    {
        public readonly byte[] Q = new byte[64];
        private readonly int[] _recip = new int[64];
        private readonly int[] _corr = new int[64];
        private const uint Shift = 15; // 2*8-1

        public QuantTable(ushort[] baseTable, byte quality, bool luma)
        {
            uint q = Math.Clamp(quality, (byte)1, (byte)100);
            uint scale = q < 50 ? 5000 / q : 200 - q * 2;
            for (var i = 0; i < 64; i++)
            {
                uint v = ((uint)baseTable[i] * scale + 50) / 100;
                v = Math.Clamp(v, 1u, 255u);
                Q[i] = (byte)v;
                uint divisor = v << 3;
                var (r, c) = Reciprocal(divisor);
                _recip[i] = r;
                _corr[i] = c;
            }
        }

        private static (int Recip, int Corr) Reciprocal(uint divisor)
        {
            if (divisor <= 1) return (1, 0);
            int recip = (int)((1 << (int)Shift) / divisor);
            int frac = (int)((1 << (int)Shift) % divisor);
            int corr = (int)(divisor / 2);
            if (frac != 0)
            {
                if (frac <= corr) corr += 1;
                else recip += 1;
            }
            return (recip, corr);
        }

        public short Quantize(short value, int index)
        {
            int v = value;
            int abs = Math.Abs(v);
            int product = (abs + _corr[index]) * _recip[index];
            product >>= (int)Shift;
            return (short)(v < 0 ? -product : product);
        }
    }

    private sealed class HuffTable
    {
        public readonly byte[] Lengths = new byte[16];
        public readonly byte[] Values;
        internal readonly (byte Size, ushort Code)[] _lookup = new (byte, ushort)[256];

        public HuffTable(byte[] lengths, byte[] values)
        {
            Array.Copy(lengths, Lengths, 16);
            Values = (byte[])values.Clone();
            var sizes = new byte[256];
            int k = 0;
            for (var i = 0; i < 16; i++)
                for (var j = 0; j < lengths[i]; j++)
                    sizes[k++] = (byte)(i + 1);
            var codes = new ushort[256];
            ushort cur = 0;
            byte curSize = sizes[0];
            for (var i = 0; i < 256 && sizes[i] != 0; i++)
            {
                if (curSize != sizes[i]) { cur <<= sizes[i] - curSize; curSize = sizes[i]; }
                codes[i] = cur++;
            }
            for (var i = 0; i < 256 && sizes[i] != 0; i++)
                _lookup[values[i]] = (sizes[i], codes[i]);
        }
    }

    private sealed class Writer(List<byte> output)
    {
        private ulong _buf;
        private int _free = 64;

        public void U8(byte v) => output.Add(v);
        public void U16(ushort v) { output.Add((byte)(v >> 8)); output.Add((byte)v); }
        public void Marker(byte m) { output.Add(0xff); output.Add(m); }

        private void FlushByte(int free)
        {
            var b = (byte)((_buf >> (64 - 8 - free)) & 0xFF);
            output.Add(b);
            if (b == 0xff) output.Add(0x00);
        }

        private void WriteBuffered()
        {
            if ((_buf & 0x8080808080808080UL & ~(_buf + 0x0101010101010101UL)) != 0)
            {
                for (var i = 0; i < 8; i++) FlushByte(i * 8);
            }
            else
            {
                var be = _buf;
                for (var i = 7; i >= 0; i--) output.Add((byte)(be >> (i * 8)));
            }
        }

        public void Bits(uint value, byte size)
        {
            int s = size;
            ulong v = value;
            int free = _free - s;
            if (free < 0)
            {
                _buf = (_buf << (s + free)) | (v >> -free);
                WriteBuffered();
                _buf = v;
                _free = free + 64;
            }
            else
            {
                _free = free;
                _buf = (_buf << s) | v;
            }
        }

        public void FlushBits()
        {
            while (_free <= 56) { FlushByte(_free); _free += 8; }
        }

        public void FinalizeBits()
        {
            Bits(0x7F, 7);
            FlushBits();
            _buf = 0;
            _free = 64;
        }

        private static (byte Size, ushort Coeff) GetCode(short value)
        {
            // T.81 類別：0 → 0 位元；其餘為 16 位元域的位數
            int abs = Math.Abs(value);
            byte bits = (byte)(31 - System.Numerics.BitOperations.LeadingZeroCount((uint)((abs << 1) | 1)));
            int temp = value - (value < 0 ? 1 : 0);
            ushort coeff = (ushort)(temp & ((1 << bits) - 1));
            return (bits, coeff);
        }

        public void Huff(byte symbol, HuffTable t)
        {
            var (s, c) = t._lookup[symbol];
            Bits(c, s);
        }

        public void HuffValue(byte extraBits, byte symbol, ushort extra, HuffTable t)
        {
            var (s, c) = t._lookup[symbol];
            uint combined = extra;
            combined |= (uint)c << extraBits;
            Bits(combined, (byte)(extraBits + s));
        }

        public void WriteBlock(short[] zigzagBlock, short prevDc, HuffTable dc, HuffTable ac)
        {
            var (s, c) = GetCode((short)(zigzagBlock[0] - prevDc));
            HuffValue(s, s, c, dc);
            var zeroRun = 0;
            for (var i = 1; i < 64; i++)
            {
                var v = zigzagBlock[i];
                if (v == 0) { zeroRun++; continue; }
                while (zeroRun > 15) { Huff(0xF0, ac); zeroRun -= 16; }
                var (ss, cc) = GetCode(v);
                HuffValue(ss, (byte)((zeroRun << 4) | ss), cc, ac);
                zeroRun = 0;
            }
            if (zeroRun > 0) Huff(0x00, ac);
        }
    }

    private static (byte Y, byte Cb, byte Cr) RgbToYCbCr(byte r, byte g, byte b)
    {
        int y = 19595 * r + 38470 * g + 7471 * b;
        int cb = -11059 * r - 21709 * g + 32768 * b + (128 << 16);
        int cr = 32768 * r - 27439 * g - 5329 * b + (128 << 16);
        return ((byte)((y + 0x7FFF) >> 16), (byte)((cb + 0x7FFF) >> 16), (byte)((cr + 0x7FFF) >> 16));
    }

    /// <summary>編碼逐列 RGB（<paramref name="rgb"/> 長度 = width×height×3）為 baseline JPEG。</summary>
    internal static byte[] Encode(byte[] rgb, int width, int height, byte quality)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
            throw RawException.Corrupt("lossy JPEG tile: bad dimensions");
        if ((long)rgb.Length < (long)width * height * 3)
            throw RawException.Corrupt("lossy JPEG tile: short output");
        quality = Math.Clamp(quality, (byte)1, (byte)100);
        var qLuma = new QuantTable(AnnexKLuma, quality, true);
        var qChroma = new QuantTable(AnnexKChroma, quality, false);
        var dcLuma = new HuffTable(LumaDcLengths, LumaDcValues);
        var acLuma = new HuffTable(LumaAcLengths, LumaAcValues);
        var dcChroma = new HuffTable(ChromaDcLengths, ChromaDcValues);
        var acChroma = new HuffTable(ChromaAcLengths, ChromaAcValues);

        var output = new List<byte>(width * height);
        var w = new Writer(output);
        w.Marker(0xD8); // SOI
        w.Marker(0xE0); w.U16(16); // APP0 JFIF
        output.AddRange("JFIF\0"u8.ToArray());
        w.U8(0x01); w.U8(0x02); w.U8(0x00); w.U16(1); w.U16(1); w.U8(0x00); w.U8(0x00);
        WriteDqt(w, 0, qLuma);
        WriteDqt(w, 1, qChroma);
        // SOF0 baseline
        w.Marker(0xC0);
        w.U16((ushort)(8 + 3 * 3));
        w.U8(8);
        w.U16((ushort)height); w.U16((ushort)width);
        w.U8(3);
        w.U8(1); w.U8(0x11); w.U8(0);
        w.U8(2); w.U8(0x11); w.U8(1);
        w.U8(3); w.U8(0x11); w.U8(1);
        WriteDht(w, 0, 0, dcLuma);
        WriteDht(w, 1, 0, acLuma);
        WriteDht(w, 0, 1, dcChroma);
        WriteDht(w, 1, 1, acChroma);
        // SOS
        w.Marker(0xDA);
        w.U16((ushort)(6 + 2 * 3));
        w.U8(3);
        w.U8(1); w.U8(0x00);
        w.U8(2); w.U8(0x11);
        w.U8(3); w.U8(0x11);
        w.U8(0x00); w.U8(0x3F); w.U8(0x00);

        var ycc = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var (y, cb, cr) = RgbToYCbCr(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            ycc[i * 3] = y; ycc[i * 3 + 1] = cb; ycc[i * 3 + 2] = cr;
        }
        int mcuX = (width + 7) / 8, mcuY = (height + 7) / 8;
        var prevDc = new short[3];
        var raw = new short[64];
        var dct = new short[64];
        var q = new short[64];
        for (var my = 0; my < mcuY; my++)
        {
            for (var mx = 0; mx < mcuX; mx++)
            {
                for (var comp = 0; comp < 3; comp++)
                {
                    for (var y = 0; y < 8; y++)
                        for (var x = 0; x < 8; x++)
                        {
                            int sx = Math.Min(mx * 8 + x, width - 1);
                            int sy = Math.Min(my * 8 + y, height - 1);
                            raw[y * 8 + x] = (short)(ycc[(sy * width + sx) * 3 + comp] - 128);
                        }
                    Array.Copy(raw, dct, 64);
                    Fdct(dct);
                    var qt = comp == 0 ? qLuma : qChroma;
                    for (var i = 0; i < 64; i++)
                    {
                        int z = Zigzag[i];
                        q[i] = qt.Quantize(dct[z], z);
                    }
                    w.WriteBlock(q, prevDc[comp], comp == 0 ? dcLuma : dcChroma, comp == 0 ? acLuma : acChroma);
                    prevDc[comp] = q[0];
                }
            }
        }
        w.FinalizeBits();
        w.Marker(0xD9); // EOI
        return [.. output];
    }

    private static void WriteDqt(Writer w, byte dest, QuantTable t)
    {
        w.Marker(0xDB);
        w.U16((ushort)(2 + 1 + 64));
        w.U8(dest);
        for (var i = 0; i < 64; i++) w.U8(t.Q[Zigzag[i]]);
    }

    private static void WriteDht(Writer w, byte cls, byte dest, HuffTable t)
    {
        w.Marker(0xC4);
        w.U16((ushort)(2 + 1 + 16 + t.Values.Length));
        w.U8((byte)((cls << 4) | dest));
        foreach (var l in t.Lengths) w.U8(l);
        foreach (var v in t.Values) w.U8(v);
    }
}
