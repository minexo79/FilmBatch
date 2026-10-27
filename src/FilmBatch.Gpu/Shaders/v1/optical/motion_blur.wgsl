// 動態模糊（5.3.7）：沿指定方向取樣平均，保護區混合
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    angle: f32,        // 弧度
    length: f32,       // 0..1（短邊比例）
    protect_on: f32,   // 0/1
    px: f32, py: f32,  // 保護區中心（0..1）
    psize: f32,        // 0..1（短邊比例）
    short_side: f32,
    shape: i32,        // 保護區形狀：0 圓 1 橢圓 2 水平帶 3 垂直帶
    shape_aspect: f32,
    shape_cos: f32, shape_sin: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<uniform> view: FrameView;

fn bilinear(t: texture_2d<f32>, size: vec2i, x: f32, y: f32) -> vec4f {
    let x0 = clamp(i32(floor(x)), 0, size.x - 1);
    let y0 = clamp(i32(floor(y)), 0, size.y - 1);
    let x1 = clamp(i32(floor(x)) + 1, 0, size.x - 1);
    let y1 = clamp(i32(floor(y)) + 1, 0, size.y - 1);
    let tx = x - floor(x);
    let ty = y - floor(y);
    return (textureLoad(t, vec2i(x0, y0), 0) * (1.0 - tx) + textureLoad(t, vec2i(x1, y0), 0) * tx) * (1.0 - ty)
         + (textureLoad(t, vec2i(x0, y1), 0) * (1.0 - tx) + textureLoad(t, vec2i(x1, y1), 0) * tx) * ty;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }

    let fp = frame_pixel(view, id.xy);
    let fs = view.frame_size;
    var pmask = 1.0;
    if (p.protect_on > 0.5) {
        let du = (f32(fp.x) + 0.5) / fs.x - p.px;
        let dv = (f32(fp.y) + 0.5) / fs.y - p.py;
        let d = shape_distance(p.shape, vec2f(du * fs.x, dv * fs.y), p.shape_aspect, p.shape_cos, p.shape_sin);
        pmask = smoothstep_f(p.psize * p.short_side, p.psize * p.short_side + 0.15 * p.short_side, d);
    }
    let orig = textureLoad(src, pos, 0);
    if (pmask <= 0.0) { textureStore(dst, pos, orig); return; }

    let len = p.length * 0.2 * p.short_side;
    let dx = cos(p.angle);
    let dy = sin(p.angle);
    var sum = vec4f(0.0);
    let S = 16;
    for (var s = 0; s < S; s++) {
        let t = f32(s) / f32(S - 1) - 0.5;
        let sx = f32(fp.x) + 0.5 + t * len * dx - 0.5;
        let sy = f32(fp.y) + 0.5 + t * len * dy - 0.5;
        sum += bilinear(src, size, sx - f32(view.origin.x), sy - f32(view.origin.y));
    }
    textureStore(dst, pos, mix(orig, sum / f32(S), pmask));
}
