// 已完成輸出色域／TRC 的顯示樣本 → 真正 16-bit RGBA；sRGB 與 ProPhoto 共用量化。
// 以列帶（band）輸出：dst 只容納 band.rows 列，大影像分段讀回以符合緩衝區上限。
#include "common/color.wgsl"

struct Band { row: u32, rows: u32, pad0: u32, pad1: u32 };

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> dst: array<u32>;
@group(0) @binding(2) var<uniform> band: Band;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= band.rows) { return; }
    let c = textureLoad(src, vec2u(id.x, band.row + id.y), 0);
    let s = clamp(c.rgb, vec3f(0.0), vec3f(1.0));
    let a = clamp(c.a, 0.0, 1.0);
    let q = vec4u(round(vec4f(s, a) * 65535.0));
    let idx = (id.y * size.x + id.x) * 2u;
    dst[idx] = q.r | (q.g << 16u);
    dst[idx + 1u] = q.b | (q.a << 16u);
}
