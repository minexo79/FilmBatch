// 色調分離（5.3.13）：暗部與亮部各自染色，balance 控制分界（顯示空間）
#include "common/color.wgsl"

struct Params {
    shadow_hue: f32,
    shadow_amount: f32,    // 0..1
    highlight_hue: f32,
    highlight_amount: f32, // 0..1
    balance: f32,          // -1..1
    _pad0: f32, _pad1: f32, _pad2: f32,
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
    let split = 0.5 + p.balance * 0.3;
    let tS = 1.0 - smoothstep_f(split - 0.15, split + 0.15, lum);
    let tH = smoothstep_f(split - 0.15, split + 0.15, lum);
    let sc = hsv_to_rgb(p.shadow_hue, 0.6, 1.0);
    let hc = hsv_to_rgb(p.highlight_hue, 0.6, 1.0);
    var rgb = c.rgb;
    rgb = rgb * (1.0 - p.shadow_amount * tS * (1.0 - sc)) + vec3f(lum) * sc * p.shadow_amount * tS;
    rgb = rgb * (1.0 - p.highlight_amount * tH * (1.0 - hc)) + vec3f(lum) * hc * p.highlight_amount * tH;
    textureStore(dst, id.xy, vec4f(rgb, c.a));
}
