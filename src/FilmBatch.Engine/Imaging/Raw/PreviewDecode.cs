// 移植自 LightCraft crates/engine/src/files.rs（decode_raw_preview 色彩詮釋順序）、
// crates/codecs/src/convert.rs（finish 線性化／線性光縮放／to_working）、jpeg.rs
//（decode_with_fallback 的 DCT 縮放觸發條件）、space.rs（SourceSpace::to_space）與
// exif.rs（adobe_rgb_hint）——均以 Magick.NET 為 JPEG 後端重寫。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using ImageMagick;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 嵌入預覽 JPEG → 線性 Rec.2020 工作空間（上游 <c>files::decode_raw_preview</c> `files.rs:704-722`）。
/// 色彩詮釋順序（上游 <c>rgb_or_gray</c> + <c>decode_with_fallback</c>）：
/// JPEG 自帶 ICC（APP2）→ 可解析矩陣／TRC（或灰階 kTRC）用其 TRC 線性化＋D50 色料矩陣；
/// 上游會送 CMS 的 LUT 型設定檔 FilmBatch 無 CMS，按上游「存在但不可用 → sRGB」分支處理（見偏差）；
/// 否則容器宣告（<c>RawPreview.EmbeddedPreviewColorSpace</c>）僅在 JPEG 既無 ICC 又無自帶 Exif 時生效；
/// 否則 JPEG 自帶 Exif 的 Adobe RGB 提示（ColorSpace 0xFFFF＋InteropIndex R03；上游 <c>exif::summarize</c>）；
/// 否則 sRGB。Adobe RGB TRC 為 gamma 563/256（上游 <c>space.rs</c> 同值）。
/// </summary>
internal static class PreviewDecode
{
    /// <summary>
    /// 上游 <c>decode_with_fallback</c> `jpeg.rs:175-197` 的 DCT 縮放觸發條件：
    /// JPEG 任一邊 ≥ 目標盒兩倍才請求縮放。ImageMagick 以 `jpeg:size` 讀取定義重現
    /// jpeg-decoder 的 IDCT 縮放選擇（上游 `idct.rs:14-28` 的 M∈{1,2,4,8}/8）：
    /// libjpeg-turbo 由 `size` 請求得 scale_denom 並取 M＝ceil(8/scale_denom)，
    /// 故請求 `rw = floor(W·M/8)`、`rh = floor(H·M/8)` 即得目標 M（parent 實測驗證），M = 8 時省略定義。
    /// 此處直接移植 `choose_idct_size`：`scaled_request` 盒上仍覆蓋任一邊的最小 M∈{1,2,4}，否則 8。
    /// </summary>
    internal static int DctScale(uint w, uint h, uint mw, uint mh)
    {
        var (rw, rh) = PreviewJpegColour.ScaledRequest(w, h, mw, mh);
        foreach (var m in new[] { 1, 2, 4 })
        {
            if ((w * m + 7) / 8 >= rw || (h * m + 7) / 8 >= rh)
                return m;
        }
        return 8;
    }
    internal static bool AdobeRgbHint(byte[] exifBlob)
    {
        var t = new ExifBlob(exifBlob);
        if (!t.FirstIfd(out var ifd))
            return false;
        uint? exifPtr = null;
        foreach (var e in t.Entries(ifd))
        {
            if (e.Tag == 0x8769)
                exifPtr = t.UInt(e);
        }
        if (exifPtr is not { } ptr || ptr > int.MaxValue)
            return false;
        uint? cs = null;
        uint? interopPtr = null;
        foreach (var e in t.Entries((int)ptr))
        {
            if (e.Tag == 0xa001) cs = t.UInt(e);
            if (e.Tag == 0xa005) interopPtr = t.UInt(e);
        }
        if (cs != 0xffff || interopPtr is not { } ip || ip > int.MaxValue)
            return false;
        foreach (var e in t.Entries((int)ip))
        {
            if (e.Tag == 1 && t.Bytes(e) is { } b && b.Length >= 3 && b[0] == (byte)'R' && b[1] == (byte)'0' && b[2] == (byte)'3')
                return true;
        }
        return false;
    }

    /// <summary>
    /// 最小位元組序 TIFF／Exif 走訪（上游 <c>codecs::exif::Tiff</c> `exif.rs:6-83` 移植）：
    /// 只讀取色彩提示所需的 IFD、SHORT／LONG 首值與 ASCII／BYTE 段位元組。
    /// </summary>
    internal sealed class ExifBlob(byte[] data)
    {
        private readonly bool _le = data.Length >= 2 && data[0] == (byte)'I' && data[1] == (byte)'I';

        internal readonly record struct Entry(ushort Tag, ushort Type, uint Count, int ValuePos);

