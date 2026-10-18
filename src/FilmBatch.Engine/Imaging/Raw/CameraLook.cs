// 移植自 LightCraft crates/engine/src/camera_preview.rs（CameraLook：相機外觀資料型別與版本）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 單張 RAW 的起始外觀（camera_preview 的 <c>CameraLook</c>）：
/// 色彩矩陣（白平衡後相機 RGB → 線性 Rec.2020）、相機色調曲線、線性 ProPhoto 下的色相／飽和修正表。
/// </summary>
internal sealed record CameraLook(Mat3 Matrix, CameraTone Tone, HsvTable? HueSat)
{
    /// <summary>相機外觀擬合版本（camera_preview 的 <c>LOOK_VERSION</c> = 9）。</summary>
    public const uint LookVersion = 9;
}
