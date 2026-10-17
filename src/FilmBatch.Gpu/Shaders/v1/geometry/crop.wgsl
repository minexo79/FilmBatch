// 裁切（docs/cut-and-mask/plan.md Task 1.4）：輸出像素 → 來源座標（CropGeometry.OutToSource），
// 雙線性取樣、邊緣延伸；與 CpuPipeline.Crop 同演算法。src 為來源尺寸，dst 為裁切後尺寸。
#include "common/sampling.wgsl"

struct Params {
    m00: f32, m01: f32, m02: f32, src_w: f32,
    m10: f32, m11: f32, m12: f32, src_h: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(dst);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let o = vec2f(id.xy) + 0.5;
    let s = vec2f(p.m00 * o.x + p.m01 * o.y + p.m02, p.m10 * o.x + p.m11 * o.y + p.m12);
    textureStore(dst, id.xy, sample_bilinear(src, s - 0.5));
}
