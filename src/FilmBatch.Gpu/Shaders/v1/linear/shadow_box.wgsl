// 暗部還原 ②：代理格分數盒狀濾波（單軸、鏡射邊界；ShadowRecovery.Box 同式）。
// 整數半徑內權重 1，外側一格權重為小數部分；coefficients=1 時把 (M0, M1, M2)
// 轉為覆蓋率加權 guided filter 係數 (M0·a, M0·b, M0)。讀寫分屬不同工作紋理。
#include "linear/shadow_common.wgsl"

struct Params {
    direction: vec2i,    // (1,0) 水平、(0,1) 垂直
    proxy_size: vec2i,
    radius: f32,         // 代理像素，可為小數
    coefficients: u32,
    _pad0: f32,
    _pad1: f32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

fn tap(cell: vec2i, shift: i32) -> vec3f {
    let horizontal = p.direction.x != 0;
    let extent = select(p.proxy_size.y, p.proxy_size.x, horizontal);
    let index = shadow_reflect(select(cell.y, cell.x, horizontal) + shift, extent);
    return textureLoad(src, select(vec2i(cell.x, index), vec2i(index, cell.y), horizontal), 0).xyz;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let cell = vec2i(id.xy);
    if (any(cell >= p.proxy_size)) { return; }
    let whole = i32(floor(p.radius));
    let part = p.radius - f32(whole);
    var sum = vec3f(0.0);
    for (var k = -whole; k <= whole; k++) {
        sum += tap(cell, k);
    }
    if (part > 0.0) {
        sum += tap(cell, -whole - 1) * part;
        sum += tap(cell, whole + 1) * part;
    }
    if (p.coefficients != 0u) { sum = shadow_coefficients(sum); }
    textureStore(dst, cell, vec4f(sum, 0.0));
}
