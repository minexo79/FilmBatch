# ProPhoto RGB Output Implementation Plan

**Goal:** Add genuine RAW-wide-gamut ProPhoto RGB 16-bit TIFF/PNG export, preserving existing sRGB output and preview behavior.

**Architecture:** Keep scene-linear Rec.2020 RAW data and existing linear/display sRGB effect coordinates. For ProPhoto output, bypass only the RAW sRGB gamut map, carry signed extended-sRGB through display effects, then decode, Bradford-convert to ProPhoto D50, gamut-map there, encode pure gamma 1.8, and embed a matching ICC profile. Output color is explicit metadata, not a label applied to sRGB pixels.

**Tech Stack:** C#/.NET 10, existing CPU pipeline, Dawn/WGSL, Magick.NET/LCMS, Apple ImageIO/ColorSync, Avalonia shared desktop/mobile export panel.

**Spec:** User-authorized complete implementation and license-conscious plan following the ProPhoto investigation; numerical/source evidence is retained in `artifacts/verification/2026-10-23-prophoto-research.tar.gz`. This document contains the acceptance specification and execution ledger.

## Global constraints

- No new runtime dependencies. Reuse `ColorSpaces.ProPhoto`, Bradford adaptation, existing float working buffers, renderer fallback and encoding paths.
- sRGB remains the default. Preview, thumbnails, histogram, sampling and JPEG/DNG remain sRGB. ProPhoto is selectable only for TIFF16/PNG16; invalid API/CLI combinations fail before producing files. Changing the UI to JPEG/DNG resets the selection to sRGB.
- Pure gamma 1.8, no linear ROMM toe and no flare offset. Profile matrix and transfer function must match rendered pixels. Do not ship a downloaded profile with different black-point behavior.
- This is output-space support, not an all-ProPhoto effect migration, monitor soft-proofing, or wide-gamut raster-import redesign. Existing raster import stays color-managed to sRGB. Existing bounded film LUTs, curves and creative effects retain their defined sRGB-coordinate operations; colors they intentionally clip cannot be recovered. Neutral RAW output and linear exposure must retain colors inside ProPhoto but outside sRGB.
- Preserve alpha, source files, EXIF dimensions/orientation, cancellation, collision handling, GPU resource ownership, and full-size export. No per-pixel allocations or CPU readback inserted into GPU processing.
- No GPL/LGPL implementation copied. Existing LightCraft MIT material remains attributed. Generate a FilmBatch-owned ICC from numeric colorimetry using Little CMS (MIT), retaining generator/version/hash and LCMS notice.
- Current baseline: `dev-ai-denoising`, `35da12a5ef0e6b4ce17bbbb68e660f8ce65ad7e5`. Commit/push is not inferred from an earlier completed task.
- Windows rendering report is separately blocked on original export, resolved settings and build identity. Mac CPU/Metal agreement is not evidence that the Windows report is fixed.

## Contract and ownership

| Slice | Files | Contract |
|---|---|---|
| Parent: representation and rendering | `Imaging/RgbEncoding.cs`, `Imaging/SourceImage.cs`, `Rendering/{IPreviewRenderer,CpuFakeRenderer,GpuSpikeRenderer,CpuPipeline,ModuleParams,GpuEffectPipeline}.cs`, `Imaging/Raw/{RawDisplay,RawDisplayGpu}.cs`, output WGSL and renderer doubles | `RgbEncoding { Srgb, ProPhoto }`; `SourceImage.Encoding`; `Render16(SourceImage, Settings, CancellationToken = default, RgbEncoding encoding = RgbEncoding.Srgb)`; `ModuleParams.OutputEncoding`; internal `RgbEncodingInfo.ProPhotoProfile` returns matching embedded ICC bytes |
| Codec slice | `Imaging/{ImageBackend,MagickImageCodec,AppleImageCodec}.cs`, `Imaging/Raw/Profiles/prophoto-gamma18.icc`, profile generator, `THIRD-PARTY-NOTICES.txt`, codec regression tests | Encode `SourceImage.Rgba16` using its `Encoding`; no extra color conversion; ProPhoto only TIFF16/PNG16. Apple constructs color space from the exact ICC and injects exact profile bytes. Parent supplies profile resource loader. |
| Export slice | `Exporting/{ExportModels,BatchExporter}.cs`, shared export VM/view/localizations, `FilmBatch.Cli/Program.cs`, export regressions | `ExportOptions.ColorSpace: RgbEncoding`, default Srgb; `ExportLogEntry.ColorSpace` human-readable string; call Render16 with fourth argument; preserve Encoding in export resize; CLI `--color-space srgb|prophoto`; shared UI choices and unsupported-format normalization |

