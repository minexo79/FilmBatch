// RAW 語意遮罩局部調整（REQ-RAW-07）：與 RawSemanticAdjustments.ApplyCore/AdjustPixel 同式。
// src 為裁切後輸出畫框的線性 sRGB 工作緩衝；mask 為已烘到來源畫框的 R32Float alpha
// （RawSemanticAdjustments.Prepare：Active() 已含轉正、預設裁切、List3 warp 與 MaskSubArea）。
// 此 shader 只經 p 的 CropT.OutToSource 把輸出像素映回來源取樣遮罩；遮罩缺席時此 pass 不派送。
#include "common/sampling.wgsl"
#include "common/color.wgsl"

struct Params {
    m00: f32, m01: f32, m02: f32, src_w: f32,
    m10: f32, m11: f32, m12: f32, src_h: f32,
    strength: f32,   // 0..1
    exposure: f32,   // EV
    temperature: f32,// -1..1
    tint: f32,       // -1..1
    contrast: f32,   // -1..1
    saturation: f32, // -1..1
    invert: f32,     // 0/1
    _pad: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var mask: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;
@group(0) @binding(4) var<uniform> view: FrameView;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(dst);
    if (id.x >= size.x || id.y >= size.y) { return; }
    // 輸出像素中心 → 來源像素 → 遮罩平面連續座標（與 CPU Bilinear 同式、邊緣延伸）
    let o = frame_px(view, id.xy);
    let s = vec2f(p.m00 * o.x + p.m01 * o.y + p.m02, p.m10 * o.x + p.m11 * o.y + p.m12);
    let md = vec2f(textureDimensions(mask));
    let mp = s / vec2f(p.src_w, p.src_h) * md - 0.5;
    let m = sample_bilinear(mask, mp).r;
    let w = mix(m, 1.0 - m, p.invert) * p.strength;
    var c = textureLoad(src, id.xy, 0);
    if (w > 0.0 && c.a != 0.0) {
        var r = c.r;
        var g = c.g;
        var b = c.b;
        let e = p.exposure * w;
        if (e != 0.0) {
            let gain = exp2(e);
            r = r * gain; g = g * gain; b = b * gain;
        }
        let t = p.temperature * w;
        let ti = p.tint * w;
        if (t != 0.0 || ti != 0.0) {
            let y0 = luminance(vec3f(r, g, b));
            r = r * (1.0 + 0.3 * t);
            g = g * (1.0 - 0.22 * ti);
            b = b * max(1.0 - 0.3 * t, 0.0);
            let y1 = max(luminance(vec3f(r, g, b)), 1e-9);
            let k = y0 / y1;
            r = r * k; g = g * k; b = b * k;
        }
        let con = p.contrast * w;
        if (con != 0.0) {
            let y = luminance(vec3f(r, g, b));
            let l1 = clamp(log2(max(y, 1e-9) / 0.18), -6.0, 4.0);
            let gain = exp2(0.14 * con * l1);
            r = r * gain; g = g * gain; b = b * gain;
        }
        let sa = p.saturation * w;
        if (sa != 0.0) {
            let y = luminance(vec3f(r, g, b));
            let k = max(1.0 + sa, 0.0);
            r = y + (r - y) * k;
            g = y + (g - y) * k;
            b = y + (b - y) * k;
        }
        c = vec4f(r, g, b, c.a);
    }
    textureStore(dst, id.xy, c);
}