        internal bool FirstIfd(out int ifd)
        {
            ifd = 0;
            if (data.Length < 8 || (data[0] != (byte)'I' && data[0] != (byte)'M'))
                return false;
            if (U16(2) != 42)
                return false;
            var off = U32(4);
            if (off > int.MaxValue || off >= data.Length)
                return false;
            ifd = (int)off;
            return true;
        }

        internal List<Entry> Entries(int pos)
        {
            var output = new List<Entry>();
            if (pos < 0 || pos + 2 > data.Length)
                return output;
            var n = U16(pos);
            if (n > 4096)
                return output;
            for (var i = 0; i < n; i++)
            {
                var e = pos + 2 + i * 12;
                if (e + 12 > data.Length)
                    break;
                output.Add(new Entry(U16(e), U16(e + 2), U32(e + 4), e + 8));
            }
            return output;
        }

        internal uint? UInt(Entry e) => e.Type switch
        {
            3 => U16(e.ValuePos),
            4 or 13 => U32(e.ValuePos),
            1 or 7 => data[e.ValuePos],
            _ => null,
        };

        internal byte[]? Bytes(Entry e)
        {
            var size = e.Type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8,
                _ => 0,
            };
            if (size == 0)
                return null;
            var len = checked((long)size * e.Count);
            if (len > data.Length)
                return null;
            int start;
            if (len <= 4)
                start = e.ValuePos;
            else
            {
                var off = U32(e.ValuePos);
                if (off > int.MaxValue || (long)off + len > data.Length)
                    return null;
                start = (int)off;
            }
            return data[start..(start + (int)len)];
        }

        private ushort U16(int pos)
        {
            var b0 = data[pos]; var b1 = data[pos + 1];
            return _le ? (ushort)(b0 | (b1 << 8)) : (ushort)((b0 << 8) | b1);
        }

