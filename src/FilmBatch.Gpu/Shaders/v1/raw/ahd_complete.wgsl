// AHD 去馬賽克 ②：以一個方向的綠色平面補齊 R/B，並轉成 CIELab 供同質性比較。
// 水平與垂直各派送一次（輸入的 g 不同）；只處理內縮 4 像素的範圍。
#include "raw/ahd_math.wgsl"

@group(0) @binding(0) var<storage, read> mos: array<f32>;
@group(0) @binding(1) var<storage, read> col: array<u32>;
@group(0) @binding(2) var<storage, read> g: array<f32>;
@group(0) @binding(3) var<storage, read_write> rgb: array<vec4f>;
@group(0) @binding(4) var<storage, read_write> lab: array<vec4f>;
@group(0) @binding(5) var<uniform> p: Params;

fn with_channel(v: vec3f, c: u32, value: f32) -> vec3f {
    var q = v;
    if (c == 0u) { q.x = value; } else if (c == 1u) { q.y = value; } else { q.z = value; }
    return q;
}

// 每一步與 CPU Lab 同序；立方根保留餘差，避免 pow 的誤差翻轉同質性比較。
fn lab_f(t: f32) -> f32 {
    if (t > 0.008856) { return cbrt_stable(t); }
    return rounded(rounded(7.787 * t) + 16.0 / 116.0);
}

fn to_lab(c: vec3f) -> vec3f {
    let x = divide_rounded(rounded(rounded(rounded(0.4124 * c.x) + rounded(0.3576 * c.y)) + rounded(0.1805 * c.z)), 0.95047);
    let y = rounded(rounded(rounded(0.2126 * c.x) + rounded(0.7152 * c.y)) + rounded(0.0722 * c.z));
    let z = divide_rounded(rounded(rounded(rounded(0.0193 * c.x) + rounded(0.1192 * c.y)) + rounded(0.9505 * c.z)), 1.08883);
    let fx = lab_f(x);
    let fy = lab_f(y);
    let fz = lab_f(z);
    return vec3f(rounded(rounded(116.0 * fy) - 16.0),
        rounded(500.0 * rounded(fx - fy)), rounded(200.0 * rounded(fy - fz)));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let x = i32(id.x) + 4;
    let y = i32(id.y) + 4;
    if (x >= p.bw - 4 || y >= p.bh - 4) { return; }
    let bw = p.bw;
    let i = y * bw + x;
    let own = col[i];
    let gg = g[i];
    var c = vec3f(0.0, gg, 0.0);
    if (own == 1u) {
        let horizontal = rounded(rounded(mos[i - 1] - g[i - 1]) + rounded(mos[i + 1] - g[i + 1]));
        c = with_channel(c, col[i + 1], rounded(gg + rounded(horizontal * 0.5)));
        let vertical = rounded(rounded(mos[i - bw] - g[i - bw]) + rounded(mos[i + bw] - g[i + bw]));
        c = with_channel(c, col[i + bw], rounded(gg + rounded(vertical * 0.5)));
    } else {
        c = with_channel(c, own, mos[i]);
        let d1 = rounded(mos[i - bw - 1] - g[i - bw - 1]);
        let d2 = rounded(mos[i - bw + 1] - g[i - bw + 1]);
        let d3 = rounded(mos[i + bw - 1] - g[i + bw - 1]);
        let d4 = rounded(mos[i + bw + 1] - g[i + bw + 1]);
        let diagonal = rounded(rounded(rounded(d1 + d2) + d3) + d4);
        c = with_channel(c, 2u - own, rounded(gg + rounded(diagonal * 0.25)));
    }
    rgb[i] = vec4f(c, 0.0);
    lab[i] = vec4f(to_lab(c), 0.0);
}
