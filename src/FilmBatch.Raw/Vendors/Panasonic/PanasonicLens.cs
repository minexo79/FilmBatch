// 移植自 LightCraft crates/raw/src/vendor/rw2.rs（Panasonic／Leica 鏡頭畸變校正：標籤 0x0119 → WarpRectilinear）。
// 標籤名取自 ExifTool（DistortionInfo 及其字組名）；字組意義為對自家嵌入 JPEG 的量測（見上游檔頭註解）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Panasonic;

/// <summary>
/// Panasonic／Leica RW2 的相機鏡頭畸變校正（標籤 <c>0x0119</c>），等價 DNG <c>WarpRectilinear</c>（OpcodeList3，
/// 相對 active area），走既有 profile 校正路徑。相機未開啟校正、恆等或值不合理時回傳 null（不扭曲顯示）。
/// <list type="bullet">
/// <item>字組 7 低 4 位：1 = 校正開啟（DMC-GH1 為 0，其 JPEG 即未校正渲染）。</item>
/// <item>字組 12：正規化半徑（像素），active area 半對角線。</item>
/// <item>字組 8、4、11（÷32768）為校正的 k2、k4、k6：來源半徑 ρ（以字組 12 為單位，active area 中心起）
/// 在校正影像中落在 ρ·(1 + k2ρ² + k4ρ⁴ + k6ρ⁶)。</item>
/// <item>字組 5（÷32768）+1 為相機事後裁切的 zoom z：輸出半徑 = 校正半徑／z。</item>
/// </list>
/// </summary>
internal static class PanasonicLens
{
    /// <summary>相機畸變校正（ExifTool 名 DistortionInfo）。</summary>
    internal const ushort DistortionInfo = 0x0119;

    /// <summary>畸變係數與 zoom 的定點 1。</summary>
    private const double DistortionOne = 32768.0;

    /// <summary>擬合反函數可接受的最大誤差（相對半對角線）；真實鏡頭遠小於此（上游 1e-3）。</summary>
    private const double MaxFitError = 1e-3;

    internal static Opcode? Distortion(byte[]? block, PixelRect active)
        => block is null ? null : Distortion(Words(block), active);

    private static ushort[] Words(byte[] block)
    {
        var n = block.Length / 2;
        var words = new ushort[n];
        for (var i = 0; i < n; i++)
            words[i] = (ushort)(block[2 * i] | (block[2 * i + 1] << 8));
        return words;
    }

    internal static Opcode? Distortion(ushort[] w, PixelRect active)
    {
        if (w.Length < 16) return null;
        if ((w[7] & 0x000f) != 1) return null;
        static double S(ushort v) => unchecked((short)v);
        var (k2, k4, k6) = (S(w[8]) / DistortionOne, S(w[4]) / DistortionOne, S(w[11]) / DistortionOne);
        var zoom = 1.0 + S(w[5]) / DistortionOne;
        var radius = w[12];
        if (k2 == 0.0 && k4 == 0.0 && k6 == 0.0 && zoom == 1.0) return null;
        // DNG 正規化：中心到最遠角距離（像素索引單位）。
        var halfDiag = LensFit.Hypot(active.Width - 1.0, active.Height - 1.0) / 2.0;
        if (!(halfDiag >= 1.0 && radius >= 0.5 * halfDiag && radius <= 2.0 * halfDiag && zoom is >= 0.75 and <= 1.25))
            return null;
        // 相機校正：來源半徑 r（以 radius 為單位）的點移到 r·g(r)。
        double Corrected(double r)
        {
            var r2 = r * r;
            return r * (1.0 + r2 * (k2 + r2 * (k4 + r2 * k6)));
        }
        // DNG 正規化單位（角距離 = 1）下工作；輸出角落在半徑 zoom。
        var scale = radius / halfDiag;
        if (LensFit.WarpFromForwardMap(s => Corrected(s / scale) * scale, zoom, MaxFitError) is not { } k)
            return null;
        return new WarpRectilinear([[k[0], k[1], k[2], k[3], 0.0, 0.0]], [0.5, 0.5]);
    }

    /// <summary>raw IFD 的畸變校正（上游 <c>rw2::distortion(ifd, active)</c> 語意：位元組取自 IFD 值）。</summary>
    internal static Opcode? Distortion(Ifd ifd, PixelRect active)
        => Distortion(ifd.Bytes(DistortionInfo), active);
}
