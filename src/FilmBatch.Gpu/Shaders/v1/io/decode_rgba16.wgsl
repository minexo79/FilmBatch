// 上傳的 16-bit sRGB（rgba16uint）→ 線性光工作紋理（rgba32float）
#include "common/color.wgsl"

@group(0) @binding(0) var src: texture_2d<u32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let raw = vec4f(textureLoad(src, id.xy, 0)) / 65535.0;
    textureStore(dst, id.xy, vec4f(srgb_to_linear(raw.rgb), raw.a));
}
