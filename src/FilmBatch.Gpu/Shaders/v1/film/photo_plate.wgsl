// 古典版材（5.3.12）：單色轉換 + 版材染色 + 邊緣剝落 / 污漬 / 塗佈不均（程序化，以種子驅動）
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    plate_type: i32,    // 0 tintype / 1 wetplate / 2 cyanotype / 3 albumen
    strength: f32,      // 0..1
    ortho: f32,         // 0..1
    tint_hue: f32,      // 0..360
    tint_amount: f32,   // 0..1
    edge_erosion: f32,  // 0..1
    stains: f32,        // 0..1
    uneven: f32,        // 0..1
    seed: u32,
    aspect: f32,
    _pad0: u32,
    _pad1: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<uniform> view: FrameView;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let c = textureLoad(src, pos, 0);

    var tint = vec3f(0.7, 0.5, 0.3);
    switch p.plate_type {
        case 0: { tint = vec3f(0.75, 0.55, 0.35); }
        case 1: { tint = vec3f(0.5, 0.6, 0.75); }
        case 2: { tint = vec3f(0.15, 0.35, 0.6); }
        default: { tint = vec3f(0.7, 0.5, 0.3); }
    }
    if (p.tint_amount > 0.0) {
        let ct = hsv_to_rgb(p.tint_hue, 0.8, 0.8);
        tint = mix(tint, ct, p.tint_amount);
    }

    var lum = luminance(c.rgb);
    let lumOrtho = p.ortho * (0.08 * c.r + 0.25 * c.g + 0.67 * c.b);
    lum = mix(lum, lumOrtho, p.ortho);
    lum = lum * 0.85 + 0.075;
    var r = lum * tint.r * 1.6;
    var g = lum * tint.g * 1.6;
    var b = lum * tint.b * 1.6;

    let fp = frame_pixel(view, id.xy);
    let u = (f32(fp.x) + 0.5) / view.frame_size.x;
    let v = (f32(fp.y) + 0.5) / view.frame_size.y;
    // 邊緣剝落
    if (p.edge_erosion > 0.0) {
        let edge = min(u, min(v, min(1.0 - u, 1.0 - v)));
        let n1 = value_noise2(u * 8.0, v * 8.0, p.seed);
        let n2 = value_noise2(u * 24.0, v * 24.0, p.seed + 7u);
        let mask = edge - (n1 * 0.5 + n2 * 0.25) * p.edge_erosion * 0.4;
        if (mask < 0.0) {
            let k = max(0.0, 1.0 + mask * 8.0);
            r *= k; g *= k; b *= k;
        }
    }
    // 污漬
    if (p.stains > 0.0) {
        let s1 = value_noise2(u * 5.0, v * 5.0, p.seed + 13u);
        let s2 = value_noise2(u * 11.0, v * 13.0, p.seed + 29u);
        let m = s1 * s2;
        let thr = 0.5 - p.stains * 0.2;
        if (m > thr) {
            let k = min(1.0, (m - thr) * 8.0) * p.stains;
            r *= 1.0 - k * 0.5; g *= 1.0 - k * 0.45; b *= 1.0 - k * 0.4;
        }
    }
    // 塗佈不均
    if (p.uneven > 0.0) {
        let un = value_noise2(u * 3.0, v * 3.0, p.seed + 41u);
        let k = 1.0 + (un - 0.5) * p.uneven * 0.35;
        r *= k; g *= k; b *= k;
    }

    let rgb = mix(c.rgb, vec3f(r, g, b), p.strength);
    textureStore(dst, pos, vec4f(rgb, c.a));
}
