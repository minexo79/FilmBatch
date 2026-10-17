// 移植自 LightCraft crates/engine/src/files.rs（decode_raw_preview 色彩詮釋順序）、
// crates/codecs/src/convert.rs（finish 線性化／線性光縮放／to_working、rgb_or_gray／cmyk 的 CMS 分支）、
// jpeg.rs（decode_with_fallback 的 DCT 縮放觸發條件與雙後端分派）、space.rs（SourceSpace::to_space）與
// exif.rs（adobe_rgb_hint）——JPEG 解碼改用 FilmBatch.Raw.Codecs.JpegPreview（zune-jpeg 埠／jpeg-decoder 埠，
// 與上游逐位元一致）；LUT／CMYK 設定檔走真正 CMS（PreviewCms，LCMS 經 Magick.NET）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 嵌入預覽 JPEG → 線性 Rec.2020 工作空間（上游 <c>files::decode_raw_preview</c> `files.rs:704-722`）。
/// 色彩詮釋順序（上游 <c>rgb_or_gray</c> + <c>cmyk</c> + <c>decode_with_fallback</c>）：
/// JPEG 自帶 ICC（APP2）→ 可解析矩陣／TRC（或灰階 kTRC）用其 TRC 線性化＋D50 色料矩陣；
/// LUT／CMYK 型設定檔（NeedsCms）走真正 CMS 直達線性 Rec.2020（CMS 失敗才按上游「存在但不可用 → sRGB」分支）；
/// 否則容器宣告（<c>RawPreview.EmbeddedPreviewColorSpace</c>）僅在 JPEG 既無 ICC 又無自帶 Exif 時生效；
/// 否則 JPEG 自帶 Exif 的 Adobe RGB 提示（ColorSpace 0xFFFF＋InteropIndex R03；上游 <c>exif::summarize</c>）；
/// 否則 sRGB。Adobe RGB TRC 為 gamma 563/256（上游 <c>space.rs</c> 同值）。
/// </summary>
internal static class PreviewDecode
{
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
    /// 色彩詮釋解析（上游 <c>rgb_or_gray</c> `convert.rs:255-312` 的分支順序）：
    /// ICC 矩陣／TRC → ICC 灰階 kTRC → ICC NeedsCms（模型合致者走真正 CMS，
    /// 見 <see cref="PreviewCms"/>；模型不合或 CMS 失敗由呼叫端按上游 `icc.is_some` 分支走 sRGB）
    /// → 容器宣告（僅 JPEG 既無 ICC 又無自帶 Exif）→ Exif Adobe 提示 → sRGB。
    /// 回傳線性化 TRC、D50 色料矩陣與具名空間；NeedsCms 成功時 <c>Cms</c> 非 null，
    /// 呼叫端直接採用其輸出（已為線性 Rec.2020，上游 `SourceSpace::named(Rec2020, IccCms)`、trc 無）。
    /// </summary>
    internal sealed record ResolvedSpace(PreviewTrc[] Trc, Mat3 ToXyzD50, RgbSpace? Named, PreviewIccInfo? Cms);

