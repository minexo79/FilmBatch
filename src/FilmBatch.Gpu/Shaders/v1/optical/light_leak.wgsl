// 漏光（5.3.10）：程序化模式以種子決定的低頻雜訊產生從邊緣進入的光斑（空間包絡），
// 再以三色電漿上色並依混合模式疊入線性光。
// 三色電漿改編自 danielzeller/LightLeaksUnity（MIT License, Copyright (c) 2016 Daniel Zeller,
// https://github.com/danielzeller/LightLeaksUnity, Assets/LightLeaks/LightLeaks.shader）。
// 與 CPU CpuPipeline.LightLeak 逐式一致。
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    mode: i32,          // 0 procedural / 1 texture（texture 無外部檔時退化程序化）
    amount: f32,        // 0..1
    angle: f32,         // 弧度
    size: f32,          // 0..1
    hue: f32,           // 0..360（整體色相旋轉）
    irregularity: f32,  // 0..1
    count: i32,
    seed: u32,
    aspect: f32,
    short_side: f32,
    blend: i32,         // 0 screen / 1 add / 2 lighten
    red: f32,           // 0..1
    yellow: f32,        // 0..1
    blue: f32,          // 0..1
    _pad0: u32,
    _pad1: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let c = textureLoad(src, pos, 0);

    let u = (f32(pos.x) + 0.5) / f32(size.x);
    let v = (f32(pos.y) + 0.5) / f32(size.y);
    let cu = u - 0.5;
    let cv = v - 0.5;
    let dirX = cos(p.angle);
    let dirY = sin(p.angle);
    // 置中、邊緣參考的投影（與 CPU LightLeak 同式）：sMax 為中心到該方向邊緣的距離，
    // perp 平移 sMax 使全圓非負（含雜湊索引與值雜訊定義）。
    let sMax = 0.5 * (abs(dirX) + abs(dirY));
    let range = sMax * 2.0;

    var leak = 0.0;
    for (var ci = 0; ci < p.count; ci++) {
        let s = p.seed + u32(ci) * 911u;
        let off = (pcg_unit(s) - 0.5) * p.irregularity * 0.4;
        let along = cu * dirX + cv * dirY + off;
        let perp = cu * -dirY + cv * dirX + sMax;
        let d = (sMax - along) / range;
        let e = exp(-d * d / (p.size * p.size * 0.35));
        let noise = value_noise1(perp * 3.0, s + 3u) * p.irregularity;
        let m = e * (0.7 + 0.3 * noise);
        leak += clamp(m * 1.2, 0.0, 1.0);
    }
    leak = min(1.0, leak);
    if (leak <= 0.0) {
        textureStore(dst, pos, c);
        return;
    }

    // 三色電漿（LightLeaksUnity）：種子決定的「時間」t 固定圖樣；各場 -2..2 映到 0..1 再 smoothstep（同 CPU）
    let t = 8.0 + 56.0 * pcg_unit(p.seed + 4099u);
    let yp = sin(u * cos(t) * 6.0 * cos(v * t / 232.0)) + sin(v * sin(t) * 3.0 * cos(u * t / 212.0));
    let rp = cos(u * sin(t) * 5.0 * cos(v * t / 211.0)) + sin(v * sin(t) * 6.0 * cos(u * t / 234.0));
    let bp = cos(u * cos(t) * 4.0 * cos(v * t / 311.0)) + sin(v * sin(t) * 7.0 * cos(u * t / 321.0));
    let yw = smoothstep_f(0.1, 0.9, yp * 0.25 + 0.5) * p.yellow;
    let rw = smoothstep_f(0.1, 0.9, rp * 0.25 + 0.5) * p.red;
    let bw = smoothstep_f(0.1, 0.9, bp * 0.25 + 0.5) * p.blue;
    // 原 shader 的黃 (1, .9, .63) 轉為線性光 ≈ (1, 0.787, 0.355)
    var col = vec3f(yw + rw, 0.787 * yw, 0.355 * yw + bw);
    if (p.hue != 0.0) {
        let hsv = rgb_to_hsv(col);
        col = hsv_to_rgb(hsv.x + p.hue, hsv.y, hsv.z);
    }
    let l = col * leak;
    let k = p.amount;
    var rgb: vec3f;
    if (p.blend == 1) {
        rgb = c.rgb + k * l;
    } else if (p.blend == 2) {
        rgb = c.rgb + k * max(l - c.rgb, vec3f(0.0));
    } else {
        rgb = c.rgb + k * l * (1.0 - min(c.rgb, vec3f(1.0)));
    }
    textureStore(dst, pos, vec4f(rgb, c.a));
}
