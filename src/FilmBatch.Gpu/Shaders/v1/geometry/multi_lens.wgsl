// 多格分鏡（5.3.3）：每格的位移 / 縮放 / 曝光 / 色偏由 CPU 端以種子算出，以 uniform 傳入
#include "common/color.wgsl"

struct Params {
    arrangement: i32,  // 0 h2 / 1 h3 / 2 h4 / 3 v2 / 4 grid / 5 stagger
    divider: f32,      // 0..1 羽化寬度
    _pad0: f32,
    _pad1: f32,
    // 每格：dxy_zoom_exp = (dx, dy, zoom, exposureEv)；tint = 色偏通道權重 (r,g,b,-)
    cell0: vec4f, tint0: vec4f,
    cell1: vec4f, tint1: vec4f,
    cell2: vec4f, tint2: vec4f,
    cell3: vec4f, tint3: vec4f,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

fn cell_params(i: i32) -> vec4f {
    switch i {
        case 1: { return p.cell1; }
        case 2: { return p.cell2; }
        case 3: { return p.cell3; }
        default: { return p.cell0; }
    }
}
fn cell_tint(i: i32) -> vec4f {
    switch i {
        case 1: { return p.tint1; }
        case 2: { return p.tint2; }
        case 3: { return p.tint3; }
        default: { return p.tint0; }
    }
}

fn sample_cell(size: vec2i, u: f32, v: f32, col: i32, row: i32, cols: i32, rows: i32, idx: i32) -> vec3f {
    let cp = cell_params(idx);
    let lu = u * f32(cols) - f32(col);
    let lv = v * f32(rows) - f32(row);
    let cu = lu - 0.5;
    let cv = lv - 0.5;
    let sx = (f32(col) + 0.5 + cu / cp.z + cp.x) * f32(size.x) / f32(cols) - 0.5;
    let sy = (f32(row) + 0.5 + cv / cp.z + cp.y) * f32(size.y) / f32(rows) - 0.5;
    // bilinear
    let x0 = clamp(i32(floor(sx)), 0, size.x - 1);
    let y0 = clamp(i32(floor(sy)), 0, size.y - 1);
    let x1 = clamp(i32(floor(sx)) + 1, 0, size.x - 1);
    let y1 = clamp(i32(floor(sy)) + 1, 0, size.y - 1);
    let tx = sx - floor(sx);
    let ty = sy - floor(sy);
    let c00 = textureLoad(src, vec2i(x0, y0), 0).rgb;
    let c10 = textureLoad(src, vec2i(x1, y0), 0).rgb;
    let c01 = textureLoad(src, vec2i(x0, y1), 0).rgb;
    let c11 = textureLoad(src, vec2i(x1, y1), 0).rgb;
    var c = (c00 * (1.0 - tx) + c10 * tx) * (1.0 - ty) + (c01 * (1.0 - tx) + c11 * tx) * ty;
    c = c * exp2(cp.w);              // 曝光差異（線性增益）
    return c * cell_tint(idx).rgb;   // 色偏
}

fn divider_distance(u: f32, v: f32, cols: i32, rows: i32) -> f32 {
    let fu = u * f32(cols);
    let fv = v * f32(rows);
    var du = min(fu - floor(fu), 1.0 - (fu - floor(fu)));
    var dv = 1.0;
    if (rows > 1) { dv = min(fv - floor(fv), 1.0 - (fv - floor(fv))); }
    return min(du, dv);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = vec2i(textureDimensions(src));
    let pos = vec2i(id.xy);
    if (pos.x >= size.x || pos.y >= size.y) { return; }
    let u = (f32(pos.x) + 0.5) / f32(size.x);
    let v = (f32(pos.y) + 0.5) / f32(size.y);

    var cols = 2; var rows = 1;
    switch p.arrangement {
        case 1: { cols = 3; rows = 1; }
        case 2: { cols = 4; rows = 1; }
        case 3: { cols = 1; rows = 2; }
        case 4: { cols = 2; rows = 2; }
        case 5: { cols = 2; rows = 2; }
        default: { cols = 2; rows = 1; }
    }

    let col = min(cols - 1, i32(u * f32(cols)));
    let row = min(rows - 1, i32(v * f32(rows)));
    let idx = row * cols + col;
    var c = sample_cell(size, u, v, col, row, cols, rows, idx);

    if (p.divider > 0.0) {
        let edge = divider_distance(u, v, cols, rows);
        let t = smoothstep_f(0.0, max(1e-4, p.divider * 0.5), edge);
        if (t < 1.0) {
            var ncol = col;
            if (u * f32(cols) - f32(col) - 0.5 > 0.0) { ncol = min(cols - 1, col + 1); }
            else { ncol = max(0, col - 1); }
            var nrow = row;
            if (v * f32(rows) - f32(row) - 0.5 > 0.0) { nrow = min(rows - 1, row + 1); }
            else { nrow = max(0, row - 1); }
            let ni = nrow * cols + ncol;
            let nc = sample_cell(size, u, v, ncol, nrow, cols, rows, ni);
            let mix = 1.0 - t;
            c = c * (1.0 - mix) + nc * mix;
        }
    }
    let a = textureLoad(src, pos, 0).a;
    textureStore(dst, pos, vec4f(c, a));
}
