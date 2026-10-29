// 嵌入預覽的 CMS 變換（上游 icc::cms_to_linear_rec2020 `icc.rs:218-254` 的後端實作）：
// 裝置樣本（0..1 已正規化、通道數須合乎設定檔色彩模型：RGB→3、Gray→1、CMYK→4 系列）
// 經完整 CMS 變換（relative colorimetric）直達線性 Rec.2020；輸出為寬範圍 float（HDRI，不削波，
// 上游同：cms_to_linear_rec2020 不做 clamp），失敗回傳 null（上游 `None`：parse 失敗、模型不合、transform 失敗）。
// 目標設定檔為產生的線性 Rec.2020 ICC（內嵌資源 Profiles/linear-rec2020.icc；
// 對應上游 linear_rec2020_profile：BT.2020 原色＋線性 TRC；D50 PCS、chad Bradford，檔頭日期清零以保確定性）。
// 執行層由 ImageBackend 依後端分派：桌面為 Magick.NET/LCMS，iOS 為 ColorSync。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

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

    /// <summary>原生 CMS 依 ICC 模型讀取通道；呼叫前必須確保樣本緩衝符合該模型。</summary>
    internal static bool MatchesDeviceModel(byte[] icc, int channels, bool gray) =>
        icc.Length >= 20 && (gray
            ? channels == 1 && icc.AsSpan(16, 4).SequenceEqual("GRAY"u8)
            : channels == 3 && icc.AsSpan(16, 4).SequenceEqual("RGB "u8)
                || channels == 4 && icc.AsSpan(16, 4).SequenceEqual("CMYK"u8));

    /// <summary>
    /// 裝置樣本 → 線性 Rec.2020（上游 <c>cms_to_linear_rec2020</c>）：
    /// <paramref name="samples"/> 為 0..1 交錯裝置值（<paramref name="channels"/> = 1／3／4），
    /// <paramref name="gray"/> 時以單通道匯入。輸出寬範圍 float RGB（不削波）；任一步失敗回傳 null。
    /// </summary>
    internal static RgbImage? ToLinearRec2020(byte[] icc, float[] samples, int channels, int width, int height, bool gray) =>
        ImageBackend.ToLinearRec2020(icc, samples, channels, width, height, gray);
}
