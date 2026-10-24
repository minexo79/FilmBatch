#include "common/color.wgsl"
#include "common/sampling.wgsl"
struct Params {
    scale: f32, amount: f32, cosine: f32, sine: f32,
    hue: f32, irregularity: f32, density: f32, short_side: f32,
    count: i32, polarity: i32, seed: u32, leak: i32,
    blend: i32, _pad0: u32, _pad1: u32, _pad2: u32,   // blend（僅 leak）：0 screen / 1 add / 2 lighten
}
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var asset: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;
@group(0) @binding(4) var<uniform> view: FrameView;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (any(id.xy >= size)) { return; }
    let asset_size = vec2f(textureDimensions(asset));
    let input = textureLoad(src, id.xy, 0);
    var rgb = input.rgb;
    for (var n = 0; n < p.count; n++) {
        let uv = (vec2f(frame_pixel(view, id.xy)) + .5 - view.frame_size * .5) / p.short_side / p.scale;
        var rx = uv.x * p.cosine - uv.y * p.sine;
        var ry = uv.x * p.sine + uv.y * p.cosine;
        if (n == 1) { rx = -rx; }
        if (n == 2) { ry = -ry; }
        let point = (vec2f(rx, ry) + .5) * asset_size - .5;
        if (any(point < vec2f(0.0)) || any(point > asset_size - 1.0)) { continue; }
        let color = sample_bilinear(asset, point);
        if (p.leak != 0) {
            let hsv = rgb_to_hsv(color.rgb);
            let tint = hsv_to_rgb(hsv.x + p.hue, hsv.y, hsv.z);
            let noise = 1.0 - p.irregularity * value_noise2(rx * 4.0, ry * 4.0, p.seed + u32(n));
            let l = tint * color.a * noise;
            if (p.blend == 1) {
                rgb = rgb + p.amount * l;
            } else if (p.blend == 2) {
                rgb = rgb + p.amount * max(l - rgb, vec3f(0.0));
            } else {
                rgb = rgb + p.amount * l * (1.0 - min(rgb, vec3f(1.0)));
            }
        } else {
            let light = p.polarity == 1 || (p.polarity == 2 && pcg_unit(p.seed + u32(n)) > .5);
            let mask = select(1.0 - luminance(color.rgb), luminance(color.rgb), light) * color.a;
            let amount = mask * p.amount * p.density;
            rgb = rgb * (1.0 - amount) + select(0.0, amount, light);
        }
    }
    textureStore(dst, id.xy, vec4f(rgb, input.a));
}
