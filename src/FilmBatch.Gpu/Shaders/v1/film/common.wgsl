#include "common/color.wgsl"
// Exact layout of Engine/Rendering/FilmParameters.cs (13 aligned 16-byte rows).
struct FilmParams {
    wb0: vec4f, wb1: vec4f, wb2: vec4f,
    row0: vec4f, row1: vec4f, row2: vec4f,
    curve: vec4f, effect: vec4f, tint: vec4f, weights: vec4f,
    spatial: vec4f, blend: vec4f,
    seed: u32, pad0: u32, pad1: u32, pad2: u32,
}
fn film_transform(c: vec3f, r: vec4f, g: vec4f, b: vec4f) -> vec3f {
    return vec3f(dot(c, r.xyz), dot(c, g.xyz), dot(c, b.xyz));
}
fn film_curve(value: f32, slope: f32, shift: f32) -> f32 {
    if (slope == 1.0 && shift == 0.0) { return value; }
    if (value <= 0.0) { return 0.0; }
    if (value >= 1.0) { return 1.0; }
    return 1.0 / (1.0 + exp(-(slope * log(value / (1.0 - value)) - shift)));
}
fn film_saturate(rgb: vec3f, saturation: f32) -> vec3f {
    var lms = vec3f(dot(rgb, vec3f(.41222147, .53633254, .05144599)),
        dot(rgb, vec3f(.21190350, .68069955, .10739696)),
        dot(rgb, vec3f(.08830246, .28171884, .62997870)));
    lms = sign(lms) * pow(abs(lms), vec3f(1.0 / 3.0));
    let lightness = dot(lms, vec3f(.21045426, .79361779, -.00407205));
    let a = dot(lms, vec3f(1.97799850, -2.42859221, .45059371)) * saturation;
    let b = dot(lms, vec3f(.02590404, .78277177, -.80867577)) * saturation;
    lms = vec3f(lightness + .39633778 * a + .21580376 * b,
        lightness - .10556135 * a - .06385417 * b,
        lightness - .08948418 * a - 1.29148555 * b);
    lms *= lms * lms;
    return vec3f(dot(lms, vec3f(4.07674166, -3.30771159, .23096993)),
        dot(lms, vec3f(-1.26843800, 2.60975740, -.34131940)),
        dot(lms, vec3f(-.00419609, -.70341861, 1.70761470)));
}
