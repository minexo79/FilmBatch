// Late RAW finish for unclipped linear sRGB working textures.
struct Band { width: u32, rows: u32, rounding_mask: u32, extended_srgb: u32 };

struct FinishParams {
    row0: vec4f, // Rec2020→sRGB 第 0 列
    row1: vec4f, // 第 1 列
    row2: vec4f, // 第 2 列
    band: Band,
    scene0: vec4f, // 線性 sRGB 工作域 → Rec.2020
    scene1: vec4f,
    scene2: vec4f,
};

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var<storage, read> lut: array<f32>;
@group(0) @binding(2) var<storage, read> chroma: array<f32>;
@group(0) @binding(3) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(4) var<uniform> p: FinishParams;

#include "common/raw_finish_math.wgsl"

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= p.band.width || id.y >= p.band.rows) { return; }
    let px = id.y * p.band.width + id.x;
    let pixel = textureLoad(src, vec2i(id.xy), 0);
    let c = vec3f(dot3_ordered(p.scene0.xyz, pixel.rgb),
        dot3_ordered(p.scene1.xyz, pixel.rgb), dot3_ordered(p.scene2.xyz, pixel.rgb));

    let encoded = raw_finish(c, LUMA2020);
    textureStore(dst, vec2i(id.xy), vec4f(encoded, pixel.a));
}
