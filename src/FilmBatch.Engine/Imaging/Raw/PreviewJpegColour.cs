// 移植自 LightCraft crates/codecs/src/jpeg.rs（parse_markers APP2 ICC 收集／排序拼接、
// scaled_request DCT 縮放請求盒）與 crates/codecs/src/space.rs（Trc、lut8）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>JPEG 標記解析出的色彩詮釋（上游 <c>jpeg::Markers</c> 的子集：ICC、Exif、SOF）。</summary>
internal sealed record JpegColourMarkers(byte[]? Icc, byte[]? Exif, uint Width, uint Height, int Components);

/// <summary>
/// 嵌入預覽 JPEG 的標記級色彩解析（上游 <c>jpeg.rs::parse_markers</c> 移植），以及
/// DCT 縮放請求盒（上游 <c>scaled_request</c>）與 per-channel LUT 線性化（上游 <c>linearize</c> + <c>Trc::lut8</c>）。
/// </summary>
internal static class PreviewJpegColour
{
    /// <summary>
    /// 解析 JPEG 標記（上游 <c>parse_markers</c> `jpeg.rs:41-118`）：SOI 起始，容忍段間垃圾（重同步下一個 0xFF）；
    /// 第一個 SOF（非獨立／無損以外）定尺寸與分量數；SOS 停止；APP1 首個 `Exif\0\0` 段為 Exif；
    /// APP2 `ICC_PROFILE\0` 段按序號排序後拼接為 ICC（上游 `icc_chunks.sort_by_key` + `flat_map`）。
    /// </summary>
    internal static JpegColourMarkers? ParseMarkers(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            return null;
        var iccChunks = new List<(byte Seq, byte[] Data)>();
        byte[]? exif = null;
        uint width = 0, height = 0;
        var components = 0;
        var p = 2;
        while (p + 4 <= jpeg.Length)
        {
            if (jpeg[p] != 0xff)
            {
                // 段間垃圾：重同步到下一個 0xFF（上游同）。
                p++;
                continue;
            }
            var marker = jpeg[p + 1];
            if (marker == 0xff)
            {
                p++;
                continue;
            }
            if (marker == 0xd8 || (marker >= 0xd0 && marker <= 0xd7) || marker == 0x01)
            {
                p += 2;
                continue;
            }
            if (marker == 0xd9)
                break;
            if (marker == 0xda)
                break;
            var len = (jpeg[p + 2] << 8) | jpeg[p + 3];
            if (len < 2)
                return null;
            var start = p + 4;
            var end = Math.Min(p + 2 + len, jpeg.Length);
            var segLen = end - start;
            switch (marker)
            {
                case >= 0xc0 and <= 0xcf when marker != 0xc4 && marker != 0xc8 && marker != 0xcc && segLen >= 6 && components == 0:
                    height = (uint)((jpeg[start + 1] << 8) | jpeg[start + 2]);
                    width = (uint)((jpeg[start + 3] << 8) | jpeg[start + 4]);
                    components = jpeg[start + 5];
                    break;
                case 0xe1 when segLen >= 6
                    && jpeg[start] == (byte)'E' && jpeg[start + 1] == (byte)'x'
                    && jpeg[start + 2] == (byte)'i' && jpeg[start + 3] == (byte)'f'
                    && jpeg[start + 4] == 0 && jpeg[start + 5] == 0 && exif is null:
                    exif = jpeg[(start + 6)..end];
                    break;
                case 0xe2 when segLen >= 14
                    && jpeg[start] == (byte)'I' && jpeg[start + 1] == (byte)'C' && jpeg[start + 2] == (byte)'C'
                    && jpeg[start + 3] == (byte)'_' && jpeg[start + 4] == (byte)'P'
                    && jpeg[start + 5] == (byte)'R' && jpeg[start + 6] == (byte)'O'
                    && jpeg[start + 7] == (byte)'F' && jpeg[start + 8] == (byte)'I'
                    && jpeg[start + 9] == (byte)'L' && jpeg[start + 10] == (byte)'E'
                    && jpeg[start + 11] == 0:
                    iccChunks.Add((jpeg[start + 12], jpeg[(start + 14)..end]));
                    break;
            }
            p += 2 + len;
        }
        byte[]? icc = null;
        if (iccChunks.Count > 0)
        {
            iccChunks.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
            var total = 0;
            foreach (var c in iccChunks) total += c.Data.Length;
            icc = new byte[total];
            var at = 0;
            foreach (var c in iccChunks)
            {
                Buffer.BlockCopy(c.Data, 0, icc, at, c.Data.Length);
                at += c.Data.Length;
            }
        }
        return new JpegColourMarkers(icc, exif, width, height, components);
    }

