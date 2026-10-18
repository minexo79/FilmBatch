// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0；Copyright (c) jpeg-decoder contributors）
// 的標記前走訪（上游 `Decoder::decode_internal` 的 SOF 前分支＋`parse_markers` 語意），
// 與 LightCraft crates/codecs/src/jpeg.rs（MIT OR Apache-2.0）的分派條件（`decode_with_fallback`
// `jpeg.rs:175-197`：components == 4 || scale_to || precision > 8 || non-interleaved || lossless
// 走 jpeg-decoder，否則走 zune-jpeg 埠 `Jpeg8`；lossless 不做 DCT 縮放；
// `scaled_request` `jpeg.rs:272-277`；`lossless_samples` 左對齊 `jpeg.rs:250-253`）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// 內嵌預覽 JPEG 解碼結果（上游 <c>codecs::Raw</c> 的子集：<c>width/height/model/buf</c>）。
/// <c>U8</c>／<c>U16</c> 恰其一為非 null（一次原生解碼緩衝）：8-bit 為儲存樣本，
/// 16-bit 為 lossless 原生小端樣本左對齊（<c>value &lt;&lt; (16 - precision)</c>，
/// 與上游 <c>lossless_samples</c> 一致；precision &lt; 8 的 lossless
/// 按上游原樣取原生位元組，不做正規化）。
/// 通道數為上游模型通道（1 灰階／3 RGB／4 CMYK 系列），已做解碼器內色彩轉換
///（YCbCr→RGB、YCCK→CMYK 系列），不含 ICC／矩陣／縮放等色彩管理。
/// <c>Width/Height</c> 為 DCT 縮放後的解碼緩衝尺寸；來源標記尺寸由呼叫端另行取得。
/// </summary>
internal sealed record JpegPreviewPixels(int Width, int Height, int Channels, byte[]? U8, ushort[]? U16);

