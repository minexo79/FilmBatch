// 暗部還原 ③：全解析度套用（ShadowRecovery.Apply 第 ④ 步同式）。
// 像素中心 ((x+0.5)/s−0.5) 雙線性取回 (ΣM0·a, ΣM0·b, ΣM0)，以本像素導引重建基底
// B = (A·u + Bc)/W（邊緣感知上採樣）；增益前做亮度／色度降噪（alpha × (1−Δu²)²₊ ×
// 空間 Gaussian、鏡射邊界、鄰域差加權平均），再乘共同增益 2^ψ(B)。
// alpha=0 或非有限顏色原樣通過；純黑不降噪；alpha 不變；不夾取負值／HDR。
#include "linear/shadow_common.wgsl"

struct Params {
    exposure: f32,       // 最大抬升（EV）
    factor: f32,         // 代理格區塊邊長 s
    luma: f32,           // 亮度降噪強度 0..1（足跡太小時為 0）
    chroma: f32,         // 色度降噪強度 0..1
    luma_sigma: f32,     // 輸出像素
    chroma_sigma: f32,
    support: i32,        // 降噪半視窗（輸出像素）
    _pad0: i32,
    proxy_size: vec2i,
    _pad1: vec2i,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var coef: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;

fn coefficients_at(point: vec2f) -> vec3f {
    let corner = floor(point);
    let f = point - corner;
    let last = p.proxy_size - 1;
    let lo = clamp(vec2i(corner), vec2i(0), last);
    let hi = clamp(vec2i(corner) + 1, vec2i(0), last);
    let c00 = textureLoad(coef, lo, 0).xyz;
    let c10 = textureLoad(coef, vec2i(hi.x, lo.y), 0).xyz;
    let c01 = textureLoad(coef, vec2i(lo.x, hi.y), 0).xyz;
    let c11 = textureLoad(coef, hi, 0).xyz;
    return (c00 * (1.0 - f.x) + c10 * f.x) * (1.0 - f.y) + (c01 * (1.0 - f.x) + c11 * f.x) * f.y;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(dst));
    let pos = vec2i(id.xy);
    if (any(pos >= size)) { return; }
    let c = textureLoad(src, pos, 0);
    let lum = luminance(c.rgb);
    if (!(shadow_weight(c.a, lum) > 0.0)) {
        textureStore(dst, pos, c);
        return;
    }
    let u = shadow_guide(lum);
    let k = coefficients_at((vec2f(pos) + 0.5) / p.factor - 0.5);
    let ev = shadow_base_ev((k.x * (u + SHADOW_MOMENT_SHIFT) + k.y) / k.z - SHADOW_MOMENT_SHIFT);
    let gain = exp2(shadow_lift(p.exposure, ev));
    let dark = shadow_dark(ev);
    let lk = p.luma * dark;
    let ck = p.chroma * dark;
    var rgb = c.rgb;
    if ((lk > 0.0 || ck > 0.0) && any(c.rgb != vec3f(0.0))) {
        let center = c.rgb - vec3f(lum);
        let il = -0.5 / (p.luma_sigma * p.luma_sigma);
        let ic = -0.5 / (p.chroma_sigma * p.chroma_sigma);
        var wl = 0.0;
        var dl = 0.0;
        var wc = 0.0;
        var dc = vec3f(0.0);
        for (var oy = -p.support; oy <= p.support; oy++) {
            let yy = shadow_reflect(pos.y + oy, size.y);
            for (var ox = -p.support; ox <= p.support; ox++) {
                let n = textureLoad(src, vec2i(shadow_reflect(pos.x + ox, size.x), yy), 0);
                let ln = luminance(n.rgb);
                let wn = shadow_weight(n.a, ln);
                if (!(wn > 0.0)) { continue; }
                let du = shadow_guide(ln) - u;
                let t = 1.0 - du * du;
                if (!(t > 0.0)) { continue; }
                let wr = wn * t * t;
                let d2 = f32(ox * ox + oy * oy);
                if (lk > 0.0) {
                    let a = exp(d2 * il) * wr;
                    wl += a;
                    dl += a * (ln - lum);
                }
                if (ck > 0.0) {
                    let b = exp(d2 * ic) * wr;
                    wc += b;
                    dc += b * ((n.rgb - vec3f(ln)) - center);
                }
            }
        }
        if (lk > 0.0) { rgb += vec3f(lk * (dl / wl)); }
        if (ck > 0.0) { rgb += ck * (dc / wc); }
    }
    textureStore(dst, pos, vec4f(rgb * gain, c.a));
}
