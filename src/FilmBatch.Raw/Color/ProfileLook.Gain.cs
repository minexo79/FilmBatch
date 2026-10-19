// 移植自 LightCraft crates/raw/src/profile.rs（ProfileLook 的 GainTableMap 部分；lightcraft-raw gaintable）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public sealed partial class ProfileLook
{
    /// <summary><c>ProfileGainTableMap</c>／<c>ProfileGainTableMap2</c>（DNG 1.6／1.7；Apple ProRAW 有此表）。</summary>
    public FilmBatch.Raw.GainTableMap? GainTableMap { get; set; }
}
