// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0）`idct.rs` 的純量路徑：
// `choose_idct_size`、`dequantize_and_idct_block`（8x8 stb、`kernel_x/kernel_t/kernel`、
// 4x4/2x2/1x1 縮放 IDCT、stbi_clamp/f2f/fsh），整數運算一律 wrapping（上游 `Wrapping<i32>`）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Codecs;

/// <summary>IDCT（上游 <c>idct</c> 純量路徑；x86 SSSE3／NEON 向量路徑與此位元一致）。</summary>
internal static class JpegIdct
{
    /// <summary>
    /// 選擇 IDCT 尺寸（上游 <c>choose_idct_size</c>）：<c>scaled(len,s)=(len*s-1)/8+1</c>，
    /// 取寬或高仍覆蓋請求盒的最小 <c>s ∈ {1,2,4}</c>，否則 8。
    /// </summary>
    internal static int ChooseIdctSize(int fullW, int fullH, int reqW, int reqH)
    {
        for (int k = 0; k < 3; k++)
        {
            int s = 1 << k;
            if ((fullW * s - 1) / 8 + 1 >= reqW || (fullH * s - 1) / 8 + 1 >= reqH)
                return s;
        }
        return 8;
    }

    internal static void DequantizeAndIdctBlock(int scale, ReadOnlySpan<short> coeff, ReadOnlySpan<ushort> qt, int lineStride, Span<byte> output, int outOff)
    {
        switch (scale)
        {
            case 8: Block8x8(coeff, qt, lineStride, output, outOff); break;
            case 4: Block4x4(coeff, qt, lineStride, output, outOff); break;
            case 2: Block2x2(coeff, qt, lineStride, output, outOff); break;
            case 1: Block1x1(coeff, qt, output, outOff); break;
            default: throw RawException.Corrupt($"preview JPEG: unsupported IDCT scale {scale}/8");
        }
    }

    private static void Block8x8(ReadOnlySpan<short> c, ReadOnlySpan<ushort> q, int lineStride, Span<byte> output, int outOff)
    {
        // 上游 dequantize_and_idct_block_8x8_inner（stb）：先縱向 8 列、再橫向 8 行。
        Span<int> tmp = stackalloc int[64];
        for (int i = 0; i < 8; i++)
        {
            if (c[i + 8] == 0 && c[i + 16] == 0 && c[i + 24] == 0 && c[i + 32] == 0
                && c[i + 40] == 0 && c[i + 48] == 0 && c[i + 56] == 0)
            {
                int dcterm = unchecked(c[i] * q[i]) << 2;
                tmp[i] = dcterm;
                tmp[i + 8] = dcterm;
                tmp[i + 16] = dcterm;
                tmp[i + 24] = dcterm;
                tmp[i + 32] = dcterm;
                tmp[i + 40] = dcterm;
                tmp[i + 48] = dcterm;
                tmp[i + 56] = dcterm;
            }
            else
            {
                Kernel(
                    unchecked(c[i] * q[i]),
                    unchecked(c[i + 8] * q[i + 8]),
                    unchecked(c[i + 16] * q[i + 16]),
                    unchecked(c[i + 24] * q[i + 24]),
                    unchecked(c[i + 32] * q[i + 32]),
                    unchecked(c[i + 40] * q[i + 40]),
                    unchecked(c[i + 48] * q[i + 48]),
                    unchecked(c[i + 56] * q[i + 56]),
                    512,
                    out int x0, out int x1, out int x2, out int x3,
                    out int t0, out int t1, out int t2, out int t3);
                tmp[i] = unchecked((x0 + t3) >> 10);
                tmp[i + 56] = unchecked((x0 - t3) >> 10);
                tmp[i + 8] = unchecked((x1 + t2) >> 10);
                tmp[i + 48] = unchecked((x1 - t2) >> 10);
                tmp[i + 16] = unchecked((x2 + t1) >> 10);
                tmp[i + 40] = unchecked((x2 - t1) >> 10);
                tmp[i + 24] = unchecked((x3 + t0) >> 10);
                tmp[i + 32] = unchecked((x3 - t0) >> 10);
            }
        }
        const int XScale = 65536 + (128 << 17);
        for (int r = 0; r < 8; r++)
        {
            int baseOff = outOff + r * lineStride;
            bool restZero = true;
            for (int k = 1; k < 8; k++)
            {
                if (tmp[r * 8 + k] != 0)
                {
                    restZero = false;
                    break;
                }
            }
            if (restZero)
            {
                byte v = StbiClamp(unchecked((StbiFsh(tmp[r * 8]) + XScale) >> 17));
                for (int k = 0; k < 8; k++)
                    output[baseOff + k] = v;
            }
            else
            {
                Kernel(tmp[r * 8], tmp[r * 8 + 1], tmp[r * 8 + 2], tmp[r * 8 + 3],
                    tmp[r * 8 + 4], tmp[r * 8 + 5], tmp[r * 8 + 6], tmp[r * 8 + 7],
                    XScale,
                    out int x0, out int x1, out int x2, out int x3,
                    out int t0, out int t1, out int t2, out int t3);
                output[baseOff] = StbiClamp(unchecked((x0 + t3) >> 17));
                output[baseOff + 7] = StbiClamp(unchecked((x0 - t3) >> 17));
                output[baseOff + 1] = StbiClamp(unchecked((x1 + t2) >> 17));
                output[baseOff + 6] = StbiClamp(unchecked((x1 - t2) >> 17));
                output[baseOff + 2] = StbiClamp(unchecked((x2 + t1) >> 17));
                output[baseOff + 5] = StbiClamp(unchecked((x2 - t1) >> 17));
                output[baseOff + 3] = StbiClamp(unchecked((x3 + t0) >> 17));
                output[baseOff + 4] = StbiClamp(unchecked((x3 - t0) >> 17));
            }
        }
    }

