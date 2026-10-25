// RAW 場景顯影的色彩收尾（RawSourceLoader.ApplyColour 的 GPU 版）：
// 白平衡 → 相機矩陣 → [描述檔 HueSatMap → 曝光增益 → LookTable] → [HueSat 修正] → 夾成有限非負值。
// 就地處理 w × rows 的相機 RGB 浮點資料（三個 f32 一組）。
// 本檔 helper 與 common 同名但語義不同（見各函式註解），故意不 include common/color.wgsl。
struct Table {
    div: vec4i,   // 色相、飽和、明度分格數與 SrgbValue 旗標
    info: vec4i,  // x = 在 tables 緩衝區中的起點
}

struct Params {
    wb: vec4f,        // xyz = 白平衡增益，w = 曝光增益
    m0: vec4f, m1: vec4f, m2: vec4f,        // 相機 → Rec.2020 矩陣（列）
    to0: vec4f, to1: vec4f, to2: vec4f,     // Rec.2020 D65 → ProPhoto D50
    from0: vec4f, from1: vec4f, from2: vec4f,
    info: vec4i,      // x = w，y = rows，z = 旗標 bit0 描述檔 bit1 HueSatMap bit2 LookTable bit3 HueSat
    t0: Table, t1: Table, t2: Table,
}

@group(0) @binding(0) var<storage, read_write> pix: array<f32>;
@group(0) @binding(1) var<storage, read> tables: array<f32>;
@group(0) @binding(2) var<uniform> p: Params;

fn mul3(a: vec4f, b: vec4f, c: vec4f, v: vec3f) -> vec3f {
    return vec3f(dot(a.xyz, v), dot(b.xyz, v), dot(c.xyz, v));
}

// 通用 wrap；common 沒有同簽名函式（look.wgsl 的 wrap360/180 是色相專用、公式不同），保留本地版。
fn wrap(x: f32, period: f32) -> f32 {
    var r = x - period * floor(x / period);
    if (r >= period) { r = 0.0; }
    return r;
}

// 純量 sRGB 轉換，對應 Raw/Color/Transfer.cs；不是 common/color.wgsl 的向量版
//（簽名不同；且向量 linear_to_srgb 把負值夾到 0，此處與 CPU 同樣保留 v * 12.92）。
// 未選取的 pow 分支以 max 保持有效底數，避免 D3D12/WARP 裝置遺失（common/color.wgsl 同一做法）；
// 已選取分支的結果不變，不改變精度。
fn srgb_to_linear(v: f32) -> f32 {
    let high = pow((max(v, 0.04045) + 0.055) / 1.055, 2.4);
    return select(high, v / 12.92, v <= 0.04045);
}

fn linear_to_srgb(v: f32) -> f32 {
    let high = 1.055 * pow(max(v, 0.0031308), 1.0 / 2.4) - 0.055;
    return select(high, v * 12.92, v <= 0.0031308);
}

fn limited(x: f32, k: f32) -> f32 {
    return select(x * min(k, 1.0), min(x * k, 1.0), x <= 1.0);
}

fn entry(t: Table, index: i32) -> vec3f {
    let i = t.info.x + index * 3;
    return vec3f(tables[i], tables[i + 1], tables[i + 2]);
}

fn lookup(t: Table, h: f32, s: f32, v: f32) -> vec3f {
    let hd = t.div.x;
    let sd = t.div.y;
    let vd = t.div.z;
    let hf = clamp(wrap(h, 360.0) / 360.0 * f32(hd), 0.0, f32(hd));
    let h0 = min(i32(hf), hd - 1);
    let h1 = (h0 + 1) % hd;
    let th = clamp(hf - f32(h0), 0.0, 1.0);
    let sf = clamp(s, 0.0, 1.0) * f32(sd - 1);
    let s0 = min(i32(sf), max(sd - 2, 0));
    let s1 = min(s0 + 1, sd - 1);
    let ts = clamp(sf - f32(s0), 0.0, 1.0);
    var v0 = 0;
    var v1 = 0;
    var tv = 0.0;
    if (vd > 1) {
        let vf = clamp(v, 0.0, 1.0) * f32(vd - 1);
        v0 = min(i32(vf), max(vd - 2, 0));
        v1 = min(v0 + 1, vd - 1);
        tv = clamp(vf - f32(v0), 0.0, 1.0);
    }
    let lower = plane(t, v0, h0, h1, s0, s1, ts, th);
    if (v0 == v1) { return lower; }
    let upper = plane(t, v1, h0, h1, s0, s1, ts, th);
    return lower + (upper - lower) * tv;
}

fn plane(t: Table, vi: i32, h0: i32, h1: i32, s0: i32, s1: i32, ts: f32, th: f32) -> vec3f {
    let sd = t.div.y;
    let plane_base = vi * t.div.x;
    let a0 = entry(t, (plane_base + h0) * sd + s0);
    let a1 = entry(t, (plane_base + h0) * sd + s1);
    let b0 = entry(t, (plane_base + h1) * sd + s0);
    let b1 = entry(t, (plane_base + h1) * sd + s1);
    let lo = a0 + (a1 - a0) * ts;
    let hi = b0 + (b1 - b0) * ts;
    return lo + (hi - lo) * th;
}