Parent owns integration and all builds/tests/smokes. Slice workers do not run build/lint/test/formatters mid-flight and do not touch other slices.

## Task 1 — Establish the missing-gamut regression

- [x] Add `tests/FilmBatch.Core.Tests/ProPhotoOutputTests.cs`. Construct scene-linear Rec.2020 `[0, .18, 0]`, render through the real CPU renderer, decode output in its declared space and compare chromaticity in D65 XYZ to `(0.1700, 0.7970)` within `0.0002`. Assert source scene is unchanged. The current sRGB gamut map must fail this invariant before implementation.
- [x] Run `dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ProPhotoOutputTests` and retain the actual failure. After the new API is implemented, change only the call to request ProPhoto and the output decode to its declared gamma/primaries; retain the chromaticity oracle.

## Task 2 — Explicit output representation and CPU/GPU processing

- [x] Add enum and shared conversion/profile helper. Cache matrices and lazy ICC bytes; reject unknown enum values. `SourceImage` constructor gains optional encoding, clone/resize retain it; raw display copy remains sRGB.
- [x] Extend Render16 and all implementations/doubles with the optional last encoding argument. Preview Render continues to request sRGB. CPU/GPU fallback receives `ModuleParams.OutputEncoding` and cannot silently drop it.
- [x] Keep existing RAW tone/highlight order. `RawDisplay.FinishLinear(..., bool extendedSrgb = false)` skips the sRGB gamut reduction only when extended; signed transfer is `sign(x) * srgb(abs(x))`. WGSL uses a u32 flag in the existing finish uniform's final band padding slot (offset 60, unchanged 112-byte total uniform).
- [x] On final ProPhoto output, signed sRGB-decode, multiply cached sRGB/D65-to-ProPhoto/D50 matrix, reduce out-of-target chroma toward target luminance, clamp to [0,1], and encode `pow(x, 1/1.8)`. Alpha is untouched. CPU fuses conversion with existing RGBA16 quantization. GPU uses one output conversion pass on existing nonaliasing rgba32float work textures, followed by existing banded RGBA16 encode/readback. The new kernel is lazy and renderer-owned.
- [x] Tagged rendered ProPhoto input is decoded to the existing linear-sRGB work buffer, rather than interpreted as sRGB. Regular preview remains sRGB; generic raster import remains unchanged.
- [x] Add meaningful numerical checks: Rec.2020 green chromaticity; neutral gamma/black/white; nonopaque alpha; saturated/HDR/negative patches; GPU CPU parity including a tail readback band and repeated sRGB/ProPhoto alternation; preview remains sRGB after export. Keep established sRGB tolerances, use at most 2 RGBA16 codes for neutral ProPhoto CPU/GPU comparisons.

## Task 3 — Matching profile and native encoders

- [x] Generate ICC with LCMS 2.17, FilmBatch ProPhoto primaries `(0.734699,0.265301)`, `(0.159597,0.840403)`, `(0.036598,0.000105)`, white `(0.3457,0.3585)` and gamma 1.8. Use stable description/copyright/date and profile ID after canonicalization. Retain reproducible generator under existing `tools/`, binary under existing embedded profile glob.
- [x] Magick assigns matching output ICC without transforming already-encoded pixels. Preserve RGBA16, EXIF and existing compression/container rules. Apple creates CGColorSpace from matching ICC, retains alpha/16-bit output, then replaces generated container ICC with exact canonical bytes as existing metadata injection permits.
- [x] Verify TIFF/PNG profile bytes, 16-bit samples, alpha and metadata through actual native encoders. Compare ICC-managed decoded XYZ or linear Rec.2020 against independent numeric patch expectations; a tag-only test is insufficient. Include black, gray and wide-gamut green to catch TRC/white-point/flare mismatch.
- [x] Update third-party notices with origin, selected license, tool version, numeric definition, profile hash, and exact distinctions from strict ROMM. Do not adopt GPL/LGPL peer code or downloaded profiles.

## Task 4 — Export UI, CLI and log propagation

- [x] Add `ExportOptions.ColorSpace` and validate enum/format at public export entry and direct encode entry before writing. Unsupported ProPhoto JPEG/DNG fails rather than silently switching format or tagging.
- [x] Pass selected encoding to Render16 and preserve it across Lanczos downscale. Log the selected color space for success/error entries so exported bytes can be reproduced.
- [x] Shared export panel adds sRGB and ProPhoto RGB options. ProPhoto enabled only for TIFF/PNG; format change to JPEG/DNG resets sRGB. Selection is part of the immutable export snapshot, not persisted photo settings or a new project schema.
- [x] CLI adds `--color-space` with the same validated options; invalid values return nonzero without output. Update command help and both existing localization catalogs.
- [x] Existing tests must cover actual export format transitions, native file profile/pixels, resizing/alpha, and invalid combinations; avoid source-text/wiring tests.

