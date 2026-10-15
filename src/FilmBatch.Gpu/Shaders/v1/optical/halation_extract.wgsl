// 光暈第一步（5.3.9）：取亮度超過軟閾值的部分
#include "common/color.wgsl"

struct Params {
    amount: f32,
    threshold: f32,   // 0..1
    radius: f32,      // 0..1（短邊比例；僅供後續 pass 使用）
    hue: f32,
    short_side: f32,
    _pad0: f32,
    _pad1: f32,
    _pad2: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    let lum = luminance(c.rgb);
    let t = smoothstep_f(p.threshold, min(1.0, p.threshold + 0.08), lum);
    textureStore(dst, id.xy, vec4f(c.rgb * t, 0.0));
}
