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
    let gain = select(1.0, (lum + (lum - base) * p.amount) / lum, lum > .0001);
    textureStore(dst, id.xy, vec4f(max(c.rgb * gain, vec3f(0.0)), c.a));
}
