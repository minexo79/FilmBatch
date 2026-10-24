// 鏡頭變形（5.3.2）：r' = r·(1+k1·r²+k2·r⁴) 反算來源座標；R/B 通道以不同 k 產生色散
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    amount: f32,       // -1..1
    chromatic: f32,    // 0..1
    edge_soften: f32,  // 0..1
    auto_crop: f32,    // 0/1
    aspect: f32,       // w/h
    short_side: f32,   // min(w,h)
    _pad0: f32,
    _pad1: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var blurSrc: texture_2d<f32>; // 邊緣柔化用的小半徑模糊版
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;
@group(0) @binding(4) var<uniform> view: FrameView;

fn bilinear(t: texture_2d<f32>, size: vec2i, x: f32, y: f32) -> vec4f {
    let x0 = i32(floor(x));
    let y0 = i32(floor(y));
    let tx = x - f32(x0);
    let ty = y - f32(y0);
    let x1 = clamp(x0 + 1, 0, size.x - 1);
    let y1 = clamp(y0 + 1, 0, size.y - 1);
    let cx0 = clamp(x0, 0, size.x - 1);
    let cy0 = clamp(y0, 0, size.y - 1);
    let c00 = textureLoad(t, vec2i(cx0, cy0), 0);
    let c10 = textureLoad(t, vec2i(x1, cy0), 0);
    let c01 = textureLoad(t, vec2i(cx0, y1), 0);
    let c11 = textureLoad(t, vec2i(x1, y1), 0);
    return (c00 * (1.0 - tx) + c10 * tx) * (1.0 - ty) + (c01 * (1.0 - tx) + c11 * tx) * ty;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }

    // 鏡頭中心與短邊以畫框計算；取樣座標減去渲染區域原點後在本地紋理內夾取
    let fp = frame_pixel(view, id.xy);
    let o = vec2f(view.origin);
    let half = p.short_side * 0.5;
    let cx = view.frame_size.x * 0.5 / half;
    let cy = view.frame_size.y * 0.5 / half;
    let u = (f32(fp.x) + 0.5) / half - cx;
    let v = (f32(fp.y) + 0.5) / half - cy;
    let r2 = u * u + v * v;

    let k1 = p.amount * 0.6;
    let k2 = p.amount * 0.35;
    let chroma = p.chromatic * 0.02;
    var crop = 1.0;
    if (p.auto_crop > 0.5) {
        let f = 1.0 + k1 + k2;
        crop = 1.0 / max(0.5, f);
    }

    let fr = 1.0 + (k1 + chroma) * r2 + k2 * r2 * r2;
    let fg = 1.0 + k1 * r2 + k2 * r2 * r2;
    let fb = 1.0 + (k1 - chroma) * r2 + k2 * r2 * r2;

    let sxr = (u * fr * crop + cx) * half - 0.5;
    let syr = (v * fr * crop + cy) * half - 0.5;
    let sxg = (u * fg * crop + cx) * half - 0.5;
    let syg = (v * fg * crop + cy) * half - 0.5;
    let sxb = (u * fb * crop + cx) * half - 0.5;
    let syb = (v * fb * crop + cy) * half - 0.5;

    var col = vec4f(
        bilinear(src, size, sxr - o.x, syr - o.y).r,
        bilinear(src, size, sxg - o.x, syg - o.y).g,
        bilinear(src, size, sxb - o.x, syb - o.y).b,
        bilinear(src, size, sxg - o.x, syg - o.y).a);

    // 邊緣柔化：依半徑混入小半徑模糊版
    if (p.edge_soften > 0.0) {
        let edgeT = smoothstep_f(0.55, 0.95, sqrt(r2));
        let k = edgeT * p.edge_soften;
        let bc = bilinear(blurSrc, size, sxg - o.x, syg - o.y);
        col = vec4f(mix(col.rgb, bc.rgb, k), col.a);
    }
    textureStore(dst, pos, col);
}
