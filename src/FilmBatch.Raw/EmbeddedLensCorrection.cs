// 內嵌鏡頭校正的最終套用（parent 在最終顯影／分箱解析度呼叫；不碰 RawSourceLoader）。
// 語意：上游 Develop.finish_demosaiced（去馬賽克後套 OpcodeList3，再預設裁切）＋
// engine files.rs embedded_lens（List3 轉最終影像座標）＋ pipeline optics 的 profile 校正開關。
// 呼叫順序由 parent 掌控：fit 前先取幾何（CameraPreviewLens），此處只在最終影像上套用一次，不雙重校正。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw;

/// <summary>
/// 內嵌鏡頭校正的最終套用（parent 用；RawSourceLoader 不碰）。
/// <paramref name="lensOps"/> 為檔案內已驗證的鏡頭校正（RawImage.Opcodes.List3 的鏡頭子集，
/// parent 在 fit 前取幾何後保留者）；<paramref name="developed"/> 為最終顯影解析度的相機 RGB
/// （去馬賽克後、裁切前；active-area 座標）；<paramref name="crop"/> 為預設裁切（active-area 相對）；
/// <paramref name="orientation"/> 為 EXIF 方向。回傳轉正前、已裁切的校正影像。
/// 空清單時只做裁切（不改像素幾何）。
/// </summary>
public static class EmbeddedLensCorrection
{
    /// <summary>最終影像是否攜帶可套用的鏡頭校正。</summary>
    public static bool HasCorrection(IReadOnlyList<Opcode> lensOps) =>
        lensOps.Any(op => op.IsLensCorrection() && op.IsApplied());

    /// <summary>
    /// 在最終顯影解析度套用鏡頭校正：先對 developed（active-area 框）套 warp／vignette，
    /// 再做預設裁切（與像素高寬比無關；拉伸由呼叫端既有流程處理），最後依方向轉正。
    /// 與 <see cref="RawImage.FinishDemosaiced"/> 的 opcode 順序一致（含 DNG 1.6 跳過規則）。
    /// </summary>
    public static RgbImage Apply(
        IReadOnlyList<Opcode> lensOps, RgbImage developed, PixelRect crop, Orientation orientation)
    {
        var corrected = lensOps.Count == 0 ? developed : OpcodeOps.ApplyList3(lensOps, developed);
        var c = crop.Clipped(developed.Width, developed.Height);
        if (c.Width > 1 && c.Height > 1
            && (c.X != 0 || c.Y != 0 || c.Width != developed.Width || c.Height != developed.Height))
            corrected = corrected.Crop(c.X, c.Y, c.Width, c.Height);
        return corrected.Oriented(orientation);
    }

    /// <summary>
    /// 自完整 List3 取出鏡頭校正子集（parent 在 fit 前快照幾何後、移除前呼叫；
    /// 與 RawSourceLoader 的 <c>RemoveAll(IsLensCorrection)</c> 對偶）。
    /// </summary>
    public static List<Opcode> Snapshot(IReadOnlyList<Opcode> list3) =>
        list3.Where(op => op.IsLensCorrection()).ToList();
}
