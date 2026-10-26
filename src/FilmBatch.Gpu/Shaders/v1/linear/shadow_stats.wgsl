// 暗部還原 ①：工作線性影像 → 代理格區塊統計（ShadowRecovery.Apply 第 ①② 步同式）。
// 每個代理像素累加 s×s 區塊（右／下緣不完整區塊只含實際像素）的 Σw、Σw·v、Σw·v²，v=u+4。
// 輸出寫在工作紋理左上 proxy_w×proxy_h 範圍，不另配置紋理。
#include "linear/shadow_common.wgsl"

struct Params {
    factor: i32,
    proxy_w: i32,
    proxy_h: i32,
    _pad: i32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let cell = vec2i(id.xy);
    if (cell.x >= p.proxy_w || cell.y >= p.proxy_h) { return; }
    let size = vec2i(textureDimensions(src));
    let start = cell * p.factor;
    let stop = min(start + vec2i(p.factor), size);
    var s0 = 0.0;
    var s1 = 0.0;
    var s2 = 0.0;
    for (var y = start.y; y < stop.y; y++) {
        for (var x = start.x; x < stop.x; x++) {
            let c = textureLoad(src, vec2i(x, y), 0);
            let lum = luminance(c.rgb);
            let w = shadow_weight(c.a, lum);
            if (w > 0.0) {
                let u = shadow_guide(lum) + SHADOW_MOMENT_SHIFT;
                s0 += w;
                s1 += w * u;
                s2 += w * u * u;
            }
        }
    }
    textureStore(dst, cell, vec4f(s0, s1, s2, 0.0));
}