        private uint U32(int pos)
        {
            var b0 = data[pos]; var b1 = data[pos + 1]; var b2 = data[pos + 2]; var b3 = data[pos + 3];
            return _le
                ? (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24))
                : (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
        }
    }

    /// <summary>
    /// 色彩詮釋解析（上游 <c>rgb_or_gray</c> `convert.rs:230-290` 的分支順序）：
    /// ICC → 容器宣告 → Exif Adobe 提示 → sRGB。回傳線性化 TRC、D50 色料矩陣與具名空間
    ///（具名者矩陣走 `RgbSpace.ToSpace`，與上游 `SourceSpace::to_space` 的 D65 白點分支一致）。
    /// </summary>
    internal static (PreviewTrc[] Trc, Mat3 ToXyzD50, RgbSpace? Named) ResolveSpace(
        byte[]? icc, byte[]? jpegExif, PreviewColorSpace? declared, int components)
    {
        var gray = components == 1;
        if (icc is not null)
        {
            var info = PreviewIcc.Parse(icc);
            if (info is not null)
            {
                if (info.Kind == PreviewIccKind.MatrixTrc && !gray)
                    return (info.Trc, info.ToXyzD50, info.Named);
                if (info.Kind == PreviewIccKind.MatrixTrc && gray)
                {
                    // 灰階配 RGB 設定檔：中性軸只需綠曲線（上游同分支）。
                    var g = info.Trc[1];
                    return ([g, g, g], info.ToXyzD50, info.Named);
                }
                if (info.Kind == PreviewIccKind.Gray && gray)
                    return (info.Trc, PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb);
                if (info.Kind == PreviewIccKind.NeedsCms
                    && ((info.Model == PreviewIccModel.Gray) == gray))
                {
                    // FilmBatch 無 CMS（moxcms）：上游會送 `cms_to_linear_rec2020`；
                    // 按上游「存在但不可用 → sRGB」分支處理（見偏差說明）。
                }
            }
            // 存在但不可用（損毀、模型不合、CMS 失敗）：一律 sRGB（上游 `icc.is_some` 分支）。
            var unsupported = PreviewTrc.Srgb;
            return ([unsupported, unsupported, unsupported], PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb);
        }
        if (declared is { } space && jpegExif is null)
        {
            // 容器外界宣告僅在 JPEG 既無 ICC 又無自帶 Exif 時生效（上游 `hint` filter）。
            var named = space == PreviewColorSpace.AdobeRgb ? ColorSpaces.AdobeRgb : ColorSpaces.Srgb;
            var t = named == ColorSpaces.AdobeRgb ? PreviewTrc.Gamma(563f / 256f) : PreviewTrc.Srgb;
            return ([t, t, t], PreviewIcc.RgbToXyzD50(named), named);
        }
        if (jpegExif is not null && AdobeRgbHint(jpegExif))
        {
            var t = PreviewTrc.Gamma(563f / 256f);
            return ([t, t, t], PreviewIcc.RgbToXyzD50(ColorSpaces.AdobeRgb), ColorSpaces.AdobeRgb);
        }
        var srgb = PreviewTrc.Srgb;
        return ([srgb, srgb, srgb], PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb);
    }

    /// <summary>
    /// 單次 8-bit 讀取 → LUT 線性化（上游 `linearize` `convert.rs:185-209` 的 U8 分支；
    /// 灰階複用綠通道 LUT）→ 線性光縮放（`factor >= 2 ? Box : Mitchell`）→ 來源→工作矩陣
    ///（上游 `to_working` 經 `SourceSpace::to_space`，`lib.rs:253-262`）。
    /// 8-bit JPEG 以 `ToByteArray` 單次讀取編碼值；灰階以單通道讀取後複用三通道。
    /// </summary>
    internal static RgbImage Decode8(
        byte[] jpeg, JpegColourMarkers markers, PreviewColorSpace? declared, uint edge)
    {
        var space = ResolveSpace(markers.Icc, markers.Exif, declared, markers.Components);
        var (trc, toXyzD50, named) = space;
        var luts = trc.Select(PreviewJpegColour.Lut8).ToArray();
        var grayLut = luts[1];
        var gray = markers.Components == 1;

        var maxEdge = (int)Math.Min(edge, int.MaxValue);
        // edge = 0 時上游 max_size (0,0) 被 filter 丟掉：不做任何縮放（見 decode_with_fallback）。
        var box = edge == 0 ? 0u : (uint)Math.Max(1, maxEdge);
        var settings = new MagickReadSettings();
        if (box != 0 && (markers.Width >= box * 2 || markers.Height >= box * 2))
        {
            // DCT 縮放（上游 `decode_jpeg_decoder` + `scaled_request` 經 `jpeg:size` 讀取定義；M = 8 時省略）。
            var m = DctScale(markers.Width, markers.Height, box, box);
            if (m != 8)
                settings.SetDefine(MagickFormat.Jpeg, "size", $"{markers.Width * m / 8}x{markers.Height * m / 8}");
        }
        byte[] encoded;
        int w, h;
        using (var magick = new MagickImage(jpeg, settings))
        {
            w = (int)magick.Width;
            h = (int)magick.Height;
            using (var pixels = magick.GetPixelsUnsafe())
            {
                // 單次傳輸解碼：絕不設 ColorSpace、不 Resize；灰階以 RGB 讀取後取綠通道（上游複用綠 LUT）。
                encoded = pixels.ToByteArray(PixelMapping.RGB)
                    ?? throw new InvalidDataException("無法讀取預覽像素。");
            }
        }
        if (encoded.Length != (long)w * h * 3)
            throw new InvalidDataException("預覽像素長度不合。");
        var linear = new float[(long)w * h * 3];
        if (gray)
        {
            // 上游 `linearize` 灰階取綠 LUT（`luts[1]`）：編碼灰即三通道中值，取 G 通道線性化後複用。
            for (var i = 0; i < w * h; i++)
            {
                var l = grayLut[encoded[i * 3 + 1]];
                linear[i * 3] = l;
                linear[i * 3 + 1] = l;
                linear[i * 3 + 2] = l;
            }
        }
        else
        {
            var lr = luts[0]; var lg = luts[1]; var lb = luts[2];
            for (var i = 0; i < w * h; i++)
            {
                linear[i * 3] = lr[encoded[i * 3]];
                linear[i * 3 + 1] = lg[encoded[i * 3 + 1]];
                linear[i * 3 + 2] = lb[encoded[i * 3 + 2]];
            }
        }
        var img = new RgbImage(w, h, linear);
        if (box != 0 && ((uint)Math.Max(w, h) > box))
        {
            // 線性光下縮放（上游 `finish`：`factor >= 2 ? Box : Mitchell`，`convert.rs:129-133`）。
            var factor = Math.Max(w / (double)box, h / (double)box);
            img = Resample.Fit(img, maxEdge, maxEdge, factor >= 2.0 ? ResampleFilter.Box : ResampleFilter.Mitchell);
        }
        if (named is { } s && s == ColorSpaces.Working)
            return img;
        // 來源→工作矩陣（上游 `to_working`，`lib.rs:253-262`）。
        var m2 = PreviewIcc.ToWorking(toXyzD50, named).ToF32();
        var d = img.Data;
        for (var i = 0; i < d.Length; i += 3)
        {
            var r = d[i]; var g = d[i + 1]; var b = d[i + 2];
            d[i] = m2[0] * r + m2[1] * g + m2[2] * b;
            d[i + 1] = m2[3] * r + m2[4] * g + m2[5] * b;
            d[i + 2] = m2[6] * r + m2[7] * g + m2[8] * b;
        }
        return img;
    }
}
