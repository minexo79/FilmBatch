// 移植自 LightCraft crates/raw/src/dngwrite.rs（DNG 寫入選項與壓縮模式）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>DNG 像素壓縮（lightcraft <c>DngCompression</c>）。</summary>
public abstract record DngCompression
{
    /// <summary>未壓縮條帶（16 位元整數；浮點為 32 位元）。</summary>
    public sealed record Uncompressed : DngCompression;

    /// <summary>
    /// 無失真 JPEG 磚塊（<c>tile × tile</c> 像素，<c>tile</c> 為偶數）。
    /// CFA 磚塊編碼為兩個半寬交錯分量（常見 DNG 版面）；線性資料每樣本一個分量。
    /// </summary>
    public sealed record Lj92(uint Tile) : DngCompression;

    /// <summary>
    /// Deflate（zlib）磚塊。浮點資料存 16 位元（<paramref name="Half"/>）或 32 位元，
    /// 用浮點預測器（TIFF predictor 3）；整數用水平差分（predictor 2）。
    /// </summary>
    public sealed record Deflate(uint Tile, bool Half) : DngCompression;

    /// <summary>
    /// 有損 DNG（DNG 1.4 compression 34892）：去馬賽克後的線性相機 RGB，
    /// 縮到長邊至多 <paramref name="LongEdge"/> 像素，以 8-bit baseline JPEG 磚塊儲存，
    /// 品質 <paramref name="Quality"/>（1–100）。8-bit 碼走 1/2.2 冪曲線，
    /// 由 <c>LinearizationTable</c> 映射回線性 16 位元值。
    /// </summary>
    public sealed record LossyJpeg(uint Tile, byte Quality, uint? LongEdge) : DngCompression;
}

/// <summary>DNG 寫入選項（lightcraft <c>DngWriteOptions</c>）。</summary>
public sealed class DngWriteOptions
{
    public DngCompression Compression { get; set; } = new DngCompression.Lj92(256);

    public ByteOrder Order { get; set; } = ByteOrder.Little;

    /// <summary>要嵌入的 XMP 包（tag 700）；null 時由影像中繼資料產生。</summary>
    public string? Xmp { get; set; }
}
