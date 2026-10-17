// RAW 顯示收尾：線性 Rec.2020 →（相機曲線或預設延伸 Reinhard）→ 亮度／色度映射 →
// 高光去飽和 → Rec.2020→sRGB → 色域映射 → 精確 sRGB 曲線 → 打包 16-bit RGBA。
// 與 Engine/Imaging/Raw/RawDisplay.cs 同順序；LUT 由 CPU 端 ToneMap 建好後以 storage buffer 傳入。
// 輸入輸出皆為 storage buffer，以 array<f32> 避免 RGB vec3 的 16-byte 對齊：
//   src:  每像素 3 個 f32（RGB 逐像素）
//   lut:  4096 個 f32（ToneMap 對數域 LUT）
//   chroma: 8 個 f32（CameraTone 色度縮放；預設全 1）
//   dst:  每像素 2 個 u32（R|G<<16, B|A<<16），只寫 band.rows 列
// matrix 以 3 個 vec4 傳（row-major 前 3 欄；第 4 欄 padding，配合 16-byte 對齊）。

struct Band { width: u32, rows: u32, rounding_mask: u32, pad1: u32 };

struct FinishParams {
    row0: vec4f, // Rec2020→sRGB 第 0 列
    row1: vec4f, // 第 1 列
    row2: vec4f, // 第 2 列
    band: Band,
};

@group(0) @binding(0) var<storage, read> src: array<f32>;
@group(0) @binding(1) var<storage, read> lut: array<f32>;
@group(0) @binding(2) var<storage, read> chroma: array<f32>;
@group(0) @binding(3) var<storage, read_write> dst: array<u32>;
@group(0) @binding(4) var<uniform> p: FinishParams;

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

fn tone_apply(y: f32) -> f32 {
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

fn linear_to_srgb1(v: f32) -> f32 {
    let x = max(v, 0.0);
    if (x <= 0.0031308) { return x * 12.92; }
    return rounded(rounded(1.055 * pow(x, 1.0 / 2.4)) - 0.055);
}

fn clamp_f(v: f32, lo: f32, hi: f32) -> f32 {
    return min(max(v, lo), hi);
}

// CPU：(int)(e * 65535f + 0.5f)；乘、加各自捨入，不讓後端合併成 mad／fma。
fn quantize16(e: f32) -> u32 {
    return u32(floor(rounded(rounded(e * 65535.0) + 0.5)));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= p.band.width || id.y >= p.band.rows) { return; }
    let px = id.y * p.band.width + id.x;
    let c = vec3f(src[px * 3u], src[px * 3u + 1u], src[px * 3u + 2u]);

    // --- tone map on luminance + highlight desaturation（finish_with 原順序）
    let yl = dot3_ordered(c, LUMA2020);
    let o = tone_apply(yl);
    var d: vec3f;
    if (yl > 1e-9) {
        let k = divide_rounded(o, yl);
        d = rounded3(c * k);
    } else {
        d = vec3f(0.0);
    }
    let ck = chroma_scale(o);
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
    let q = vec4u(quantize16(er), quantize16(gg), quantize16(bb), 65535u);
    let idx = (id.y * p.band.width + id.x) * 2u;
    dst[idx] = q.r | (q.g << 16u);
    dst[idx + 1u] = q.b | (q.a << 16u);
}
