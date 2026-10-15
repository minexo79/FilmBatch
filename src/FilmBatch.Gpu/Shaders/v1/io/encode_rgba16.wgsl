// 顯示空間 sRGB → 真正 16-bit RGBA；輸入已完成 linear_to_display。
#include "common/color.wgsl"

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> dst: array<u32>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    let s = clamp(c.rgb, vec3f(0.0), vec3f(1.0));
    let a = clamp(c.a, 0.0, 1.0);
    let q = vec4u(round(vec4f(s, a) * 65535.0));
    let idx = (id.y * size.x + id.x) * 2u;
    dst[idx] = q.r | (q.g << 16u);
    dst[idx + 1u] = q.b | (q.a << 16u);
}
