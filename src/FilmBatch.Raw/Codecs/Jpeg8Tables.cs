// 移植自 zune-jpeg 0.5.15（MIT OR Apache-2.0 OR Zlib；Copyright (c) 2023 zune-jpeg contributors）
// 的 Huffman／IDCT／上採樣／色彩轉換（src/huffman.rs、idct/scalar.rs、upsampler/scalar.rs、
// color_convert/scalar.rs；純量路徑，與各平台的向量路徑位元一致）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Codecs;

/// <summary>Huffman 表（zune <c>HuffmanTable</c>；9 位元前瞻＋慢速路徑）。</summary>
internal sealed class JpegHuffman
{
    public const int Lookahead = 9;

    public int[] Maxcode = new int[18];
    public int[] Offset = new int[18];
    public int[] Lookup = new int[1 << Lookahead];
    public int[]? AcLookup;
    public byte[] Values = new byte[256];

    public static JpegHuffman Create(byte[] bits, byte[] values, bool isDc)
    {
        var t = new JpegHuffman();
        int tooLong = (Lookahead + 1) << Lookahead;
        Array.Fill(t.Lookup, tooLong);
        t.Values = values;
        t.BuildDerived(bits, isDc);
        return t;
    }
    /// <summary>短值表（MJPEG 預設表）專用：補零到 256 後再建表（即 zune <c>new_unfilled</c>）。</summary>
    public static JpegHuffman Create(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values, bool isDc)
    {
        var b = new byte[17];
        var v = new byte[256];
        for (var i = 0; i < Math.Min(17, bits.Length); i++) b[i] = bits[i];
        for (var i = 0; i < Math.Min(256, values.Length); i++) v[i] = values[i];
        return Create(b, v, isDc);
    }

    private void BuildDerived(byte[] bits, bool isDc)
    {
        var huffSize = new byte[257];
        var huffCode = new uint[257];
        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int i = bits[l];
            while (i != 0)
            {
                huffSize[p++] = (byte)l;
                i--;
            }
        }
        huffSize[p] = 0;
        int numSymbols = p;
        uint code = 0;
        int si = huffSize[0];
        p = 0;
        while (huffSize[p] != 0)
        {
            while (huffSize[p] == si)
            {
                huffCode[p] = code;
                code++;
                p++;
            }
            Maxcode[si] = (int)(code << (16 - si));
            if ((int)code >= 1 << si)
                throw RawException.Corrupt("lossy JPEG tile: bad Huffman table");
            code <<= 1;
            si++;
        }
        p = 0;
        for (int l = 0; l <= 16; l++)
        {
            if (bits[l] == 0)
            {
                Maxcode[l] = -1;
            }
            else
            {
                Offset[l] = p - (int)huffCode[p];
                p += bits[l];
            }
        }
        Offset[17] = 0;
        Maxcode[17] = 0x000F_FFFF;
        p = 0;
        for (int l = 1; l <= Lookahead; l++)
        {
            for (int i = 0; i < bits[l]; i++)
            {
                int look = (int)(huffCode[p] << (Lookahead - l));
                for (int j = 0; j < 1 << (Lookahead - l); j++)
                    Lookup[look++] = (l << Lookahead) | Values[p];
                p++;
            }
        }
        if (!isDc)
        {
            var fast = new short[1 << Lookahead];
            Array.Fill(fast, (short)255);
            for (int i = 0; i < numSymbols; i++)
            {
                int s = huffSize[i];
                if (s <= Lookahead)
                {
                    int c = (int)(huffCode[i] << (Lookahead - s));
                    int m = 1 << (Lookahead - s);
                    for (int j = 0; j < m; j++)
                        fast[c + j] = (short)i;
                }
            }
            var fastAc = new int[1 << Lookahead];
            for (int i = 0; i < 1 << Lookahead; i++)
            {
                short fv = fast[i];
                if (fv < 255)
                {
                    byte rs = Values[fv];
                    short run = (short)((rs >> 4) & 15);
                    short mag = (short)(rs & 15);
                    short len = huffSize[fv];
                    if (mag != 0 && len + mag <= Lookahead)
                    {
                        int k = (((i << len) & ((1 << Lookahead) - 1)) >> (Lookahead - mag));
                        int m = 1 << (mag - 1);
                        if (k < m)
                            k += (int)((~0 << mag) + 1);
                        if (k is >= -128 and <= 127)
                            fastAc[i] = (k << 8) + (run << 4) + (len + mag);
                    }
                }
            }
            AcLookup = fastAc;
        }
        if (isDc)
        {
            for (int i = 0; i < numSymbols; i++)
            {
                if (Values[i] > 15)
                    throw RawException.Corrupt("lossy JPEG tile: bad Huffman table");
            }
        }
    }
}

