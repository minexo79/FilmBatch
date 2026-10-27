// 光暈合成：模糊後的高光染色加回線性影像
#include "common/color.wgsl"

struct Params {
    amount: f32,
    threshold: f32,
    radius: f32,
    hue: f32,
    short_side: f32,
    saturation: f32,  // 0..1
    _pad1: f32,
    _pad2: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;    // 原圖
@group(0) @binding(1) var glow: texture_2d<f32>;   // 已模糊的高光層
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    let g = textureLoad(glow, id.xy, 0).rgb;
    let tint = hsv_to_rgb(p.hue, p.saturation, 1.0);
    textureStore(dst, id.xy, vec4f(c.rgb + g * tint * p.amount, c.a));
}
