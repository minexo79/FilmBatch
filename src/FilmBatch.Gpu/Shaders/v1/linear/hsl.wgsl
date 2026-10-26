// 分色相修正（hsl 模組）：與 HslProcessing.cs 同演算法的 OKLCh 八色相帶調整。
// 獨立於底片風格，線性域中在 film/look 之後、photo_plate 之前執行；alpha 不動。
struct HslParams {
    bands: array<vec4f, 8>, // 色相、飽和、明度（-1..1）、0
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: HslParams;

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
    textureStore(dst, id.xy, vec4f(hue_bands(px.rgb), px.a));
}