    private static void Kernel(int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7, int xScale,
        out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3)
    {
        // kernel_x（偶索引）
        int p2x = s2, p3x = s6;
        int p1x = unchecked((p2x + p3x) * StbiF2f(0.5411961f));
        int tx2 = unchecked(p1x + p3x * StbiF2f(-1.847759065f));
        int tx3 = unchecked(p1x + p2x * StbiF2f(0.765366865f));
        int tx0 = unchecked((s0 + s4) << 12);
        int tx1 = unchecked((s0 - s4) << 12);
        x0 = unchecked(tx0 + tx3 + xScale);
        x3 = unchecked(tx0 - tx3 + xScale);
        x1 = unchecked(tx1 + tx2 + xScale);
        x2 = unchecked(tx1 - tx2 + xScale);
        // kernel_t（奇索引）
        int tt0 = s7, tt1 = s5, tt2 = s3, tt3 = s1;
        int pp3 = unchecked(tt0 + tt2);
        int pp4 = unchecked(tt1 + tt3);
        int pp1 = unchecked(tt0 + tt3);
        int pp2 = unchecked(tt1 + tt2);
        int pp5 = unchecked((pp3 + pp4) * StbiF2f(1.175875602f));
        tt0 = unchecked(tt0 * StbiF2f(0.298631336f));
        tt1 = unchecked(tt1 * StbiF2f(2.053119869f));
        tt2 = unchecked(tt2 * StbiF2f(3.072711026f));
        tt3 = unchecked(tt3 * StbiF2f(1.501321110f));
        pp1 = unchecked(pp5 + pp1 * StbiF2f(-0.899976223f));
        pp2 = unchecked(pp5 + pp2 * StbiF2f(-2.562915447f));
        pp3 = unchecked(pp3 * StbiF2f(-1.961570560f));
        pp4 = unchecked(pp4 * StbiF2f(-0.390180644f));
        t3 = unchecked(tt3 + pp1 + pp4);
        t2 = unchecked(tt2 + pp2 + pp3);
        t1 = unchecked(tt1 + pp2 + pp4);
        t0 = unchecked(tt0 + pp1 + pp3);
    }