    internal static ResolvedSpace ResolveSpace(
        byte[]? icc, byte[]? jpegExif, PreviewColorSpace? declared, int components)
    {
        var gray = components == 1;
        if (icc is not null)
        {
            var info = PreviewIcc.Parse(icc);
            if (info is not null)
            {
                if (info.Kind == PreviewIccKind.MatrixTrc && !gray)
                    return new ResolvedSpace(info.Trc, info.ToXyzD50, info.Named, null);
                if (info.Kind == PreviewIccKind.MatrixTrc && gray)
                {
                    // 灰階配 RGB 設定檔：中性軸只需綠曲線（上游同分支）。
                    var g = info.Trc[1];
                    return new ResolvedSpace([g, g, g], info.ToXyzD50, info.Named, null);
                }
                if (info.Kind == PreviewIccKind.Gray && gray)
                    return new ResolvedSpace(info.Trc, PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb, null);
                if (info.Kind == PreviewIccKind.NeedsCms
                    && ((info.Model == PreviewIccModel.Gray) == gray)
                    && info.Model != PreviewIccModel.Cmyk)
                {
                    // 上游 `cms_to_linear_rec2020` 分支：RGB／Gray 模型合致者走真正 CMS（見 PreviewCms）；
                    // CMYK 設定檔配 RGB／灰階資料為模型不合 → 下方 sRGB；失敗時亦走 sRGB。
                    return new ResolvedSpace([], Mat3.Identity, ColorSpaces.Working, info);
                }
            }
            // 存在但不可用（損毀、模型不合、CMS 失敗）：一律 sRGB（上游 `icc.is_some` 分支）。
            var unsupported = PreviewTrc.Srgb;
            return new ResolvedSpace([unsupported, unsupported, unsupported], PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb, null);
        }
        if (declared is { } space && jpegExif is null)
        {
            // 容器外界宣告僅在 JPEG 既無 ICC 又無自帶 Exif 時生效（上游 `hint` filter）。
            var named = space == PreviewColorSpace.AdobeRgb ? ColorSpaces.AdobeRgb : ColorSpaces.Srgb;
            var t = named == ColorSpaces.AdobeRgb ? PreviewTrc.Gamma(563f / 256f) : PreviewTrc.Srgb;
            return new ResolvedSpace([t, t, t], PreviewIcc.RgbToXyzD50(named), named, null);
        }
        if (jpegExif is not null && AdobeRgbHint(jpegExif))
        {
            var t = PreviewTrc.Gamma(563f / 256f);
            return new ResolvedSpace([t, t, t], PreviewIcc.RgbToXyzD50(ColorSpaces.AdobeRgb), ColorSpaces.AdobeRgb, null);
        }
        var srgb = PreviewTrc.Srgb;
        return new ResolvedSpace([srgb, srgb, srgb], PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb, null);
    }

