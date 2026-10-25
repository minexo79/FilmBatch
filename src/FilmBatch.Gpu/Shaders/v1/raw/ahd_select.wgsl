// AHD 去馬賽克 ④：在 3×3 內比較水平／垂直同質性總和，選出候選（平手取平均）並寫成 RGB 浮點。
// 內縮 6 像素＝帶狀輸入的有效範圍；輸出為 w × rows 的 RGB（三個 f32 一組）。
struct Params { bw: i32, bh: i32, w: i32, rows: i32 }

@group(0) @binding(0) var<storage, read> rh: array<vec4f>;
@group(0) @binding(1) var<storage, read> rv: array<vec4f>;
@group(0) @binding(2) var<storage, read> hh: array<u32>;
@group(0) @binding(3) var<storage, read> hv: array<u32>;
@group(0) @binding(4) var<storage, read_write> outp: array<f32>;
@group(0) @binding(5) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let ox = i32(id.x);
    let oy = i32(id.y);
    if (ox >= p.w || oy >= p.rows) { return; }
    let bw = p.bw;
    let i = (oy + 6) * bw + ox + 6;
    var sh = 0u;
    var sv = 0u;
    for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
            sh += hh[i + dy * bw + dx];
            sv += hv[i + dy * bw + dx];
        }
    }
    var c: vec3f;
    if (sh > sv) { c = rh[i].xyz; }
    else if (sv > sh) { c = rv[i].xyz; }
    else { c = (rh[i].xyz + rv[i].xyz) * 0.5; }
    let o = (oy * p.w + ox) * 3;
    outp[o] = c.x;
    outp[o + 1] = c.y;
    outp[o + 2] = c.z;
}
