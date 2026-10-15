struct Params { direction: vec2i, radius: i32, pad: f32, }
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<storage, read> weights: array<f32>;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (any(pos >= size)) { return; }
    var sum = vec4f(0.0);
    for (var i = -p.radius; i <= p.radius; i++) {
        let point = clamp(pos + p.direction * i, vec2i(0), size - 1);
        sum += textureLoad(src, point, 0) * weights[i + p.radius];
    }
    textureStore(dst, pos, sum);
}
