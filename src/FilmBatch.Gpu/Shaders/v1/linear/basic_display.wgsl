// 基本調整（顯示部分，5.3.1 後段）：亮度 / 對比 / 亮暗部 / 飽和 / 細節提取（顯示空間）
#include "common/color.wgsl"

struct Params {
    brightness: f32,   // -1..1
    contrast: f32,     // -1..1
    highlights: f32,   // -1..1
    shadow_toe: f32,   // exp2(-2 * shadows/100) - 1, precomputed per dispatch
    saturation: f32,   // -1..1
    structure: f32,    // -1..1
    short_side: f32,
    _pad: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var structBlur: texture_2d<f32>; // structure 用的中半徑模糊版（structure=0 時可傳 src）
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    var c = textureLoad(src, id.xy, 0);
    var r = c.r; var g = c.g; var b = c.b;
    // 與 CPU 相同：細節先比較未做全域階調的像素／模糊基底，不能混用對比前後的亮度。
    if (p.structure != 0.0) {
        let bl = textureLoad(structBlur, id.xy, 0);
        let l = luminance(vec3f(r, g, b));
        let blum = luminance(bl.rgb);
        let diff = (l - blum) * p.structure * 0.6;
        let scale = select(1.0, (l + diff) / l, l > 1e-4);
        r = max(r * scale, 0.0);
        g = max(g * scale, 0.0);
        b = max(b * scale, 0.0);
    }

    // 亮度：中間調 gamma（0 恆等）
    if (p.brightness != 0.0) {
        let gm = exp2(-p.brightness);
        r = pow(clamp(r, 0.0, 1.0), gm);
        g = pow(clamp(g, 0.0, 1.0), gm);
        b = pow(clamp(b, 0.0, 1.0), gm);
    }
    // 對比：圍繞中間調
    if (p.contrast != 0.0) {
        let ck = 1.0 + p.contrast;
        r = (r - 0.5) * ck + 0.5;
        g = (g - 0.5) * ck + 0.5;
        b = (b - 0.5) * ck + 0.5;
    }
    // 與 CPU 相同的單調暗部 toe：固定黑點、共同 RGB 增益，Y=0.45 接回恆等。
    let lum = luminance(vec3f(r, g, b));
    if (p.shadow_toe != 0.0 && lum > 0.0 && lum < 0.45) {
        let toe = 1.0 - lum / 0.45;
        let gain = 1.0 / (1.0 + p.shadow_toe * toe * toe);
        r *= gain; g *= gain; b *= gain;
    }
    let hi = smoothstep_f(0.45, 0.95, lum);
    let k = p.highlights * hi * 0.35;
    r += k; g += k; b += k;
    // 飽和度
    if (p.saturation != 0.0) {
        let l = luminance(vec3f(r, g, b));
        let kk = 1.0 + p.saturation;
        r = max(l + (r - l) * kk, 0.0);
        g = max(l + (g - l) * kk, 0.0);
        b = max(l + (b - l) * kk, 0.0);
    }
    textureStore(dst, id.xy, vec4f(r, g, b, c.a));
}
