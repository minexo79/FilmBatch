// 雙重曝光（5.3.4）：第二來源以反向仿射映射取樣，於線性光中合成
#include "common/color.wgsl"

struct Params {
    source: i32,   // 0 self / 1 photo / 2 file（引擎層把外部影像上傳為第二張紋理）
    blend: i32,    // 0 add / 1 screen / 2 lighten / 3 multiply
    mirror: i32,   // 0 none / 1 horizontal / 2 vertical
    opacity: f32,
    offset_x: f32,
    offset_y: f32,
    scale: f32,
    rotation: f32, // 弧度
    exposure_balance: f32, // -1..1
    _pad0: f32,
    _pad1: f32,
    _pad2: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;    // 第一次曝光
@group(0) @binding(1) var second: texture_2d<f32>; // 第二次曝光（可能與 src 相同）
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: Params;

fn bilinear(t: texture_2d<f32>, size: vec2i, x: f32, y: f32) -> vec3f {
    let x0 = clamp(i32(floor(x)), 0, size.x - 1);
    let y0 = clamp(i32(floor(y)), 0, size.y - 1);
    let x1 = clamp(i32(floor(x)) + 1, 0, size.x - 1);
    let y1 = clamp(i32(floor(y)) + 1, 0, size.y - 1);
    let tx = x - floor(x);
    let ty = y - floor(y);
    let c00 = textureLoad(t, vec2i(x0, y0), 0).rgb;
    let c10 = textureLoad(t, vec2i(x1, y0), 0).rgb;
    let c01 = textureLoad(t, vec2i(x0, y1), 0).rgb;
    let c11 = textureLoad(t, vec2i(x1, y1), 0).rgb;
    return (c00 * (1.0 - tx) + c10 * tx) * (1.0 - ty) + (c01 * (1.0 - tx) + c11 * tx) * ty;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let u = (f32(pos.x) + 0.5) / f32(size.x);
    let v = (f32(pos.y) + 0.5) / f32(size.y);

    var su = (u - 0.5 - p.offset_x) / p.scale;
    var sv = (v - 0.5 - p.offset_y) / p.scale;
    let cs = cos(p.rotation);
    let sn = sin(p.rotation);
    var ru = su * cs - sv * sn;
    var rv = su * sn + sv * cs;
    if (p.mirror == 1) { ru = -ru; }
    else if (p.mirror == 2) { rv = -rv; }
    let ssize = vec2i(textureDimensions(second));
    let sx = (ru + 0.5) * f32(ssize.x) - 0.5;
    let sy = (rv + 0.5) * f32(ssize.y) - 0.5;

    let a = textureLoad(src, pos, 0);
    let b = vec4f(bilinear(second, ssize, sx, sy), 1.0);
    let k2 = p.opacity;
    let bal = p.exposure_balance;
    var rgb: vec3f;
    switch p.blend {
        case 0: { rgb = a.rgb * (1.0 - bal * 0.5) + b.rgb * k2 * (1.0 + bal * 0.5); } // add
        case 1: { rgb = 1.0 - (1.0 - a.rgb) * (1.0 - b.rgb * k2); }                   // screen
        case 2: { rgb = max(a.rgb, b.rgb * k2); }                                     // lighten
        default: { rgb = a.rgb * (1.0 - k2 + k2 * b.rgb); }                           // multiply
    }
    textureStore(dst, pos, vec4f(rgb, a.a));
}
