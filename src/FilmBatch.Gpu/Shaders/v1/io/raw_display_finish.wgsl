// RAW 顯示收尾：線性 Rec.2020 →（相機曲線或預設延伸 Reinhard）→ 亮度／色度映射 →
// 高光去飽和 → Rec.2020→sRGB → 色域映射 → 精確 sRGB 曲線 → 打包 16-bit RGBA。
// 與 Engine/Imaging/Raw/RawDisplay.cs 同順序；LUT 由 CPU 端 ToneMap 建好後以 storage buffer 傳入。
// 輸入輸出皆為 storage buffer，以 array<f32> 避免 RGB vec3 的 16-byte 對齊：
//   src:  每像素 3 個 f32（RGB 逐像素）
//   lut:  4096 個 f32（ToneMap 對數域 LUT）
//   chroma: 8 個 f32（CameraTone 色度縮放；預設全 1）
//   dst:  每像素 2 個 u32（R|G<<16, B|A<<16），只寫 band.rows 列
// matrix 以 3 個 vec4 傳（row-major 前 3 欄；第 4 欄 padding，配合 16-byte 對齊）。

struct Band { width: u32, rows: u32, rounding_mask: u32, pad1: u32 };

struct FinishParams {
    row0: vec4f, // Rec2020→sRGB 第 0 列
    row1: vec4f, // 第 1 列
    row2: vec4f, // 第 2 列
    band: Band,
};

@group(0) @binding(0) var<storage, read> src: array<f32>;
@group(0) @binding(1) var<storage, read> lut: array<f32>;
@group(0) @binding(2) var<storage, read> chroma: array<f32>;
@group(0) @binding(3) var<storage, read_write> dst: array<u32>;
@group(0) @binding(4) var<uniform> p: FinishParams;

#include "common/raw_finish_math.wgsl"

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= p.band.width || id.y >= p.band.rows) { return; }
    let px = id.y * p.band.width + id.x;
    let c = vec3f(src[px * 3u], src[px * 3u + 1u], src[px * 3u + 2u]);

    let encoded = raw_finish(c, LUMA2020);
    let q = vec4u(quantize16(encoded.r), quantize16(encoded.g), quantize16(encoded.b), 65535u);
    let idx = (id.y * p.band.width + id.x) * 2u;
    dst[idx] = q.r | (q.g << 16u);
    dst[idx + 1u] = q.b | (q.a << 16u);
}
