// 場景線性亮部保護，與 HighlightRolloff.Compress/Apply 同式（f32）。
// 工作線性 sRGB → Rec.2020 量亮度；膝點以下原樣寫回。共同乘 RGB，alpha 不變。
struct Params {
    to0: vec4f,    // 線性 sRGB → Rec.2020
    to1: vec4f,
    to2: vec4f,
    from0: vec4f,  // Rec.2020 → 線性 sRGB
    from1: vec4f,
    from2: vec4f,
    curve: vec4f,  // knee, reach EV, slope, pad
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

const LUMA2020: vec3f = vec3f(0.2627, 0.6780, 0.0593);

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let c = textureLoad(src, id.xy, 0);
    let rec = vec3f(dot(p.to0.xyz, c.rgb), dot(p.to1.xyz, c.rgb), dot(p.to2.xyz, c.rgb));
    let y = dot(LUMA2020, rec);
    let knee = p.curve.x;
    let slope = p.curve.z;
    if (!(y > knee) || slope >= 1.0) {
        textureStore(dst, id.xy, c);
        return;
    }
    let ev = log2(y / knee);
    let reach = p.curve.y;
    var ev_c: f32;
    if (ev >= reach) {
        ev_c = ev * slope;
    } else {
        // 與 HighlightRolloff.Compress 相同的三次 Hermite。
        let t = ev / reach;
        let h10 = t * (t - 1.0) * (t - 1.0);
        let tail = t * t * (2.0 - t);
        ev_c = reach * (h10 + slope * tail);
    }
    let y2 = knee * exp2(ev_c);
    let k = y2 / y;
    let scaled = rec * k;
    let rgb = vec3f(dot(p.from0.xyz, scaled), dot(p.from1.xyz, scaled), dot(p.from2.xyz, scaled));
    textureStore(dst, id.xy, vec4f(rgb, c.a));
}
