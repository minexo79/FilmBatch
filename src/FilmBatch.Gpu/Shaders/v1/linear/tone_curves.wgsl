// 色調曲線（顯示編碼 RGB）：分段 → 主曲線 → 各通道已由 ToneCurveSet 合成為 R、G、B 三列查表，
// 線性內插取值（輸入夾到 0–1）；曲線飽和度 refine < 1 時把飽和度（色度 ÷ 亮度）拉回曲線前，亮度維持曲線結果。alpha 不變。
// 移植自 LightCraft v0.13.0（MIT OR Apache-2.0）crates/pipeline/src/finish.rs 的 curve_luts／refine_saturation
// 與 crates/color/src/spline.rs 的 Lut1::eval；須與 ToneCurveSet.Apply 逐式一致。
#include "common/color.wgsl"

struct Params {
    refine: f32,   // 0..1；1 = 保留曲線後的飽和度（略過修正）
    size: u32,     // 每通道查表格數（ToneCurveSet.Size）
    _pad0: u32,
    _pad1: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<storage, read> lut: array<f32>; // R、G、B 三列，各 p.size 格

fn lut_sample(start: u32, x: f32) -> f32 {
    let f = clamp(x, 0.0, 1.0) * f32(p.size - 1u);
    let i = min(u32(f), p.size - 2u);
    let a = lut[start + i];
    return a + (lut[start + i + 1u] - a) * (f - f32(i));
}

fn chroma(c: vec3f) -> f32 {
    return max(max(c.r, c.g), c.b) - min(min(c.r, c.g), c.b);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    var e = vec3f(lut_sample(0u, c.r), lut_sample(p.size, c.g), lut_sample(2u * p.size, c.b));
    if (p.refine < 1.0) {
        let y1 = luminance(e);
        let s0 = chroma(c.rgb) / max(luminance(c.rgb), 1e-4);
        let s1 = chroma(e) / max(y1, 1e-4);
        // 與 CPU 相同：任一飽和度近 0（含 NaN）就保留曲線結果；未選取時 pow 底數仍保持有效（1）。
        let colored = s0 > 1e-6 && s1 > 1e-6;
        let k = clamp(pow(select(1.0, s0 / max(s1, 1e-6), colored), 1.0 - p.refine), 0.0, 4.0);
        e = select(e, vec3f(y1) + (e - vec3f(y1)) * k, colored);
    }
    textureStore(dst, id.xy, vec4f(e, c.a));
}
