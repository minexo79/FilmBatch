// 漏光（5.3.10）：程序化模式以種子決定的低頻雜訊產生從邊緣進入的光斑，線性光中相加
#include "common/color.wgsl"

struct Params {
    mode: i32,          // 0 procedural / 1 texture（texture 無外部檔時退化程序化）
    amount: f32,        // 0..1
    angle: f32,         // 弧度
    size: f32,          // 0..1
    hue: f32,           // 0..360
    irregularity: f32,  // 0..1
    count: i32,
    seed: u32,
    aspect: f32,
    short_side: f32,
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
    let tint = hsv_to_rgb(p.hue, 0.9, 1.0);
    let k = p.amount * leak;
    let rgb = c.rgb + vec3f(
        (0.85 + 0.15 * tint.r) * k * 0.6,
        (0.85 + 0.15 * tint.g) * k * 0.55,
        (0.85 + 0.15 * tint.b) * k * 0.5);
    textureStore(dst, pos, vec4f(rgb, c.a));
}
