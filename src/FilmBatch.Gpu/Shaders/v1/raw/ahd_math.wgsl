// AHD 的 f32 運算順序與 CPU Ahd.cs 一致；同質性比較不能承受捨入誤差翻轉方向。
struct Params { bw: i32, bh: i32, w: i32, rows: i32, rounding_mask: u32, pad0: u32, pad1: u32, pad2: u32 }

// CPU 固定傳 0，但編譯時未知；沿用 raw_finish_math 的整數屏障，不截斷任何有效位元。
fn rounded(v: f32) -> f32 {
    return bitcast<f32>(bitcast<u32>(v) ^ p.rounding_mask);
}

fn split_f32(a: f32) -> vec2f {
    let scaled = rounded(4097.0 * a);
    let high = rounded(scaled - rounded(scaled - a));
    return vec2f(high, rounded(a - high));
}

// a*b = product + error（正常範圍內精確）；WGSL fma 允許展開，不能拿來取得精確餘差。
fn product_error(a: f32, b: f32, product: f32) -> f32 {
    let x = split_f32(a);
    let y = split_f32(b);
    let residual = rounded(rounded(x.x * y.x) - product);
    return rounded(rounded(rounded(residual + rounded(x.x * y.y)) + rounded(x.y * y.x)) + rounded(x.y * y.y));
}

fn two_sum(a: f32, b: f32) -> vec2f {
    let s = rounded(a + b);
    let bp = rounded(s - a);
    return vec2f(s, rounded(rounded(a - rounded(s - bp)) + rounded(b - bp)));
}

// 修正 WGSL 除法容許的近似誤差；與既有 RAW 收尾同式。
fn divide_rounded(numerator: f32, denominator: f32) -> f32 {
    let quotient = rounded(numerator / denominator);
    let product = rounded(quotient * denominator);
    let remainder = rounded(rounded(numerator - product) - product_error(quotient, denominator, product));
    return rounded(quotient + rounded(remainder / denominator));
}

struct CubeParts { p2: f32, e2: f32, p3: f32, ea: f32, eb: f32, eeb: f32 }

// y²=p2+e2；y³=p3+ea+eb+eeb，保留 Newton 與中點判定所需的乘積餘差。
fn cube_exact(y: f32) -> CubeParts {
    let p2 = rounded(y * y);
    let e2 = product_error(y, y, p2);
    let p3 = rounded(p2 * y);
    let ea = product_error(p2, y, p3);
    let eb = rounded(e2 * y);
    let eeb = product_error(e2, y, eb);
    return CubeParts(p2, e2, p3, ea, eb, eeb);
}

// sign(x−m³)，m=(y+neighbor)/2；m³−y³=3y²h+3yh²+h³，h 是精確的 2 冪尺度。
fn boundary_sign(x: f32, y: f32, neighbor: f32, c: CubeParts) -> f32 {
    let h = rounded((neighbor - y) * 0.5);
    let t1 = rounded(3.0 * h);
    let a1 = rounded(t1 * c.p2);
    let a1e = product_error(t1, c.p2, a1);
    let a1b = rounded(t1 * c.e2);
    let a1be = product_error(t1, c.e2, a1b);
    let h2 = rounded(h * h);
    let t2 = rounded(3.0 * h2);
    let a2 = rounded(t2 * y);
    let a2e = product_error(t2, y, a2);
    let a3 = rounded(h2 * h);
    let start = two_sum(x, -c.p3);
    var high = start.x;
    var low = 0.0;
    let terms = array<f32, 11>(start.y, -c.ea, -c.eb, -c.eeb, -a1, -a1e, -a1b, -a1be, -a2, -a2e, -a3);
    for (var k = 0; k < 11; k++) {
        let sum = two_sum(high, terms[k]);
        high = sum.x;
        low = two_sum(low, sum.y).x;
    }
    return rounded(high + low);
}

// Lab 的正值分支；不以 pow(t, fl32(1/3)) 近似立方根。
fn cbrt_stable(value: f32) -> f32 {
    let x = max(value, 0.008856);
    let third = 0x1.555556p-2;
    var inverse = bitcast<f32>(0x54A232A3 - bitcast<i32>(x) / 3);
    for (var k = 0; k < 3; k++) {
        let square = rounded(inverse * inverse);
        let error = rounded(rounded(rounded(x * square) * inverse) - 1.0);
        inverse = rounded(inverse - rounded(rounded(inverse * error) * third));
    }
    var y = rounded(rounded(x * inverse) * inverse);
    var cube = cube_exact(y);
    let residual = rounded(rounded(rounded(rounded(x - cube.p3) - cube.ea) - cube.eb) - cube.eeb);
    y = rounded(y + rounded(residual / rounded(cube.p2 * 3.0)));
    let bits = bitcast<u32>(y);
    let up = bitcast<f32>(bits + 1u);
    let down = bitcast<f32>(bits - 1u);
    cube = cube_exact(y);
    let upper = boundary_sign(x, y, up, cube);
    if (upper > 0.0 || (upper == 0.0 && (bits & 1u) != 0u)) { return up; }
    let lower = boundary_sign(x, y, down, cube);
    if (lower < 0.0 || (lower == 0.0 && (bits & 1u) != 0u)) { return down; }
    return y;
}
