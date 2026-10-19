#include "common/color.wgsl"
struct Params { amount: f32, pad0: f32, pad1: f32, pad2: f32, }
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var blurred: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let c = textureLoad(src, id.xy, 0);
    let lum = luminance(c.rgb);
    let base = luminance(textureLoad(blurred, id.xy, 0).rgb);
    // 一般亮度等比縮放（保留飽和度）；接近黑時改為三通道等量相加，避免倍率爆大把暗部色雜訊放大成彩色斑點（與 CPU 相同）
    let diff = (lum - base) * p.amount;
    let ratio = smoothstep_f(0.02, 0.1, lum);
    var rgb = c.rgb + vec3f(diff);
    if (ratio > 0.0) { rgb = c.rgb + vec3f(diff) * (c.rgb * (ratio / lum) + vec3f(1.0 - ratio)); }
    textureStore(dst, id.xy, vec4f(max(rgb, vec3f(0.0)), c.a));
}
