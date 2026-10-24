// 顆粒（5.3.16）：三種可選模型（clumps／correlated／optical），全部以「已現像來源像素」為
// 座標與尺度的參考。輸出像素經 map_x/map_y 逆仿射映回來源參考平面，裁切、旋轉、翻轉、
// 縮小預覽與分帶讀回都不改變底層紋理；輸出像素的參考足跡大於顆粒結構時做足跡濾波淡出，
// 而不是把顆粒座標直接縮小。暗部／中間調／高光響應為獨立的顯示亮度帶權重（tone.x/y/z）。
// 柔焦仍沿用「與高斯模糊版混合」的既有行為。
// 演算法須與 CpuPipeline.Grain 逐步相同；模型常數由 CPU 端 DeriveGrain 計算後以 uniform 傳入。
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    a: vec4f,       // amount, color, softness-mix, corr_gain
    b: vec4f,       // opt_sigma, opt_cell, radius_spread, corr_norm
    map_x: vec4f,   // A00, A01, B0, -
    map_y: vec4f,   // A10, A11, B1, r0
    seed: u32,
    model: u32,     // 0 clumps, 1 correlated, 2 optical
    samples: u32,   // 4..32
    rough_q: u32,   // round(roughness * 256)
    tone: vec4f,    // shadows, midtones, highlights, footprint(pixel_scale)
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var blurred: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;
@group(0) @binding(4) var<uniform> view: FrameView;

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

// 柔邊團塊（既有預設）：每格兩顆隨機半徑、隨機正負的柔邊圓點；足跡超過圓點半徑時淡出。
fn grain_clumps(x: f32, y: f32, r0: f32, shadow: f32, rough: f32, rough_q: i32,
                footprint: f32, seed: u32) -> Grain {
    let fade = 1.0 - smoothstep_f(1.0, 2.6, footprint / max(r0, 0.001));
    if (fade <= 0.0) { return Grain(0.0, vec3f(0.0)); }
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
    return Grain(clamp(luma * fade, -1.0, 1.0),
        clamp((chroma - vec3f(mean)) * fade, vec3f(-1.0), vec3f(1.0)));
}

