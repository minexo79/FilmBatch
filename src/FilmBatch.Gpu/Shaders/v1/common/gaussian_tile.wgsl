// 平鋪式可分離高斯模糊：與 common/gaussian.wgsl 完全相同的權重、clamp 邊界與逐 tap 累加順序。
// 每個 256-thread workgroup 沿模糊軸協作載入 tile＋光暈到 workgroup 記憶體；大半徑時每個源像素
// 只從紋理讀取一次（直接版每個輸出 tap 各讀一次）。tile 上限：256＋2×320＝896 個 vec4f＝14 KiB，
// 低於 WebGPU 基準 maxComputeWorkgroupStorageSize（16 KiB）；半徑超過 320 由 CPU 端改派直接版。
// uniform 與 binding 配置與 gaussian.wgsl 相同；first_group 是展平後的 workgroup 起點。
struct Params { direction: vec2i, radius: i32, first_group: u32, }
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<storage, read> weights: array<f32>;
var<workgroup> tile: array<vec4f, 896>;
@compute @workgroup_size(256)
fn main(@builtin(workgroup_id) group: vec3u, @builtin(local_invocation_id) lane: vec3u) {
    let size = vec2i(textureDimensions(src));
    let horizontal = p.direction.x != 0;
    let major = select(size.y, size.x, horizontal);
    let minor = select(size.x, size.y, horizontal);
    let columns = u32((major + 255) / 256);
    let index = p.first_group + group.x;
    let line = i32(index / columns);
    if (line >= minor) { return; }
    let base = i32(index % columns) * 256;
    // 協作載入 tile＋兩側光暈（含 clamp 邊界；此行每個源像素只讀一次）。
    for (var t = i32(lane.x); t < 256 + 2 * p.radius; t += 256) {
        let major_pos = clamp(base - p.radius + t, 0, major - 1);
        tile[t] = textureLoad(src, select(vec2i(line, major_pos), vec2i(major_pos, line), horizontal), 0);
    }
    workgroupBarrier();
    // 與 gaussian.wgsl 相同的 tap 順序與累加。
    let out_major = base + i32(lane.x);
    if (out_major < major) {
        var sum = vec4f(0.0);
        for (var i = -p.radius; i <= p.radius; i++) {
            sum += tile[out_major - base + p.radius + i] * weights[i + p.radius];
        }
        textureStore(dst, select(vec2i(line, out_major), vec2i(out_major, line), horizontal), sum);
    }
}
