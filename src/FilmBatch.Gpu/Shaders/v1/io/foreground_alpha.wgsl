// 去背遮罩在原尺寸取樣，再與來源 alpha 相乘一次（先於所有幾何 pass）。
// RGB 保持 straight；不放大上傳遮罩，也不預乘顏色。
#include "common/sampling.wgsl"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var mask: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(dst);
    if (id.x >= size.x || id.y >= size.y) { return; }
    var c = textureLoad(src, id.xy, 0);
    let point = (vec2f(id.xy) + 0.5) * vec2f(textureDimensions(mask)) / vec2f(size) - 0.5;
    c = vec4f(c.rgb, c.a * sample_bilinear(mask, point).r);
    textureStore(dst, id.xy, c);
}
