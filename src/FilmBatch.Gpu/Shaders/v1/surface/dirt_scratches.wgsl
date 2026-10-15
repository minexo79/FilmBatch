// 灰塵與刮痕（5.3.15）：程序化圖形（點、線段、貝茲毛髮），以距離遮罩提亮/壓暗
// 所有形狀由種子在 CPU 端決定參數範圍，此處以同樣的整數雜湊在 shader 內產生，
// 保證 CPU / GPU 結果一致（計畫書 5.4：GPU 亂數用整數雜湊）。
#include "common/color.wgsl"

struct Params {
    mode: i32,        // 0 procedural / 1 texture（texture 無檔時退化程序化）
    dust: f32,        // 0..1
    scratches: f32,   // 0..1
    hairs: f32,       // 0..1
    polarity: i32,    // 0 dark / 1 light / 2 mixed
    strength: f32,    // 0..1
    scale: f32,       // 0.5..2.0
    seed: u32,
    short_side: f32,
    _pad0: u32, _pad1: u32, _pad2: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

fn dist_seg(px: f32, py: f32, x0: f32, y0: f32, x1: f32, y1: f32) -> f32 {
    let dx = x1 - x0;
    let dy = y1 - y0;
    let len2 = dx * dx + dy * dy;
    var t = 0.0;
    if (len2 > 0.0) { t = clamp(((px - x0) * dx + (py - y0) * dy) / len2, 0.0, 1.0); }
    let qx = x0 + t * dx;
    let qy = y0 + t * dy;
    return sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    var c = textureLoad(src, pos, 0);
    let px = f32(pos.x) + 0.5;
    let py = f32(pos.y) + 0.5;
    let fw = f32(size.x);
    let fh = f32(size.y);
    let ss = p.short_side;

    var delta = 0.0;
    // 灰塵點（數量依 dust，seed 決定位置/大小/極性）
    let dustN = i32(round(p.dust * 40.0));
    for (var d = 0; d < dustN; d++) {
        let s = p.seed + u32(d) * 131u;
        let cx = pcg_unit(s) * fw;
        let cy = pcg_unit(s + 1u) * fh;
        let r = (2.0 + pcg_unit(s + 2u) * 12.0) * p.scale * ss / 800.0;
        var pol = -1.0;
        if (p.polarity == 1) { pol = 1.0; }
        else if (p.polarity == 2) { pol = select(-1.0, 1.0, pcg_unit(s + 3u) > 0.5); }
        let d2 = (px - cx) * (px - cx) + (py - cy) * (py - cy);
        let t = exp(-d2 / (r * r * 0.5));
        if (t > 0.01) { delta += pol * p.strength * 0.5 * t; }
    }
    // 刮痕（線段）
    let scrN = i32(round(p.scratches * 25.0));
    for (var d = 0; d < scrN; d++) {
        let s = p.seed + 977u + u32(d) * 173u;
        let x0 = pcg_unit(s) * fw;
        let y0 = pcg_unit(s + 1u) * fh;
        let x1 = x0 + (pcg_unit(s + 2u) - 0.5) * fw * 0.4;
        let y1 = y0 + (pcg_unit(s + 3u) - 0.5) * fh * 0.4;
        let wd = (0.8 + pcg_unit(s + 4u) * 2.5) * p.scale * ss / 800.0;
        var pol = -1.0;
        if (p.polarity == 1) { pol = 1.0; }
        else if (p.polarity == 2) { pol = select(-1.0, 1.0, pcg_unit(s + 5u) > 0.5); }
        let dist = dist_seg(px, py, x0, y0, x1, y1);
        let m = exp(-dist * dist / (wd * wd));
        if (m > 0.01) { delta += pol * p.strength * 0.4 * m; }
    }
    // 毛髮（二次貝茲，以線段序列近似）
    let hairN = i32(round(p.hairs * 12.0));
    for (var d = 0; d < hairN; d++) {
        let s = p.seed + 2027u + u32(d) * 211u;
        let x0 = pcg_unit(s) * fw;
        let y0 = pcg_unit(s + 1u) * fh;
        let cx2 = x0 + (pcg_unit(s + 2u) - 0.5) * fw * 0.3;
        let cy2 = y0 + (pcg_unit(s + 3u) - 0.5) * fh * 0.3;
        let x1 = x0 + (pcg_unit(s + 4u) - 0.5) * fw * 0.5;
        let y1 = y0 + (pcg_unit(s + 5u) - 0.5) * fh * 0.5;
        let wd = (0.6 + pcg_unit(s + 6u) * 1.4) * p.scale * ss / 800.0;
        var pol = -1.0;
        if (p.polarity == 1) { pol = 1.0; }
        else if (p.polarity == 2) { pol = select(-1.0, 1.0, pcg_unit(s + 7u) > 0.5); }
        let segs = 40;
        for (var sg = 0; sg < segs; sg++) {
            let t0 = f32(sg) / f32(segs);
            let t1 = f32(sg + 1) / f32(segs);
            let ax = (1.0 - t0) * (1.0 - t0) * x0 + 2.0 * (1.0 - t0) * t0 * cx2 + t0 * t0 * x1;
            let ay = (1.0 - t0) * (1.0 - t0) * y0 + 2.0 * (1.0 - t0) * t0 * cy2 + t0 * t0 * y1;
            let bx = (1.0 - t1) * (1.0 - t1) * x0 + 2.0 * (1.0 - t1) * t1 * cx2 + t1 * t1 * x1;
            let by = (1.0 - t1) * (1.0 - t1) * y0 + 2.0 * (1.0 - t1) * t1 * cy2 + t1 * t1 * y1;
            let dist = dist_seg(px, py, ax, ay, bx, by);
            let m = exp(-dist * dist / (wd * wd));
            if (m > 0.01) { delta += pol * p.strength * 0.35 * m / f32(segs) * 3.0; }
        }
    }

    if (abs(delta) > 0.0) {
        let lum = luminance(c.rgb);
        let adjusted = clamp(lum + delta, 0.0, 1.0);
        let rgb = select(vec3f(adjusted), clamp(c.rgb * (adjusted / lum), vec3f(0.0), vec3f(1.0)), lum > 1e-4);
        c = vec4f(rgb, c.a);
    }
    textureStore(dst, pos, c);
}
