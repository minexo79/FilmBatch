// 線性光 → 顯示空間（sRGB），為後續「顯示」區段模組建立正確的工作空間
#include "common/color.wgsl"

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> extended_srgb: vec4u;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    var encoded = linear_to_srgb(c.rgb);
    if (extended_srgb.x != 0u) { encoded = sign(c.rgb) * linear_to_srgb(abs(c.rgb)); }
    textureStore(dst, id.xy, vec4f(encoded, c.a));
}
