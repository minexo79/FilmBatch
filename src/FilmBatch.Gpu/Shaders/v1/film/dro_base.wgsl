#include "film/common.wgsl"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: FilmParams;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (any(pos >= size)) { return; }
    let center = luminance(textureLoad(src, pos, 0).rgb);
    let step = max(1, i32(round(p.blend.w * .02)));
    var sum = 0.0;
    var weights = 0.0;
    for (var dy = -2; dy <= 2; dy++) {
        for (var dx = -2; dx <= 2; dx++) {
            let lum = luminance(textureLoad(src, clamp(pos + vec2i(dx, dy) * step, vec2i(0), size - 1), 0).rgb);
            let weight = exp(-abs(lum - center) * 12.0) / f32(1 + dx * dx + dy * dy);
            sum += lum * weight;
            weights += weight;
        }
    }
    textureStore(dst, pos, vec4f(sum / weights));
}