// 等向的連續多尺度相關場：固定 λ 的齊次 Poisson 脈衝，經緊支撐徑向 Wendland 核重建。
// 不用軸向插值或格狀密度調變；有界計數的尾端近似見 poisson_cell_count。
// 搜尋 ±2 格：格寬 R/2、核半徑 R。norm 是單倍頻解析方差正規化；
// gain 只按未濾波的 Σamp² 正規化，足跡淡出造成的能量衰減不被抵消。
fn grain_correlated(x: f32, y: f32, r0: f32, rough_q: i32, footprint: f32,
                    norm: f32, gain: f32, seed: u32) -> Grain {
    let persistence = 0.28 + 0.52 * f32(rough_q) * (1.0 / 256.0);
    var luma = 0.0;
    var chroma = vec3f(0.0);
    var amp = 1.0;
    var rad = r0 * 8.8;
    for (var octave = 0u; octave < 5u; octave++) {
        let cell = rad * 0.5;
        let inv_r = 1.0 / rad;
        let fade = 1.0 - smoothstep_f(0.7, 1.8, footprint / (rad * 0.25));
        if (fade > 0.0) {
            let os = seed + octave * 911u;
            let ccx = i32(floor(x / cell));
            let ccy = i32(floor(y / cell));
            var sum = 0.0;
            var acc = vec3f(0.0);
            for (var dy = -2; dy <= 2; dy++) {
                for (var dx = -2; dx <= 2; dx++) {
                    let cx = ccx + dx;
                    let cy = ccy + dy;
                    let density = 700;
                    let base = os + bitcast<u32>(cx) * 4001u + bitcast<u32>(cy) * 7919u;
                    let count = poisson_cell_count(pcg(base), density);
                    for (var j = 0; j < count; j++) {
                        var h = pcg(base + u32(j) * 2654435769u + 0x9E3779B9u);
                        let px = (f32(cx) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                        h = pcg(h); let py = (f32(cy) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                        let ddx = x - px;
                        let ddy = y - py;
                        let t = sqrt(ddx * ddx + ddy * ddy) * inv_r;
                        if (t >= 1.0) { continue; }
                        let u = 1.0 - t;
                        let w = u * u * u * u * (1.0 + 4.0 * t);
                        h = pcg(h); sum += w * (f32(h >> 8u) * (1.0 / 16777216.0) * 2.0 - 1.0);
                        h = pcg(h); let cr = f32(h >> 8u) * (1.0 / 16777216.0);
                        h = pcg(h); let cg = f32(h >> 8u) * (1.0 / 16777216.0);
                        h = pcg(h); let cb = f32(h >> 8u) * (1.0 / 16777216.0);
                        acc += w * (vec3f(cr, cg, cb) * 2.0 - 1.0);
                    }
                }
            }
            let a = amp * fade;
            luma += a * sum * norm;
            chroma += a * acc * norm;
        }
        amp *= persistence;
        rad *= 0.5;
    }
    let mean = (chroma.x + chroma.y + chroma.z) / 3.0;
    return Grain(clamp(luma * gain, -1.5, 1.5),
        clamp((chroma - vec3f(mean)) * gain, vec3f(-1.5), vec3f(1.5)));
}

// 光學模型的每格圓盤數：真正的 Poisson(λ) 計數（λ = density/640），反函數查表累積機率質量；
// N 截斷於 8（λ ≤ 1.6 時 P(N≥9) < 0.0046%）；CPU 與此函式使用同一個累積順序。
fn poisson_cell_count(h: u32, density: i32) -> i32 {
    let lambda = f32(density) * (1.0 / 640.0);
    let u = f32(h >> 8u) * (1.0 / 16777216.0);
    var prob = exp(-lambda);
    var cum = prob;
    if (u < cum) { return 0; }
    for (var n = 1; n <= 8; n++) {
        prob = prob * lambda / f32(n);
        cum += prob;
        if (u < cum) { return n; }
    }
    return 8;
}

// 單一光圈取樣點的布林覆蓋：每格圓盤數為有界 Poisson(λ)，圓盤位置／粒徑／色度獨立雜湊；
// 覆蓋率為聯集空隙率的補數 1 − ∏(1 − w_i)，w_i 為柔邊圓盤權重（邊緣平滑、無硬門檻）；
// 色度按逐層吸收量 w_i·∏_j<i(1 − w_j) 加權（固定列舉序，前層先吸收）。
// 候選搜尋 ±2 格有界：格寬 2.4·r0，最大觸及 2.88·r0 + 0.72·r0 = 3.6·r0 < 2 格寬。
fn optical_coverage(qx: f32, qy: f32, r0: f32, shadow: f32, rough_q: i32,
                    spread: f32, cell: f32, seed: u32) -> Grain {
    let ccx = i32(floor(qx / cell));
    let ccy = i32(floor(qy / cell));
    var transmit = 1.0;
    var chroma = vec3f(0.0);
    for (var dy = -2; dy <= 2; dy++) {
        for (var dx = -2; dx <= 2; dx++) {
            let cx = ccx + dx;
            let cy = ccy + dy;
            // 每格圓盤密度的低頻調變（0..1024）：基準 700，粗糙度越高覆蓋越不均
            let density = clamp(700 + clump_density(cx, cy, rough_q, seed + 1409u) - 614, 0, 1024);
            let base = seed + bitcast<u32>(cx) * 4001u + bitcast<u32>(cy) * 7919u + 1409u;
            let count = poisson_cell_count(pcg(base), density);
            for (var j = 0; j < count; j++) {
                var h = pcg(base + u32(j) * 2654435769u + 0x9E3779B9u);
                let px = (f32(cx) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                h = pcg(h); let py = (f32(cy) + f32(h >> 8u) * (1.0 / 16777216.0)) * cell;
                h = pcg(h);
                let rr = r0 * (1.0 + 0.6 * shadow) * (1.0 + spread * (f32(h >> 8u) * (1.0 / 16777216.0) * 2.0 - 1.0) * 0.8);
                let ddx = qx - px;
                let ddy = qy - py;
                let edge = max(rr * 0.25, 0.35);
                let w = 1.0 - smoothstep_f(rr - edge, rr + edge, sqrt(ddx * ddx + ddy * ddy));
                if (w <= 0.0) { continue; }
                let absorbed = w * transmit;
                transmit *= 1.0 - w;
                h = pcg(h); let cr = f32(h >> 8u) * (1.0 / 16777216.0);
                h = pcg(h); let cg = f32(h >> 8u) * (1.0 / 16777216.0);
                h = pcg(h); let cb = f32(h >> 8u) * (1.0 / 16777216.0);
                chroma += absorbed * (vec3f(cr, cg, cb) * 2.0 - 1.0);
            }
        }
    }
    let cover = 1.0 - transmit;
    if (cover <= 0.0) { return Grain(0.0, vec3f(0.0)); }
    let inv = 1.0 / cover;
    return Grain(cover, clamp(chroma * inv, vec3f(-1.0), vec3f(1.0)));
}

// 2D Poisson／布林圓盤的光學統計近似（獨立實作；不是顯影化學，也未採用 IPOL 參考碼）：
// 有界高斯光圈 Monte Carlo（等面積極座標分層＋每像素種子抖動）估算覆蓋率；
// 逐像素以當前 shadow 的布林期望空隙率置中。光圈 σ 已含像素足跡，縮小預覽自然低通。
fn grain_optical(x: f32, y: f32, r0: f32, shadow: f32, rough_q: i32, spread: f32,
                 samples: u32, cell: f32, sigma: f32, seed: u32) -> Grain {
    let pix = pcg(seed + bitcast<u32>(i32(floor(x))) * 668265263u
        + bitcast<u32>(i32(floor(y))) * 2246822519u);
    // 布林模型期望覆蓋率：E[cover] = 1 − e^(−λ̄·E[A])，λ̄ = 平均密度 700/640 每格；
    // E[A] = π·(E[rr²] + E[edge²])，edge = max(rr/4, 0.35) 分段近似。
    let g = 1.0 + 0.6 * shadow;
    let mean_r2 = r0 * r0 * g * g * (1.0 + 0.64 * spread * spread / 3.0);
    let mean_a = 3.14159265358979 * (mean_r2 + max(mean_r2 * 0.0625, 0.1225));
    let p_exp = 1.0 - exp(-(700.0 / 640.0) * mean_a / (cell * cell));
    var luma = 0.0;
    var chroma = vec3f(0.0);
    for (var s_index = 0u; s_index < samples; s_index++) {
        var h = pcg(pix + s_index * 747796405u);
        let u1 = f32(h >> 8u) * (1.0 / 16777216.0);
        h = pcg(h);
        let u2 = f32(h >> 8u) * (1.0 / 16777216.0);
        // 等面積高斯光圈取樣：角度按取樣序分層＋抖動，半徑 r = σ·√(−2·ln(1−u))
        let ang = (f32(s_index) + u2) / f32(samples) * (2.0 * 3.14159265358979);
        let radius = sigma * sqrt(-2.0 * log(1.0 - u1 * 0.9999));
        let qx = x + radius * cos(ang);
        let qy = y + radius * sin(ang);
        let cov = optical_coverage(qx, qy, r0, shadow, rough_q, spread, cell, seed);
        let dev = cov.luma - p_exp;
        luma += dev;
        chroma += cov.chroma * dev;
    }
    let inv = 1.0 / f32(samples);
    return Grain(luma * inv, chroma * inv);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let orig = textureLoad(src, pos, 0);
    var c = orig.rgb + (textureLoad(blurred, pos, 0).rgb - orig.rgb) * p.a.z;

    let lum = luminance(c);
    let s = smoothstep_f(0.05, 0.5, lum);
    let t2 = smoothstep_f(0.4, 0.9, lum);
    let band = p.tone.x * (1.0 - s) + p.tone.y * (s * (1.0 - t2)) + p.tone.z * t2;
    let k = p.a.x * band;
    if (k > 0.0) {
        let shadow = 1.0 - s;
        // 顆粒座標：畫框像素中心經裁切仿射映到來源參考平面（窗口內的顆粒與整張相同）
        let fp = frame_pixel(view, id.xy);
        let gx = p.map_x.x * (f32(fp.x) + 0.5) + p.map_x.y * (f32(fp.y) + 0.5) + p.map_x.z;
        let gy = p.map_y.x * (f32(fp.x) + 0.5) + p.map_y.y * (f32(fp.y) + 0.5) + p.map_y.z;
        let rough_q = i32(p.rough_q);
        let rough = f32(p.rough_q) * (1.0 / 256.0);
        var g = Grain(0.0, vec3f(0.0));
        if (p.model == 2u) {
            g = grain_optical(gx, gy, p.map_y.w, shadow, rough_q, p.b.z,
                p.samples, p.b.y, p.b.x, p.seed);
            g.luma *= 0.35;
            g.chroma *= 0.35;
        } else if (p.model == 1u) {
            g = grain_correlated(gx, gy, p.map_y.w, rough_q, p.tone.w, p.b.w, p.a.w, p.seed);
            g.luma *= 0.16;
            g.chroma *= 0.16;
        } else {
            g = grain_clumps(gx, gy, p.map_y.w, shadow, rough, rough_q, p.tone.w, p.seed);
            g.luma *= 0.16;
            g.chroma *= 0.16;
        }
        // 深暗部的負向顆粒會被 0 截掉，只剩彩色分量 → 黑底冒出彩色斑點；彩色顆粒在暗部淡出
        let ck = p.a.y * 0.6 * smoothstep_f(0.03, 0.3, lum);
        c = clamp(c + (vec3f(g.luma) + g.chroma * ck) * k, vec3f(0.0), vec3f(1.0));
    }
    textureStore(dst, pos, vec4f(c, orig.a));
}
