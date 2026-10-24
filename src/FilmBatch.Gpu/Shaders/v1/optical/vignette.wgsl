// 鏡頭暈影（5.3.8）：超橢圓衰減，線性光中乘法；amount>0 變暗、<0 變亮
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    amount: f32,    // -1..1
    size: f32,      // 0..1
    roundness: f32, // -1..1
    feather: f32,   // 0..1
    cx: f32, cy: f32,
    aspect: f32,
    _pad: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<uniform> view: FrameView;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);

    let fp = frame_pixel(view, id.xy);
    let u = (f32(fp.x) + 0.5) / view.frame_size.x - p.cx;
    let v = (f32(fp.y) + 0.5) / view.frame_size.y - p.cy;
    let uu = u * p.aspect;
    var power = 2.0 + p.roundness * 3.0;
    if (p.roundness < 0.0) { power = 2.0 / (1.0 - p.roundness); }
    let d = pow(pow(abs(uu), power) + pow(abs(v), power), 1.0 / power);
    let inner = p.size * 0.7;
    let t = smoothstep_f(inner, inner + max(0.001, p.feather) * 0.5, d);
    let k = 1.0 - p.amount * t;
    textureStore(dst, id.xy, vec4f(c.rgb * k, c.a));
}
