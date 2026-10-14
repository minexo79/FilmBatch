// M0 GPU spike：曝光、亮度、飽和度（逐像素，線性光）
#include "common/color.wgsl"

struct Params {
    exposure: f32,    // EV
    brightness: f32,  // -1 ~ 1，中間調 gamma
    saturation: f32,  // -1 ~ 1
    _pad: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    var rgb = c.rgb * exp2(p.exposure);

    // 亮度：在顯示空間以 gamma 調整中間調（0 為恆等）
    if (p.brightness != 0.0) {
        let g = exp2(-p.brightness);
        rgb = srgb_to_linear(pow(clamp(linear_to_srgb(rgb), vec3f(0.0), vec3f(1.0)), vec3f(g)));
    }

    // 飽和度：與亮度混合（0 為恆等）
    let y = luminance(rgb);
    rgb = max(mix(vec3f(y), rgb, 1.0 + p.saturation), vec3f(0.0));

    textureStore(dst, id.xy, vec4f(rgb, c.a));
}
