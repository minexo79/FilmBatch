// 局部膚色輔助＋Zone 階調（計畫 W4／Z2）：與 PhotoAdjustments.Adjust/SkinWeight/ZoneLuminance
// 同式（f32）。在 BasicLinear／RAW WB 與語意調整之後、幾何與底片之前執行。
// 輸出像素中心經 map 仿射（CropT.OutToSource）映回來源像素座標再正規化：
// 裁切／旋轉／翻轉下膚色橢圓仍錨定同一來源位置，區域不綁輸出畫框。
// 皮膚：正規化來源座標羽化橢圓內的獨立暖冷／綠洋紅／參考增益，亮度守恆（Rec.709
// 正規化回調整前亮度）、不動全域 WB、不帶膚色分類器；半徑內全強度，向外羽化帶
// smoothstep 衰減，feather=0 為硬邊。Zone：場景線性專用，Y>0 時
// Y' = 0.18·(Y/0.18)^slope·2^offset（單調、保持色度）；黑色、非正、非有限亮度通過。
// Alpha 通道不變；兩段旗標皆由 CPU Uniforms() 決定（含參數合法性）。
// 以 IEEE-754 指數位元檢查有限值，不依賴 fast-math 可將 x-x 消去的算術恆等式。
#include "common/color.wgsl"
#include "common/sampling.wgsl"

struct Params {
    region: vec4f,     // cx, cy, rx, ry（正規化來源座標）
    adjustment: vec4f, // temperature(-1..1), tint(-1..1), strength(0..1), feather(0..1)
    gains: vec4f,      // ref_red, ref_green, ref_blue, 0
    zone: vec4f,       // slope, offset(EV), skin_on(0/1), zone_on(0/1)
    map_x: vec4f,      // OutToSource m00, m01, m02, source_width
    map_y: vec4f,      // OutToSource m10, m11, m12, source_height
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;
@group(0) @binding(3) var<uniform> view: FrameView;

fn is_finite(x: f32) -> bool {
    return (bitcast<u32>(x) & 0x7f800000u) != 0x7f800000u;
}

fn skin_weight(nx: f32, ny: f32) -> f32 {
    let dx = (nx - p.region.x) / p.region.z;
    let dy = (ny - p.region.y) / p.region.w;
    let d = sqrt(dx * dx + dy * dy);
    let feather = min(p.adjustment.w, 1.0);
    if (feather <= 0.0) { return select(0.0, 1.0, d <= 1.0); }
    if (d >= 1.0 + feather) { return 0.0; }
    let t = clamp((d - 1.0) / feather, 0.0, 1.0);
    return 1.0 - t * t * (3.0 - 2.0 * t);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(dst);
    if (id.x >= size.x || id.y >= size.y) { return; }
    var c = textureLoad(src, id.xy, 0);
    if (c.a == 0.0) {
        textureStore(dst, id.xy, c);
        return;
    }
    // 輸出像素中心 → 來源像素座標 → 正規化來源座標（與 CPU Apply 同式）。
    let o = frame_px(view, id.xy);
    let s = vec2f(p.map_x.x * o.x + p.map_x.y * o.y + p.map_x.z,
                  p.map_y.x * o.x + p.map_y.y * o.y + p.map_y.z);
    let n = s / vec2f(p.map_x.w, p.map_y.w);
    var r = c.r;
    var g = c.g;
    var b = c.b;
    // 皮膚區域（先行）：獨立暖冷／綠洋紅／參考增益、亮度守恆。
    if (p.zone.z != 0.0) {
        let w = skin_weight(n.x, n.y) * min(p.adjustment.z, 1.0);
        if (w > 0.0) {
            let t = p.adjustment.x * w;
            let ti = p.adjustment.y * w;
            let gr = (1.0 + 0.3 * t) * (1.0 + w * (clamp(p.gains.x, 0.25, 4.0) - 1.0));
            let gg = (1.0 - 0.22 * ti) * (1.0 + w * (clamp(p.gains.y, 0.25, 4.0) - 1.0));
            let gb = max(1.0 - 0.3 * t, 0.0) * (1.0 + w * (clamp(p.gains.z, 0.25, 4.0) - 1.0));
            if (gr != 1.0 || gg != 1.0 || gb != 1.0) {
                let y0 = luminance(vec3f(r, g, b));
                r = r * gr;
                g = g * gg;
                b = b * gb;
                if (is_finite(y0) && y0 > 0.0) {
                    let y1 = luminance(vec3f(r, g, b));
                    if (is_finite(y1) && y1 > 1e-9) {
                        let k = y0 / y1;
                        r = r * k; g = g * k; b = b * k;
                    }
                }
            }
        }
    }
    // Zone（後行）：log2 亮度域單調曲線，保持色度與黑色。
    if (p.zone.w != 0.0) {
        let y = luminance(vec3f(r, g, b));
        if (is_finite(y) && y > 0.0) {
            let scaled = 0.18 * pow(y / 0.18, p.zone.x) * exp2(p.zone.y);
            if (is_finite(scaled) && scaled > 0.0) {
                let k = scaled / y;
                r = r * k; g = g * k; b = b * k;
            }
        }
    }
    textureStore(dst, id.xy, vec4f(r, g, b, c.a));
}
