const GREY: f32 = 0.18;
const LUT_MIN_EV: f32 = -14.0;
const LUT_MAX_EV: f32 = 10.0;
const LUT_COUNT: u32 = 4096u;
const CHROMA_COUNT: u32 = 8u;
// RawDisplay.OutLuma（管線沿用的圓整 BT.709 權重）
const OUT_LUMA: vec3f = vec3f(0.2126, 0.7152, 0.0722);
// RawDisplay 逐像素亮度（ColorSpaces.Luma2020）
const LUMA2020: vec3f = vec3f(0.2627, 0.6780, 0.0593);

fn is_finite(v: f32) -> bool {
    return (bitcast<u32>(v) & 0x7f800000u) != 0x7f800000u;
}

// rounding_mask 由 CPU 固定傳 0，但編譯時未知；整數運算保留 f32 捨入點，
// 防止編譯器融合乘加或重新結合（Metal fast-math、D3D12 mad 等）。不可改成編譯期常數。
fn rounded(v: f32) -> f32 {
    return bitcast<f32>(bitcast<u32>(v) ^ p.band.rounding_mask);
}

fn rounded3(v: vec3f) -> vec3f {
    return bitcast<vec3f>(bitcast<vec3u>(v) ^ vec3u(p.band.rounding_mask));
}

// WGSL 規格允許 fma 展開成「乘法捨入後再加法」（D3D12 的 mad 也可能未融合），
// 不能用來取得精確餘差；改以 Veltkamp 分割與 Dekker 乘積，只依賴正確捨入的加、減、乘。
fn split_f32(a: f32) -> vec2f {
    let scaled = rounded(4097.0 * a);
    let high = rounded(scaled - rounded(scaled - a));
    return vec2f(high, rounded(a - high));
}

// a × b = product + 回傳值（product 為 a × b 的 f32 捨入值；無溢位／下溢時精確）。
fn product_error(a: f32, b: f32, product: f32) -> f32 {
    let x = split_f32(a);
    let y = split_f32(b);
    let residual = rounded(rounded(x.x * y.x) - product);
    return rounded(rounded(rounded(residual + rounded(x.x * y.y)) + rounded(x.y * y.x)) + rounded(x.y * y.y));
}

fn dot3_ordered(a: vec3f, b: vec3f) -> f32 {
    let product = rounded3(a * b);
    return rounded(rounded(product.x + product.y) + product.z);
}

// f32 除法依規格可有 2.5 ULP 誤差；以精確餘差修正成 CPU 的正確捨入商。
fn divide_rounded(numerator: f32, denominator: f32) -> f32 {
    let quotient = rounded(numerator / denominator);
    let product = rounded(quotient * denominator);
    let remainder = rounded(rounded(numerator - product) - product_error(quotient, denominator, product));
    return rounded(quotient + rounded(remainder / denominator));
}

// 2/ln(2) 拆成 f32 high 與餘差 low。
const LOG2_SCALE_HIGH: f32 = 2.885390043258667;
const LOG2_SCALE_LOW: f32 = 3.851925978e-8;

// mantissa 收斂到 [sqrt(0.5), sqrt(2)]，以 2*atanh(t) 級數取得 LUT 用的 log2。
// 原生 log2 的容許誤差在高光色域壓縮後會放大，因此不直接用於此座標。
fn log2_lut(x: f32) -> f32 {
    var v = x;
    var offset = 0.0;
    if (v < bitcast<f32>(0x00800000u)) {
        v = v * 8388608.0;
        offset = -23.0;
    }
    let bits = bitcast<u32>(v);
    var exponent = f32(i32((bits >> 23u) & 255u) - 127) + offset;
    var m = bitcast<f32>((bits & 0x007fffffu) | 0x3f800000u);
    if (m > 1.4142135623730951) {
        m = m * 0.5;
        exponent = exponent + 1.0;
    }
    // t + t_low = (m - 1) / (m + 1)：保留分母捨入與除法餘差。
    let numerator = rounded(m - 1.0);
    let denominator = rounded(m + 1.0);
    let denominator_low = rounded(m - rounded(denominator - 1.0));
    let t = rounded(numerator / denominator);
    let product = rounded(t * denominator);
    let remainder = rounded(rounded(numerator - product) - product_error(t, denominator, product));
    let t_low = rounded(rounded(remainder - rounded(t * denominator_low)) / denominator);
    // atanh(t) = t + t × correction；t_low 以導數 1 / (1 - t²) 補入。
    let tt = rounded(t * t);
    var correction = rounded(tt / 11.0);
    correction = rounded(tt * rounded(1.0 / 9.0 + correction));
    correction = rounded(tt * rounded(1.0 / 7.0 + correction));
    correction = rounded(tt * rounded(1.0 / 5.0 + correction));
    correction = rounded(tt * rounded(1.0 / 3.0 + correction));
    let tail = rounded(t * correction);
    let series = rounded(t + tail);
    let series_low = rounded(rounded(rounded(t - series) + tail) + rounded(t_low / rounded(1.0 - tt)));
    // log2(x) = exponent + (series + series_low) × 2/ln(2)；high/low 保留每次乘、加的捨入餘差，
    // 避免 EV 落到相鄰 f32 而在高光色域壓縮後放大成多個色碼。
    let scaled = rounded(series * LOG2_SCALE_HIGH);
    let high = rounded(exponent + scaled);
    let low = rounded(rounded(rounded(rounded(scaled - rounded(high - exponent))
        + product_error(series, LOG2_SCALE_HIGH, scaled))
        + rounded(series * LOG2_SCALE_LOW)) + rounded(series_low * LOG2_SCALE_HIGH));
    return rounded(high + low);
}