    /// <summary>
    /// DCT 縮放請求盒（上游 <c>scaled_request</c> `jpeg.rs:272-277`）：
    /// <c>s = min(mw/w, mh/h, 1)</c>，<c>tw = ceil(w·s)</c>、<c>th = ceil(h·s)</c>（至少 1，
    /// 夾到 u16 max）。呼叫端的 `jpeg:size` 讀取定義即以此盒請求。
    /// </summary>
    internal static (ushort W, ushort H) ScaledRequest(uint w, uint h, uint mw, uint mh)
    {
        var s = Math.Min((double)mw / w, (double)mh / h);
        s = Math.Min(s, 1.0);
        var tw = (uint)Math.Ceiling(w * s);
        var th = (uint)Math.Ceiling(h * s);
        tw = Math.Clamp(tw, 1u, ushort.MaxValue);
        th = Math.Clamp(th, 1u, ushort.MaxValue);
        return ((ushort)tw, (ushort)th);
    }

    /// <summary>
    /// 8-bit 編碼值 → 線性光的 256 項表（上游 <c>Trc::lut8</c> `space.rs`）：
    /// <c>lut8[i] = trc.to_linear(i / 255)</c>。灰階複用綠通道（上游 `linearize` 灰階取 `luts[1]`）。
    /// </summary>
    internal static float[] Lut8(PreviewTrc trc)
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
            lut[i] = trc.ToLinear(i / 255f);
        return lut;
    }

    /// <summary>
    /// 16-bit 正規化值 → 線性光的 65536 項表（上游 <c>Trc::lut16</c> `space.rs:119-124`；
    /// 線性 TRC 走恆等：`lut16[i] = trc.to_linear(i / 65535)`）。
    /// </summary>
    internal static float[] Lut16(PreviewTrc trc)
    {
        var lut = new float[65536];
        for (var i = 0; i < 65536; i++)
            lut[i] = trc.ToLinear(i / 65535f);
        return lut;
    }
}

/// <summary>
/// 傳輸曲線（上游 <c>space::Trc</c> `space.rs:14-80` 的預覽解碼子集：Linear／Srgb／Gamma／Rec709／
/// 參數式／取樣表；`to_linear` 含負值鏡射，`eval_table` 線性內插）。
/// </summary>
internal abstract class PreviewTrc
{
    internal abstract float ToLinear(float v);

    /// <summary>參數式曲線參數（僅 para 解析結果非 null；供 ICC 有效性檢查用）。</summary>
    internal virtual float[]? ParametricParameters => null;

    /// <summary>Gamma 值（僅 gamma 解析結果非 null；供 ICC 有效性檢查用）。</summary>
    internal virtual float? GammaValue => null;

    internal static PreviewTrc Srgb { get; } = new SrgbTrc();
    internal static PreviewTrc Linear { get; } = new GammaTrc(1f);
    internal static PreviewTrc Rec709 { get; } = new Rec709Trc();
    internal static PreviewTrc Gamma(float g) => new GammaTrc(g);
    internal static PreviewTrc Parametric(float[] p) => new ParametricTrc(p);
    internal static PreviewTrc Table(ushort[] t) => new TableTrc(t);

    private static float Pow(float b, float g) => b > 0f ? MathF.Pow(b, g) : 0f;

    private sealed class SrgbTrc : PreviewTrc
    {
        internal override float ToLinear(float v) => Transfer.SrgbToLinear(v);
    }

    private sealed class GammaTrc(float g) : PreviewTrc
    {
        internal override float? GammaValue => g;

        internal override float ToLinear(float v) => Transfer.GammaToLinear(v, g);
    }

    private sealed class Rec709Trc : PreviewTrc
    {
        internal override float ToLinear(float v)
        {
            if (v < 0f) return -ToLinear(-v);
            return v < 0.081f ? v / 4.5f : MathF.Pow((v + 0.099f) / 1.099f, 1f / 0.45f);
        }
    }
    private sealed class ParametricTrc(float[] p) : PreviewTrc
    {
        internal override float[]? ParametricParameters => p;

        internal override float ToLinear(float v)
        {
            if (v < 0f) return -ToLinear(-v);
            return p.Length switch
            {
                1 => Pow(v, p[0]),
                3 => p[1] != 0f && v >= -p[2] / p[1] ? Pow(p[1] * v + p[2], p[0]) : 0f,
                4 => p[1] != 0f && v >= -p[2] / p[1] ? Pow(p[1] * v + p[2], p[0]) + p[3] : p[3],
                5 => v >= p[4] ? Pow(p[1] * v + p[2], p[0]) : p[3] * v,
                7 => v >= p[4] ? Pow(p[1] * v + p[2], p[0]) + p[5] : p[3] * v + p[6],
                _ => v,
            };
        }
    }

    private sealed class TableTrc(ushort[] t) : PreviewTrc
    {
        internal override float ToLinear(float v)
        {
            if (v < 0f) return -ToLinear(-v);
            switch (t.Length)
            {
                case 0: return v;
                case 1: return MathF.Pow(v, t[0] / 256f);
                default:
                    var pos = v * (t.Length - 1);
                    var i = Math.Min((int)MathF.Floor(pos), t.Length - 2);
                    var f = pos - i;
                    var a = t[i] / 65535f;
                    var b = t[i + 1] / 65535f;
                    return a + (b - a) * f;
            }
        }
    }
}