    private static void Block4x4(ReadOnlySpan<short> c, ReadOnlySpan<ushort> q, int lineStride, Span<byte> output, int outOff)
    {
        Span<int> tmp = stackalloc int[16];
        const int ConstBits = 12;
        const int Pass1Bits = 2;
        const int FinalBits = ConstBits + Pass1Bits + 3;
        for (int i = 0; i < 4; i++)
        {
            int s0 = unchecked(c[i] * q[i]);
            int s1 = unchecked(c[i + 8] * q[i + 8]);
            int s2 = unchecked(c[i + 16] * q[i + 16]);
            int s3 = unchecked(c[i + 24] * q[i + 24]);
            int x0 = unchecked((s0 + s2) << Pass1Bits);
            int x2 = unchecked((s0 - s2) << Pass1Bits);
            int p1 = unchecked((s1 + s3) * StbiF2f(0.541196100f));
            int t0 = unchecked(p1 + s3 * StbiF2f(-1.847759065f) + 512) >> (ConstBits - Pass1Bits);
            int t2 = unchecked(p1 + s1 * StbiF2f(0.765366865f) + 512) >> (ConstBits - Pass1Bits);
            tmp[i] = unchecked(x0 + t2);
            tmp[i + 12] = unchecked(x0 - t2);
            tmp[i + 4] = unchecked(x2 + t0);
            tmp[i + 8] = unchecked(x2 - t0);
        }
        for (int i = 0; i < 4; i++)
        {
            int s0 = tmp[i * 4], s1 = tmp[i * 4 + 1], s2 = tmp[i * 4 + 2], s3 = tmp[i * 4 + 3];
            int x0 = unchecked((s0 + s2) << ConstBits);
            int x2 = unchecked((s0 - s2) << ConstBits);
            int p1 = unchecked((s1 + s3) * StbiF2f(0.541196100f));
            int t0 = unchecked(p1 + s3 * StbiF2f(-1.847759065f));
            int t2 = unchecked(p1 + s1 * StbiF2f(0.765366865f));
            int add = unchecked((1 << (FinalBits - 1)) + (128 << FinalBits));
            x0 = unchecked(x0 + add);
            x2 = unchecked(x2 + add);
            int o = outOff + i * lineStride;
            output[o] = StbiClamp(unchecked((x0 + t2) >> FinalBits));
            output[o + 3] = StbiClamp(unchecked((x0 - t2) >> FinalBits));
            output[o + 1] = StbiClamp(unchecked((x2 + t0) >> FinalBits));
            output[o + 2] = StbiClamp(unchecked((x2 - t0) >> FinalBits));
        }
    }

    private static void Block2x2(ReadOnlySpan<short> c, ReadOnlySpan<ushort> q, int lineStride, Span<byte> output, int outOff)
    {
        const int ScaleBits = 3;
        int s00 = unchecked(c[0] * q[0]);
        int s10 = unchecked(c[8] * q[8]);
        int x0 = unchecked(s00 + s10);
        int x2 = unchecked(s00 - s10);
        int s01 = unchecked(c[1] * q[1]);
        int s11 = unchecked(c[9] * q[9]);
        int x1 = unchecked(s01 + s11);
        int x3 = unchecked(s01 - s11);
        int add = unchecked((1 << (ScaleBits - 1)) + (128 << ScaleBits));
        x0 = unchecked(x0 + add);
        x2 = unchecked(x2 + add);
        output[outOff] = StbiClamp(unchecked((x0 + x1) >> ScaleBits));
        output[outOff + 1] = StbiClamp(unchecked((x0 - x1) >> ScaleBits));
        output[outOff + lineStride] = StbiClamp(unchecked((x2 + x3) >> ScaleBits));
        output[outOff + lineStride + 1] = StbiClamp(unchecked((x2 - x3) >> ScaleBits));
    }

    private static void Block1x1(ReadOnlySpan<short> c, ReadOnlySpan<ushort> q, Span<byte> output, int outOff)
    {
        // 上游：(c*q + 128*8) / 8（Wrapping 除法；除數恆 8，C# 整數除法同為截尾，負數語意與 Rust 相同）。
        int s0 = unchecked(c[0] * q[0] + 128 * 8) / 8;
        output[outOff] = StbiClamp(s0);
    }

    private static byte StbiClamp(int x) => (byte)Math.Clamp(x, 0, 255);

    private static int StbiF2f(float x) => (int)(x * 4096.0f + 0.5f);

    private static int StbiFsh(int x) => unchecked(x << 12);
}