fn lut_position(ev: f32) -> f32 {
    let shifted = rounded(ev - LUT_MIN_EV);
    let unit = divide_rounded(shifted, LUT_MAX_EV - LUT_MIN_EV);
    return rounded(clamp(unit, 0.0, 1.0) * f32(LUT_COUNT - 1u));
}

// 外部 DCP tone 儲存原始 (x,y) 節點；scene0.w 為節點數，0 表示既有 EV LUT。
fn profile_slope(i: u32) -> f32 {
    return (lut[2u * i + 3u] - lut[2u * i + 1u]) / (lut[2u * i + 2u] - lut[2u * i]);
}

fn profile_tangent(a: f32, b: f32) -> f32 {
    if (a * b <= 0.0) { return 0.0; }
    return 2.0 / (1.0 / a + 1.0 / b);
}

fn profile_tone(y: f32) -> f32 {
    if (!is_finite(y) || y <= 0.0) { return 0.0; }
    let count = u32(p.scene0.w);
    let x = clamp(y, 0.0, 1.0);
    var lo = 0u;
    var hi = count - 1u;
    while (lo + 1u < hi) {
        let mid = (lo + hi) / 2u;
        if (lut[2u * mid] <= x) { lo = mid; } else { hi = mid; }
    }
    let i = min(lo, count - 2u);
    let x0 = lut[2u * i];
    let x1 = lut[2u * i + 2u];
    let h = x1 - x0;
    let t = (x - x0) / h;
    let t2 = t * t;
    let t3 = t2 * t;
    let slope = profile_slope(i);
    var left = slope;
    if (i > 0u) { left = profile_tangent(profile_slope(i - 1u), slope); }
    var right = slope;
    if (i + 1u < count - 1u) { right = profile_tangent(slope, profile_slope(i + 1u)); }
    let result = (2.0 * t3 - 3.0 * t2 + 1.0) * lut[2u * i + 1u]
        + (t3 - 2.0 * t2 + t) * h * left
        + (-2.0 * t3 + 3.0 * t2) * lut[2u * i + 3u]
        + (t3 - t2) * h * right;
    return clamp(result, 0.0, 1.0);
}

fn tone_apply(y: f32) -> f32 {
    if (p.scene0.w != 0.0) { return profile_tone(y); }
    // ToneMap.Apply：非有限或 <= 0 回 0（CPU 端永不傳播 NaN）
    if (!is_finite(y) || y <= 0.0) { return 0.0; }
    if (y >= GREY * 1024.0) { return lut[LUT_COUNT - 1u]; }
    let ev = log2_lut(divide_rounded(y, GREY));
    let f = lut_position(ev);
    let i = min(u32(f), LUT_COUNT - 2u);
    let t = f - f32(i);
    let v = rounded(lut[i] + rounded((lut[i + 1u] - lut[i]) * t));
    // 低於 LUT 下限時按指數外插（CPU：v * (y / (GREY * 2^min))）
    if (ev < LUT_MIN_EV) { return rounded(v * divide_rounded(y, GREY * exp2(LUT_MIN_EV))); }
    return v;
}

fn chroma_scale(o: f32) -> f32 {
    // ToneMap.ChromaScale：非有限回 1； clamp(o,0,1) 後線性內插
    if (!is_finite(o)) { return 1.0; }
    let f = rounded(clamp(o, 0.0, 1.0) * f32(CHROMA_COUNT - 1u));
    let i = min(u32(f), CHROMA_COUNT - 2u);
    let t = f - f32(i);
    let a = chroma[i];
    let b = chroma[i + 1u];
    if (a == b) { return a; }
    return rounded(a + rounded((b - a) * t));
}