/// <summary>IDCT／上採樣／YCbCr→RGB（zune 純量路徑）。整數運算與上游完全同序。</summary>
internal static class JpegDsp
{
    public static readonly int[] UnZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private const int ScaleBits = 512 + 65536 + (128 << 17);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Wa(int a, int b) => unchecked(a + b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Ws(int a, int b) => unchecked(a - b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Wm(int a, int b) => unchecked(a * b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int F2F(float x) => (int)(x * 4096.0f + 0.5f);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Fsh(int x) => unchecked(x << 12);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static short Clamp16(int a) => (short)Math.Clamp(a, 0, 255);

    public static void Idct1x1(int[] vin, Span<short> vout, int stride)
    {
        int c = Math.Clamp(unchecked(((vin[0] + 4) + 1024) >> 3), 0, 255);
        for (int r = 0; r < 8; r++)
        {
            var row = vout.Slice(r * stride, 8);
            for (int i = 0; i < 8; i++)
                row[i] = (short)c;
        }
    }

    public static void Idct(int[] vin, Span<short> vout, int stride)
    {
        bool dcOnly = true;
        for (int i = 1; i < 64; i++)
        {
            if (vin[i] != 0) { dcOnly = false; break; }
        }
        if (dcOnly)
        {
            Idct1x1(vin, vout, stride);
            return;
        }
        for (int ptr = 0; ptr < 8; ptr++)
        {
            int p2 = vin[ptr + 16];
            int p3 = vin[ptr + 48];
            int p1 = Wm(Wa(p2, p3), 2217);
            int t2 = Wa(p1, Wm(p3, -7567));
            int t3 = Wa(p1, Wm(p2, 3135));
            p2 = vin[ptr];
            p3 = vin[ptr + 32];
            int t0 = Fsh(Wa(p2, p3));
            int t1 = Fsh(Ws(p2, p3));
            int x0 = Wa(Wa(t0, t3), 512);
            int x3 = Wa(Ws(t0, t3), 512);
            int x1 = Wa(Wa(t1, t2), 512);
            int x2 = Wa(Ws(t1, t2), 512);
            int u0 = vin[ptr + 56];
            int u1 = vin[ptr + 40];
            int u2 = vin[ptr + 24];
            int u3 = vin[ptr + 8];
            int q3 = Wa(u0, u2);
            int q4 = Wa(u1, u3);
            int q1 = Wa(u0, u3);
            int q2 = Wa(u1, u2);
            int q5 = Wm(Wa(q3, q4), 4816);
            u0 = Wm(u0, 1223);
            u1 = Wm(u1, 8410);
            u2 = Wm(u2, 12586);
            u3 = Wm(u3, 6149);
            q1 = Wa(q5, Wm(q1, -3685));
            q2 = Wa(q5, Wm(q2, -10497));
            q3 = Wm(q3, -8034);
            q4 = Wm(q4, -1597);
            u3 = Wa(u3, Wa(q1, q4));
            u2 = Wa(u2, Wa(q2, q3));
            u1 = Wa(u1, Wa(q2, q4));
            u0 = Wa(u0, Wa(q1, q3));
            unchecked
            {
                vin[ptr] = Ws(Wa(x0, u3), 0) >> 10;
                vin[ptr + 8] = Ws(Wa(x1, u2), 0) >> 10;
                vin[ptr + 16] = Ws(Wa(x2, u1), 0) >> 10;
                vin[ptr + 24] = Ws(Wa(x3, u0), 0) >> 10;
                vin[ptr + 32] = Ws(Ws(x3, u0), 0) >> 10;
                vin[ptr + 40] = Ws(Ws(x2, u1), 0) >> 10;
                vin[ptr + 48] = Ws(Ws(x1, u2), 0) >> 10;
                vin[ptr + 56] = Ws(Ws(x0, u3), 0) >> 10;
            }
        }
        int pos = 0;
        for (int i = 0; i < 64; i += 8)
        {
            int p2 = vin[i + 2];
            int p3 = vin[i + 6];
            int p1 = Wm(Wa(p2, p3), 2217);
            int t2 = Wa(p1, Wm(p3, -7567));
            int t3 = Wa(p1, Wm(p2, 3135));
            p2 = vin[i];
            p3 = vin[i + 4];
            int t0 = Fsh(Wa(p2, p3));
            int t1 = Fsh(Ws(p2, p3));
            int x0 = Wa(Wa(t0, t3), ScaleBits);
            int x3 = Wa(Ws(t0, t3), ScaleBits);
            int x1 = Wa(Wa(t1, t2), ScaleBits);
            int x2 = Wa(Ws(t1, t2), ScaleBits);
            int u0 = vin[i + 7];
            int u1 = vin[i + 5];
            int u2 = vin[i + 3];
            int u3 = vin[i + 1];
            int q3 = Wa(u0, u2);
            int q4 = Wa(u1, u3);
            int q1 = Wa(u0, u3);
            int q2 = Wa(u1, u2);
            int q5 = Wm(Wa(q3, q4), F2F(1.175875602f));
            u0 = Wm(u0, 1223);
            u1 = Wm(u1, 8410);
            u2 = Wm(u2, 12586);
            u3 = Wm(u3, 6149);
            q1 = Wa(q5, Wm(q1, -3685));
            q2 = Wa(q5, Wm(q2, -10497));
            q3 = Wm(q3, -8034);
            q4 = Wm(q4, -1597);
            u3 = Wa(u3, Wa(q1, q4));
            u2 = Wa(u2, Wa(q2, q3));
            u1 = Wa(u1, Wa(q2, q4));
            u0 = Wa(u0, Wa(q1, q3));
            unchecked
            {
                vout[pos] = Clamp16(Wa(x0, u3) >> 17);
                vout[pos + 1] = Clamp16(Wa(x1, u2) >> 17);
                vout[pos + 2] = Clamp16(Wa(x2, u1) >> 17);
                vout[pos + 3] = Clamp16(Wa(x3, u0) >> 17);
                vout[pos + 4] = Clamp16(Ws(x3, u0) >> 17);
                vout[pos + 5] = Clamp16(Ws(x2, u1) >> 17);
                vout[pos + 6] = Clamp16(Ws(x1, u2) >> 17);
                vout[pos + 7] = Clamp16(Ws(x0, u3) >> 17);
            }
            pos += stride;
        }
    }

    /// <summary>4x4 IDCT（zune idct4x4；呼叫端保證只有前 4x4 有係數，返回時清後半）。</summary>
    public static void Idct4x4(int[] vin, Span<short> vout, int stride)
    {
        for (int ptr = 0; ptr < 4; ptr++)
        {
            int i0 = Wa(Fsh(vin[ptr]), 512);
            int i2 = vin[ptr + 16];
            int pp1 = Wm(i2, 2217);
            int pp3 = Wm(i2, 5352);
            int x0 = Wa(i0, pp3);
            int x1 = Wa(i0, pp1);
            int x2 = Ws(i0, pp1);
            int x3 = Ws(i0, pp3);
            int i4 = vin[ptr + 24];
            int i3 = vin[ptr + 8];
            int p5 = Wm(Wa(i4, i3), 4816);
            pp1 = Wa(p5, Wm(i3, -3685));
            int p2 = Wa(p5, Wm(i4, -10497));
            int tt3 = Wa(p5, Wm(i3, 867));
            int tt2 = Wa(p5, Wm(i4, -5945));
            int tt1 = Wa(p2, Wm(i3, -1597));
            int tt0 = Wa(pp1, Wm(i4, -8034));
            unchecked
            {
                vin[ptr] = Wa(x0, tt3) >> 10;
                vin[ptr + 8] = Wa(x1, tt2) >> 10;
                vin[ptr + 16] = Wa(x2, tt1) >> 10;
                vin[ptr + 24] = Wa(x3, tt0) >> 10;
                vin[ptr + 32] = Ws(x3, tt0) >> 10;
                vin[ptr + 40] = Ws(x2, tt1) >> 10;
                vin[ptr + 48] = Ws(x1, tt2) >> 10;
                vin[ptr + 56] = Ws(x0, tt3) >> 10;
            }
        }
        int pos = 0;
        for (int row = 0; row < 8; row++)
        {
            int i = row * 8;
            int i2 = vin[i + 2];
            int i0 = vin[i];
            int t0 = Wa(Fsh(i0), ScaleBits);
            int t2 = Wm(i2, 2217);
            int t3 = Wm(i2, 5352);
            int x0 = Wa(t0, t3);
            int x3 = Ws(t0, t3);
            int x1 = Wa(t0, t2);
            int x2 = Ws(t0, t2);
            int i3 = vin[i + 3];
            int i1 = vin[i + 1];
            int p5 = Wm(Wa(i3, i1), F2F(1.175875602f));
            int q1 = Wa(p5, Wm(i1, -3685));
            int q2 = Wa(p5, Wm(i3, -10497));
            t3 = Wa(p5, Wm(i1, 867));
            t2 = Wa(p5, Wm(i3, -5945));
            int t1 = Wa(q2, Wm(i1, -1597));
            t0 = Wa(q1, Wm(i3, -8034));
            unchecked
            {
                vout[pos] = Clamp16(Wa(x0, t3) >> 17);
                vout[pos + 1] = Clamp16(Wa(x1, t2) >> 17);
                vout[pos + 2] = Clamp16(Wa(x2, t1) >> 17);
                vout[pos + 3] = Clamp16(Wa(x3, t0) >> 17);
                vout[pos + 4] = Clamp16(Ws(x3, t0) >> 17);
                vout[pos + 5] = Clamp16(Ws(x2, t1) >> 17);
                vout[pos + 6] = Clamp16(Ws(x1, t2) >> 17);
                vout[pos + 7] = Clamp16(Ws(x0, t3) >> 17);
            }
            pos += stride;
        }
        Array.Clear(vin, 32, 4);
        Array.Clear(vin, 40, 4);
        Array.Clear(vin, 48, 4);
        Array.Clear(vin, 56, 4);
    }

    // ---- 上採樣（zune upsampler/scalar.rs） ----

    public static void UpsampleH(ReadOnlySpan<short> input, Span<short> output)
    {
        output[0] = input[0];
        output[1] = (short)((input[0] * 3 + input[1] + 2) >> 2);
        int o = 2;
        for (int i = 1; i < input.Length - 1; i++)
        {
            int s = input[i] * 3 + 2;
            output[o++] = (short)((s + input[i - 1]) >> 2);
            output[o++] = (short)((s + input[i + 1]) >> 2);
        }
        int n = input.Length;
        output[o++] = (short)((3 * input[n - 1] + input[n - 2] + 2) >> 2);
        output[o] = input[n - 1];
    }

    public static void UpsampleV(ReadOnlySpan<short> input, ReadOnlySpan<short> near, ReadOnlySpan<short> far, Span<short> output)
    {
        // 上游語義：input＝當前列；near＝上一列，far＝下一列。
        // out_top＝(3*input+near)/4，out_bottom＝(3*input+far)/4。
        int half = output.Length / 2;
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (short)((3 * input[i] + 2 + near[i]) >> 2);
            output[half + i] = (short)((3 * input[i] + 2 + far[i]) >> 2);
        }
    }

    public static void UpsampleHv(ReadOnlySpan<short> input, ReadOnlySpan<short> near, ReadOnlySpan<short> far, Span<short> scratch, Span<short> output)
    {
        int half = input.Length;
        UpsampleV(input, near, far, scratch.Slice(0, half * 2));
        int oh = output.Length / 2;
        UpsampleH(scratch.Slice(0, half), output.Slice(0, oh));
        UpsampleH(scratch.Slice(half, half), output.Slice(oh, oh));
    }

    public static void UpsampleGeneric(ReadOnlySpan<short> input, Span<short> output, int factor)
    {
        for (int i = 0; i < input.Length; i++)
        {
            var dst = output.Slice(i * factor, factor);
            for (int j = 0; j < factor; j++)
                dst[j] = input[i];
        }
    }

    // ---- 色彩轉換（zune color_convert/scalar.rs；BT.601 全幅，14 位元定點） ----

    private const short YCf = 16384;
    private const short CrCf = 22970;
    private const short CbCf = 29032;
    private const short GCrCf = -11700;
    private const short GCbCf = -5638;
    private const int YuvRnd = (1 << 13) - 1;

    private static byte YuvClamp(int a) => (byte)Math.Clamp(a, 0, 255);

    public static void YcbcrToRgb(ReadOnlySpan<short> y, ReadOnlySpan<short> cb, ReadOnlySpan<short> cr, Span<byte> output, int outCh)
    {
        for (int i = 0; i < y.Length; i++)
        {
            int cbr = cb[i] - 128;
            int crr = cr[i] - 128;
            int y0 = y[i] * YCf + YuvRnd;
            int r = (y0 + crr * CrCf) >> 14;
            int g = (y0 + crr * GCrCf + cbr * GCbCf) >> 14;
            int b = (y0 + cbr * CbCf) >> 14;
            output[i * outCh] = YuvClamp(r);
            output[i * outCh + 1] = YuvClamp(g);
            output[i * outCh + 2] = YuvClamp(b);
            if (outCh == 4)
                output[i * outCh + 3] = 255;
        }
    }
    /// <summary>補上 MJPEG 省略的 Huffman 預設表（JPEG Annex K.3；即 zune <c>fill_default_mjpeg_tables</c>）。已存在的表不覆寫。</summary>
    public static void FillMjpegTables(JpegHuffman?[] dc, JpegHuffman?[] ac)
    {
        if (dc.Length > 0 && dc[0] is null)
            dc[0] = JpegHuffman.Create((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
                (ReadOnlySpan<byte>)[0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B],
                true);
        if (dc.Length > 1 && dc[1] is null)
            dc[1] = JpegHuffman.Create((ReadOnlySpan<byte>)[0x00, 0x00, 0x03, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00],
                (ReadOnlySpan<byte>)[0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B],
                true);
        if (ac.Length > 0 && ac[0] is null)
            ac[0] = JpegHuffman.Create((ReadOnlySpan<byte>)[0x00, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D],
                (ReadOnlySpan<byte>)[
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
                ],
                false);
        if (ac.Length > 1 && ac[1] is null)
            ac[1] = JpegHuffman.Create((ReadOnlySpan<byte>)[0x00, 0x00, 0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77],
                (ReadOnlySpan<byte>)[
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
                ],
                false);
    }

    public static byte Blinn8x8(byte v, byte k)
    {
        int t = v * k + 128;
        return (byte)((t + (t >> 8)) >> 8);
    }
}
