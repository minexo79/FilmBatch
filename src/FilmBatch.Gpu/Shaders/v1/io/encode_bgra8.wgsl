// 線性光 → sRGB 8-bit，打包成 BGRA（低位元組為 B），直接對應 Avalonia 的 Bgra8888 WriteableBitmap
#include "common/color.wgsl"

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> dst: array<u32>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    let s = clamp(linear_to_srgb(c.rgb), vec3f(0.0), vec3f(1.0));
    let q = vec3u(round(s * 255.0));
    let a = u32(round(clamp(c.a, 0.0, 1.0) * 255.0));
    dst[id.y * size.x + id.x] = q.b | (q.g << 8u) | (q.r << 16u) | (a << 24u);
}
