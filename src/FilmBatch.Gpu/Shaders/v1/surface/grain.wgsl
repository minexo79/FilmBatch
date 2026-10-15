// 顆粒（5.3.16）：以 PCG 整數雜湊產生兩層雜訊；強度隨亮度變化（中間調最明顯）
#include "common/color.wgsl"

struct Params {
    amount: f32,    // 0..1
    size: f32,      // 0..1（短邊比例）
    roughness: f32, // 0..1
    color: f32,     // 0..1
    seed: u32,
    short_side: f32,
    _pad0: u32,
    _pad1: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    var c = textureLoad(src, pos, 0);

    let lum = luminance(c.rgb);
    let lumW = exp(-pow((lum - 0.5) / 0.28, 2.0));
    let k = p.amount * 0.22 * lumW;
    if (k <= 0.0) { textureStore(dst, pos, c); return; }

    let cell = max(1.0, p.size * 6.0) * p.short_side / 800.0;
    let n1 = grain_cell(pos.x, pos.y, cell, p.seed);
    let n2 = grain_cell(pos.x, pos.y, cell * 2.7, p.seed + 31u);
    let gn = (n1 * (1.0 - p.roughness) + n2 * p.roughness) * 2.0 - 1.0;

    var rN = gn; var gN = gn; var bN = gn;
    if (p.color > 0.0) {
        rN = grain_cell(pos.x, pos.y, cell, p.seed + 101u) * 2.0 - 1.0;
        gN = grain_cell(pos.x, pos.y, cell, p.seed + 103u) * 2.0 - 1.0;
        bN = grain_cell(pos.x, pos.y, cell, p.seed + 107u) * 2.0 - 1.0;
    }
    let mk = p.color;
    c = vec4f(
        clamp(c.r + (gn * (1.0 - mk) + rN * mk) * k, 0.0, 1.0),
        clamp(c.g + (gn * (1.0 - mk) + gN * mk) * k, 0.0, 1.0),
        clamp(c.b + (gn * (1.0 - mk) + bN * mk) * k, 0.0, 1.0),
        c.a);
    textureStore(dst, pos, c);
}