// sRGB 冪次：避免後端 pow 的寬鬆精度影響 16-bit 輸出；呼叫端已夾取到 (0.0031308, 1]。
// x = m·2^e（m∈[1,2)），x^y = m^y·2^(e·y)。兩段以 Chebyshev 節點的
// 7 次／6 次多項式求值，再以精確的 2^zi 還原比例；y 與 CPU 的 f32(1/2.4) 一致。
// Horner 內部可融合乘加；不像色域壓縮前的運算，不需逐步 rounding barrier。
const SRGB_POW_Y: f32 = 0.416666657;
fn srgb_pow_unit(m: f32) -> f32 {
    // (1+t)^y，t = m-1 ∈ [0,1)：Sterbenz 精確，Horner 求值。
    let t = m - 1.0;
    var p = 0.00147123239;
    p = p * t + -0.00789271016;
    p = p * t + 0.0205478109;
    p = p * t + -0.0378344692;
    p = p * t + 0.0633168221;
    p = p * t + -0.121431045;
    p = p * t + 0.416662186;
    return p * t + 1.0;
}
fn srgb_pow_frac(zf: f32) -> f32 {
    // 2^zf，zf ∈ [0,1)：Horner 求值。
    var q = 0.000218657849;
    q = q * zf + 0.00123913318;
    q = q * zf + 0.00968418643;
    q = q * zf + 0.0554806292;
    q = q * zf + 0.240230456;
    q = q * zf + 0.693146944;
    return q * zf + 1.0;
}
fn srgb_pow(x: f32) -> f32 {
    // 滿幅通道可略過多項式，且保持 pow(1,y) = 1。
    if (x == 1.0) { return 1.0; }
    let b = bitcast<u32>(x);
    let e = i32((b >> 23u) & 255u) - 127;
    let m = bitcast<f32>((b & 0x007fffffu) | 0x3f800000u);
    let z = f32(e) * SRGB_POW_Y;
    let zi = i32(floor(z));
    let zf = z - f32(zi);
    let scale = bitcast<f32>((u32(127 + zi) << 23u));
    return srgb_pow_unit(m) * srgb_pow_frac(zf) * scale;
}

fn linear_to_srgb1(v: f32) -> f32 {
    let x = max(v, 0.0);
    if (x <= 0.0031308) { return x * 12.92; }
    return rounded(rounded(1.055 * srgb_pow(x)) - 0.055);
}

fn clamp_f(v: f32, lo: f32, hi: f32) -> f32 {
    return min(max(v, lo), hi);
}

// CPU：(int)(e * 65535f + 0.5f)；乘、加各自捨入，不讓後端合併成 mad／fma。
fn quantize16(e: f32) -> u32 {
    return u32(floor(rounded(rounded(e * 65535.0) + 0.5)));
}


fn raw_finish(c: vec3f, luma: vec3f) -> vec3f {
    // --- tone map on luminance + highlight desaturation（finish_with 原順序）
    let yl = dot3_ordered(c, luma);
    let o = tone_apply(yl);
    var d: vec3f;
    if (yl > 1e-9) {
        let k = divide_rounded(o, yl);
        d = rounded3(c * k);
    } else {
        d = vec3f(0.0);
    }
    var ck = 1.0;
    if (p.scene0.w == 0.0) { ck = chroma_scale(o); }
    if (ck != 1.0) {
        d = rounded3(vec3f(o) + rounded3(rounded3(d - vec3f(o)) * ck));
    }
    let mx = max(d.r, max(d.g, d.b));
    if (mx > 1.0) {
        let t = clamp_f(divide_rounded(rounded(mx - 1.0), max(rounded(mx - o), 1e-6)), 0.0, 1.0);
        d = rounded3(d + rounded3(rounded3(vec3f(o) - d) * t));
    }

    // --- gamut map to sRGB（gamut_map_to，peak = 1）
    var r = vec3f(dot3_ordered(p.row0.xyz, d), dot3_ordered(p.row1.xyz, d), dot3_ordered(p.row2.xyz, d));
    if (p.band.extended_srgb != 0u) {
        // Keep negative/over-one sRGB channels until the final output-space conversion.
        return sign(r) * vec3f(linear_to_srgb1(abs(r.r)), linear_to_srgb1(abs(r.g)), linear_to_srgb1(abs(r.b)));
    }
    let yy = clamp_f(dot3_ordered(OUT_LUMA, r), 0.0, 1.0);
    var t2 = 1.0;
    if (r.r < 0.0) { t2 = min(t2, divide_rounded(yy, max(rounded(yy - r.r), 1e-9))); }
    else if (r.r > 1.0) { t2 = min(t2, divide_rounded(rounded(1.0 - yy), max(rounded(r.r - yy), 1e-9))); }
    if (r.g < 0.0) { t2 = min(t2, divide_rounded(yy, max(rounded(yy - r.g), 1e-9))); }
    else if (r.g > 1.0) { t2 = min(t2, divide_rounded(rounded(1.0 - yy), max(rounded(r.g - yy), 1e-9))); }
    if (r.b < 0.0) { t2 = min(t2, divide_rounded(yy, max(rounded(yy - r.b), 1e-9))); }
    else if (r.b > 1.0) { t2 = min(t2, divide_rounded(rounded(1.0 - yy), max(rounded(r.b - yy), 1e-9))); }
    if (t2 < 1.0) {
        r = rounded3(vec3f(yy) + rounded3(rounded3(r - vec3f(yy)) * t2));
    }
    // --- exact sRGB encode，捨入到 16-bit（finish_deep 的 q：floor(e*65535+.5)）
    let er = clamp_f(linear_to_srgb1(clamp_f(r.r, 0.0, 1.0)), 0.0, 1.0);
    let gg = clamp_f(linear_to_srgb1(clamp_f(r.g, 0.0, 1.0)), 0.0, 1.0);
    let bb = clamp_f(linear_to_srgb1(clamp_f(r.b, 0.0, 1.0)), 0.0, 1.0);
    return vec3f(er, gg, bb);
}
