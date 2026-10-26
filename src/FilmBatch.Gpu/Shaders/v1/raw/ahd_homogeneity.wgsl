// AHD 去馬賽克 ③：以 CIELab 距離計算每個方向候選的同質性計數；只處理內縮 5 像素的範圍。
#include "raw/ahd_math.wgsl"

@group(0) @binding(0) var<storage, read> lh: array<vec4f>;
@group(0) @binding(1) var<storage, read> lv: array<vec4f>;
@group(0) @binding(2) var<storage, read_write> hh: array<u32>;
@group(0) @binding(3) var<storage, read_write> hv: array<u32>;
@group(0) @binding(4) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let x = i32(id.x) + 5;
    let y = i32(id.y) + 5;
    if (x >= p.bw - 5 || y >= p.bh - 5) { return; }
    let bw = p.bw;
    let i = y * bw + x;
    var nb = array<i32, 4>(i - 1, i + 1, i - bw, i + bw);
    let a = lh[i];
    let b = lv[i];
    var dlh: array<f32, 4>;
    var dch: array<f32, 4>;
    var dlv: array<f32, 4>;
    var dcv: array<f32, 4>;
    for (var k = 0; k < 4; k++) {
        let nh = lh[nb[k]];
        let nv = lv[nb[k]];
        dlh[k] = abs(rounded(a.x - nh.x));
        let dy_h = rounded(a.y - nh.y);
        let dz_h = rounded(a.z - nh.z);
        dch[k] = rounded(rounded(dy_h * dy_h) + rounded(dz_h * dz_h));
        dlv[k] = abs(rounded(b.x - nv.x));
        let dy_v = rounded(b.y - nv.y);
        let dz_v = rounded(b.z - nv.z);
        dcv[k] = rounded(rounded(dy_v * dy_v) + rounded(dz_v * dz_v));
    }
    let el = min(max(dlh[0], dlh[1]), max(dlv[2], dlv[3]));
    let ec = min(max(dch[0], dch[1]), max(dcv[2], dcv[3]));
    var ch = 0u;
    var cv = 0u;
    for (var k = 0; k < 4; k++) {
        if (dlh[k] <= el && dch[k] <= ec) { ch++; }
        if (dlv[k] <= el && dcv[k] <= ec) { cv++; }
    }
    hh[i] = ch;
    hv[i] = cv;
}
