//! FilmBatch port oracle (local tooling, not part of LightCraft).
//!
//! `stagedump IN OUTDIR` decodes IN with lightcraft-raw and writes, into OUTDIR:
//! - `info.json`: RawInfo + black/white levels, as-shot xy, file-only and spectral camera transforms,
//!   and for every stage its dimensions, FNV-1a-64 over the little-endian bytes of the whole buffer,
//!   per-channel sums (f64) and the window rectangles dumped below.
//! - `<stage>.<window>.bin`: raw little-endian samples of each window (u16 for `raw`, f32 otherwise;
//!   interleaved channels, row-major). Windows: `tl` = 64×64 at (0, 0), `c` = 128×128 at the even-aligned
//!   centre (the `raw` stage uses sensor coordinates; the others their own buffer's coordinates).
//! - `binned<k>.mask.bin`: the clip mask of `develop_binned_masked(k, 0.99)` for the `c` window.
//! - `preview.jpg`: the embedded preview (`embedded_preview`), when there is one.
//!
//! Stages: raw, normalized, develop-bilinear, develop-ppg, develop-ahd, highlight-ahd (reconstruct with the
//! file-only transform's wb at clip 0.99), binned2, binned4.

use lightcraft_raw::{Method, RawData, color, decode, embedded_preview, highlight, spectral};
use serde_json::{Value, json};
use std::fs;
use std::path::Path;

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

fn fnv(bytes: impl Iterator<Item = u8>) -> u64 {
    bytes.fold(FNV_OFFSET, |h, b| (h ^ u64::from(b)).wrapping_mul(FNV_PRIME))
}

fn windows(w: usize, h: usize) -> Vec<(&'static str, usize, usize, usize, usize)> {
    let tl = (64.min(w), 64.min(h));
    let cw = 128.min(w);
    let ch = 128.min(h);
    let cx = ((w - cw) / 2) & !1;
    let cy = ((h - ch) / 2) & !1;
    vec![("tl", 0, 0, tl.0, tl.1), ("c", cx, cy, cw, ch)]
}

/// Hash, sums and windows of an interleaved f32 buffer of `w × h × cpp`.
fn stage_f32(out: &Path, name: &str, w: usize, h: usize, cpp: usize, data: &[f32]) -> Value {
    let hash = fnv(data.iter().flat_map(|v| v.to_le_bytes()));
    let mut sums = vec![0f64; cpp];
    for (i, v) in data.iter().enumerate() {
        sums[i % cpp] += f64::from(*v);
    }
    let mut wins = Vec::new();
    for (wn, x0, y0, ww, wh) in windows(w, h) {
        let mut bytes = Vec::with_capacity(ww * wh * cpp * 4);
        for y in y0..y0 + wh {
            for x in x0..x0 + ww {
                for c in 0..cpp {
                    bytes.extend_from_slice(&data[(y * w + x) * cpp + c].to_le_bytes());
                }
            }
        }
        fs::write(out.join(format!("{name}.{wn}.bin")), bytes).expect("write window");
        wins.push(json!({"name": wn, "x": x0, "y": y0, "width": ww, "height": wh}));
    }
    json!({"width": w, "height": h, "cpp": cpp, "type": "f32", "fnv1a64": format!("{hash:016x}"), "sums": sums, "windows": wins})
}

fn rgb_flat(img: &lightcraft_raw::Rgb32f) -> Vec<f32> {
    img.data.iter().flat_map(|p| p.iter().copied()).collect()
}

