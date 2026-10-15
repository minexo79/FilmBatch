@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (any(pos >= size)) { return; }
    var sum = vec4f(0.0);
    for (var y = -1; y <= 1; y++) {
        for (var x = -1; x <= 1; x++) {
            sum += textureLoad(src, clamp(pos + vec2i(x, y), vec2i(0), size - 1), 0);
        }
    }
    textureStore(dst, pos, sum / 9.0);
}
