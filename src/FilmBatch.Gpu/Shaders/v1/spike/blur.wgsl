// M0 GPU spike：可分離高斯模糊（水平或垂直一次），於線性光中計算

struct Params {
    direction: vec2i, // (1,0) 水平、(0,1) 垂直
    radius: i32,      // 像素
    sigma: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }

    let k = -0.5 / (p.sigma * p.sigma);
    var sum = vec4f(0.0);
    var wsum = 0.0;
    for (var i = -p.radius; i <= p.radius; i++) {
        let q = clamp(pos + p.direction * i, vec2i(0), size - 1);
        let w = exp(f32(i * i) * k);
        sum += textureLoad(src, q, 0) * w;
        wsum += w;
    }
    textureStore(dst, pos, sum / wsum);
}
