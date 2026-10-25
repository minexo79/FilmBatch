// AHD 去馬賽克 ①：水平／垂直綠色通道內插（FilmBatch.Raw Demosaic.AhdTile 的第一步）。
// 輸入是已含 6 像素反射邊界的帶狀馬賽克；只處理內縮 3 像素的範圍。
struct Params { bw: i32, bh: i32, w: i32, rows: i32 }

@group(0) @binding(0) var<storage, read> mos: array<f32>;
@group(0) @binding(1) var<storage, read> col: array<u32>;
@group(0) @binding(2) var<storage, read_write> gh: array<f32>;
@group(0) @binding(3) var<storage, read_write> gv: array<f32>;
@group(0) @binding(4) var<uniform> p: Params;

fn clamp_between(v: f32, a: f32, b: f32) -> f32 {
    let lo = min(a, b);
    let hi = max(a, b);
    return select(v, clamp(v, lo, hi), lo <= hi);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let x = i32(id.x) + 3;
    let y = i32(id.y) + 3;
    if (x >= p.bw - 3 || y >= p.bh - 3) { return; }
    let i = y * p.bw + x;
    let c = mos[i];
    if (col[i] == 1u) {
        gh[i] = c;
        gv[i] = c;
        return;
    }
    let l = mos[i - 1];
    let r = mos[i + 1];
    gh[i] = clamp_between((l + r) * 0.5 + (2.0 * c - mos[i - 2] - mos[i + 2]) * 0.25, l, r);
    let u = mos[i - p.bw];
    let d = mos[i + p.bw];
    gv[i] = clamp_between((u + d) * 0.5 + (2.0 * c - mos[i - 2 * p.bw] - mos[i + 2 * p.bw]) * 0.25, u, d);
}
