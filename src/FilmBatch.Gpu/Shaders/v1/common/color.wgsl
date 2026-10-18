// 色彩空間轉換（sRGB ↔ 線性）、亮度與確定性雜湊（與 Core/Random/SeedHash.cs 相同演算法）

fn srgb_to_linear1(c: f32) -> f32 {
    if (c <= 0.04045) { return c / 12.92; }
    return pow((c + 0.055) / 1.055, 2.4);
}

fn linear_to_srgb1(c: f32) -> f32 {
    let x = max(c, 0.0);
    if (x <= 0.0031308) { return x * 12.92; }
    return 1.055 * pow(x, 1.0 / 2.4) - 0.055;
}

fn srgb_to_linear(c: vec3f) -> vec3f {
    return vec3f(srgb_to_linear1(c.r), srgb_to_linear1(c.g), srgb_to_linear1(c.b));
}

fn linear_to_srgb(c: vec3f) -> vec3f {
    return vec3f(linear_to_srgb1(c.r), linear_to_srgb1(c.g), linear_to_srgb1(c.b));
}

// Rec.709 亮度權重（線性光）
fn luminance(c: vec3f) -> f32 {
    return dot(c, vec3f(0.2126, 0.7152, 0.0722));
}

// ── PCG 整數雜湊（與 C# SeedHash.Pcg 逐位元相同；不依賴浮點運算順序）──
fn pcg(v: u32) -> u32 {
    let state = v * 747796405u + 2891336453u;
    let word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

fn pcg_unit(v: u32) -> f32 {
    return f32(pcg(v) >> 8u) * (1.0 / 16777216.0);
}

// HSV → RGB（h 0–360、0–360 度；s、v 為 0–1）
fn hsv_to_rgb(h: f32, s: f32, v: f32) -> vec3f {
    let c = v * s;
    let hp = (((h % 360.0) + 360.0) % 360.0) / 60.0;
    let x = c * (1.0 - abs(hp % 2.0 - 1.0));
    var r = 0.0; var g = 0.0; var b = 0.0;
    if (hp < 1.0)      { r = c; g = x; }
    else if (hp < 2.0) { r = x; g = c; }
    else if (hp < 3.0) { g = c; b = x; }
    else if (hp < 4.0) { g = x; b = c; }
    else if (hp < 5.0) { r = x; b = c; }
    else               { r = c; b = x; }
    let m = v - c;
    return vec3f(r + m, g + m, b + m);
}

fn smoothstep_f(e0: f32, e1: f32, x: f32) -> f32 {
    let t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

// 2D 值雜訊（正規化座標 + 種子；與 CPU 版相同格座標 → 雜湊 → smoothstep 內插）
fn value_noise2(x: f32, y: f32, seed: u32) -> f32 {
    let ix = i32(floor(x));
    let iy = i32(floor(y));
    let fx = x - f32(ix);
    let fy = y - f32(iy);
    let ux = fx * fx * (3.0 - 2.0 * fx);
    let uy = fy * fy * (3.0 - 2.0 * fy);
    let h00 = pcg_unit(seed + u32(ix) * 379u + u32(iy) * 733u);
    let h10 = pcg_unit(seed + u32(ix + 1) * 379u + u32(iy) * 733u);
    let h01 = pcg_unit(seed + u32(ix) * 379u + u32(iy + 1) * 733u);
    let h11 = pcg_unit(seed + u32(ix + 1) * 379u + u32(iy + 1) * 733u);
    return (h00 * (1.0 - ux) + h10 * ux) * (1.0 - uy) + (h01 * (1.0 - ux) + h11 * ux) * uy;
}

fn value_noise1(x: f32, seed: u32) -> f32 {
    let i = i32(floor(x));
    let f = x - f32(i);
    let a = pcg_unit(seed + u32(i) * 197u);
    let b = pcg_unit(seed + u32(i + 1) * 197u);
    return a + (b - a) * (f * f * (3.0 - 2.0 * f));
}