/// <summary>
/// 內嵌預覽 JPEG 解碼器（上游 <c>codecs::jpeg::decode_with_fallback</c> 的分派＋雙後端）：
/// 常見 8-bit 交錯 baseline／progressive 走既有 zune-jpeg 埠 <see cref="Jpeg8"/>（位元一致）；
/// 4 分量、需 DCT 縮放、precision &gt; 8、SOF C0/C1 非交錯掃描、lossless（SOF3）走
/// 本目錄的 jpeg-decoder 0.3.2 埠（<see cref="JpegDecoderPort"/>，純量路徑）。
/// 損毀或不支援的串流只丟 <see cref="RawException"/>；檔頭尺寸先做上限檢查才配置。
/// </summary>
internal static class JpegPreview
{
    /// <summary>
    /// 解 JPEG 串流。<c>maxWidth/maxHeight</c> 為上游 <c>DecodeOptions::max_size</c>：
    /// 任一為 0、或來源任一邊 &lt; 目標盒兩倍時全尺寸解碼；否則按
    /// <c>scaled_request</c> 求盒並取仍覆蓋該盒的最小 DCT 縮放（1/8、1/4、1/2、全尺寸）。
    /// Lossless（SOF3）恆全尺寸解碼（上游註解：<c>scale</c> 會裁掉每平面左上以外的資料）。
    /// </summary>
    internal static JpegPreviewPixels Decode(ReadOnlyMemory<byte> jpeg, uint maxWidth, uint maxHeight)
    {
        try
        {
            var markers = JpegPreviewMarkers.Parse(jpeg.Span);
            long pixels = (long)markers.Width * markers.Height;
            if (pixels == 0)
                throw RawException.Corrupt("preview JPEG: zero dimension");
            if (pixels > RawLimits.MaxSamples)
                throw RawException.Limit("preview JPEG: image too large");

            bool scaleTo = maxWidth > 0 && maxHeight > 0
                && (markers.Width >= maxWidth * 2 || markers.Height >= maxHeight * 2);
            bool lossless = markers.Sof == 0xC3;
            bool nonInterleaved = (markers.Sof == 0xC0 || markers.Sof == 0xC1)
                && markers.FirstScanComponents < markers.Components;
            bool usePort = markers.Components == 4 || (scaleTo && !lossless)
                || markers.Precision > 8 || nonInterleaved || lossless;
            if (!usePort)
            {
                // 與 TiffRaw.LossyJpeg 相同：zune 後端；cpp==1 取 Luma，否則 RGB。
                int req = markers.Components == 1 ? 1 : 3;
                var (samples, w, h, c) = Jpeg8.Decode(jpeg.Span, req);
                if (w == 0 || h == 0 || samples.Length < (long)w * h * c)
                    throw RawException.Corrupt("preview JPEG: short output");
                return new JpegPreviewPixels(w, h, c, samples, null);
            }
            (ushort reqW, ushort reqH) = scaleTo && !lossless
                ? ScaledRequest(markers.Width, markers.Height, maxWidth, maxHeight)
                : ((ushort)0, (ushort)0);
            if (!lossless && reqW == 0 && reqH == 0 && scaleTo)
                throw RawException.Corrupt("preview JPEG: bad scale request");
            var frame = new JpegFrameDecoder(jpeg);
            return frame.Run(reqW, reqH, scaleTo && !lossless);
        }
        catch (RawException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException
            or ArgumentOutOfRangeException
            or ArgumentException
            or OverflowException
            or NullReferenceException
            or InvalidOperationException)
        {
            throw RawException.Corrupt($"preview JPEG: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// DCT 縮放請求盒（上游 <c>scaled_request</c> <c>jpeg.rs:272-277</c>）：
    /// <c>s = min(mw/w, mh/h, 1)</c>，<c>tw = ceil(w·s)</c>、<c>th = ceil(h·s)</c>
    ///（至少 1，夾到 u16 max）。
    /// </summary>
    internal static (ushort W, ushort H) ScaledRequest(uint w, uint h, uint mw, uint mh)
    {
        double s = Math.Min((double)mw / w, (double)mh / h);
        s = Math.Min(s, 1.0);
        uint tw = (uint)Math.Ceiling(w * s);
        uint th = (uint)Math.Ceiling(h * s);
        tw = Math.Clamp(tw, 1u, (uint)ushort.MaxValue);
        th = Math.Clamp(th, 1u, (uint)ushort.MaxValue);
        return ((ushort)tw, (ushort)th);
    }
}

/// <summary>
/// JPEG 標記前走訪（上游 <c>jpeg::parse_markers</c> <c>jpeg.rs:41-118</c> 中分派所需的子集，
/// 加上 <c>header</c> <c>jpeg.rs:128-145</c> 的拒絕規則）：
/// SOI 起始；容忍段間垃圾（重同步下一個 0xFF）；跳過填塞／RST／TEM；
/// EOI 結束；SOS 記首掃描分量數後停止；第一個 SOF（DHT/DAC 除外）定尺寸／分量／精度。
/// </summary>
internal readonly struct JpegPreviewMarkers
{
    public readonly uint Width;
    public readonly uint Height;
    public readonly byte Components;
    public readonly byte Precision;
    /// <summary>SOF 標記低位元組（如 0xC0 baseline、0xC1 extended、0xC2 progressive、0xC3 lossless）。</summary>
    public readonly byte Sof;
    /// <summary>首個 SOS 的 Ns（非交錯 sequential 檔會小於分量數）。</summary>
    public readonly byte FirstScanComponents;

    private JpegPreviewMarkers(uint w, uint h, byte c, byte p, byte sof, byte nsos)
    {
        Width = w; Height = h; Components = c; Precision = p; Sof = sof; FirstScanComponents = nsos;
    }

    internal static JpegPreviewMarkers Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2 || b[0] != 0xFF || b[1] != 0xD8)
            throw RawException.Corrupt("preview JPEG: missing SOI");
        uint w = 0, h = 0;
        byte comp = 0, prec = 0, sof = 0, nsos = 0;
        bool haveSof = false;
        int p = 2;
        while (p + 4 <= b.Length)
        {
            if (b[p] != 0xFF)
            {
                // 容忍段間垃圾：重同步下一個 0xFF（上游 parse_markers）。
                p++;
                continue;
            }
            byte marker = b[p + 1];
            if (marker == 0xFF)
            {
                p++;
                continue;
            }
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            {
                p += 2;
                continue;
            }
            if (marker == 0xD9)
                break;
            if (marker == 0xDA)
            {
                nsos = b[p + 4];
                break;
            }
            int len = (b[p + 2] << 8) | b[p + 3];
            if (len < 2)
                throw RawException.Corrupt("preview JPEG: bad segment length");
            int start = p + 4;
            int end = Math.Min(p + 2 + len, b.Length);
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC
                && end - start >= 6 && !haveSof)
            {
                prec = b[start];
                sof = marker;
                h = (uint)((b[start + 1] << 8) | b[start + 2]);
                w = (uint)((b[start + 3] << 8) | b[start + 4]);
                comp = b[start + 5];
                haveSof = true;
            }
            p += 2 + len;
        }
        if (!haveSof || comp == 0)
            throw RawException.Corrupt("preview JPEG: no frame header");
        if (sof == 0xC4 || sof == 0xC8 || sof == 0xCC
            || !((sof >= 0xC0 && sof <= 0xC3) || (sof >= 0xC5 && sof <= 0xCF)))
            throw RawException.Corrupt("preview JPEG: no frame header");
        // 上游 header：只接受 Huffman 非差分 SOF0-3；分量 1/3/4；DHT 的 SOF4 在此已因 comp==0 被拒。
        bool differential = (sof >= 0xC5 && sof <= 0xC7) || sof >= 0xCD;
        bool arithmetic = sof >= 0xC9 && sof <= 0xCB;
        if (differential)
            throw RawException.Unsupported("preview JPEG: differential/hierarchical JPEG");
        if (arithmetic)
            throw RawException.Unsupported("preview JPEG: arithmetic-coded JPEG");
        if (comp != 1 && comp != 3 && comp != 4)
            throw RawException.Corrupt($"preview JPEG: {comp} colour components");
        return new JpegPreviewMarkers(w, h, comp, prec, sof, nsos);
    }
}
