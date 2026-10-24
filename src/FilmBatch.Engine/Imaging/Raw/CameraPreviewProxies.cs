// 移植自 LightCraft crates/engine/src/camera_preview.rs（proxies：感光／JPEG 代理與截斷遮罩）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class CameraPreview
{
    private sealed record ProxiesResult(RgbImage Sensor, RgbImage Reference, bool[] Clipped);

    /// <summary>
    /// 同尺寸的感光代理（白平衡、基準曝光、經變換矩陣）與相機 JPEG 代理（線性 Rec.2020），
    /// 以及每代理像素是否有覆蓋截斷樣本（camera_preview 的 <c>proxies</c>）。
    /// 感光代理來自 DevelopBinned；CR3 方向對齊後再套色；其餘先套色。
    /// <paramref name="lens"/> 為檔案自家鏡頭校正 warp（parent 在移除 List3 鏡頭 ops 前以
    /// <see cref="CameraPreviewLens.LensGeometryOf"/> 快照）：相機常對自家 JPEG 施此校正
    /// （Nikon Z 鏡頭恆施，最大 9% zoom），感光代理亦 warp 後以 alignment 擇優配對；
    /// 未校正 JPEG 的檔案保留原代理。CR3 不走 lens warp（上游同）。不做最終校正（見
    /// <see cref="FilmBatch.Raw.EmbeddedLensCorrection"/>，parent 在最終解析度另行套用）。
    /// </summary>
    private static ProxiesResult? Proxies(RawImage raw, ReadOnlyMemory<byte> bytes, CameraTransform transform, int size, double minFraction, CameraPreviewLens? lens = null)
    {
        var edge = (uint)Math.Max(2 * size, 384);
        if (!TryDecodePreview(bytes, edge, out var reference, out var sourceW, out var sourceH))
            return null;
        var a = raw.ActiveArea;
        var crop = raw.Crop.Clipped(a.Width, a.Height);
        if (minFraction > 0.0)
        {
            var need = Math.Max(minFraction * Math.Max(crop.Width, crop.Height), 384.0);
            if (Math.Max(sourceW, sourceH) < need)
                return null;
        }
        if (crop.Width == 0 || crop.Height == 0 || reference.Width == 0 || reference.Height == 0)
            return null;
        static bool Matches(int w, int h, int rw, int rh) =>
            Math.Abs((double)rw / rh / ((double)w / h) - 1.0) <= 0.02;
        if (!Matches(crop.Width, crop.Height, reference.Width, reference.Height))
        {
            if (!Matches(a.Width, a.Height, reference.Width, reference.Height))
                return null;
            var sx = (double)reference.Width / a.Width;
            var sy = (double)reference.Height / a.Height;
            var x = (int)Math.Round(crop.X * sx);
            var y = (int)Math.Round(crop.Y * sy);
            var w = (int)Math.Round(crop.Width * sx);
            var h = (int)Math.Round(crop.Height * sy);
            if (w < 16 || h < 16 || x + w > reference.Width || y + h > reference.Height)
                return null;
            reference = reference.Crop(x, y, w, h);
        }
        var k = checked(Math.Max((crop.Width > crop.Height ? crop.Width : crop.Height).DivCeil((int)edge), 2).DivCeil(2) * 2);
        var sensorImg = SensorProxy(raw, k, (int)edge);
        if (sensorImg is null)
            return null;
        var clipGrid = ClipMask(sensorImg);
        var clipFlat = FlatMask(clipGrid);
        RgbImage? warped = null;
        bool[]? warpedClipped = null;
        // CR3 不在此走 lens warp（上游同）；其餘候選 warp，alignment 擇優。
        // lens 由 parent 在移除 List3 鏡頭 ops 前快照傳入（此處不再自 List3 讀取，避免雙重校正時序依賴）。
        if (lens is not null && raw.Format != RawFormat.Cr3)
        {
            var wimg = CameraPreviewLens.IntoGeometry(sensorImg, raw, k, lens);
            var wclip = wimg is null ? null : CameraPreviewLens.IntoGeometry(MaskToImage(clipGrid, sensorImg.Width, sensorImg.Height), raw, k, lens);
            if (wimg is not null && wclip is not null)
            {
                warped = wimg;
                var wb = new bool[wclip.Width * wclip.Height];
                for (var i = 0; i < wb.Length; i++)
                    wb[i] = wclip.Data[i * 3] > 0f;
                warpedClipped = wb;
            }
        }
        var gain = MathF.Pow(2f, (float)transform.BaselineExposure);
        var wbv = transform.Wb;
        var mat = transform.Matrix.ToF32();
        void ToWorking(RgbImage img)
        {
            var d = img.Data;
            for (var i = 0; i < d.Length; i += 3)
            {
                var c0 = d[i] * wbv[0] * gain;
                var c1 = d[i + 1] * wbv[1] * gain;
                var c2 = d[i + 2] * wbv[2] * gain;
                var r = mat[0] * c0 + mat[1] * c1 + mat[2] * c2;
                var g = mat[3] * c0 + mat[4] * c1 + mat[5] * c2;
                var b = mat[6] * c0 + mat[7] * c1 + mat[8] * c2;
                d[i] = r; d[i + 1] = g; d[i + 2] = b;
            }
        }
        bool[] clipNow = clipFlat;
        if (raw.Format == RawFormat.Cr3)
        {
            sensorImg = sensorImg.Oriented(raw.Orientation);
            clipGrid = OrientMask(clipGrid, sensorImg.Width, sensorImg.Height, raw.Orientation);
            reference = reference.Oriented(raw.Orientation);
            ToWorking(sensorImg);
            var aligned = AlignCr3Framing(sensorImg, MaskToImage(clipGrid, sensorImg.Width, sensorImg.Height), reference);
            sensorImg = aligned.Sensor;
            clipGrid = aligned.Clipped;
            clipNow = FlatMask(clipGrid);
        }
        var small = Resample.Fit(sensorImg, size, size, ResampleFilter.Box);
        var smallRef = ReferenceAt(reference, small.Width, small.Height);
        if (warped is not null && warpedClipped is not null)
        {
            var cand = Resample.Fit(warped, size, size, ResampleFilter.Box);
            var candRef = ReferenceAt(reference, cand.Width, cand.Height);
            if (Alignment(cand, candRef) > Alignment(small, smallRef))
            {
                small = cand;
                smallRef = candRef;
                clipNow = ResizeMask(warpedClipped, (warped.Width, warped.Height), small.Width, small.Height);
            }
            else
            {
                clipNow = ResizeMask(clipNow, (sensorImg.Width, sensorImg.Height), small.Width, small.Height);
            }
        }
        else
        {
            clipNow = ResizeMask(clipNow, (sensorImg.Width, sensorImg.Height), small.Width, small.Height);
        }
        if (raw.Format != RawFormat.Cr3)
            ToWorking(small);
        return new ProxiesResult(small, smallRef, clipNow);
    }

    private static (int W, int H) WarpedSize(RgbImage img) => (img.Width, img.Height);

    private static bool[] FlatMask(bool[,] m)
    {
        var h = m.GetLength(0); var w = m.GetLength(1);
        var flat = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                flat[y * w + x] = m[y, x];
        return flat;
    }

    /// <summary>相機 JPEG 精確縮到感光代理尺寸（camera_preview 的 <c>reference_at</c>）。</summary>
    internal static RgbImage ReferenceAt(RgbImage reference, int w, int h) =>
        Resample.Resize(reference, w, h, ResampleFilter.Box);

    private static RgbImage SensorProxyImage(RgbImage img) => img;

    /// <summary>
    /// 未校正的感光代理。List3 只含鏡頭鏈（原廠或 Lensfun）時照樣分箱、不套用：鏡頭幾何由
    /// <see cref="Proxies"/> 的 warp 候選處理；否則 Lensfun 會讓沒有檔內矩陣的 RAW 失去相機外觀。
    /// </summary>
    internal static RgbImage? SensorProxy(RawImage raw, int k, int edge)
    {
        if (k <= 0)
            return null;
        try
        {
            var list3 = raw.Opcodes.List3;
            var binned = raw.DevelopBinned(k, SensorClip, list3.Count != 0 && RawSourceGeometry.IsLensChainOnly(list3));
            if (binned is not null)
                return binned;
        }
        catch { return null; }
        if (raw.Cpp == 3 && raw.Cfa is null)
        {
            try
            {
                var full = raw.Develop(DemosaicMethod.Bilinear);
                return Resample.Fit(full, edge, edge, ResampleFilter.Box);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>任一通道 ≥ 截斷位準即 1 的遮罩（camera_preview 的 <c>clip_mask</c>，相機 RGB 上）。</summary>
    private static bool[,] ClipMask(RgbImage camera)
    {
        var mask = new bool[camera.Height, camera.Width];
        var d = camera.Data;
        for (var y = 0; y < camera.Height; y++)
            for (var x = 0; x < camera.Width; x++)
            {
                var i = (y * camera.Width + x) * 3;
                mask[y, x] = d[i] >= SensorClip || d[i + 1] >= SensorClip || d[i + 2] >= SensorClip;
            }
        return mask;
    }

    private static RgbImage MaskToImage(bool[,] mask, int w, int h)
    {
        var img = new RgbImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = mask[y, x] ? 1f : 0f;
                img.Data[(y * w + x) * 3] = v;
                img.Data[(y * w + x) * 3 + 1] = v;
                img.Data[(y * w + x) * 3 + 2] = v;
            }
        return img;
    }

    private static bool[,] OrientMask(bool[,] mask, int w, int h, Orientation o)
    {
        var img = MaskToImage(mask, mask.GetLength(1), mask.GetLength(0));
        var oriented = img.Oriented(o);
        var ys = new bool[oriented.Height, oriented.Width];
        for (var y = 0; y < oriented.Height; y++)
            for (var x = 0; x < oriented.Width; x++)
                ys[y, x] = oriented.Data[(y * oriented.Width + x) * 3] > 0f;
        return ys;
    }

    private static bool[] ResizeMask(bool[] flat, (int W, int H) from, int w, int h)
    {
        var img = new RgbImage(from.W, from.H);
        for (var i = 0; i < flat.Length && i < img.Data.Length / 3; i++)
        {
            var v = flat[i] ? 1f : 0f;
            img.Data[i * 3] = v;
            img.Data[i * 3 + 1] = v;
            img.Data[i * 3 + 2] = v;
        }
        var resized = Resample.Resize(img, Math.Max(1, w), Math.Max(1, h), ResampleFilter.Box);
        var @out = new bool[resized.Width * resized.Height];
        for (var i = 0; i < @out.Length; i++)
            @out[i] = resized.Data[i * 3] > 0f;
        return @out;
    }

    /// <summary>截斷像素排除（camera_preview 的 <c>without_clipped</c>；尺寸不合或全無截斷回傳 null）。</summary>
    private static RgbImage? WithoutClipped(RgbImage sensor, bool[] clipped)
    {
        if (clipped.Length != sensor.Width * sensor.Height || !clipped.Contains(true))
            return null;
        var @out = new RgbImage(sensor.Width, sensor.Height, (float[])sensor.Data.Clone());
        for (var i = 0; i < clipped.Length; i++)
        {
            if (clipped[i])
            {
                @out.Data[i * 3] = @out.Data[i * 3 + 1] = @out.Data[i * 3 + 2] = float.NaN;
            }
        }
        return @out;
    }

    /// <summary>
    /// 嵌入 JPEG 解碼為線性 Rec.2020（上游 <c>files::decode_raw_preview</c> `files.rs:704-722` 的 Magick.NET 版）：
    /// JPEG 自帶 ICC／Exif 詮釋優先（上游 <c>decode_with_fallback</c>；僅兩者皆無時用容器宣告當 fallback，
    /// 於線性化前套用，絕不套到已重採樣像素）。線性光下縮放（`factor &gt;= 2 ? Box : Mitchell`）。
    /// JPEG 位元組來自 RawPreview（SonyPreview 切片）；解碼上限 <see cref="FitPreviewMaxPixels"/>。
    /// 與上游 JPEG 解碼非逐位元一致：IDCT／上採樣由托管解碼器執行而非 jpeg-decoder／zune；
    /// LUT/CMYK 型 ICC 的 CMS 依後端：桌面經 Magick.NET/LCMS、iOS 經 ColorSync（見 <see cref="PreviewCms"/>）。
    /// </summary>
    internal static bool TryDecodePreview(ReadOnlyMemory<byte> bytes, uint edge, out RgbImage image, out int sourceW, out int sourceH)
    {
        image = new RgbImage(1, 1);
        sourceW = sourceH = 0;
        var jpeg = RawPreview.EmbeddedPreview(bytes);
        if (jpeg is null || jpeg.Length == 0)
            return false;
        var declared = RawPreview.EmbeddedPreviewColorSpace(bytes);
        var markers = PreviewJpegColour.ParseMarkers(jpeg);
        if (markers is null || markers.Components == 0 || markers.Width == 0 || markers.Height == 0)
            return false;
        sourceW = (int)Math.Min(markers.Width, int.MaxValue);
        sourceH = (int)Math.Min(markers.Height, int.MaxValue);
        if ((ulong)markers.Width * markers.Height > FitPreviewMaxPixels)
            return false;
        try
        {
            image = PreviewDecode.Decode8(jpeg, markers, declared, edge);
            var pixels = (ulong)image.Width * (ulong)image.Height;
            if (pixels == 0 || pixels * 3 > (ulong)RawLimits.MaxSamples * 3UL / 2UL)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class CameraPreviewInts
{
    internal static int DivCeil(this int value, int divisor) => (value + divisor - 1) / divisor;
}
