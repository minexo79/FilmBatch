// 基本調整（線性部分）：曝光、色溫、色調（計畫書 5.3.1 前段）
#include "common/color.wgsl"

struct Params {
    exposure: f32,     // EV
    temperature: f32,  // -1..1
    tint: f32,         // -1..1
    _pad: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    var c = textureLoad(src, id.xy, 0);
    c = vec4f(c.rgb * exp2(p.exposure), c.a);
    // 色溫（線性光中的近似通道增益）與色調（綠↔洋紅）
    c = vec4f(
        c.r * (1.0 + p.temperature * 0.18),
        c.g * (1.0 + p.tint * 0.12),
        c.b * (1.0 - p.temperature * 0.18),
        c.a);
    textureStore(dst, id.xy, c);
}
