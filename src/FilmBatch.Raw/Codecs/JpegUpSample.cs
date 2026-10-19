// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0）`upsampler.rs`：
// `Upsampler::new`、`choose_upsampler`、H1V1/H2V1/H1V2/H2V2/Generic 列上採樣，
// `upsample_and_interleave_row` 的列緩衝＋交錯，以及 `decoder.rs` 的
// `compute_image`（單分量裁剪）、`choose_color_convert_func`、四種色彩轉換、
// `ycbcr_to_rgb`（BT.601 定點）、`decoder/lossless.rs` 的 `compute_image_lossless`。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using static FilmBatch.Raw.Codecs.JpegDecoderPort;

namespace FilmBatch.Raw.Codecs;

/// <summary>上採樣＋交錯＋色彩轉換（上游 <c>upsampler</c>＋<c>compute_image*</c>）。</summary>
internal static class JpegUpSample
{
    internal enum Kind : byte
    {
        H1V1,
        H2V1,
        H1V2,
        H2V2,
        Generic,
    }

    internal sealed class Plane
    {
        public Kind Kind;
        public byte Hs; // Generic 用
        public byte Vs; // Generic 用
        public int Width;
        public int Height;
        public int RowStride;
    }

    internal static Plane[] Build(Component[] comps, int outW, int outH)
    {
        byte hMax = 0, vMax = 0;
        foreach (var c in comps)
        {
            hMax = Math.Max(hMax, c.H);
            vMax = Math.Max(vMax, c.V);
        }
        var planes = new Plane[comps.Length];
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            planes[i] = new Plane
            {
                Kind = Choose(c.H, c.V, hMax, vMax, outW, outH),
                Hs = (byte)(hMax / c.H),
                Vs = (byte)(vMax / c.V),
                Width = c.Width,
                Height = c.Height,
                RowStride = c.BlockW * c.DctScale,
            };
        }
        return planes;
    }

    private static Kind Choose(byte h, byte v, byte hMax, byte vMax, int outW, int outH)
    {
        bool h1 = h == hMax || outW == 1;
        bool v1 = v == vMax || outH == 1;
        bool h2 = h * 2 == hMax;
        bool v2 = v * 2 == vMax;
        if (h1 && v1)
            return Kind.H1V1;
        if (h2 && v1)
            return Kind.H2V1;
        if (h1 && v2)
            return Kind.H1V2;
        if (h2 && v2)
            return Kind.H2V2;
        if (hMax % h != 0 || vMax % v != 0)
            throw RawException.Unsupported("preview JPEG: non-integer subsampling ratio");
        return Kind.Generic;
    }

    internal static int LineBufferSize(Component[] comps)
    {
        int wMax = 0;
        byte hMax = 0;
        foreach (var c in comps)
        {
            wMax = Math.Max(wMax, c.Width);
            hMax = Math.Max(hMax, c.H);
        }
        return wMax * hMax;
    }

    /// <summary>
    /// 上採樣一列並交錯（上游 `upsample_and_interleave_row`）：每分量先上採樣到列緩衝，
    /// 再以色彩轉換逐像素交錯寫入輸出列。
    /// </summary>
    internal static void UpsampleRow(Plane[] planes, byte[][] compData, int row, int outW, byte[][] lines, byte[] outLine, ColorTransform ct)
    {
        for (int i = 0; i < planes.Length; i++)
            UpsampleOne(planes[i], compData[i], row, outW, lines[i]);
        ColorConvert(ct, planes.Length, lines, outLine, outW);
    }

    private static void UpsampleOne(Plane p, byte[] input, int row, int outW, byte[] output)
    {
        switch (p.Kind)
        {
            case Kind.H1V1:
            {
                int off = row * p.RowStride;
                input.AsSpan(off, outW).CopyTo(output.AsSpan(0, outW));
                break;
            }
            case Kind.H2V1:
            {
                int off = row * p.RowStride;
                int iw = p.Width;
                if (iw == 1)
                {
                    output[0] = input[off];
                    output[1] = input[off];
                    return;
                }
                output[0] = input[off];
                output[1] = (byte)(((uint)input[off] * 3 + input[off + 1] + 2) >> 2);
                for (int i = 1; i < iw - 1; i++)
                {
                    uint s = 3 * (uint)input[off + i] + 2;
                    output[i * 2] = (byte)((s + input[off + i - 1]) >> 2);
                    output[i * 2 + 1] = (byte)((s + input[off + i + 1]) >> 2);
                }
                output[(iw - 1) * 2] = (byte)(((uint)input[off + iw - 1] * 3 + input[off + iw - 2] + 2) >> 2);
                output[(iw - 1) * 2 + 1] = input[off + iw - 1];
                break;
            }
            case Kind.H1V2:
            {
                // 上游用 f32 近／遠列權重 3:1（row_near=row/2.0，row_far 由 fract 決定並夾到 h-1）。
                float rowNear = row / 2.0f;
                float frac = rowNear - (float)Math.Floor(rowNear);
                float rowFar = Math.Min(rowNear + frac * 3.0f - 0.25f, p.Height - 1);
                int nearOff = (int)rowNear * p.RowStride;
                int farOff = (int)rowFar * p.RowStride;
                for (int i = 0; i < outW; i++)
                    output[i] = (byte)((3 * (uint)input[nearOff + i] + input[farOff + i] + 2) >> 2);
                break;
            }
            case Kind.H2V2:
            {
                float rowNear = row / 2.0f;
                float frac = rowNear - (float)Math.Floor(rowNear);
                float rowFar = Math.Min(rowNear + frac * 3.0f - 0.25f, p.Height - 1);
                int nearOff = (int)rowNear * p.RowStride;
                int farOff = (int)rowFar * p.RowStride;
                int iw = p.Width;
                if (iw == 1)
                {
                    byte v = (byte)((3 * (uint)input[nearOff] + input[farOff] + 2) >> 2);
                    output[0] = v;
                    output[1] = v;
                    return;
                }
                uint t1 = 3 * (uint)input[nearOff] + input[farOff];
                output[0] = (byte)((t1 + 2) >> 2);
                for (int i = 1; i < iw; i++)
                {
                    uint t0 = t1;
                    t1 = 3 * (uint)input[nearOff + i] + input[farOff + i];
                    output[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) >> 4);
                    output[i * 2] = (byte)((3 * t1 + t0 + 8) >> 4);
                }
                output[iw * 2 - 1] = (byte)((t1 + 2) >> 2);
                break;
            }
            default:
            {
                // Generic：最近鄰（上游註解明示）。
                int start = (row / p.Vs) * p.RowStride;
                int idx = 0;
                for (int i = 0; i < p.Width; i++)
                {
                    byte v = input[start + i];
                    for (int k = 0; k < p.Hs; k++)
                        output[idx++] = v;
                }
                break;
            }
        }
    }

    internal static void ColorConvert(ColorTransform ct, int ncomp, byte[][] lines, byte[] output, int outW)
    {
        switch (ncomp)
        {
            case 3 when ct == ColorTransform.Rgb:
                ConvertRgb(lines, output, outW);
                break;
            case 3 when ct == ColorTransform.YCbCr:
                ConvertYCbCr(lines, output, outW);
                break;
            case 4 when ct == ColorTransform.Cmyk:
                ConvertCmyk(lines, output, outW);
                break;
            case 4 when ct == ColorTransform.YccK:
                ConvertYccK(lines, output, outW);
                break;
            default:
                throw RawException.Corrupt("preview JPEG: unknown colour transform");
        }
    }

    private static void ConvertRgb(byte[][] d, byte[] o, int w)
    {
        var (r, g, b) = (d[0], d[1], d[2]);
        for (int i = 0; i < w; i++)
        {
            o[i * 3] = r[i];
            o[i * 3 + 1] = g[i];
            o[i * 3 + 2] = b[i];
        }
    }

    private static void ConvertYCbCr(byte[][] d, byte[] o, int w)
    {
        var (y, cb, cr) = (d[0], d[1], d[2]);
        for (int i = 0; i < w; i++)
        {
            var (rr, gg, bb) = YCbCrToRgb(y[i], cb[i], cr[i]);
            o[i * 3] = rr;
            o[i * 3 + 1] = gg;
            o[i * 3 + 2] = bb;
        }
    }

    private static void ConvertYccK(byte[][] d, byte[] o, int w)
    {
        var (c, m, y, k) = (d[0], d[1], d[2], d[3]);
        for (int i = 0; i < w; i++)
        {
            var (rr, gg, bb) = YCbCrToRgb(c[i], m[i], y[i]);
            o[i * 4] = rr;
            o[i * 4 + 1] = gg;
            o[i * 4 + 2] = bb;
            o[i * 4 + 3] = (byte)(255 - k[i]);
        }
    }

    private static void ConvertCmyk(byte[][] d, byte[] o, int w)
    {
        var (c, m, y, k) = (d[0], d[1], d[2], d[3]);
        for (int i = 0; i < w; i++)
        {
            o[i * 4] = (byte)(255 - c[i]);
            o[i * 4 + 1] = (byte)(255 - m[i]);
            o[i * 4 + 2] = (byte)(255 - y[i]);
            o[i * 4 + 3] = (byte)(255 - k[i]);
        }
    }

    private const int FixedOffset = 20;
    private const int Half = (1 << FixedOffset) / 2;

    /// <summary>BT.601（上游 `ycbcr_to_rgb`，libjpeg-turbo jdcolext 係數；定點 OFFSET=20）。</summary>
    internal static (byte R, byte G, byte B) YCbCrToRgb(byte y, byte cb, byte cr)
    {
        int yy = (int)y * (1 << FixedOffset) + Half;
        int cbb = cb - 128;
        int crr = cr - 128;
        byte r = ClampFp(unchecked(yy + F2f(1.40200f) * crr));
        byte g = ClampFp(unchecked(yy - F2f(0.34414f) * cbb - F2f(0.71414f) * crr));
        byte b = ClampFp(unchecked(yy + F2f(1.77200f) * cbb));
        return (r, g, b);
    }

    private static int F2f(float x) => (int)(x * (1 << FixedOffset) + 0.5f);

    private static byte ClampFp(int v) => (byte)Math.Clamp(v >> FixedOffset, 0, 255);

    /// <summary>
    /// 單分量組圖（上游 `compute_image` 的 1 分量分支）：行距與寬不等時由上而下前移各列
    ///（`copy_within` 語意），再截到 <c>width*height</c>。
    /// </summary>
    internal static byte[] FinishGray(byte[] decoded, int width, int height, int lineStride)
    {
        if (width != lineStride)
        {
            for (int y = 1; y < height; y++)
                decoded.AsSpan(y * lineStride, width).CopyTo(decoded.AsSpan(y * width, width));
        }
        if (decoded.Length != width * height)
            Array.Resize(ref decoded, width * height);
        return decoded;
    }

    /// <summary>
    /// Lossless 組圖（上游 `compute_image_lossless`＋`convert_to_u8`）：單分量直接取；
    /// 多分量按像素交錯；precision==8 取低位元組，否則按原生小端序輸出
    ///（上游 `to_ne_bytes`；FilmBatch 只跑小端平台，與 `Ljpeg` 一致）。
    /// </summary>
    internal static byte[] FinishLossless(ushort[][] planes, int width, int height, int precision)
    {
        if (planes.Length == 1)
            return ToU8(planes[0], precision);
        int n = width * height;
        var inter = new ushort[planes.Length * n];
        for (int x = 0; x < n; x++)
            for (int i = 0; i < planes.Length; i++)
                inter[x * planes.Length + i] = planes[i][x];
        return ToU8(inter, precision);
    }

    private static byte[] ToU8(ushort[] data, int precision)
    {
        if (precision == 8)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                o[i] = (byte)data[i];
            return o;
        }
        var b = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            b[i * 2] = (byte)data[i];
            b[i * 2 + 1] = (byte)(data[i] >> 8);
        }
        return b;
    }
}
