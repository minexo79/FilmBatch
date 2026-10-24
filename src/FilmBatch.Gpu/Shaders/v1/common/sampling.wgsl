// 窗口渲染的畫框視圖（GpuEffectPipeline 的 ViewUniforms，16 bytes）：frame_size 為裁切後畫框尺寸，
// origin 為本次渲染區域左上角在畫框中的像素位置。整張渲染時 origin = 0、frame_size = 紋理尺寸，
// 所有畫框相對量（短邊、中心、正規化座標、雜訊與顆粒座標）都以畫框計算、與窗口無關。
struct FrameView {
    frame_size: vec2f,
    origin: vec2i,
}

// 本地像素 → 畫框整數像素（像素中心為 + 0.5）。
fn frame_pixel(v: FrameView, id: vec2u) -> vec2i {
    return vec2i(id) + v.origin;
}

// 本地像素 → 畫框像素中心。
fn frame_px(v: FrameView, id: vec2u) -> vec2f {
    return vec2f(frame_pixel(v, id)) + 0.5;
}

fn sample_bilinear(t: texture_2d<f32>, point: vec2f) -> vec4f {
    let size = vec2i(textureDimensions(t));
    let base = vec2i(floor(point));
    let f = point - floor(point);
    let a = clamp(base, vec2i(0), size - 1);
    let b = clamp(base + 1, vec2i(0), size - 1);
    return (textureLoad(t, a, 0) * (1.0 - f.x) + textureLoad(t, vec2i(b.x, a.y), 0) * f.x) * (1.0 - f.y)
        + (textureLoad(t, vec2i(a.x, b.y), 0) * (1.0 - f.x) + textureLoad(t, b, 0) * f.x) * f.y;
}

fn rgb_to_hsv(c: vec3f) -> vec3f {
    let hi = max(c.r, max(c.g, c.b));
    let lo = min(c.r, min(c.g, c.b));
    let delta = hi - lo;
    var hue = 0.0;
    if (delta > 0.000001) {
        if (hi == c.r) { hue = 60.0 * (((c.g - c.b) / delta) % 6.0); }
        else if (hi == c.g) { hue = 60.0 * ((c.b - c.r) / delta + 2.0); }
        else { hue = 60.0 * ((c.r - c.g) / delta + 4.0); }
    }
    if (hue < 0.0) { hue += 360.0; }
    return vec3f(hue, select(0.0, delta / hi, hi > 0.000001), hi);
}
