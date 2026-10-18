// 顆粒（5.3.16）：柔邊團塊模型（簡化布林模型）＋柔焦
// - 每格放兩顆隨機半徑的柔邊圓點，正負號隨機 → 零均值、不規則團塊；格密度由低頻雜訊調變（粗糙度越高越聚集）
// - 亮部（≥ 0.9）完全沒有顆粒；暗部團塊較大且最明顯
// - 柔焦：與高斯模糊版混合；模糊半徑隨短邊縮放，縮圖下低於一個顯示像素（看起來清晰），放大後才看得出柔焦
// 演算法須與 CpuPipeline.Grain 逐步相同。
#include "common/color.wgsl"

struct Params {
    amount: f32,    // 0..1
    size: f32,      // 0..1
    roughness: f32, // 0..1
    color: f32,     // 0..1
    softness: f32,  // 0..1
    seed: u32,
    short_side: f32,
    _pad0: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var blurred: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;

struct Grain { luma: f32, chroma: vec3f, }

// 團聚密度（0..1024）：全整數運算，CPU 與 GPU 逐位元相同，避免圓點有無在門檻邊緣翻轉
// smoothstep(t / 4) × 32，t = 0..3
fn smooth4(t: i32) -> i32 { return select(select(select(27, 16, t == 2), 5, t == 1), 0, t == 0); }
fn clump_density(cx: i32, cy: i32, rough_q: i32, seed: u32) -> i32 {
    let ix = cx >> 2u;
    let iy = cy >> 2u;
    let wx = smooth4(cx & 3);
    let wy = smooth4(cy & 3);
    let h00 = i32(pcg(seed + bitcast<u32>(ix) * 379u + bitcast<u32>(iy) * 733u) >> 24u);
    let h10 = i32(pcg(seed + bitcast<u32>(ix + 1) * 379u + bitcast<u32>(iy) * 733u) >> 24u);
    let h01 = i32(pcg(seed + bitcast<u32>(ix) * 379u + bitcast<u32>(iy + 1) * 733u) >> 24u);
    let h11 = i32(pcg(seed + bitcast<u32>(ix + 1) * 379u + bitcast<u32>(iy + 1) * 733u) >> 24u);
    let v = (h00 * (32 - wx) + h10 * wx) * (32 - wy) + (h01 * (32 - wx) + h11 * wx) * wy;
    let clump = clamp((v - 65280) * 2, 0, 261120) / 255;
    return 614 + (clump - 614) * rough_q / 256;
}

fn grain_clumps(x: f32, y: f32, r0: f32, shadow: f32, rough: f32, seed: u32) -> Grain {
    let rough_q = i32(round(rough * 256.0));
    let cell = r0 * 2.6;
    let ccx = i32(floor(x / cell));
    let ccy = i32(floor(y / cell));
    var luma = 0.0;
    var chroma = vec3f(0.0);
    for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
            let cx = ccx + dx;
            let cy = ccy + dy;
            let density = u32(clump_density(cx, cy, rough_q, seed + 71u));
            let base = seed + bitcast<u32>(cx) * 4001u + bitcast<u32>(cy) * 7919u;
            for (var j = 0u; j < 2u; j++) {
                var h = pcg(base ^ (j * 2654435769u));
                if ((h >> 22u) >= density) { continue; }
                h = pcg(h); let px = (f32(cx) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                h = pcg(h); let py = (f32(cy) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                h = pcg(h); let rr = r0 * (1.0 + 0.6 * shadow) * (1.0 + rough * (f32(h >> 8u) * (1.0 / 16777216.0) - 0.5));
                let ddx = x - px;
                let ddy = y - py;
                let soft = 1.0 - smoothstep_f(rr * 0.35, rr, sqrt(ddx * ddx + ddy * ddy));
                if (soft <= 0.0) { continue; }
                h = pcg(h); let sgn = select(-1.0, 1.0, (h & 1u) == 1u);
                luma += sgn * soft;
                h = pcg(h); let cr = f32(h >> 8u) * (1.0 / 16777216.0);
                h = pcg(h); let cg = f32(h >> 8u) * (1.0 / 16777216.0);
                h = pcg(h); let cb = f32(h >> 8u) * (1.0 / 16777216.0);
                chroma += soft * (vec3f(cr, cg, cb) * 2.0 - 1.0);
            }
        }
    }
    let mean = (chroma.x + chroma.y + chroma.z) / 3.0;
    return Grain(clamp(luma, -1.0, 1.0), clamp(chroma - vec3f(mean), vec3f(-1.0), vec3f(1.0)));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let orig = textureLoad(src, pos, 0);
    var c = orig.rgb + (textureLoad(blurred, pos, 0).rgb - orig.rgb) * p.softness;

    let lum = luminance(c);
    let k = p.amount * 0.16 * (1.0 - smoothstep_f(0.4, 0.9, lum));
    if (k > 0.0) {
        let r0 = max(0.5, (0.5 + p.size * 2.5) * p.short_side / 3000.0);
        let shadow = 1.0 - smoothstep_f(0.05, 0.5, lum);
        let g = grain_clumps(f32(pos.x) + 0.5, f32(pos.y) + 0.5, r0, shadow, p.roughness, p.seed);
        // 深暗部的負向顆粒會被 0 截掉，只剩彩色分量 → 黑底冒出彩色斑點；彩色顆粒在暗部淡出，黑底只留中性團塊
        let ck = p.color * 0.6 * smoothstep_f(0.03, 0.3, lum);
        c = clamp(c + (vec3f(g.luma) + g.chroma * ck) * k, vec3f(0.0), vec3f(1.0));
    }
    textureStore(dst, pos, vec4f(c, orig.a));
}
