// 底片風格（docs/film-looks.md）：與 FilmLookProcessing.cs 同演算法。
// 八色相帶修正已獨立成 hsl 模組（linear/hsl.wgsl），在此 pass 之後執行。
#include "common/color.wgsl"

struct LookParams {
    mix: vec4f,   // 效果比例、暖色、褪色、LUT 邊長
    to0: vec4f, to1: vec4f, to2: vec4f,       // sRGB → ProPhoto；to0.w 為 ProPhoto 旗標
    from0: vec4f, from1: vec4f, from2: vec4f, // ProPhoto → sRGB
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var lut: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: LookParams;

fn lut_at(r: i32, g: i32, b: i32, n: i32) -> vec3f {
    return textureLoad(lut, vec2i(r + b * n, g), 0).rgb;
}

fn lut_sample(c: vec3f) -> vec3f {
    let n = i32(p.mix.w);
    let maxi = f32(n - 1);
    let pos = clamp(c, vec3f(0.0), vec3f(1.0)) * maxi;
    let i0 = min(vec3i(pos), vec3i(n - 2));
    let f = pos - vec3f(i0);
    let r = i0.x; let g = i0.y; let b = i0.z;
    let c000 = lut_at(r, g, b, n);
    let c111 = lut_at(r + 1, g + 1, b + 1, n);
    if (f.x >= f.y) {
        if (f.y >= f.z) {
            let c100 = lut_at(r + 1, g, b, n); let c110 = lut_at(r + 1, g + 1, b, n);
            return c000 + f.x * (c100 - c000) + f.y * (c110 - c100) + f.z * (c111 - c110);
        }
        if (f.x >= f.z) {
            let c100 = lut_at(r + 1, g, b, n); let c101 = lut_at(r + 1, g, b + 1, n);
            return c000 + f.x * (c100 - c000) + f.z * (c101 - c100) + f.y * (c111 - c101);
        }
        let c001 = lut_at(r, g, b + 1, n); let c101 = lut_at(r + 1, g, b + 1, n);
        return c000 + f.z * (c001 - c000) + f.x * (c101 - c001) + f.y * (c111 - c101);
    }
    if (f.z >= f.y) {
        let c001 = lut_at(r, g, b + 1, n); let c011 = lut_at(r, g + 1, b + 1, n);
        return c000 + f.z * (c001 - c000) + f.y * (c011 - c001) + f.x * (c111 - c011);
    }
    if (f.z >= f.x) {
        let c010 = lut_at(r, g + 1, b, n); let c011 = lut_at(r, g + 1, b + 1, n);
        return c000 + f.y * (c010 - c000) + f.z * (c011 - c010) + f.x * (c111 - c011);
    }
    let c010 = lut_at(r, g + 1, b, n); let c110 = lut_at(r + 1, g + 1, b, n);
    return c000 + f.y * (c010 - c000) + f.x * (c110 - c010) + f.z * (c111 - c110);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let px = textureLoad(src, id.xy, 0);
    let original = px.rgb;
    var look: vec3f;
    if (p.to0.w != 0.0) {
        let prophoto = clamp(vec3f(dot(p.to0.xyz, original), dot(p.to1.xyz, original), dot(p.to2.xyz, original)), vec3f(0.0), vec3f(1.0));
        let encoded = pow(prophoto, vec3f(1.0 / 1.8));
        let sample = pow(max(lut_sample(encoded), vec3f(0.0)), vec3f(1.8));
        look = vec3f(dot(p.from0.xyz, sample), dot(p.from1.xyz, sample), dot(p.from2.xyz, sample));
    } else {
        look = srgb_to_linear(lut_sample(linear_to_srgb(clamp(original, vec3f(0.0), vec3f(1.0)))));
    }
    let warmth = p.mix.y;
    if (warmth != 0.0) { look *= vec3f(1.0 + 0.12 * warmth, 1.0, 1.0 - 0.12 * warmth); }
    let fade = p.mix.z;
    if (fade != 0.0) { look = srgb_to_linear(linear_to_srgb(max(look, vec3f(0.0))) * (1.0 - 0.2 * fade) + vec3f(0.1 * fade)); }
    textureStore(dst, id.xy, vec4f(mix(original, look, p.mix.x), px.a));
}
