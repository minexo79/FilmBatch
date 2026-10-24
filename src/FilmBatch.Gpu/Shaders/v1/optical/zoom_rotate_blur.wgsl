// 縮放旋轉模糊（5.3.6）：沿放射方向與圓弧方向取樣平均；中心保護區混合
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    cx: f32, cy: f32,      // 中心（0..1）
    zoom: f32,             // 0..1
    rotate: f32,           // -1..1
    protect: f32,          // 0..1（短邊比例）
    transition: f32,       // 0..1（短邊比例）
    aspect: f32,
    short_side: f32,
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
    let u = (f32(fp.x) + 0.5) / fs.x - p.cx;
    let v = (f32(fp.y) + 0.5) / fs.y - p.cy;
    let dcx = u * fs.x;
    let dcy = v * fs.y;
    let dist = sqrt(dcx * dcx + dcy * dcy);
    let protectPx = p.protect * p.short_side;
    let transPx = max(1e-3, p.transition * p.short_side);
    let protectT = smoothstep_f(protectPx, protectPx + transPx, dist);
    let orig = textureLoad(src, pos, 0);
    if (protectT <= 0.0) { textureStore(dst, pos, orig); return; }

    let zoomLen = p.zoom * 0.15 * p.short_side;
    let rotLen = abs(p.rotate) * 0.1 * p.short_side;
    let rotSign = select(-1.0, 1.0, p.rotate >= 0.0);
    var nx = 0.0; var ny = 0.0;
    if (dist > 0.0) { nx = dcx / dist; ny = dcy / dist; }

    var sum = vec4f(0.0);
    let S = 12;
    for (var s = 0; s < S; s++) {
        let t = f32(s) / f32(S - 1) - 0.5;
        let z = t * zoomLen;
        let angle = t * rotLen * rotSign / p.short_side;
        let ca = cos(angle);
        let sa = sin(angle);
        let rx = dcx * ca - dcy * sa - z * nx;
        let ry = dcx * sa + dcy * ca - z * ny;
        let sx = p.cx * fs.x + rx - 0.5;
        let sy = p.cy * fs.y + ry - 0.5;
        sum += bilinear(src, size, sx - f32(view.origin.x), sy - f32(view.origin.y));
    }
    let avg = sum / f32(S);
    textureStore(dst, pos, mix(orig, avg, protectT));
}
