// 資料驅動的色彩轉換（CT-2）：一次 16×16 dispatch 依序執行 16-byte op 與 flat f32 表。
// 與 ColorTransformUniforms 相同，Params 是 8×vec4 = 128 bytes。
// 強度 0 原樣寫回來源像素。否則：stage 像素轉入宣告輸入、依序執行節點、輸出轉回線性 sRGB、
// 暖色後褪色、再以強度混合；Display stage 最後做 sign-preserving extended sRGB。alpha 不參與運算。
// 節點公式對齊 ColorTransformProcessing；tetrahedral 的 >= 分支順序不可改。
#include "common/color.wgsl"

struct Params {
    mix: vec4f,    // x 強度、y 暖色、z 褪色、w stage（0 WorkingLinear，非 0 DisplayEncoded）
    domain: vec4f, // x 輸入編碼、y 輸出編碼：0 LinearSrgb、1 Srgb、2 ProPhotoGamma18
    to0: vec4f, to1: vec4f, to2: vec4f,       // 線性 sRGB → ProPhoto；只用 xyz
    from0: vec4f, from1: vec4f, from2: vec4f, // 線性 ProPhoto → 線性 sRGB；只用 xyz
}

struct Op {
    code: u32,
    offset: u32,
    length: u32,
    param: u32,
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> u: Params;
@group(0) @binding(3) var<storage, read> ops: array<Op>;
@group(0) @binding(4) var<storage, read> table: array<f32>;

const ENCODING_SRGB: f32 = 1.0;
const ENCODING_PROPHOTO: f32 = 2.0;
const OP_MATRIX: u32 = 0u;
const OP_RANGE: u32 = 1u;
const OP_LUT1D: u32 = 2u;
const OP_LUT3D: u32 = 3u;
const LUT_MASK: u32 = 0xFFFFFu;
const LUT_SHIFT: u32 = 20u;

fn encode_extended_srgb(v: vec3f) -> vec3f {
    let x = abs(v);
    let high = 1.055 * pow(max(x, vec3f(0.0031308)), vec3f(1.0 / 2.4)) - vec3f(0.055);
    let encoded = select(high, x * 12.92, x <= vec3f(0.0031308));
    return select(encoded, -encoded, v < vec3f(0.0));
}

fn decode_extended_srgb(v: vec3f) -> vec3f {
    let x = abs(v);
    let high = pow((max(x, vec3f(0.04045)) + vec3f(0.055)) / 1.055, vec3f(2.4));
    let linear = select(high, x / 12.92, x <= vec3f(0.04045));
    return select(linear, -linear, v < vec3f(0.0));
}

fn signed_pow(v: vec3f, exponent: f32) -> vec3f {
    let magnitude = pow(abs(v), vec3f(exponent));
    return select(magnitude, -magnitude, v < vec3f(0.0));
}

fn apply_rows(r0: vec4f, r1: vec4f, r2: vec4f, v: vec3f) -> vec3f {
    return vec3f(dot(r0.xyz, v), dot(r1.xyz, v), dot(r2.xyz, v));
}

fn enter(pixel: vec3f) -> vec3f {
    var rgb = pixel;
    let display = u.mix.w != 0.0;
    if (display && u.domain.x != ENCODING_SRGB) {
        rgb = decode_extended_srgb(rgb);
    } else if (!display && u.domain.x == ENCODING_SRGB) {
        rgb = encode_extended_srgb(rgb);
    }
    if (u.domain.x == ENCODING_PROPHOTO) {
        let linear = apply_rows(u.to0, u.to1, u.to2, rgb);
        rgb = signed_pow(linear, 1.0 / 1.8);
    }
    return rgb;
}

fn leave(rgb: vec3f) -> vec3f {
    if (u.domain.y == ENCODING_SRGB) { return decode_extended_srgb(rgb); }
    if (u.domain.y == ENCODING_PROPHOTO) {
        let linear = signed_pow(rgb, 1.8);
        return apply_rows(u.from0, u.from1, u.from2, linear);
    }
    return rgb;
}

fn apply_matrix(base: u32, rgb: vec3f) -> vec3f {
    let r0 = vec4f(table[base], table[base + 1u], table[base + 2u], table[base + 3u]);
    let r1 = vec4f(table[base + 4u], table[base + 5u], table[base + 6u], table[base + 7u]);
    let r2 = vec4f(table[base + 8u], table[base + 9u], table[base + 10u], table[base + 11u]);
    return vec3f(dot(r0.xyz, rgb) + r0.w, dot(r1.xyz, rgb) + r1.w, dot(r2.xyz, rgb) + r2.w);
}

fn apply_range(base: u32, param: u32, rgb: vec3f) -> vec3f {
    let min_in = table[base];
    let max_in = table[base + 1u];
    let min_out = table[base + 2u];
    let max_out = table[base + 3u];
    let has_min = (param & 1u) != 0u;
    let has_max = (param & 2u) != 0u;
    if (has_min && has_max) {
        let scale = (max_out - min_out) / (max_in - min_in);
        var mapped = (rgb - vec3f(min_in)) * scale + vec3f(min_out);
        if ((param & 4u) != 0u) { mapped = clamp(mapped, vec3f(min_out), vec3f(max_out)); }
        return mapped;
    }
    if (has_min) { return max(rgb, vec3f(min_in)); }
    return min(rgb, vec3f(max_in));
}

fn lut1d_channel(base: u32, size: u32, channels: u32, channel: u32, input: f32) -> f32 {
    let coord = clamp(input, 0.0, 1.0) * f32(size - 1u);
    let lo = min(u32(coord), size - 2u);
    let amount = coord - f32(lo);
    let a = table[base + lo * channels + channel];
    let b = table[base + (lo + 1u) * channels + channel];
    return a + (b - a) * amount;
}

fn apply_lut1d(base: u32, param: u32, rgb: vec3f) -> vec3f {
    let size = param & LUT_MASK;
    let channels = param >> LUT_SHIFT;
    let g_channel = select(1u, 0u, channels == 1u);
    let b_channel = select(2u, 0u, channels == 1u);
    return vec3f(
        lut1d_channel(base, size, channels, 0u, rgb.x),
        lut1d_channel(base, size, channels, g_channel, rgb.y),
        lut1d_channel(base, size, channels, b_channel, rgb.z));
}

fn lut3d_node(base: u32, size: u32, r: u32, g: u32, b: u32) -> vec3f {
    let i = base + ((r * size + g) * size + b) * 3u;
    return vec3f(table[i], table[i + 1u], table[i + 2u]);
}

fn apply_lut3d(base: u32, param: u32, input: vec3f) -> vec3f {
    let size = param & LUT_MASK;
    let coord = clamp(input, vec3f(0.0), vec3f(1.0)) * f32(size - 1u);
    let lo = min(vec3u(coord), vec3u(size - 2u));
    let f = coord - vec3f(lo);
    let r = lo.x;
    let g = lo.y;
    let b = lo.z;
    let c000 = lut3d_node(base, size, r, g, b);
    let c111 = lut3d_node(base, size, r + 1u, g + 1u, b + 1u);
    if ((param >> LUT_SHIFT) != 0u) {
        var first = c000;
        var second = c000;
        var f1 = 0.0;
        var f2 = 0.0;
        var f3 = 0.0;
        if (f.x >= f.y) {
            if (f.y >= f.z) {
                first = lut3d_node(base, size, r + 1u, g, b);
                second = lut3d_node(base, size, r + 1u, g + 1u, b);
                f1 = f.x; f2 = f.y; f3 = f.z;
            } else if (f.x >= f.z) {
                first = lut3d_node(base, size, r + 1u, g, b);
                second = lut3d_node(base, size, r + 1u, g, b + 1u);
                f1 = f.x; f2 = f.z; f3 = f.y;
            } else {
                first = lut3d_node(base, size, r, g, b + 1u);
                second = lut3d_node(base, size, r + 1u, g, b + 1u);
                f1 = f.z; f2 = f.x; f3 = f.y;
            }
        } else {
            if (f.x >= f.z) {
                first = lut3d_node(base, size, r, g + 1u, b);
                second = lut3d_node(base, size, r + 1u, g + 1u, b);
                f1 = f.y; f2 = f.x; f3 = f.z;
            } else if (f.y >= f.z) {
                first = lut3d_node(base, size, r, g + 1u, b);
                second = lut3d_node(base, size, r, g + 1u, b + 1u);
                f1 = f.y; f2 = f.z; f3 = f.x;
            } else {
                first = lut3d_node(base, size, r, g, b + 1u);
                second = lut3d_node(base, size, r, g + 1u, b + 1u);
                f1 = f.z; f2 = f.y; f3 = f.x;
            }
        }
        return c000 + (first - c000) * f1 + (second - first) * f2 + (c111 - second) * f3;
    }
    let c001 = lut3d_node(base, size, r, g, b + 1u);
    let c010 = lut3d_node(base, size, r, g + 1u, b);
    let c011 = lut3d_node(base, size, r, g + 1u, b + 1u);
    let c100 = lut3d_node(base, size, r + 1u, g, b);
    let c101 = lut3d_node(base, size, r + 1u, g, b + 1u);
    let c110 = lut3d_node(base, size, r + 1u, g + 1u, b);
    let y0 = mix(mix(c000, c001, f.z), mix(c010, c011, f.z), f.y);
    let y1 = mix(mix(c100, c101, f.z), mix(c110, c111, f.z), f.y);
    return mix(y0, y1, f.x);
}

fn execute(input: vec3f) -> vec3f {
    var rgb = input;
    let count = min(arrayLength(&ops), 64u);
    for (var i = 0u; i < count; i++) {
        let op = ops[i];
        if (op.code == OP_MATRIX) { rgb = apply_matrix(op.offset, rgb); }
        else if (op.code == OP_RANGE) { rgb = apply_range(op.offset, op.param, rgb); }
        else if (op.code == OP_LUT1D) { rgb = apply_lut1d(op.offset, op.param, rgb); }
        else if (op.code == OP_LUT3D) { rgb = apply_lut3d(op.offset, op.param, rgb); }
    }
    return rgb;
}

fn apply_warmth(rgb: vec3f, warmth: f32) -> vec3f {
    if (warmth == 0.0) { return rgb; }
    return rgb * vec3f(1.0 + 0.12 * warmth, 1.0, 1.0 - 0.12 * warmth);
}

fn apply_fade(rgb: vec3f, fade: f32) -> vec3f {
    if (fade == 0.0) { return rgb; }
    let encoded = linear_to_srgb(max(rgb, vec3f(0.0))) * (1.0 - 0.2 * fade) + vec3f(0.1 * fade);
    return srgb_to_linear(encoded);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let px = textureLoad(src, id.xy, 0);
    if (u.mix.x == 0.0) {
        textureStore(dst, id.xy, px);
        return;
    }
    var original = px.rgb;
    if (u.mix.w != 0.0) { original = decode_extended_srgb(original); }
    var look = leave(execute(enter(px.rgb)));
    look = apply_fade(apply_warmth(look, u.mix.y), u.mix.z);
    var result = mix(original, look, u.mix.x);
    if (u.mix.w != 0.0) { result = encode_extended_srgb(result); }
    textureStore(dst, id.xy, vec4f(result, px.a));
}