## Task 5 — Integrated verification and documentation

- [x] Run focused Core/App tests, full Release solution suite once integrated, and `build/CheckShaders.cs` with Naga for all expanded compute entry points. Report external-corpus/hardware skips separately.
- [x] Run the actual CLI against an isolated copy of `dist/bug202610231506/DSC01282.ARW`: ProPhoto TIFF16/PNG16, CPU and Metal, plus default sRGB and invalid JPEG/DNG combinations. Verify size, ICC, sample depth, alpha, logs and original SHA-256. Compare decoded wide-gamut pixels and color-managed sRGB appearance, not raw code values across spaces.
- [x] Launch native Avalonia app in an isolated profile; select TIFF/PNG, choose ProPhoto, export a real RAW, and inspect output. Confirm preview/histogram remains sRGB and switching to JPEG resets selection. Inspect a native screenshot; headless tests alone do not establish UI acceptance.
- [x] Exercise Apple encoder through the existing macOS test harness even though desktop defaults to Magick. Windows/iOS execution remains an explicit untested boundary when unavailable, not inferred from Mac results.
- [x] Update existing architecture/building/history documentation with supported formats, CLI syntax, profile semantics, bounded-effect/import limitations, verification receipts and Windows blocker. Retain compact local evidence without uploading original photographs or model weights.

## Acceptance ledger

| Criterion | Status | Evidence |
|---|---|---|
| AC-P1 Complete plan and source/license contract | verified | this document; retained research archive; no new runtime dependency or GPL/LGPL implementation |
| AC-P2 True neutral RAW-wide-gamut CPU/GPU output | verified on Mac CPU/Metal | green xy within 0.0002 at exposure 0/+1; six-face boundary regression; full-photo maximum difference 1/65535 |
| AC-P3 Existing sRGB behavior and preview unchanged | verified on Mac | full Release suite; native export preserved preview bytes and histogram; subsequent sRGB rendering regression |
| AC-P4 Matching 16-bit samples/ICC/alpha on Magick and Apple | verified on macOS codecs | four real PNG/TIFF encoder cases preserved exact samples, alpha and ICC; independent CMS PCS XYZ error ≤0.0001 |
| AC-P5 UI/CLI/resize/log propagation and invalid-format behavior | verified; device boundaries below | actual native export, TIFF/JPEG transitions, eight CLI scenarios and export/resize regressions |
| AC-P6 License and generated-profile provenance | verified | LCMS 2.17 MIT generator, reproducible ICC v4.3 bytes, numeric TRC and profile hash; third-party notices |
| AC-P7 Source preservation and full-size real-photo export | verified | 4672×7008×16-bit files; original/copy SHA-256 unchanged; decoded file checks |
| WIN-P Windows/macOS RAW report | blocked, not fixed | supplied export proves pixel-level difference; missing resolved settings/build and Windows runtime |
| JPEG-P Windows/macOS JPEG report | blocked, not fixed | identical output ICC/quantization tables do not explain pixel-level black/blue artifacts; missing matched settings/build/runtime |

Consumer map: CPU renderer, GPU renderer/fallback, preview/thumbnails, source clone/resize, batch Lanczos resize, Magick/Apple codec, CLI, shared desktop/mobile panel, log serializer, renderer test doubles, documentation. All are in scope; runtime Windows/iOS boundaries must be named explicitly. No claim of wide-gamut monitor display, wide-gamut raster import, or appearance-invariant arbitrary creative effects.

## Final verification evidence — 2026-10-23

Executed on Apple M3/Metal against the working tree based on `35da12a5ef0e6b4ce17bbbb68e660f8ce65ad7e5`; no commit, push, new distribution package or notarization is claimed. Compact receipts, TRX files, exercised binary/source hashes, native screenshots and disposable drivers are retained in [`2026-10-23-prophoto-output.tar.gz`](../../../artifacts/verification/2026-10-23-prophoto-output.tar.gz), SHA-256 `349a476c372138218a8f0950151b3e01baab279514045cdc8c68dcbb48bebd8d`. No original photograph, full-size export or model weights are in that archive.

| Final Release suite | Passed | Skipped | Failed |
|---|---:|---:|---:|
| Core | 752 | 27 | 0 |
| App | 303 | 0 | 0 |
| Raw | 643 | 293 | 0 |
| Total | 1,698 | 320 | 0 |

