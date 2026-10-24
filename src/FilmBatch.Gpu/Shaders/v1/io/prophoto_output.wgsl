// Signed sRGB effect coordinates -> ProPhoto D50 gamma 1.8. Alpha is unchanged.
#include "common/color.wgsl"

struct Params {
    row0: vec4f,
    row1: vec4f,
    row2: vec4f,
    luma: vec4f,
}; // Host OutputColorUniforms: offsets 0/16/32/48, 64 bytes.
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let pixel = textureLoad(src, id.xy, 0);
    let linear = sign(pixel.rgb) * srgb_to_linear(abs(pixel.rgb));
    var rgb = vec3f(dot(p.row0.xyz, linear), dot(p.row1.xyz, linear), dot(p.row2.xyz, linear));
    let y = clamp(dot(p.luma.xyz, rgb), 0.0, 1.0);
    let lo = min(rgb.r, min(rgb.g, rgb.b));
    let hi = max(rgb.r, max(rgb.g, rgb.b));
    // Both denominator branches remain finite; no per-channel function early returns on DX12.
    let low_scale = select(1.0, y / max(y - lo, 1e-9), lo < 0.0);
    let high_scale = select(1.0, (1.0 - y) / max(hi - y, 1e-9), hi > 1.0);
    // Anchor the same neutral-axis projection at its limiting face. This avoids a tiny
    // positive cancellation residue at zero, which pure gamma 1.8 would magnify.
    if (low_scale < 1.0 && low_scale <= high_scale) {
        rgb = (rgb - vec3f(lo)) * low_scale;
    } else if (high_scale < 1.0) {
        rgb = vec3f(1.0) + (rgb - vec3f(hi)) * high_scale;
    }
    textureStore(dst, id.xy, vec4f(pow(clamp(rgb, vec3f(0.0), vec3f(1.0)), vec3f(1.0 / 1.8)), pixel.a));
}
