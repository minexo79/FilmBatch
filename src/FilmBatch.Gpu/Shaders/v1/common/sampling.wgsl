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
