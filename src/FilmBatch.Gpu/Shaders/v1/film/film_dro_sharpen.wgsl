#include "film/common.wgsl"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var base: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: FilmParams;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let c = textureLoad(src, id.xy, 0);
    var t = clamp((textureLoad(base, id.xy, 0).r - .08) / .77, 0.0, 1.0);
    t = t * t * (3.0 - 2.0 * t);
    let delta = p.spatial.x * (1.0 - t) + p.spatial.y * t;
    textureStore(dst, id.xy, vec4f(max(c.rgb + delta, vec3f(0.0)), c.a));
}
