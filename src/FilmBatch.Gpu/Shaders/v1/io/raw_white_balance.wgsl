// Rec.2020 white balance conjugated into the unclipped linear sRGB working domain.
struct Params { row0: vec4f, row1: vec4f, row2: vec4f };
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(dst))) { return; }
    let c = textureLoad(src, vec2i(id.xy), 0);
    textureStore(dst, vec2i(id.xy), vec4f(dot(p.row0.xyz, c.rgb), dot(p.row1.xyz, c.rgb), dot(p.row2.xyz, c.rgb), c.a));
}