// HSV → RGB，對應 Raw/Color/Profile.cs 的 sector 算法（與 CPU 逐運算相同）；
// common/color.wgsl 的同名函式對應 Engine CpuPipeline 的 c/x 算法，捨入順序不同，不可共用。
fn hsv_to_rgb(h: f32, s: f32, v: f32) -> vec3f {
    let hp = wrap(h, 360.0) / 60.0;
    let sector = min(u32(max(hp, 0.0)), 5u);
    let f = hp - f32(sector);
    let pp = v * (1.0 - s);
    let q = v * (1.0 - s * f);
    let t = v * (1.0 - s * (1.0 - f));
    switch (sector) {
        case 0u: { return vec3f(v, t, pp); }
        case 1u: { return vec3f(q, v, pp); }
        case 2u: { return vec3f(pp, v, t); }
        case 3u: { return vec3f(pp, q, v); }
        case 4u: { return vec3f(t, pp, v); }
        default: { return vec3f(v, pp, q); }
    }
}

fn apply_table(t: Table, c: vec3f) -> vec3f {
    let mx = max(max(c.x, c.y), c.z);
    if (!(mx > 0.0 && mx < 3.0e38)) { return c; }
    let d = mx - min(min(c.x, c.y), c.z);
    var hue = 0.0;
    if (d > 0.0) {
        var hh: f32;
        if (mx == c.x) { hh = wrap((c.y - c.z) / d, 6.0); }
        else if (mx == c.y) { hh = (c.z - c.x) / d + 2.0; }
        else { hh = (c.x - c.y) / d + 4.0; }
        hue = hh * 60.0;
    }
    let sat = d / mx;
    let srgb = t.div.w != 0;
    var v_index = mx;
    if (srgb) { v_index = linear_to_srgb(min(mx, 1.0)); }
    let look = lookup(t, hue, sat, v_index);
    let s2 = limited(sat, look.y);
    var v2: f32;
    if (srgb && mx <= 1.0) {
        v2 = select(srgb_to_linear(min(v_index * look.z, 1.0)), mx, look.z == 1.0);
    } else if (srgb) {
        v2 = mx * min(srgb_to_linear(min(look.z, 1.0)), 1.0);
    } else {
        v2 = limited(mx, look.z);
    }
    return hsv_to_rgb(hue + look.x, s2, v2);
}

// Rec.2020 亮度，對應 Engine HueSat 與 Raw ColorSpaces.Luma2020；
// common/color.wgsl 的 luminance 是 Rec.709，不可共用。
fn luminance(c: vec3f) -> f32 {
    return c.x * 0.2627 + c.y * 0.6780 + c.z * 0.0593;
}

fn hue_sat_correction(c: vec3f) -> vec3f {
    let q = mul3(p.from0, p.from1, p.from2, apply_table(p.t2, mul3(p.to0, p.to1, p.to2, c)));
    let before = luminance(c);
    let after = luminance(q);
    let finite = all(abs(q) < vec3f(3.0e38));
    if (before <= 0.0 || after <= 0.0 || !finite) { return c; }
    let t = (after / before - 0.15) / (0.25 - 0.15);
    let tc = clamp(t, 0.0, 1.0);
    let w = tc * tc * (3.0 - 2.0 * tc);
    let k = before / after;
    if (w >= 1.0) { return q * k; }
    if (w <= 0.0) { return c; }
    return c + (q * k - c) * w;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let x = i32(id.x);
    let y = i32(id.y);
    if (x >= p.info.x || y >= p.info.y) { return; }
    let o = (y * p.info.x + x) * 3;
    let flags = p.info.z;
    var c = vec3f(pix[o], pix[o + 1], pix[o + 2]) * p.wb.xyz;
    var rgb = mul3(p.m0, p.m1, p.m2, c);
    let gain = p.wb.w;
    if ((flags & 1) != 0) {
        var pr = mul3(p.to0, p.to1, p.to2, rgb);
        if ((flags & 2) != 0) { pr = apply_table(p.t0, pr); }
        pr = pr * gain;
        if ((flags & 4) != 0) { pr = apply_table(p.t1, pr); }
        rgb = mul3(p.from0, p.from1, p.from2, pr);
    } else {
        rgb = rgb * gain;
    }
    if ((flags & 8) != 0) { rgb = hue_sat_correction(rgb); }
    let ok = (rgb > vec3f(0.0)) & (rgb < vec3f(3.0e38));
    pix[o] = select(0.0, rgb.x, ok.x);
    pix[o + 1] = select(0.0, rgb.y, ok.y);
    pix[o + 2] = select(0.0, rgb.z, ok.z);
}
