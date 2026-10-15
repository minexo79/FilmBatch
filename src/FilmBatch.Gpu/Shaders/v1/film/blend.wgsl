#include "film/common.wgsl"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var original: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: FilmParams;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let c = textureLoad(src, id.xy, 0);
    let input = textureLoad(original, id.xy, 0);
    textureStore(dst, id.xy, vec4f(input.rgb * (1.0 - p.blend.x) + c.rgb * p.blend.x, c.a));
}