The 320 skips comprise 314 missing external RAW/oracle cases, one unavailable `rawinfo` example binary, and five Windows-only cases. They are not passes. Focused ProPhoto tests passed 28/28; the final full suite includes the shared export UI tests. Naga validated all 38 expanded WGSL entry points. The Release CLI build completed with zero warnings/errors.

Real-photo results (`final/file-checks/receipt.json`):

- All six outputs were 4672×7008, 16-bit; opaque alpha remained 65535. CPU/Metal ProPhoto maximum difference was **1/65535**, below the unchanged **2/65535** criterion. Same-renderer TIFF/PNG samples were identical; native GUI and CLI Metal samples were identical.
- Both ProPhoto renderers retained 31,390 out-of-sRGB pixels in a deterministic 271,150-pixel grid. This proves retained wide-gamut samples, not full-image gamut coverage.
- Independent LCMS ProPhoto-to-sRGB comparison used 91,565 in-gamut sample pixels: mean error 0.01161, p99 0.03113, maximum 0.07782 in 8-bit code units. Raw RGB values across different encodings were not compared as if they described the same colors.
- Five valid CLI exports returned 0 with the expected `Renderer` and `ColorSpace`; invalid ProPhoto JPEG/DNG and unknown color each returned 2 without output files.
- Native UI selected ProPhoto PNG and completed an export; preview bytes and histogram were unchanged. TIFF retained ProPhoto; JPEG reset to disabled sRGB. All three captured native surfaces were inspected.
- Original and isolated RAW copy remained SHA-256 `9bfbc40308ee7654b40e131dc5455f04d55ba19574456b16649e8db120d946b7`.

### Precision failures caught before acceptance

The original Rec.2020-green check failed at x≈0.26666256 rather than 0.1700, demonstrating the prior sRGB gamut loss. The first generated ICC's v2 `curv` rounded gamma to 1.796875; the final v4.3 parametric profile retains 1.8000030517578125. Numerical CMS checks use the actual ICC PCS D50 XYZ, not a different xy-derived white; the absolute 0.0001 error bound was retained.

Full-photo verification then found 51 channels beyond the CPU/Metal tolerance, maximum 4/65535. Extracted pixels reproduced the issue; a six-face saturated-ramp regression failed at CPU 0/GPU 3. Neutral-axis interpolation `y + (channel - y) * t` left tiny positive cancellation residues at the limiting zero channel, amplified by pure gamma 1.8. CPU and WGSL now use the equivalent boundary-anchored projection. A rounding barrier alone was insufficient; no epsilon clamp or tolerance relaxation was used. The regression passed and all 32 extracted trouble pixels agreed exactly before the final full-image check. `boundary-red.txt`, `deltas*/receipt.json` and `precision/precision.json` retain before/after evidence; the diagnostic shader-instrumentation driver applies to the pre-fix shader, not the final implementation.

### Consumer coverage and runtime limits

- **Covered:** CPU/GPU renderer and CPU fallback encoding, source metadata and resize, preview/thumbnail defaults, batch resize, native Magick/Apple encoding, CLI/log snapshots, renderer doubles and shared export panel. Evidence is the final suite plus native/CLI/file smoke, not only a first-caller assertion.
- **Unchanged by design:** persisted photo/project schema, sRGB preview/histogram/sampling, ordinary raster import, bounded creative-effect semantics and existing sRGB gamut compression.
- **Untested:** Windows/DX12 execution and iOS device/simulator execution. macOS ImageIO tests exercise shared native codec logic but do not establish iOS runtime acceptance. No monitor soft-proofing or appearance-invariant arbitrary creative effects is claimed.

### Separate Windows investigations

`dist/bug202610231506/filmbatch-windows_export.jpg` contains a genuine darker exported sky/shadow result, not merely a screenshot-profile issue. It is 4486×6730 sRGB JPEG with only orientation metadata; settings and build are not embedded. Earlier controlled Mac RAW runs at 1600/2560 and lens on/off differed by at most 1/65535 between CPU/Metal, but did not replay the unknown Windows settings.

For `dist/bug202610232110`, the Windows JPEG's black/blue posterization is in decoded pixels. Mac/Windows exports have byte-identical ICC and JPEG quantization tables; color-managed decoding preserves the discrepancy. Six controlled settings on immutable pre-ProPhoto binaries had CPU/Metal difference at most 1/65535 without reproducing the artifact. These are not a replay of those exports and do not distinguish rendering from JPEG encoding.

Both reports need the corresponding `.filmbatch/export_log.json` (or project/preset snapshots), Windows build revision and renderer; matched Windows CPU/GPU execution may then be required. No configured Windows SSH host/VM was available. No speculative DX12 patch was made, and this ProPhoto precision correction is **not** claimed to fix either report. Compact forensic and controlled-run receipts are included in the evidence archive.

