//! FilmBatch port oracle (local tooling, not part of LightCraft).
//!
//! `loaddump IN OUTDIR MAX_EDGE` runs the app's raw source loader (`lightcraft_engine::files::load_bytes`):
//! decode, starting colour (camera-JPEG look fit / measured matrices / Kelvin presets / fallback), binned or
//! demosaiced development, highlight reconstruction, colour model, box fit to MAX_EDGE and orientation. Writes
//! `load<MAX_EDGE>.bin` (u32 width, u32 height, then width × height × 3 little-endian f32, linear Rec.2020)
//! and `load<MAX_EDGE>.json` (dimensions, FNV-1a-64 of the f32 bytes, per-channel sums, the SourceInfo facts
//! and the fitted camera tone/chroma curve).

use serde_json::json;
use std::fs;
use std::path::Path;

fn main() {
    let a: Vec<String> = std::env::args().skip(1).collect();
    let (input, out) = (Path::new(&a[0]), Path::new(&a[1]));
    let max_edge: usize = a.get(2).and_then(|s| s.parse().ok()).unwrap_or(800);
    fs::create_dir_all(out).expect("mkdir");
    let bytes = fs::read(input).expect("read");
    let (img, info) = lightcraft_engine::files::load_bytes(&bytes, max_edge).expect("load");
    let mut bin = Vec::with_capacity(8 + img.data.len() * 12);
    bin.extend_from_slice(&(img.width as u32).to_le_bytes());
    bin.extend_from_slice(&(img.height as u32).to_le_bytes());
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    let mut sums = [0f64; 3];
    for p in &img.data {
        for (c, v) in p.iter().enumerate() {
            sums[c] += f64::from(*v);
            for b in v.to_le_bytes() {
                bin.push(b);
                hash = (hash ^ u64::from(b)).wrapping_mul(0x0000_0100_0000_01b3);
            }
        }
    }
    fs::write(out.join(format!("load{max_edge}.bin")), bin).expect("write");
    let doc = json!({
        "file": input.file_name().map(|f| f.to_string_lossy().to_string()),
        "max_edge": max_edge,
        "width": img.width,
        "height": img.height,
        "fnv1a64": format!("{hash:016x}"),
        "sums": sums,
        "raw": info.raw,
        "as_shot_temp": info.as_shot_temp,
        "as_shot_tint": info.as_shot_tint,
        "relative_wb": info.relative_wb,
        "legacy_relative_wb": info.legacy_relative_wb,
        "camera_color": info.camera_color.is_some(),
        "camera_tone": info.camera_tone,
        "lens": info.lens.is_some(),
    });
    fs::write(out.join(format!("load{max_edge}.json")), serde_json::to_string_pretty(&doc).expect("json")).expect("write");
    println!("{} {}x{} tone={}", input.display(), img.width, img.height, info.camera_tone.is_some());
}