    /// <summary>
    /// 原生 JPEG 解碼（<see cref="FilmBatch.Raw.Codecs.JpegPreview"/>，上游 `decode_with_fallback` 分派：
    /// zune-jpeg 埠／jpeg-decoder 埠；DCT 縮放、CMYK 系列、lossless 16-bit 皆由其處理）
    /// → 色彩詮釋（上游 `rgb_or_gray`／`cmyk`，`convert.rs:255-336`）→ 線性光縮放
    /// （`factor &gt;= 2 ? Box : Mitchell`，`convert.rs:129-133`）→ 來源→工作矩陣
    /// （上游 `to_working` 經 `SourceSpace::to_space`，`lib.rs:253-262`）。
    /// NeedsCms（LUT／CMYK 設定檔）走真正 CMS（<see cref="PreviewCms"/>，
    /// 上游 `cms_to_linear_rec2020`，relative colorimetric；輸出已為線性 Rec.2020，
    /// 寬範圍 float 不削波）；CMS 失敗時按上游 `icc.is_some` 分支走 sRGB。
    /// 灰階線性化複用綠通道 LUT（上游 `linearize` 取 `luts[1]`）；CMYK 無 ICC 時走 naive
    /// sRGB 假設（上游 `cmyk`：`RGB = (1-C)(1-K)…` 視為 sRGB 編碼）。
    /// </summary>
    internal static RgbImage Decode8(
        byte[] jpeg, JpegColourMarkers markers, PreviewColorSpace? declared, uint edge)
    {
        var maxEdge = (int)Math.Min(edge, int.MaxValue);
        // edge = 0 時上游 max_size (0,0) 被 filter 丟掉：不做任何縮放（見 decode_with_fallback）。
        var box = edge == 0 ? 0u : (uint)Math.Max(1, maxEdge);
        var decoded = FilmBatch.Raw.Codecs.JpegPreview.Decode(jpeg, box, box);
        var gray = decoded.Channels == 1;
        var cmyk = decoded.Channels == 4;
        if ((!gray && !cmyk && decoded.Channels != 3) || decoded.Width <= 0 || decoded.Height <= 0)
            throw new InvalidDataException("預覽像素分量不合。");
        var n = checked((long)decoded.Width * decoded.Height);
        var ch = decoded.Channels;
        float[] samples;
        if (decoded.U8 is { } u8)
        {
            // 上游 `Buf::U8::norm`：v / 255（jpeg-decoder 的 YCCK→CMYK 系列位元組已由解碼端轉換，直接取用）。
            if (u8.Length != n * ch)
                throw new InvalidDataException("預覽像素長度不合。");
            samples = new float[n * ch];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = u8[i] / 255f;
        }
        else if (decoded.U16 is { } u16)
        {
            // 上游 `Buf::U16::norm`：左對齊 16-bit 值／65535（JpegPreview 已左對齊）。
            if (u16.Length != n * ch)
                throw new InvalidDataException("預覽像素長度不合。");
            samples = new float[n * ch];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = u16[i] / 65535f;
        }
        else throw new InvalidDataException("預覽像素為空。");
        var w = decoded.Width;
        var h = decoded.Height;

        if (cmyk)
            return DecodeCmyk(markers.Icc, samples, w, h, box, maxEdge);

        var space = ResolveSpace(markers.Icc, markers.Exif, declared, ch);
        if (space.Cms is not null && markers.Icc is { } iccBytes)
        {
            // 上游 NeedsCms 分支：裝置樣本 clamp 0..1 後送 `cms_to_linear_rec2020`（`device_samples`）。
            var clamped = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                clamped[i] = Math.Clamp(samples[i], 0f, 1f);
            if (PreviewCms.ToLinearRec2020(iccBytes, clamped, ch, w, h, gray) is { } cmsImage)
                return FitWorking(cmsImage, box, maxEdge);
            // CMS 失敗：按上游 `icc.is_some` 分支走 sRGB（見 ResolveSpace 的 unsupported 分支）。
            var unsupported = PreviewTrc.Srgb;
            space = new ResolvedSpace([unsupported, unsupported, unsupported], PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb, null);
        }
        var (trc, toXyzD50, named, _) = space;
        var linear = new float[n * 3];
        if (decoded.U16 is not null)
        {
            // 上游 `linearize` 的 U16 分支（`convert.rs:198-215`）：三通道相同（或灰階）時只建綠表複用，
            // 灰階取 `l0 = trc[1].lut16()`；直接以 16-bit 樣本索引，保持 lossless 精度。
            var same = trc[0].Equals(trc[1]) && trc[1].Equals(trc[2]);
            var l0 = PreviewJpegColour.Lut16(trc[1]);
            var lr = same || gray ? l0 : PreviewJpegColour.Lut16(trc[0]);
            var lb = same || gray ? l0 : PreviewJpegColour.Lut16(trc[2]);
            if (gray)
            {
                for (var i = 0; i < n; i++)
                {
                    var l = l0[U16Index(samples[i])];
                    linear[i * 3] = l;
                    linear[i * 3 + 1] = l;
                    linear[i * 3 + 2] = l;
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    linear[i * 3] = lr[U16Index(samples[i * 3])];
                    linear[i * 3 + 1] = l0[U16Index(samples[i * 3 + 1])];
                    linear[i * 3 + 2] = lb[U16Index(samples[i * 3 + 2])];
                }
            }
        }
        else if (gray)
        {
            // 上游 `linearize` 灰階取綠 LUT（`luts[1]`）：單通道值線性化後複用三通道。
            var grayLut = PreviewJpegColour.Lut8(trc[1]);
            for (var i = 0; i < n; i++)
            {
                var l = grayLut[NormIndex(samples[i])];
                linear[i * 3] = l;
                linear[i * 3 + 1] = l;
                linear[i * 3 + 2] = l;
            }
        }
        else
        {
            var luts = trc.Select(PreviewJpegColour.Lut8).ToArray();
            var lr = luts[0]; var lg = luts[1]; var lb = luts[2];
            for (var i = 0; i < n; i++)
            {
                linear[i * 3] = lr[NormIndex(samples[i * 3])];
                linear[i * 3 + 1] = lg[NormIndex(samples[i * 3 + 1])];
                linear[i * 3 + 2] = lb[NormIndex(samples[i * 3 + 2])];
            }
        }
        var img = new RgbImage(w, h, linear);
        img = FitWorking(img, box, maxEdge);
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

    /// <summary>正規化樣本 → 256 項 LUT 索引（夾 0..1；上游 U8 即編碼值索引）。</summary>
    private static int NormIndex(float v) => Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>正規化樣本 → 65536 項 LUT 索引（夾 0..1；上游 U16 即左對齊樣本值）。</summary>
    private static int U16Index(float v) => Math.Clamp((int)(v * 65535f + 0.5f), 0, 65535);

    /// <summary>
    /// CMYK 預覽（上游 `cmyk` `convert.rs:314-336`）：CMYK 模型 ICC 走真正 CMS；
    /// 否則 naive（`RGB = (1-C)(1-K)…` 視為 sRGB 編碼，再經 sRGB→工作矩陣；
    /// 來源標記 `IccUnsupported`／`Naive` 皆走此矩陣，上游同）。
    /// </summary>
    private static RgbImage DecodeCmyk(byte[]? icc, float[] samples, int w, int h, uint box, int maxEdge)
    {
        if (icc is not null)
        {
            var info = PreviewIcc.Parse(icc);
            if (info is { } inf && inf.Kind == PreviewIccKind.NeedsCms && inf.Model == PreviewIccModel.Cmyk)
            {
                // 上游 `cmyk` 的模型檢查在 CMS 內（Layout::Rgba 對 Cmyk；見 cms_to_linear_rec2020）：
                // 裝置樣本 clamp 0..1 後送 CMS；失敗或非 CMYK 設定檔走下方 naive。
                var clamped = new float[samples.Length];
                for (var i = 0; i < samples.Length; i++)
                    clamped[i] = Math.Clamp(samples[i], 0f, 1f);
                if (PreviewCms.ToLinearRec2020(icc, clamped, 4, w, h, gray: false) is { } cmsImage)
                    return FitWorking(cmsImage, box, maxEdge);
            }
        }
        var n = (long)w * h;
        var linear = new float[n * 3];
        var srgb = PreviewTrc.Srgb;
        var lut = PreviewJpegColour.Lut8(srgb);
        for (var i = 0; i < n; i++)
        {
            var c = Math.Clamp(samples[i * 4], 0f, 1f);
            var m = Math.Clamp(samples[i * 4 + 1], 0f, 1f);
            var y = Math.Clamp(samples[i * 4 + 2], 0f, 1f);
            var k = Math.Clamp(samples[i * 4 + 3], 0f, 1f);
            var kk = 1f - k;
            linear[i * 3] = lut[NormIndex((1f - c) * kk)];
            linear[i * 3 + 1] = lut[NormIndex((1f - m) * kk)];
            linear[i * 3 + 2] = lut[NormIndex((1f - y) * kk)];
        }
        var img = new RgbImage(w, h, linear);
        img = FitWorking(img, box, maxEdge);
        var m2 = PreviewIcc.ToWorking(PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), ColorSpaces.Srgb).ToF32();
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

    /// <summary>線性光下縮放（上游 `finish`：`factor &gt;= 2 ? Box : Mitchell`，`convert.rs:129-133`）。</summary>
    private static RgbImage FitWorking(RgbImage img, uint box, int maxEdge)
    {
        var (ww, hh) = (img.Width, img.Height);
        if (box != 0 && ((uint)Math.Max(ww, hh) > box))
        {
            var factor = Math.Max(ww / (double)box, hh / (double)box);
            img = Resample.Fit(img, maxEdge, maxEdge, factor >= 2.0 ? ResampleFilter.Box : ResampleFilter.Mitchell);
        }
        return img;
    }
}
