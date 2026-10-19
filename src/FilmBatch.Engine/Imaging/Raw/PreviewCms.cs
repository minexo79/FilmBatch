// 嵌入預覽的 CMS 變換（上游 icc::cms_to_linear_rec2020 `icc.rs:218-254` 的 Magick.NET／LCMS 版）：
// 裝置樣本（0..1 已正規化、通道數須合乎設定檔色彩模型：RGB→3、Gray→1、CMYK→4 系列）
// 經完整 CMS 變換（relative colorimetric）直達線性 Rec.2020；輸出為寬範圍 float（HDRI，不削波，
// 上游同：cms_to_linear_rec2020 不做 clamp），失敗回傳 null（上游 `None`：parse 失敗、模型不合、transform 失敗）。
// 目標設定檔為產生的線性 Rec.2020 ICC（內嵌資源 Profiles/linear-rec2020.icc；
// 對應上游 linear_rec2020_profile：BT.2020 原色＋線性 TRC；D50 PCS、chad Bradford，檔頭日期清零以保確定性）。
// 灰階以 "I" 單通道匯入（上游 Layout::Gray）；RGB／CMYK 以同名映射匯入。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;
using ImageMagick;

namespace FilmBatch.Engine.Imaging.Raw;

internal static class PreviewCms
{
    private static byte[]? _rec2020Linear;
    private static readonly object _lock = new();

    /// <summary>線性 Rec.2020 目標 ICC 位元組（內嵌資源；讀不到時為 null，呼叫端視為 CMS 失敗）。</summary>
    internal static byte[]? Rec2020Linear
    {
        get
        {
            lock (_lock)
            {
                if (_rec2020Linear is not null)
                    return _rec2020Linear;
                try
                {
                    using var stream = typeof(PreviewCms).Assembly
                        .GetManifestResourceStream("FilmBatch.Engine.Imaging.Raw.Profiles.linear-rec2020.icc");
                    if (stream is null)
                        return null;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    _rec2020Linear = ms.ToArray();
                    return _rec2020Linear;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// 裝置樣本 → 線性 Rec.2020（上游 <c>cms_to_linear_rec2020</c>）：
    /// <paramref name="samples"/> 為 0..1 交錯裝置值（<paramref name="channels"/> = 1／3／4），
    /// <paramref name="gray"/> 時以單通道匯入。輸出寬範圍 float RGB（不削波）；任一步失敗回傳 null。
    /// </summary>
    internal static RgbImage? ToLinearRec2020(byte[] icc, float[] samples, int channels, int width, int height, bool gray)
    {
        var dst = Rec2020Linear;
        if (dst is null || samples.Length != (long)width * height * channels)
            return null;
        if (width <= 0 || height <= 0 || channels < 1)
            return null;
        try
        {
            var quanta = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                quanta[i] = samples[i] * 65535f;
            using var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
            image.ColorSpace = gray ? ColorSpace.Gray : channels == 4 ? ColorSpace.CMYK : ColorSpace.sRGB;
            PixelImportSettings import = gray
                ? new PixelImportSettings((uint)width, (uint)height, StorageType.Quantum, "I")
                : new PixelImportSettings((uint)width, (uint)height, StorageType.Quantum, channels == 4 ? PixelMapping.CMYK : PixelMapping.RGB);
            image.ImportPixels(quanta, import);
            image.SetProfile(new ColorProfile(icc));
            image.TransformColorSpace(new ColorProfile(dst));
            using var pixels = image.GetPixelsUnsafe();
            var q = pixels.ToArray();
            if (q is null || q.Length != (long)width * height * 3)
                return null;
            var linear = new float[q.Length];
            for (var i = 0; i < q.Length; i++)
                linear[i] = q[i] / 65535f;
            return new RgbImage(width, height, linear);
        }
        catch
        {
            return null;
        }
    }
}