fn main() {
    let a: Vec<String> = std::env::args().skip(1).collect();
    let (input, out) = (Path::new(&a[0]), Path::new(&a[1]));
    fs::create_dir_all(out).expect("mkdir");
    let bytes = fs::read(input).expect("read");
    let mut raw = decode(&bytes).expect("decode");
    // as the app's loader does (`files::load_bytes_now`): embedded lens corrections are not baked into the source
    let lens_ops = raw.opcodes.list3.len();
    raw.opcodes.list3.retain(|op| !op.is_lens_correction());
    let lens_ops = lens_ops - raw.opcodes.list3.len();
    let mut stages = serde_json::Map::new();

    // raw samples
    match &raw.data {
        RawData::U16(d) => {
            let hash = fnv(d.iter().flat_map(|v| v.to_le_bytes()));
            let mut sums = vec![0f64; raw.cpp];
            for (i, v) in d.iter().enumerate() {
                sums[i % raw.cpp] += f64::from(*v);
            }
            let mut wins = Vec::new();
            for (wn, x0, y0, ww, wh) in windows(raw.width, raw.height) {
                let mut b = Vec::with_capacity(ww * wh * raw.cpp * 2);
                for y in y0..y0 + wh {
                    for x in x0..x0 + ww {
                        for c in 0..raw.cpp {
                            b.extend_from_slice(&d[(y * raw.width + x) * raw.cpp + c].to_le_bytes());
                        }
                    }
                }
                fs::write(out.join(format!("raw.{wn}.bin")), b).expect("write");
                wins.push(json!({"name": wn, "x": x0, "y": y0, "width": ww, "height": wh}));
            }
            stages.insert(
                "raw".into(),
                json!({"width": raw.width, "height": raw.height, "cpp": raw.cpp, "type": "u16", "fnv1a64": format!("{hash:016x}"), "sums": sums, "windows": wins}),
            );
        }
        RawData::F32(d) => {
            stages.insert("raw".into(), stage_f32(out, "raw", raw.width, raw.height, raw.cpp, d));
        }
    }

    let n = raw.normalized().expect("normalized");
    stages.insert("normalized".into(), stage_f32(out, "normalized", n.width, n.height, n.cpp, &n.data));
    drop(n);

    let xy = color::as_shot_white_xy(&raw);
    let t = color::camera_transform(&raw, xy);
    for (label, method) in [("bilinear", Method::Bilinear), ("ppg", Method::Ppg), ("ahd", Method::Ahd)] {
        let img = raw.develop(method).expect("develop");
        stages.insert(format!("develop-{label}"), stage_f32(out, &format!("develop-{label}"), img.width, img.height, 3, &rgb_flat(&img)));
        if label == "ahd" {
            let mut img = img;
            let rebuilt = highlight::reconstruct(&mut img, t.wb, 0.99);
            let mut s = stage_f32(out, "highlight-ahd", img.width, img.height, 3, &rgb_flat(&img));
            s["rebuilt"] = json!(rebuilt);
            stages.insert("highlight-ahd".into(), s);
        }
    }
    for k in [2usize, 4] {
        if let Some((img, mask)) = raw.develop_binned_masked(k, 0.99).expect("binned") {
            let mut s = stage_f32(out, &format!("binned{k}"), img.width, img.height, 3, &rgb_flat(&img));
            s["mask_fnv1a64"] = json!(format!("{:016x}", fnv(mask.iter().copied())));
            stages.insert(format!("binned{k}"), s);
        }
    }

    let mut spectral_t = Value::Null;
    if let Some(model) = raw.metadata.model.as_deref()
        && let Some(cam) = spectral::find(raw.metadata.make.as_deref(), model)
    {
        let mut c = raw.color.clone();
        cam.fill(&mut c);
        let sxy = spectral::as_shot_white_through(cam, &raw.color, raw.wb_multipliers);
        let st = sxy.map(|sxy| color::camera_transform_of(&c, sxy));
        spectral_t = json!({"camera": cam.model, "xy": sxy, "transform": st, "filled_color": c});
    }

    let preview = embedded_preview(&bytes);
    if let Some(p) = &preview {
        fs::write(out.join("preview.jpg"), p).expect("write preview");
    }
    let info = json!({
        "file": input.file_name().map(|f| f.to_string_lossy().to_string()),
        "info": raw.info(),
        "black": raw.black,
        "white": raw.white,
        "bits": raw.bits,
        "as_shot_xy": xy,
        "transform_file_only": t,
        "spectral": spectral_t,
        "preview_bytes": preview.as_ref().map(Vec::len),
        "lens_opcodes_removed": lens_ops,
        "stages": stages,
    });
    fs::write(out.join("info.json"), serde_json::to_string_pretty(&info).expect("json")).expect("write info");
    println!("{} -> {}", input.display(), out.display());
}
