// 底片風格（docs/film-looks.md）：與 FilmLookProcessing.cs 同演算法。
#include "common/color.wgsl"

struct LookParams {
    mix: vec4f,   // 效果比例、暖色、褪色、LUT 邊長
    flags: vec4f, // x：分色相啟用
    bands: array<vec4f, 8>, // 色相、飽和、明度（-1..1）
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

fn wrap360(x: f32) -> f32 { return ((x % 360.0) + 360.0) % 360.0; }
fn wrap180(x: f32) -> f32 { let w = wrap360(x); return select(w, w - 360.0, w > 180.0); }

fn band_weight(h: f32, k: u32) -> f32 {
    var centers = array<f32, 8>(25.0, 55.0, 105.0, 140.0, 195.0, 255.0, 295.0, 335.0);
    let c = centers[k];
    let d = wrap180(h - c);
    if (d >= 0.0) { return max(0.0, 1.0 - d / wrap360(centers[(k + 1u) % 8u] - c)); }
    return max(0.0, 1.0 + d / wrap360(c - centers[(k + 7u) % 8u]));
}

fn to_oklab(c: vec3f) -> vec3f {
    let l = pow(0.41222147 * c.r + 0.53633254 * c.g + 0.05144599 * c.b, 1.0 / 3.0);
    let m = pow(0.21190350 * c.r + 0.68069955 * c.g + 0.10739696 * c.b, 1.0 / 3.0);
    let s = pow(0.08830246 * c.r + 0.28171884 * c.g + 0.62997870 * c.b, 1.0 / 3.0);
    return vec3f(0.21045426 * l + 0.79361779 * m - 0.00407205 * s,
        1.97799850 * l - 2.42859221 * m + 0.45059371 * s,
        0.02590404 * l + 0.78277177 * m - 0.80867577 * s);
}

fn from_oklab(c: vec3f) -> vec3f {
    var l = c.x + 0.39633778 * c.y + 0.21580376 * c.z;
    var m = c.x - 0.10556135 * c.y - 0.06385417 * c.z;
    var s = c.x - 0.08948418 * c.y - 1.29148555 * c.z;
    l = l * l * l; m = m * m * m; s = s * s * s;
    return vec3f(4.07674166 * l - 3.30771159 * m + 0.23096993 * s,
        -1.26843800 * l + 2.60975740 * m - 0.34131940 * s,
        -0.00419609 * l - 0.70341861 * m + 1.70761470 * s);
}

fn hue_bands(rgb: vec3f) -> vec3f {
    let lab = to_oklab(max(rgb, vec3f(0.0)));
    let chroma = length(lab.yz);
    if (chroma < 1e-5) { return rgb; }
    let hue = wrap360(degrees(atan2(lab.z, lab.y)));
    var delta = vec3f(0.0);
    for (var k = 0u; k < 8u; k++) {
        let w = band_weight(hue, k);
        if (w > 0.0) { delta += w * p.bands[k].xyz; }
    }
    let gate = smoothstep(0.02, 0.08, chroma);
    let h = radians(hue + delta.x * 30.0 * gate);
    let c = chroma * max(0.0, 1.0 + delta.y);
    let lightness = lab.x + delta.z * 0.12 * gate;
    return max(from_oklab(vec3f(lightness, c * cos(h), c * sin(h))), vec3f(0.0));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let px = textureLoad(src, id.xy, 0);
    let original = px.rgb;
    var look = srgb_to_linear(lut_sample(linear_to_srgb(clamp(original, vec3f(0.0), vec3f(1.0)))));
    if (p.flags.x != 0.0) { look = hue_bands(look); }
    let warmth = p.mix.y;
    if (warmth != 0.0) { look *= vec3f(1.0 + 0.12 * warmth, 1.0, 1.0 - 0.12 * warmth); }
    let fade = p.mix.z;
    if (fade != 0.0) { look = srgb_to_linear(linear_to_srgb(max(look, vec3f(0.0))) * (1.0 - 0.2 * fade) + vec3f(0.1 * fade)); }
    textureStore(dst, id.xy, vec4f(mix(original, look, p.mix.x), px.a));
}
