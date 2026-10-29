// 暗部還原共用數學（linear/shadow_*.wgsl）：常數與函式逐一對應 ShadowRecovery.cs（f32）。
// 導引 u = log2((max(Y,0)+c)/0.18)，c = 2^-7；覆蓋率權重 = alpha（alpha=0 或非有限亮度為 0）。
#include "common/color.wgsl"

const SHADOW_MID_GREY: f32 = 0.18;
const SHADOW_GUIDE_OFFSET: f32 = 0.0078125;  // c = 2^-7
const SHADOW_MOMENT_SHIFT: f32 = 4.0;        // 只平移統計導引，抑制暗部二階矩抵消；重建後減回
const SHADOW_EPSILON: f32 = 0.04;            // guided filter 正則化（導引 EV²）
const SHADOW_KNEE: f32 = -0.5;               // 抬升膝點（相對中性灰 EV）
const SHADOW_KNEE_WIDTH: f32 = 1.0;          // C1 膝寬（EV）
const SHADOW_CONTRAST_FLOOR: f32 = 0.3;      // 基底階調最低斜率
const SHADOW_NOISE_FULL: f32 = -2.5;         // 降噪全強度的基底 EV 上限
const SHADOW_NOISE_NONE: f32 = 0.5;          // 降噪歸零的基底 EV
const SHADOW_MIN_RATIO: f32 = 9.094947e-13;  // 2^-40

fn shadow_finite(v: f32) -> bool {
    return (bitcast<u32>(v) & 0x7f800000u) != 0x7f800000u;
}

// 隱藏（alpha=0）或非有限顏色不進任何鄰域統計。
fn shadow_weight(alpha: f32, y: f32) -> f32 {
    if (alpha > 0.0 && shadow_finite(y)) { return min(alpha, 1.0); }
    return 0.0;
}

fn shadow_guide(y: f32) -> f32 {
    return log2((max(y, 0.0) + SHADOW_GUIDE_OFFSET) / SHADOW_MID_GREY);
}

// 鏡射（邊緣重複）索引：任何偏移量與長度 1 的軸皆有效。
fn shadow_reflect(index: i32, count: i32) -> i32 {
    let period = 2 * count;
    var m = index % period;
    if (m < 0) { m += period; }
    return select(period - 1 - m, m, m < count);
}

// (M0, M1, M2) → 覆蓋率加權 guided filter 係數 (M0·a, M0·b, M0)。
fn shadow_coefficients(m: vec3f) -> vec3f {
    if (!(m.x > 0.0)) { return vec3f(0.0); }
    let mean = m.y / m.x;
    let variance = max(m.z / m.x - mean * mean, 0.0);
    let a = variance / (variance + SHADOW_EPSILON);
    return vec3f(m.x * a, m.x * (mean - a * mean), m.x);
}

// 導引域基底 → 相對中性灰 EV（非正基底取下限）。
fn shadow_base_ev(base: f32) -> f32 {
    return log2(max(exp2(base) - SHADOW_GUIDE_OFFSET / SHADOW_MID_GREY, SHADOW_MIN_RATIO));
}

// 抬升量 ψ（EV）：ψ = E·v/(1+v⁴)^¼，v = (1−floor)·q/E，q 為膝點下方距離的 C1 膝。
fn shadow_lift(exposure: f32, ev: f32) -> f32 {
    let x = SHADOW_KNEE - ev;
    if (!(exposure > 0.0) || !(x > 0.0)) { return 0.0; }
    let q = select(x - SHADOW_KNEE_WIDTH * 0.5, x * x / (2.0 * SHADOW_KNEE_WIDTH), x < SHADOW_KNEE_WIDTH);
    let v = (1.0 - SHADOW_CONTRAST_FLOOR) * q / exposure;
    let v2 = v * v;
    return exposure * v / sqrt(sqrt(1.0 + v2 * v2));
}

fn shadow_dark(ev: f32) -> f32 {
    return 1.0 - smoothstep_f(SHADOW_NOISE_FULL, SHADOW_NOISE_NONE, ev);
}
