// 色彩空間轉換（sRGB ↔ 線性）與亮度

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
