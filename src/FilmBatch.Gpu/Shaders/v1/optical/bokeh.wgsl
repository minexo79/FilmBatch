#include "common/sampling.wgsl"
struct Params {
    cx: f32, cy: f32, focus_size: f32, aspect: f32,
    cosine: f32, sine: f32, transition: f32, radius: f32,
    boost: f32, short_side: f32, focus_shape: i32, shape: i32,
}
struct Samples { points: array<vec4f, 32>, }
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<uniform> samples: Samples;
@group(0) @binding(4) var<uniform> view: FrameView;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (any(id.xy >= size)) { return; }
    let input = textureLoad(src, id.xy, 0);
    let fp = vec2f(frame_pixel(view, id.xy));
    let uv = (fp + .5 - vec2f(p.cx, p.cy) * view.frame_size) / p.short_side;
    let distance = shape_distance(p.focus_shape, uv, p.aspect, p.cosine, p.sine);
    let amount = clamp((distance - p.focus_size) / p.transition, 0.0, 1.0);
    var squeeze = 1.0;
    if (p.shape == 3) { squeeze = max(.25, 1.0 - length(uv)); }
    var sum = vec3f(0.0);
    if (amount > 0.0) {
        for (var i = 0; i < 32; i++) {
            let point = samples.points[i].xy * p.radius * vec2f(squeeze, 1.0);
            let color = sample_bilinear(src, fp + point - vec2f(view.origin)).rgb;
            let bright = clamp(color, vec3f(0.0), vec3f(1.0));
            sum += color + p.boost * bright * bright * bright * bright * .8;
        }
    }
    textureStore(dst, id.xy, vec4f(input.rgb * (1.0 - amount) + sum / 32.0 * amount, input.a));
}
