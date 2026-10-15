#include "film/common.wgsl"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: FilmParams;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let input = textureLoad(src, id.xy, 0);
    var rgb = film_transform(input.rgb * p.curve.w, p.wb0, p.wb1, p.wb2);
    switch i32(p.effect.x) {
        case 1: {
            rgb = mix(rgb, vec3f(luminance(rgb)), p.effect.y);
            rgb *= vec3f(1.0 + p.effect.z, 1.0, 1.0 - p.effect.z);
            rgb = rgb * (1.0 - p.effect.w) + p.effect.w;
        }
        case 2: {
            rgb = (rgb - .18) * p.weights.w + .18 + p.tint.w;
            rgb *= p.tint.rgb;
        }
        case 3: {
            var lum = (dot(rgb, p.weights.rgb) - .18) * p.weights.w + .18;
            let cell = vec2u((vec2f(id.xy) + .5) / p.blend.w * 256.0);
            lum += (pcg_unit(p.seed + cell.x * 4001u + cell.y * 7919u) - .5) * p.blend.z * .025;
            rgb = vec3f(lum);
        }
        default: {
            if (p.blend.y == 1.0) { rgb = vec3f(dot(rgb, p.weights.rgb)); }
            else if (p.blend.y == 2.0) { rgb = vec3f(1.05, .78, .45) * dot(rgb, p.weights.rgb); }
            else { rgb = film_transform(rgb, p.row0, p.row1, p.row2); }
            rgb = vec3f(film_curve(rgb.r, p.curve.x, p.curve.y), film_curve(rgb.g, p.curve.x, p.curve.y), film_curve(rgb.b, p.curve.x, p.curve.y));
            if (p.curve.z != 1.0) { rgb = film_saturate(rgb, p.curve.z); }
        }
    }
    textureStore(dst, id.xy, vec4f(max(rgb, vec3f(0.0)), input.a));
}
