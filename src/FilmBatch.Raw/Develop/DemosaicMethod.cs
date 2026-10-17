// 移植自 LightCraft crates/raw/src/demosaic/mod.rs（Method）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw;

/// <summary>去馬賽克方法。LightCraft 的品質預設是 Ahd（見 Demosaic.DefaultMethod）。</summary>
public enum DemosaicMethod
{
    Bilinear,
    Ppg,
    Ahd,
}
